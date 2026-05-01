using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PocketSignal.Api.Data;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.Mt5;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Forex;

public class ForexNotificationService : IForexNotificationService
{
    private const int MinimumConfidence = 72;
    private const int OppositeDirectionOverrideConfidence = 88;

    private static readonly TimeSpan SameDirectionCooldown = TimeSpan.FromMinutes(45);
    private static readonly TimeSpan SymbolCooldown = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan EntryZoneCooldown = TimeSpan.FromMinutes(90);
    private static readonly TimeSpan DatabaseDuplicateCooldown = TimeSpan.FromMinutes(60);

    private readonly ITelegramService _telegramService;
    private readonly IForexChartImageService _chartImageService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ForexNotificationService> _logger;
    private readonly PocketSignalDbContext _dbContext;
    private readonly IMt5AutoTradeQueueService _mt5QueueService;

    public ForexNotificationService(
        ITelegramService telegramService,
        IForexChartImageService chartImageService,
        IMemoryCache cache,
        ILogger<ForexNotificationService> logger,
        PocketSignalDbContext dbContext,
        IMt5AutoTradeQueueService mt5QueueService)
    {
        _telegramService = telegramService;
        _chartImageService = chartImageService;
        _cache = cache;
        _logger = logger;
        _dbContext = dbContext;
        _mt5QueueService = mt5QueueService;
    }

    public async Task<(bool Sent, string Message)> NotifyIfValidSignalAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken = default)
    {
        if (signal.Direction == "WAIT")
        {
            return (false, "Forex WAIT signal Telegram-a gonderilmedi.");
        }

        if (signal.Direction != "LONG" && signal.Direction != "SHORT")
        {
            return (false, "Forex direction LONG/SHORT deyil, Telegram-a gonderilmedi.");
        }

        if (signal.Confidence < MinimumConfidence)
        {
            return (false, $"Forex confidence {MinimumConfidence}-den asagidir, Telegram-a gonderilmedi.");
        }

        if (!IsTradePlanValid(signal))
        {
            return (false, "Forex trade plan tam deyil. Entry/SL/TP melumatlari duzgun deyil.");
        }

        var normalizedSymbol = Normalize(signal.Symbol);

        var databaseDuplicate = await HasRecentDatabaseDuplicateAsync(
            signal,
            cancellationToken);

        if (databaseDuplicate.IsDuplicate)
        {
            return (false, databaseDuplicate.Message);
        }

        var sameDirectionKey = BuildSameDirectionKey(
            normalizedSymbol,
            signal.Direction);

        if (_cache.TryGetValue(sameDirectionKey, out _))
        {
            return (false, $"Eyni {signal.Symbol} {signal.Direction} signal ucun {SameDirectionCooldown.TotalMinutes:0} deqiqelik cooldown aktivdir.");
        }

        var entryZoneKey = BuildEntryZoneKey(
            normalizedSymbol,
            signal.Direction,
            signal.EntryPrice);

        if (_cache.TryGetValue(entryZoneKey, out _))
        {
            return (false, "Bu Forex signal eyni entry zonasindan artiq gonderilib. Duplicate zone filter aktivdir.");
        }

        var symbolCooldownKey = BuildSymbolCooldownKey(normalizedSymbol);

        if (_cache.TryGetValue<LastForexSignalCacheItem>(
                symbolCooldownKey,
                out var lastSignal) &&
            lastSignal != null)
        {
            var isOppositeDirection = lastSignal.Direction != signal.Direction;
            var canOverride = isOppositeDirection &&
                              signal.Confidence >= OppositeDirectionOverrideConfidence;

            if (!canOverride)
            {
                return (
                    false,
                    $"Symbol cooldown aktivdir. Son signal: {lastSignal.Symbol} {lastSignal.Direction}. Yeni signal Telegram-a gonderilmedi.");
            }
        }

        var message = ForexMessageFormatter.Format(signal);

        var chartImagePath = await TryCreateChartImageAsync(
            signal,
            cancellationToken);

        var sentAsPhoto = false;

        if (!string.IsNullOrWhiteSpace(chartImagePath) &&
            File.Exists(chartImagePath))
        {
            try
            {
                await _telegramService.SendPhotoAsync(
                    chartImagePath,
                    message,
                    cancellationToken);

                sentAsPhoto = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Forex signal chart Telegram-a photo kimi gonderilmedi. Text fallback istifade olunacaq. Symbol: {Symbol}",
                    signal.Symbol);
            }
        }

        if (!sentAsPhoto)
        {
            await _telegramService.SendMessageAsync(
                message,
                cancellationToken);
        }

        SaveCooldowns(
            normalizedSymbol,
            signal);

        var mt5Result = await TryAddToMt5QueueAsync(
            signal,
            cancellationToken);

        var telegramMessage = sentAsPhoto
            ? "Forex signal Telegram-a chart sekli ile gonderildi."
            : "Forex signal Telegram-a text kimi gonderildi. Chart yaradilmadi ve ya gonderilmedi.";

        return (
            true,
            $"{telegramMessage} MT5: {mt5Result}");
    }

    private async Task<string> TryAddToMt5QueueAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _mt5QueueService.EnqueueAsync(
                signal,
                cancellationToken);

            if (result.Added)
            {
                _logger.LogInformation(
                    "Forex signal MT5 queue-ya elave edildi. OrderId: {OrderId} | {Symbol} {Direction}",
                    result.Order?.Id,
                    signal.Symbol,
                    signal.Direction);
            }
            else
            {
                _logger.LogInformation(
                    "Forex signal MT5 queue-ya elave edilmedi. {Message}",
                    result.Message);
            }

            return result.Message;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Forex signal MT5 queue-ya elave edilmedi. Symbol: {Symbol}",
                signal.Symbol);

            return $"MT5 queue xetasi: {ex.Message}";
        }
    }

    private async Task<(bool IsDuplicate, string Message)> HasRecentDatabaseDuplicateAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            var sinceUtc = DateTime.UtcNow.Subtract(DatabaseDuplicateCooldown);

            var recentSignals = await _dbContext.ForexSignals
                .AsNoTracking()
                .Where(x =>
                    x.CreatedAtUtc >= sinceUtc &&
                    x.Symbol == signal.Symbol &&
                    x.Direction == signal.Direction &&
                    x.IsTradable)
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(20)
                .ToListAsync(cancellationToken);

            if (recentSignals.Count == 0)
            {
                return (false, string.Empty);
            }

            var currentZone = GetEntryZone(
                Normalize(signal.Symbol),
                signal.EntryPrice);

            var sameZoneSignal = recentSignals.FirstOrDefault(x =>
                GetEntryZone(
                    Normalize(x.Symbol),
                    x.EntryPrice) == currentZone);

            if (sameZoneSignal != null)
            {
                return (
                    true,
                    $"DB duplicate filter aktivdir. Son {DatabaseDuplicateCooldown.TotalMinutes:0} deqiqede {signal.Symbol} {signal.Direction} eyni entry zonada Telegram-a gonderilib.");
            }

            var latestSameDirection = recentSignals.First();

            return (
                true,
                $"DB duplicate filter aktivdir. Son {DatabaseDuplicateCooldown.TotalMinutes:0} deqiqede {signal.Symbol} {signal.Direction} signal artiq Telegram-a gonderilib. Son entry: {latestSameDirection.EntryPrice}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Forex DB duplicate yoxlamasi ugursuz oldu. Signal prosesi davam edir. Symbol: {Symbol}",
                signal.Symbol);

            return (false, string.Empty);
        }
    }

    private async Task<string?> TryCreateChartImageAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _chartImageService.GenerateSignalChartAsync(
                signal,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Forex chart yaradilmadi. Signal yenə text kimi gonderilecek. Symbol: {Symbol}",
                signal.Symbol);

            return null;
        }
    }

    private void SaveCooldowns(
        string normalizedSymbol,
        ForexTradeSignal signal)
    {
        var sameDirectionKey = BuildSameDirectionKey(
            normalizedSymbol,
            signal.Direction);

        var entryZoneKey = BuildEntryZoneKey(
            normalizedSymbol,
            signal.Direction,
            signal.EntryPrice);

        var symbolCooldownKey = BuildSymbolCooldownKey(normalizedSymbol);

        _cache.Set(
            sameDirectionKey,
            true,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = SameDirectionCooldown
            });

        _cache.Set(
            entryZoneKey,
            true,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = EntryZoneCooldown
            });

        _cache.Set(
            symbolCooldownKey,
            new LastForexSignalCacheItem
            {
                Symbol = signal.Symbol,
                Direction = signal.Direction,
                Confidence = signal.Confidence,
                EntryPrice = signal.EntryPrice,
                CreatedAtUtc = DateTime.UtcNow
            },
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = SymbolCooldown
            });
    }

    private static bool IsTradePlanValid(ForexTradeSignal signal)
    {
        if (signal.EntryPrice <= 0)
            return false;

        if (signal.StopLoss <= 0)
            return false;

        if (signal.TakeProfit1 <= 0)
            return false;

        if (signal.TakeProfit2 <= 0)
            return false;

        if (signal.Direction == "LONG")
        {
            return signal.StopLoss < signal.EntryPrice &&
                   signal.TakeProfit1 > signal.EntryPrice &&
                   signal.TakeProfit2 > signal.TakeProfit1;
        }

        if (signal.Direction == "SHORT")
        {
            return signal.StopLoss > signal.EntryPrice &&
                   signal.TakeProfit1 < signal.EntryPrice &&
                   signal.TakeProfit2 < signal.TakeProfit1;
        }

        return false;
    }

    private static string BuildSameDirectionKey(
        string normalizedSymbol,
        string direction)
    {
        return $"forex-signal:same-direction:{normalizedSymbol}:{direction}";
    }

    private static string BuildSymbolCooldownKey(
        string normalizedSymbol)
    {
        return $"forex-signal:symbol-cooldown:{normalizedSymbol}";
    }

    private static string BuildEntryZoneKey(
        string normalizedSymbol,
        string direction,
        decimal entryPrice)
    {
        var zone = GetEntryZone(
            normalizedSymbol,
            entryPrice);

        return $"forex-signal:entry-zone:{normalizedSymbol}:{direction}:{zone}";
    }

    private static string GetEntryZone(
        string normalizedSymbol,
        decimal entryPrice)
    {
        var zoneSize = GetEntryZoneSize(
            normalizedSymbol,
            entryPrice);

        if (zoneSize <= 0)
            zoneSize = 0.0005m;

        var zoneNumber = Math.Round(
            entryPrice / zoneSize,
            0,
            MidpointRounding.AwayFromZero);

        return zoneNumber.ToString("0");
    }

    private static decimal GetEntryZoneSize(
        string normalizedSymbol,
        decimal entryPrice)
    {
        if (entryPrice <= 0)
            return 0.0005m;

        if (normalizedSymbol.Contains("JPY"))
            return 0.20m;

        if (normalizedSymbol.Contains("XAU"))
            return 2.0m;

        if (normalizedSymbol.Contains("BTC"))
            return entryPrice * 0.002m;

        if (normalizedSymbol.Contains("ETH"))
            return entryPrice * 0.002m;

        if (normalizedSymbol.Contains("USOIL"))
            return 0.20m;

        return 0.0015m;
    }

    private static string Normalize(string symbol)
    {
        return symbol
            .Replace("/", "_")
            .Replace("-", "_")
            .Replace(" ", "")
            .ToUpperInvariant();
    }

    private sealed class LastForexSignalCacheItem
    {
        public string Symbol { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public int Confidence { get; set; }
        public decimal EntryPrice { get; set; }
        public DateTime CreatedAtUtc { get; set; }
    }
}
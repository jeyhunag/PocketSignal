using Microsoft.Extensions.Caching.Memory;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Forex;

public class ForexNotificationService : IForexNotificationService
{
    private const int MinimumConfidence = 82;
    private const int OppositeDirectionOverrideConfidence = 90;

    private static readonly TimeSpan SameDirectionCooldown = TimeSpan.FromMinutes(60);
    private static readonly TimeSpan SymbolCooldown = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan EntryZoneCooldown = TimeSpan.FromMinutes(120);

    private readonly ITelegramService _telegramService;
    private readonly IForexChartImageService _chartImageService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ForexNotificationService> _logger;

    public ForexNotificationService(
        ITelegramService telegramService,
        IForexChartImageService chartImageService,
        IMemoryCache cache,
        ILogger<ForexNotificationService> logger)
    {
        _telegramService = telegramService;
        _chartImageService = chartImageService;
        _cache = cache;
        _logger = logger;
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

        return sentAsPhoto
            ? (true, "Forex signal Telegram-a chart sekli ile gonderildi.")
            : (true, "Forex signal Telegram-a text kimi gonderildi. Chart yaradilmadi ve ya gonderilmedi.");
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
        var zoneSize = IsJpyPair(normalizedSymbol)
            ? 0.05m
            : 0.0005m;

        var zoneNumber = Math.Round(
            entryPrice / zoneSize,
            0,
            MidpointRounding.AwayFromZero);

        return zoneNumber.ToString("0");
    }

    private static bool IsJpyPair(string normalizedSymbol)
    {
        return normalizedSymbol.Contains("JPY");
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
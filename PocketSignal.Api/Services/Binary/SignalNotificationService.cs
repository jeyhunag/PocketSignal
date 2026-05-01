using Microsoft.Extensions.Caching.Memory;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Services.Stats;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Binary;

public class SignalNotificationService : ISignalNotificationService
{
    private const int MinimumConfidence = 82;

    private readonly ITelegramService _telegramService;
    private readonly IBinaryChartImageService _chartImageService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SignalNotificationService> _logger;

    public SignalNotificationService(
        ITelegramService telegramService,
        IBinaryChartImageService chartImageService,
        IMemoryCache cache,
        ILogger<SignalNotificationService> logger)
    {
        _telegramService = telegramService;
        _chartImageService = chartImageService;
        _cache = cache;
        _logger = logger;
    }

    public async Task<(bool Sent, string Message)> NotifyIfValidSignalAsync(
        SmartTradeSignal signal,
        CancellationToken cancellationToken = default)
    {
        if (signal.Direction == "WAIT")
        {
            return (false, "WAIT signal Telegram-a gonderilmedi.");
        }

        if (signal.Direction != "LONG" && signal.Direction != "SHORT")
        {
            return (false, "Binary direction LONG/SHORT deyil, Telegram-a gonderilmedi.");
        }

        if (signal.Confidence < MinimumConfidence)
        {
            return (false, $"Confidence {MinimumConfidence}-den asagidir, Telegram-a gonderilmedi.");
        }

        if (signal.ExpiryMinutes <= 0)
        {
            return (false, "ExpiryMinutes duzgun deyil, Telegram-a gonderilmedi.");
        }

        if (signal.LastClose <= 0)
        {
            return (false, "Entry qiymeti duzgun deyil, Telegram-a gonderilmedi.");
        }

        // VACIB:
        // Burada expiry-ni cache key-den cixardiq.
        // Evvel: USD/CAD LONG 12m ve USD/CAD LONG 10m ayri sayilirdi.
        // Indi: USD/CAD LONG ucun expiry bitene qeder tekrar signal getmeyecek.
        var cacheKey =
            $"binary-telegram-cooldown:{Normalize(signal.Symbol)}:{signal.Direction}";

        if (_cache.TryGetValue(cacheKey, out _))
        {
            return (false, $"Binary cooldown aktivdir. {signal.Symbol} {signal.Direction} ucun expiry bitmeyib.");
        }

        var message = SignalMessageFormatter.Format(signal);

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
                    "Binary signal chart Telegram-a photo kimi gonderilmedi. Text fallback istifade olunacaq. Symbol: {Symbol}",
                    signal.Symbol);
            }
        }

        if (!sentAsPhoto)
        {
            await _telegramService.SendMessageAsync(
                message,
                cancellationToken);
        }

        var cooldownMinutes = Math.Max(1, signal.ExpiryMinutes);

        _cache.Set(
            cacheKey,
            true,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(cooldownMinutes)
            });

        return sentAsPhoto
            ? (true, $"Binary signal Telegram-a chart sekli ile gonderildi. Cooldown: {cooldownMinutes} deqiqe.")
            : (true, $"Binary signal Telegram-a text kimi gonderildi. Cooldown: {cooldownMinutes} deqiqe.");
    }

    private async Task<string?> TryCreateChartImageAsync(
        SmartTradeSignal signal,
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
                "Binary chart yaradilmadi. Signal yenə text kimi gonderilecek. Symbol: {Symbol}",
                signal.Symbol);

            return null;
        }
    }

    private static string Normalize(string symbol)
    {
        return symbol
            .Replace("/", "_")
            .Replace("-", "_")
            .Replace(" ", "")
            .ToUpperInvariant();
    }
}
using Microsoft.Extensions.Caching.Memory;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Forex;

public class ForexNotificationService : IForexNotificationService
{
    private readonly ITelegramService _telegramService;
    private readonly IMemoryCache _cache;

    public ForexNotificationService(
        ITelegramService telegramService,
        IMemoryCache cache)
    {
        _telegramService = telegramService;
        _cache = cache;
    }

    public async Task<(bool Sent, string Message)> NotifyIfValidSignalAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken = default)
    {
        if (signal.Direction == "WAIT")
        {
            return (false, "Forex WAIT signal Telegram-a gonderilmedi.");
        }

        if (signal.Confidence < 82)
        {
            return (false, "Forex confidence 82-den asagidir, Telegram-a gonderilmedi.");
        }

        var cacheKey = $"forex-telegram:{Normalize(signal.Symbol)}:{signal.Direction}";

        if (_cache.TryGetValue(cacheKey, out _))
        {
            return (false, "Bu Forex signal artiq gonderilib. Cooldown aktivdir.");
        }

        var message = ForexMessageFormatter.Format(signal);

        await _telegramService.SendMessageAsync(
            message,
            cancellationToken);

        _cache.Set(
            cacheKey,
            true,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(30)
            });

        return (true, "Forex signal Telegram-a gonderildi.");
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
using Microsoft.Extensions.Caching.Memory;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Services.Stats;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Binary;

public class SignalNotificationService : ISignalNotificationService
{
    private readonly ITelegramService _telegramService;
    private readonly IMemoryCache _cache;

    public SignalNotificationService(
        ITelegramService telegramService,
        IMemoryCache cache)
    {
        _telegramService = telegramService;
        _cache = cache;
    }

    public async Task<(bool Sent, string Message)> NotifyIfValidSignalAsync(
        SmartTradeSignal signal,
        CancellationToken cancellationToken = default)
    {
        if (signal.Direction == "WAIT")
        {
            return (false, "WAIT signal Telegram-a gonderilmedi.");
        }

        if (signal.Confidence < 82)
        {
            return (false, "Confidence 82-den asagidir, Telegram-a gonderilmedi.");
        }

        var cacheKey =
            $"telegram-signal:{signal.Symbol}:{signal.Direction}:{signal.ExpiryMinutes}";

        if (_cache.TryGetValue(cacheKey, out _))
        {
            return (false, "Bu signal artiq gonderilib. Cooldown aktivdir.");
        }

        var message = SignalMessageFormatter.Format(signal);

        await _telegramService.SendMessageAsync(
            message,
            cancellationToken);

        _cache.Set(
            cacheKey,
            true,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            });

        return (true, "Signal Telegram-a gonderildi.");
    }
}
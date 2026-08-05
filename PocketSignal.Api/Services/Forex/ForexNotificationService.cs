using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PocketSignal.Api.Data;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Forex;

/// <summary>
/// CASSANDRA notification — bias əsaslı (Entry/SL/TP DEYİL).
/// Şəkil + Cassandra mətni Telegram-a göndərir.
/// Eyni bias təkrar göndərilməsin deyə cooldown var.
/// </summary>
public class ForexNotificationService : IForexNotificationService
{
    // Eyni bias üçün cooldown — 30 dəqiqədə bir siqnal verildiyi üçün
    // eyni bias təkrarı çox tez göndərilməsin.
    private static readonly TimeSpan SameBiasCooldown = TimeSpan.FromMinutes(25);

    private readonly ITelegramService _telegramService;
    private readonly IForexChartImageService _chartImageService;
    private readonly IMemoryCache _cache;
    private readonly ILogger<ForexNotificationService> _logger;
    private readonly PocketSignalDbContext _dbContext;

    public ForexNotificationService(
        ITelegramService telegramService,
        IForexChartImageService chartImageService,
        IMemoryCache cache,
        ILogger<ForexNotificationService> logger,
        PocketSignalDbContext dbContext)
    {
        _telegramService = telegramService;
        _chartImageService = chartImageService;
        _cache = cache;
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task<(bool Sent, string Message)> NotifyIfValidSignalAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken = default)
    {
        // Yalnız SELL/BUY bias göndərilir.
        if (signal.Bias != "SELL" && signal.Bias != "BUY")
        {
            return (false, "Cassandra bias NEUTRAL/WAIT — Telegram-a göndərilmədi.");
        }

        // Ən azı bir zona olmalıdır.
        var hasZones = signal.Bias == "SELL"
            ? signal.SellZones.Count > 0
            : signal.BuyZones.Count > 0;

        if (!hasZones)
        {
            return (false, "Cassandra: zona tapılmadı — Telegram-a göndərilmədi.");
        }

        // ===== BIAS DƏYİŞİMİ (şah qırıldı) yoxlaması =====
        // Əvvəlki bias-i yadda saxlayırıq. Dəyişibsə xüsusi bildiriş,
        // və cooldown-a baxmadan göndəririk (vacib hadisədir).
        var lastBiasKey = $"cassandra:last-bias:{signal.Symbol}";
        var biasChanged = false;
        if (_cache.TryGetValue<string>(lastBiasKey, out var previousBias) &&
            !string.IsNullOrEmpty(previousBias) &&
            previousBias != signal.Bias)
        {
            biasChanged = true;
            _logger.LogInformation(
                "Cassandra bias dəyişdi: {Prev} → {New} (şah qırıldı).",
                previousBias, signal.Bias);
        }

        // Eyni bias cooldown-u (bias dəyişibsə cooldown-u keç).
        var biasKey = $"cassandra:bias:{signal.Symbol}:{signal.Bias}";
        if (!biasChanged && _cache.TryGetValue(biasKey, out _))
        {
            return (false, $"Cassandra {signal.Bias} bias üçün {SameBiasCooldown.TotalMinutes:0} dəqiqəlik cooldown aktivdir.");
        }

        // Mətn (Cassandra formatı) — bias dəyişibsə başda xəbərdarlıq.
        var message = BuildCassandraMessage(signal, biasChanged, previousBias);

        // Yeni bias-i yadda saxla (24 saat).
        _cache.Set(lastBiasKey, signal.Bias, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(24)
        });

        // Şəkil.
        var chartImagePath = await TryCreateChartImageAsync(signal, cancellationToken);

        var sentAsPhoto = false;

        if (!string.IsNullOrWhiteSpace(chartImagePath) && File.Exists(chartImagePath))
        {
            try
            {
                await _telegramService.SendPhotoAsync(chartImagePath, message, cancellationToken);
                sentAsPhoto = true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cassandra şəkil Telegram-a göndərilmədi. Text fallback istifadə olunacaq.");
            }
        }

        if (!sentAsPhoto)
        {
            await _telegramService.SendMessageAsync(message, cancellationToken);
        }

        // Cooldown saxla.
        _cache.Set(biasKey, true, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = SameBiasCooldown
        });

        var telegramMessage = sentAsPhoto
            ? "Cassandra siqnalı şəkil ilə göndərildi."
            : "Cassandra siqnalı text kimi göndərildi. Şəkil yaradılmadı.";

        return (true, telegramMessage);
    }

    private static string BuildCassandraMessage(
        ForexTradeSignal signal,
        bool biasChanged,
        string? previousBias)
    {
        var icon = signal.Bias == "SELL" ? "🔴" : "🟢";

        var changeBanner = biasChanged
            ? $"⚡ BIAS DƏYİŞDİ: {previousBias} → {signal.Bias} (şah qırıldı!)\n\n"
            : string.Empty;

        var header = $"🏷️ Bias: {signal.Bias}\n\n";

        // BiasNote strategiyada hazırlanıb — Cassandra formatı.
        var body = string.IsNullOrWhiteSpace(signal.BiasNote)
            ? signal.Message
            : signal.BiasNote;

        return $"{icon} Cassandra Analysis - GOLD\n\n{changeBanner}{header}{body}";
    }

    private async Task<string?> TryCreateChartImageAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _chartImageService.GenerateSignalChartAsync(signal, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cassandra şəkil yaradılmadı. Signal yenə text kimi göndəriləcək.");
            return null;
        }
    }
}
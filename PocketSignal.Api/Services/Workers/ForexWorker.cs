using PocketSignal.Api.Models.Admin;
using PocketSignal.Api.Services.Admin;
using PocketSignal.Api.Services.Forex;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Workers;

public class ForexWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IAdminRuntimeSettingsService _adminSettingsService;
    private readonly ILogger<ForexWorker> _logger;

    public ForexWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IAdminRuntimeSettingsService adminSettingsService,
        ILogger<ForexWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _adminSettingsService = adminSettingsService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("ForexWorker basladi.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (MarketDataApiGroupContext.Use("Forex"))
                {
                    await CheckForexSignalsAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForexWorker xetasi bas verdi.");
            }

            // İnterval timeframe-ə görə: M1 → 15 dəqiqə, M5/M15 → 30 dəqiqə.
            var settingsForInterval = await _adminSettingsService.GetAsync(stoppingToken);
            var tf = string.IsNullOrWhiteSpace(settingsForInterval.ForexTimeframe)
                ? "15min"
                : settingsForInterval.ForexTimeframe;

            var intervalSeconds = tf == "1min" ? 900 : 1800;

            await Task.Delay(
                TimeSpan.FromSeconds(intervalSeconds),
                stoppingToken);
        }
    }

    private async Task CheckForexSignalsAsync(CancellationToken cancellationToken)
    {
        var settings = await _adminSettingsService.GetAsync(cancellationToken);

        if (!settings.ForexEnabled)
        {
            _logger.LogInformation("ForexWorker admin panelden deaktiv edilib.");
            return;
        }

        var symbols = GetActiveForexSymbols(settings);

        if (symbols.Count == 0)
        {
            _logger.LogInformation("ForexWorker ucun aktiv symbol yoxdur.");
            return;
        }

        using var scope = _scopeFactory.CreateScope();

        var forexSignalService =
            scope.ServiceProvider.GetRequiredService<IForexSignalService>();

        var forexNotificationService =
            scope.ServiceProvider.GetRequiredService<IForexNotificationService>();

        var forexSignalDatabaseService =
            scope.ServiceProvider.GetRequiredService<IForexSignalDatabaseService>();

        // Cassandra Entry/SL/TP vermir — trade result tracker söndürülüb.

        // Admin paneldən seçilmiş timeframe (1min/5min/15min).
        var timeframe = string.IsNullOrWhiteSpace(settings.ForexTimeframe)
            ? "15min"
            : settings.ForexTimeframe;

        foreach (var symbol in symbols)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var signal = await forexSignalService.AnalyzeAsync(
                    symbol,
                    timeframe,
                    cancellationToken);

                var result = await forexNotificationService.NotifyIfValidSignalAsync(
                    signal,
                    cancellationToken);

                var savedSignalId = await forexSignalDatabaseService.SaveSignalAsync(
                    signal,
                    result.Sent,
                    result.Message,
                    cancellationToken);

                _logger.LogInformation(
                    "FOREX signal DB saved. Id: {Id} | Symbol: {Symbol} | Direction: {Direction} | Confidence: {Confidence}",
                    savedSignalId,
                    signal.Symbol,
                    signal.Direction,
                    signal.Confidence);

                _logger.LogInformation(
                    "FOREX | Symbol: {Symbol} | Bias: {Bias} | Direction: {Direction} | Sent: {Sent} | Sebeb: {Note} | Message: {Message}",
                    signal.Symbol,
                    signal.Bias,
                    signal.Direction,
                    result.Sent,
                    signal.BiasNote,
                    result.Message);

                // TwelveData limiti: 8 sorğu/dəqiqə. Hər cüt bir neçə sorğu edir.
                // 429-un qarşısını almaq üçün cütlər arası kifayət qədər fasilə.
                await Task.Delay(
                    TimeSpan.FromSeconds(8),
                    cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Forex symbol analizinde xeta oldu. Symbol: {Symbol}",
                    symbol);
            }
        }
    }

    private static List<string> GetActiveForexSymbols(
        AdminRuntimeSettings settings)
    {
        var result = new List<string>();

        // Seçilmiş cütləri əlavə et (checkbox).
        if (settings.ForexActiveSymbols != null)
        {
            foreach (var symbol in settings.ForexActiveSymbols)
            {
                if (string.IsNullOrWhiteSpace(symbol))
                    continue;

                if (!result.Contains(symbol))
                    result.Add(symbol);
            }
        }

        if (result.Count == 0 &&
            !string.IsNullOrWhiteSpace(settings.ForexActiveSymbol))
        {
            result.Add(settings.ForexActiveSymbol);
        }

        // === QIZIL HƏMİŞƏ İŞLƏSİN ===
        // XAU/USD checkbox seçilsə də, seçilməsə də hər zaman analiz olunur (Cassandra).
        // Digər cütlər yalnız seçiləndə işləyir.
        const string gold = "XAU/USD";
        if (!result.Any(s => s.Equals(gold, StringComparison.OrdinalIgnoreCase)))
            result.Insert(0, gold);

        return result;
    }
}
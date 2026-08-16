using PocketSignal.Api.Services.Admin;
using PocketSignal.Api.Services.Binary;
using PocketSignal.Api.Services.MarketData;
using PocketSignal.Api.Services.Stats;

namespace PocketSignal.Api.Services.Workers;

public class SignalWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly IAdminRuntimeSettingsService _adminSettingsService;
    private readonly ILogger<SignalWorker> _logger;

    public SignalWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        IAdminRuntimeSettingsService adminSettingsService,
        ILogger<SignalWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _adminSettingsService = adminSettingsService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("SignalWorker basladi.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using (MarketDataApiGroupContext.Use("Binary"))
                {
                    await CheckSignalsAsync(stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SignalWorker xetasi bas verdi.");
            }

            // İnterval binary timeframe-ə görə: M1 → 15 dəqiqə, M5/M15 → 30 dəqiqə.
            var settingsForInterval = await _adminSettingsService.GetAsync(stoppingToken);
            var btf = string.IsNullOrWhiteSpace(settingsForInterval.BinaryTimeframe)
                ? "15min"
                : settingsForInterval.BinaryTimeframe;

            var intervalSeconds = btf == "1min" ? 900 : 1800;

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task CheckSignalsAsync(CancellationToken cancellationToken)
    {
        var settings = await _adminSettingsService.GetAsync(cancellationToken);

        if (!settings.BinaryEnabled)
        {
            _logger.LogInformation("SignalWorker admin panelden deaktiv edilib.");
            return;
        }

        // Çoxlu binary cütü (forex kimi). Seçilmiş cütlərin hamısı analiz olunur.
        var symbols = new List<string>();
        if (settings.BinaryActiveSymbols != null)
        {
            foreach (var s in settings.BinaryActiveSymbols)
            {
                if (!string.IsNullOrWhiteSpace(s) && !symbols.Contains(s))
                    symbols.Add(s);
            }
        }

        // Heç biri seçilməyibsə köhnə tək symbol-a düş.
        if (symbols.Count == 0 && !string.IsNullOrWhiteSpace(settings.BinaryActiveSymbol))
            symbols.Add(settings.BinaryActiveSymbol);

        if (symbols.Count == 0)
            symbols.Add("EUR/USD");

        using var scope = _scopeFactory.CreateScope();

        var smartSignalService =
            scope.ServiceProvider.GetRequiredService<ISmartSignalService>();

        var notificationService =
            scope.ServiceProvider.GetRequiredService<ISignalNotificationService>();

        var dailyStatsService =
            scope.ServiceProvider.GetRequiredService<IDailyStatsService>();

        // Cassandra Entry/Exit vermir — trade result tracker (WIN/LOSS) söndürülüb.

        var binaryTimeframe = string.IsNullOrWhiteSpace(settings.BinaryTimeframe)
            ? "15min"
            : settings.BinaryTimeframe;

        foreach (var symbol in symbols)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                var signal = await smartSignalService.AnalyzeAsync(
                    symbol,
                    binaryTimeframe,
                    cancellationToken);

                var result = await notificationService.NotifyIfValidSignalAsync(
                    signal,
                    cancellationToken);

                dailyStatsService.RecordCheck(
                    signal,
                    result.Sent,
                    result.Message);

                _logger.LogInformation(
                    "BINARY | Symbol: {Symbol} | Direction: {Direction} | Sent: {Sent} | Message: {Message}",
                    signal.Symbol,
                    signal.Direction,
                    result.Sent,
                    result.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "BINARY analiz xətası: {Symbol}", symbol);
            }

            // API limitini aşmamaq üçün cütlər arası kiçik fasilə.
            await Task.Delay(700, cancellationToken);
        }
    }
}
using PocketSignal.Api.Services.Admin;
using PocketSignal.Api.Services.Forex;

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
                await CheckForexSignalsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ForexWorker xetasi bas verdi.");
            }

            var intervalSeconds = _configuration.GetValue<int>("ForexWorker:IntervalSeconds");

            if (intervalSeconds <= 0)
                intervalSeconds = 300;

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
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

        var symbol = settings.ForexActiveSymbol;

        if (string.IsNullOrWhiteSpace(symbol))
        {
            symbol = "GBP/JPY";
        }

        using var scope = _scopeFactory.CreateScope();

        var forexSignalService =
            scope.ServiceProvider.GetRequiredService<IForexSignalService>();

        var forexNotificationService =
            scope.ServiceProvider.GetRequiredService<IForexNotificationService>();

        var forexSignalDatabaseService =
            scope.ServiceProvider.GetRequiredService<IForexSignalDatabaseService>();

        var forexTradeResultTracker =
            scope.ServiceProvider.GetRequiredService<IForexTradeResultTracker>();

        await forexTradeResultTracker.EvaluateOpenTradesAsync(cancellationToken);

        var signal = await forexSignalService.AnalyzeAsync(
            symbol,
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

        await forexTradeResultTracker.EvaluateOpenTradesAsync(cancellationToken);

        _logger.LogInformation(
            "FOREX | Symbol: {Symbol} | Direction: {Direction} | Confidence: {Confidence} | Sent: {Sent} | Message: {Message}",
            signal.Symbol,
            signal.Direction,
            signal.Confidence,
            result.Sent,
            result.Message);
    }
}
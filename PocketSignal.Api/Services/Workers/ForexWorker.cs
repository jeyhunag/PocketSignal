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

            var intervalSeconds = _configuration.GetValue<int>("ForexWorker:IntervalSeconds");

            if (intervalSeconds <= 0)
                intervalSeconds = 300;

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

        var forexTradeResultTracker =
            scope.ServiceProvider.GetRequiredService<IForexTradeResultTracker>();

        await forexTradeResultTracker.EvaluateOpenTradesAsync(cancellationToken);

        foreach (var symbol in symbols)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
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

                _logger.LogInformation(
                    "FOREX | Symbol: {Symbol} | Direction: {Direction} | Confidence: {Confidence} | Sent: {Sent} | Message: {Message}",
                    signal.Symbol,
                    signal.Direction,
                    signal.Confidence,
                    result.Sent,
                    result.Message);

                await Task.Delay(
                    TimeSpan.FromMilliseconds(700),
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

        await forexTradeResultTracker.EvaluateOpenTradesAsync(cancellationToken);
    }

    private static List<string> GetActiveForexSymbols(
        AdminRuntimeSettings settings)
    {
        var result = new List<string>();

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

        if (result.Count == 0)
            result.Add("GBP/JPY");

        return result;
    }
}
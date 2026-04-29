using PocketSignal.Api.Models;

namespace PocketSignal.Api.Services;

public class SignalWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<SignalWorker> _logger;

    public SignalWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<SignalWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var enabled = _configuration.GetValue<bool>("SignalWorker:Enabled");

        if (!enabled)
        {
            _logger.LogInformation("SignalWorker deaktivdir.");
            return;
        }

        _logger.LogInformation("SignalWorker basladi.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckSignalsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "SignalWorker xetasi bas verdi.");
            }

            var intervalSeconds = _configuration.GetValue<int>("SignalWorker:IntervalSeconds");

            if (intervalSeconds <= 0)
                intervalSeconds = 60;

            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), stoppingToken);
        }
    }

    private async Task CheckSignalsAsync(CancellationToken cancellationToken)
    {
        var symbols = _configuration
            .GetSection("SignalWorker:Symbols")
            .GetChildren()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (symbols.Count == 0)
        {
            symbols.Add("EUR/USD");
        }

        using var scope = _scopeFactory.CreateScope();

        var smartSignalService =
            scope.ServiceProvider.GetRequiredService<ISmartSignalService>();

        var notificationService =
            scope.ServiceProvider.GetRequiredService<ISignalNotificationService>();

        var signalResultTracker =
            scope.ServiceProvider.GetRequiredService<ISignalResultTracker>();

        var dailyStatsService =
            scope.ServiceProvider.GetRequiredService<IDailyStatsService>();

        await signalResultTracker.EvaluateDueSignalsAsync(cancellationToken);

        foreach (var symbol in symbols)
        {
            var signal = await smartSignalService.AnalyzeAsync(
                symbol!,
                cancellationToken);

            var result = await notificationService.NotifyIfValidSignalAsync(
                signal,
                cancellationToken);

            if (result.Sent)
            {
                var registeredTrade = signalResultTracker.RegisterSignal(signal);

                if (registeredTrade != null)
                {
                    _logger.LogInformation(
                        "Signal registered. Id: {Id} | {Symbol} {Direction} {Expiry}m | Entry: {EntryPrice} | Due: {DueAtUtc}",
                        registeredTrade.Id,
                        registeredTrade.Symbol,
                        registeredTrade.Direction,
                        registeredTrade.ExpiryMinutes,
                        registeredTrade.EntryPrice,
                        registeredTrade.DueAtUtc);
                }
            }

            await signalResultTracker.EvaluateDueSignalsAsync(cancellationToken);

            dailyStatsService.RecordCheck(
                signal,
                result.Sent,
                result.Message);

            _logger.LogInformation(
                "Symbol: {Symbol} | Direction: {Direction} | Confidence: {Confidence} | Sent: {Sent} | Message: {Message}",
                signal.Symbol,
                signal.Direction,
                signal.Confidence,
                result.Sent,
                result.Message);

            await Task.Delay(500, cancellationToken);
        }
    }
}
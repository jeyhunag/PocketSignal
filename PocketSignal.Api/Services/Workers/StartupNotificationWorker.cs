using PocketSignal.Api.Services.Admin;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Workers;

public class StartupNotificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<StartupNotificationWorker> _logger;

    public StartupNotificationWorker(
        IServiceScopeFactory scopeFactory,
        ILogger<StartupNotificationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            using var scope = _scopeFactory.CreateScope();

            var telegramService =
                scope.ServiceProvider.GetRequiredService<ITelegramService>();

            var adminSettingsService =
                scope.ServiceProvider.GetRequiredService<IAdminRuntimeSettingsService>();

            var settings = await adminSettingsService.GetAsync(stoppingToken);

            var binarySymbolsText = settings.BinaryActiveSymbols.Count > 0
                ? string.Join(", ", settings.BinaryActiveSymbols)
                : settings.BinaryActiveSymbol;

            var forexSymbolsText = settings.ForexActiveSymbols.Count > 0
                ? string.Join(", ", settings.ForexActiveSymbols)
                : settings.ForexActiveSymbol;

            var binaryStatus = settings.BinaryEnabled
                ? $"{binarySymbolsText} aktivdir"
                : "deaktivdir";

            var forexStatus = settings.ForexEnabled
                ? $"{forexSymbolsText} aktivdir"
                : "deaktivdir";

            var message =
$"""
✅ PocketSignal başladı

Binary:
- {binaryStatus}

Forex:
- {forexStatus}

Time UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}
""";

            await telegramService.SendMessageAsync(
                message,
                stoppingToken);

            _logger.LogInformation("Startup Telegram notification gonderildi.");
        }
        catch (OperationCanceledException)
        {
            // App dayananda normal haldir.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Startup Telegram notification gonderilmedi, amma app dayandirilmadi.");
        }
    }
}
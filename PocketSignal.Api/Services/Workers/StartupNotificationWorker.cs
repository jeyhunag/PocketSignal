using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services;

public class StartupNotificationWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<StartupNotificationWorker> _logger;

    public StartupNotificationWorker(
        IServiceScopeFactory scopeFactory,
        IConfiguration configuration,
        ILogger<StartupNotificationWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _configuration = configuration;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);

            using var scope = _scopeFactory.CreateScope();

            var telegramService =
                scope.ServiceProvider.GetRequiredService<ITelegramService>();

            var binarySymbols = GetSymbols("SignalWorker:Symbols", "EUR/USD");
            var forexSymbols = GetSymbols("ForexWorker:Symbols", "GBP/JPY");

            var message = BuildStartupMessage(binarySymbols, forexSymbols);

            await telegramService.SendMessageAsync(
                message,
                stoppingToken);

            _logger.LogInformation("Startup Telegram mesaji gonderildi.");
        }
        catch (TaskCanceledException)
        {
            // App stop olarsa normaldır
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup Telegram mesaji gonderile bilmedi.");
        }
    }

    private List<string> GetSymbols(string sectionName, string defaultSymbol)
    {
        var symbols = _configuration
            .GetSection(sectionName)
            .GetChildren()
            .Select(x => x.Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();

        if (symbols.Count == 0)
            symbols.Add(defaultSymbol);

        return symbols;
    }

    private static string BuildStartupMessage(
        List<string> binarySymbols,
        List<string> forexSymbols)
    {
        var lines = new List<string>
        {
            "✅ PocketSignal başladı",
            "",
            "Binary:"
        };

        foreach (var symbol in binarySymbols)
        {
            lines.Add($"- {symbol} aktivdir");
        }

        lines.Add("");
        lines.Add("Forex:");

        foreach (var symbol in forexSymbols)
        {
            lines.Add($"- {symbol} aktivdir");
        }

        lines.Add("");
        lines.Add($"Time UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");

        return string.Join(Environment.NewLine, lines);
    }
}
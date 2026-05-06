namespace PocketSignal.Api.Services.Forex;

public class ForexSignalRouterService : IForexSignalService
{
    private readonly CoreForexSignalService _coreForexSignalService;
    private readonly XauUsdScalpingSignalService _xauUsdScalpingSignalService;

    public ForexSignalRouterService(
        CoreForexSignalService coreForexSignalService,
        XauUsdScalpingSignalService xauUsdScalpingSignalService)
    {
        _coreForexSignalService = coreForexSignalService;
        _xauUsdScalpingSignalService = xauUsdScalpingSignalService;
    }

    public Task<Models.Forex.ForexTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        if (IsXauUsd(symbol))
        {
            return _xauUsdScalpingSignalService.AnalyzeAsync(
                "XAU/USD",
                cancellationToken);
        }

        return _coreForexSignalService.AnalyzeAsync(
            symbol,
            cancellationToken);
    }

    private static bool IsXauUsd(string symbol)
    {
        var normalized = symbol
            .Trim()
            .Replace("/", "")
            .Replace("-", "")
            .Replace(" ", "")
            .ToUpperInvariant();

        return normalized == "XAUUSD";
    }
}
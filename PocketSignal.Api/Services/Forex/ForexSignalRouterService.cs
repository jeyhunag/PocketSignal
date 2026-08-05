namespace PocketSignal.Api.Services.Forex;

/// <summary>
/// Bütün cütlər (qızıl daxil) Cassandra sisteminə (CoreForexSignalService) yönləndirilir.
/// Köhnə XauUsdScalpingSignalService artıq istifadə olunmur.
/// </summary>
public class ForexSignalRouterService : IForexSignalService
{
    private readonly CoreForexSignalService _coreForexSignalService;

    public ForexSignalRouterService(
        CoreForexSignalService coreForexSignalService)
    {
        _coreForexSignalService = coreForexSignalService;
    }

    public Task<Models.Forex.ForexTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        // Qızıl da, digər cütlər də eyni Cassandra sistemi ilə analiz olunur.
        return _coreForexSignalService.AnalyzeAsync(symbol, cancellationToken);
    }
}
using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Forex;

public interface IForexSignalService
{
    Task<ForexTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}
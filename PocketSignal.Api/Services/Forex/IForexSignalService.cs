using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Forex;

public interface IForexSignalService
{
    Task<ForexTradeSignal> AnalyzeAsync(
        string symbol,
        string timeframe = "15min",
        CancellationToken cancellationToken = default);
}
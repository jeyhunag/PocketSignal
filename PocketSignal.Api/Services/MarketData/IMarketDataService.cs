using PocketSignal.Api.Models.Common;

namespace PocketSignal.Api.Services.MarketData;

public interface IMarketDataService
{
    Task<TwelveDataResponse?> GetCandlesAsync(
        string symbol,
        string interval,
        int outputSize,
        CancellationToken cancellationToken = default);
}
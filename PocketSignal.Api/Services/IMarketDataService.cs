using PocketSignal.Api.Models;

namespace PocketSignal.Api.Services;

public interface IMarketDataService
{
    Task<TwelveDataResponse?> GetCandlesAsync(
        string symbol,
        string interval,
        int outputSize,
        CancellationToken cancellationToken = default);
}
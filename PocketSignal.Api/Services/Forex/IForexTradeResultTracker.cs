using PocketSignal.Api.Data.Entities;

namespace PocketSignal.Api.Services.Forex;

public interface IForexTradeResultTracker
{
    Task EvaluateOpenTradesAsync(CancellationToken cancellationToken = default);

    Task<List<ForexTradeResultEntity>> GetTodayTradesAsync(
        CancellationToken cancellationToken = default);

    Task<string> GetTodayStatusAsync(
        CancellationToken cancellationToken = default);
}
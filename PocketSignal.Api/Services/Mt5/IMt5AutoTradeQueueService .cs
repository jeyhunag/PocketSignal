using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Mt5;

public interface IMt5AutoTradeQueueService
{
    Task<Mt5AutoTradeEnqueueResult> EnqueueAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken = default);

    Task<Mt5AutoTradeOrder?> GetNextOrderAsync(
        string eaKey,
        CancellationToken cancellationToken = default);

    Task<bool> MarkExecutedAsync(
        string eaKey,
        Guid id,
        string ticket,
        CancellationToken cancellationToken = default);

    Task<bool> MarkErrorAsync(
        string eaKey,
        Guid id,
        string error,
        CancellationToken cancellationToken = default);

    IReadOnlyList<Mt5AutoTradeOrder> GetRecentOrders();
}
using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Forex;

public interface IForexNotificationService
{
    Task<(bool Sent, string Message)> NotifyIfValidSignalAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken = default);
}
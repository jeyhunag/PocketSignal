using PocketSignal.Api.Models;

namespace PocketSignal.Api.Services;

public interface ISignalNotificationService
{
    Task<(bool Sent, string Message)> NotifyIfValidSignalAsync(
        SmartTradeSignal signal,
        CancellationToken cancellationToken = default);
}
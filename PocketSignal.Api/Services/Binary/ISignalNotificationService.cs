using PocketSignal.Api.Models.Binary;

namespace PocketSignal.Api.Services.Binary;

public interface ISignalNotificationService
{
    Task<(bool Sent, string Message)> NotifyIfValidSignalAsync(
        SmartTradeSignal signal,
        CancellationToken cancellationToken = default);
}
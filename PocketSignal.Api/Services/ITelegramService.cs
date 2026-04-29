namespace PocketSignal.Api.Services;

public interface ITelegramService
{
    Task SendMessageAsync(
        string message,
        CancellationToken cancellationToken = default);
}
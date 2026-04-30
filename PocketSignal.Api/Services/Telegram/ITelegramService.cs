namespace PocketSignal.Api.Services.Telegram;

public interface ITelegramService
{
    Task SendMessageAsync(
        string message,
        CancellationToken cancellationToken = default);

    Task SendPhotoAsync(
        string photoPath,
        string caption,
        CancellationToken cancellationToken = default);
}
using System.Net.Http.Json;

namespace PocketSignal.Api.Services;

public class TelegramService : ITelegramService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;

    public TelegramService(
        HttpClient httpClient,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    public async Task SendMessageAsync(
        string message,
        CancellationToken cancellationToken = default)
    {
        var botToken = _configuration["Telegram:BotToken"];
        var chatId = _configuration["Telegram:ChatId"];

        if (string.IsNullOrWhiteSpace(botToken))
            throw new InvalidOperationException("Telegram BotToken tapilmadi.");

        if (string.IsNullOrWhiteSpace(chatId))
            throw new InvalidOperationException("Telegram ChatId tapilmadi.");

        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";

        var payload = new
        {
            chat_id = chatId,
            text = message
        };

        var response = await _httpClient.PostAsJsonAsync(
            url,
            payload,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
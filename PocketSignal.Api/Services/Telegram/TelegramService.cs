using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace PocketSignal.Api.Services.Telegram;

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
        var botToken = GetBotToken();
        var chatId = GetChatId();

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

    public async Task SendPhotoAsync(
        string photoPath,
        string caption,
        CancellationToken cancellationToken = default)
    {
        var botToken = GetBotToken();
        var chatId = GetChatId();

        if (string.IsNullOrWhiteSpace(photoPath))
            throw new InvalidOperationException("Telegram photo path boshdur.");

        if (!File.Exists(photoPath))
            throw new FileNotFoundException("Telegram-a gonderilecek chart sekli tapilmadi.", photoPath);

        var url = $"https://api.telegram.org/bot{botToken}/sendPhoto";

        using var form = new MultipartFormDataContent();

        form.Add(new StringContent(chatId), "chat_id");

        if (!string.IsNullOrWhiteSpace(caption))
        {
            form.Add(new StringContent(caption), "caption");
        }

        await using var fileStream = File.OpenRead(photoPath);

        using var fileContent = new StreamContent(fileStream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        form.Add(
            fileContent,
            "photo",
            Path.GetFileName(photoPath));

        var response = await _httpClient.PostAsync(
            url,
            form,
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }

    private string GetBotToken()
    {
        var botToken =
            _configuration["Telegram:BotToken"] ??
            _configuration["Telegram:Token"];

        if (string.IsNullOrWhiteSpace(botToken))
            throw new InvalidOperationException("Telegram BotToken tapilmadi. Telegram:BotToken ve ya Telegram:Token yoxla.");

        return botToken;
    }

    private string GetChatId()
    {
        var chatId =
            _configuration["Telegram:ChatId"] ??
            _configuration["Telegram:Id"];

        if (string.IsNullOrWhiteSpace(chatId))
            throw new InvalidOperationException("Telegram ChatId tapilmadi. Telegram:ChatId ve ya Telegram:Id yoxla.");

        return chatId;
    }
}
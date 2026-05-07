using System.Net.Http.Json;
using System.Text.Json;

namespace PocketSignal.Api.Services.Binary;

public class GeminiBinaryClient
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiBinaryClient> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public GeminiBinaryClient(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<GeminiBinaryClient> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<GeminiBinaryDecision?> AnalyzeAsync(
        string prompt,
        CancellationToken cancellationToken = default)
    {
        var apiKey = _configuration["Gemini:ApiKey"];

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning("Gemini API key tapilmadi. Gemini:ApiKey yoxla.");
            return null;
        }

        var model = _configuration["Gemini:Model"];

        if (string.IsNullOrWhiteSpace(model))
            model = "gemini-2.0-flash";

        var temperature = _configuration.GetValue<double?>("Gemini:Temperature") ?? 0.15;

        var url =
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        var request = new
        {
            contents = new[]
            {
                new
                {
                    role = "user",
                    parts = new[]
                    {
                        new
                        {
                            text = prompt
                        }
                    }
                }
            },
            generationConfig = new
            {
                temperature = temperature,
                maxOutputTokens = 700,
                responseMimeType = "application/json"
            }
        };

        try
        {
            HttpResponseMessage? response = null;
            string responseText = string.Empty;

            for (var attempt = 1; attempt <= 3; attempt++)
            {
                response = await _httpClient.PostAsJsonAsync(
                    url,
                    request,
                    cancellationToken);

                responseText = await response.Content.ReadAsStringAsync(cancellationToken);

                if (response.IsSuccessStatusCode)
                    break;

                if ((int)response.StatusCode == 503 || (int)response.StatusCode == 429)
                {
                    _logger.LogWarning(
                        "Gemini API temporary error. Attempt {Attempt}/3. Status: {Status}. Body: {Body}",
                        attempt,
                        (int)response.StatusCode,
                        responseText);

                    if (attempt < 3)
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(5 * attempt),
                            cancellationToken);

                        continue;
                    }
                }

                _logger.LogWarning(
                    "Gemini API error. Status: {Status}. Body: {Body}",
                    (int)response.StatusCode,
                    responseText);

                return null;
            }

            if (response == null || !response.IsSuccessStatusCode)
            {
                return null;
            }

            var geminiResponse = JsonSerializer.Deserialize<GeminiGenerateContentResponse>(
                responseText,
                JsonOptions);

            var jsonText = geminiResponse?
                .Candidates?
                .FirstOrDefault()?
                .Content?
                .Parts?
                .FirstOrDefault()?
                .Text;

            if (string.IsNullOrWhiteSpace(jsonText))
            {
                _logger.LogWarning("Gemini response text bosh geldi.");
                return null;
            }

            var decision = JsonSerializer.Deserialize<GeminiBinaryDecision>(
                jsonText,
                JsonOptions);

            if (decision == null)
            {
                _logger.LogWarning("Gemini JSON parse olunmadi. Text: {Text}", jsonText);
                return null;
            }

            decision.Direction = NormalizeDirection(decision.Direction);
            decision.RiskLevel = string.IsNullOrWhiteSpace(decision.RiskLevel)
                ? "UNKNOWN"
                : decision.RiskLevel.Trim().ToUpperInvariant();

            return decision;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Gemini AI analizinde xeta oldu.");
            return null;
        }
    }

    private static string NormalizeDirection(string? direction)
    {
        direction = direction?.Trim().ToUpperInvariant();

        return direction switch
        {
            "LONG" => "LONG",
            "SHORT" => "SHORT",
            _ => "WAIT"
        };
    }

    private sealed class GeminiGenerateContentResponse
    {
        public List<GeminiCandidate>? Candidates { get; set; }
    }

    private sealed class GeminiCandidate
    {
        public GeminiContent? Content { get; set; }
    }

    private sealed class GeminiContent
    {
        public List<GeminiPart>? Parts { get; set; }
    }

    private sealed class GeminiPart
    {
        public string? Text { get; set; }
    }
}

public class GeminiBinaryDecision
{
    public string Direction { get; set; } = "WAIT";

    public int Confidence { get; set; }

    public int ExpiryMinutes { get; set; }

    public int ValidForSeconds { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string InvalidIf { get; set; } = string.Empty;

    public string RiskLevel { get; set; } = "UNKNOWN";
}
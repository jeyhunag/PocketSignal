using System.Text.Json.Serialization;

namespace PocketSignal.Api.Models;

public class TwelveDataResponse
{
    [JsonPropertyName("meta")]
    public TwelveDataMeta? Meta { get; set; }

    [JsonPropertyName("values")]
    public List<CandleDto>? Values { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("code")]
    public int? Code { get; set; }
}

public class TwelveDataMeta
{
    [JsonPropertyName("symbol")]
    public string? Symbol { get; set; }

    [JsonPropertyName("interval")]
    public string? Interval { get; set; }

    [JsonPropertyName("currency_base")]
    public string? CurrencyBase { get; set; }

    [JsonPropertyName("currency_quote")]
    public string? CurrencyQuote { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }
}

public class CandleDto
{
    [JsonPropertyName("datetime")]
    public string DateTime { get; set; } = string.Empty;

    [JsonPropertyName("open")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal Open { get; set; }

    [JsonPropertyName("high")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal High { get; set; }

    [JsonPropertyName("low")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal Low { get; set; }

    [JsonPropertyName("close")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal Close { get; set; }
}
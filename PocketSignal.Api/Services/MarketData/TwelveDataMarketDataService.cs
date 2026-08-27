using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;
using PocketSignal.Api.Models.Common;

namespace PocketSignal.Api.Services.MarketData;

public class TwelveDataMarketDataService : IMarketDataService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly IMemoryCache _cache;

    public TwelveDataMarketDataService(
        HttpClient httpClient,
        IConfiguration configuration,
        IMemoryCache cache)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _cache = cache;
    }

    public async Task<TwelveDataResponse?> GetCandlesAsync(
        string symbol,
        string interval,
        int outputSize,
        CancellationToken cancellationToken = default)
    {
        var apiGroup = MarketDataApiGroupContext.Group;
        var normalizedSymbol = NormalizeSymbol(symbol);

        var cacheKey =
            $"twelvedata:{apiGroup}:{normalizedSymbol}:{interval}:{outputSize}";

        if (_cache.TryGetValue(cacheKey, out TwelveDataResponse? cachedResponse))
        {
            return cachedResponse;
        }

        var apiKey = GetApiKeyForSymbol(
            symbol,
            apiGroup);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"TwelveData API key tapilmadi. Group: {apiGroup}, Symbol: {symbol}");
        }

        var encodedSymbol = Uri.EscapeDataString(symbol);

        var url =
            $"time_series?symbol={encodedSymbol}&interval={interval}&outputsize={outputSize}&apikey={apiKey}";

        var response = await _httpClient.GetFromJsonAsync<TwelveDataResponse>(
            url,
            cancellationToken);

        if (response is not null && response.Status == "ok")
        {
            var cacheDuration = GetCacheDuration(interval);

            _cache.Set(
                cacheKey,
                response,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = cacheDuration
                });
        }

        return response;
    }

    private string? GetApiKeyForSymbol(
        string symbol,
        string apiGroup)
    {
        var normalized = NormalizeSymbol(symbol);

        // 1. Əvvəl group daxilində symbol xüsusi key yoxlanır:
        // TwelveData:Binary:SymbolApiKeys:GBP_USD
        // TwelveData:Forex:SymbolApiKeys:EUR_USD
        var groupSymbolKey =
            _configuration[$"TwelveData:{apiGroup}:SymbolApiKeys:{normalized}"];

        if (!string.IsNullOrWhiteSpace(groupSymbolKey))
            return groupSymbolKey;

        // 2. Sonra group əsas key yoxlanır:
        // TwelveData:Binary:ApiKey
        // TwelveData:Forex:ApiKey
        var groupApiKey =
            _configuration[$"TwelveData:{apiGroup}:ApiKey"];

        if (!string.IsNullOrWhiteSpace(groupApiKey))
            return groupApiKey;

        // 3. Sonra global symbol key:
        // TwelveData:SymbolApiKeys:GBP_JPY
        var globalSymbolKey =
            _configuration[$"TwelveData:SymbolApiKeys:{normalized}"];

        if (!string.IsNullOrWhiteSpace(globalSymbolKey))
            return globalSymbolKey;

        // 4. Axırda fallback global key:
        // TwelveData:ApiKey
        return _configuration["TwelveData:ApiKey"];
    }

    private static string NormalizeSymbol(string symbol)
    {
        return symbol
            .Replace("/", "_")
            .Replace("-", "_")
            .Replace(" ", "")
            .ToUpperInvariant();
    }

    private static TimeSpan GetCacheDuration(string interval)
    {
        return interval.ToLowerInvariant() switch
        {
            "1min" => TimeSpan.FromSeconds(35),
            "5min" => TimeSpan.FromMinutes(4),
            "15min" => TimeSpan.FromMinutes(14),
            "30min" => TimeSpan.FromMinutes(29),
            "1h" => TimeSpan.FromMinutes(55),
            "2h" => TimeSpan.FromMinutes(110),
            "4h" => TimeSpan.FromHours(3),
            _ => TimeSpan.FromSeconds(30)
        };
    }
}
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
        var cacheKey = $"twelvedata:{NormalizeSymbol(symbol)}:{interval}:{outputSize}";

        if (_cache.TryGetValue(cacheKey, out TwelveDataResponse? cachedResponse))
        {
            return cachedResponse;
        }

        var apiKey = GetApiKeyForSymbol(symbol);

        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException($"TwelveData API key tapilmadi. Symbol: {symbol}");

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

    private string? GetApiKeyForSymbol(string symbol)
    {
        var normalized = NormalizeSymbol(symbol);

        var symbolSpecificKey =
            _configuration[$"TwelveData:SymbolApiKeys:{normalized}"];

        if (!string.IsNullOrWhiteSpace(symbolSpecificKey))
            return symbolSpecificKey;

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
        return interval.ToLower() switch
        {
            "1min" => TimeSpan.FromSeconds(35),
            "5min" => TimeSpan.FromMinutes(4),
            "15min" => TimeSpan.FromMinutes(14),
            "30min" => TimeSpan.FromMinutes(29),
            "1h" => TimeSpan.FromMinutes(55),
            _ => TimeSpan.FromSeconds(30)
        };
    }
}
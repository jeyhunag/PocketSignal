using System.Globalization;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Backtest;

/// <summary>
/// Backtest üçün market data servisi. Real API-yə getmir —
/// əvvəlcədən yüklənmiş tarixi candle-lardan müəyyən "pəncərə" qaytarır.
///
/// PERFORMANS: vaxtlar (DateTime) YALNIZ BİR DƏFƏ parse olunur və saxlanılır.
/// Hər sorğuda yenidən parse etmirik — bu, crash/yavaşlığın qarşısını alır.
/// </summary>
public class ReplayMarketDataService : IMarketDataService
{
    // interval -> əvvəlcədən parse olunmuş (time, candle) cütləri, köhnədən yeniyə sıralı.
    private readonly Dictionary<string, List<(DateTime Time, CandleDto Candle)>> _history = new();

    private DateTime _cursorUtc = DateTime.MaxValue;

    public void LoadHistory(string interval, List<CandleDto> candles)
    {
        var parsed = candles
            .Select(c => (Time: ParseTime(c), Candle: c))
            .Where(x => x.Time != DateTime.MinValue)
            .OrderBy(x => x.Time)
            .ToList();

        _history[interval.ToLowerInvariant()] = parsed;
    }

    public void SetCursor(DateTime cursorUtc)
    {
        _cursorUtc = cursorUtc;
    }

    /// <summary>Backtest mühərriki üçün: tam seriyanı (parse olunmuş) qaytarır.</summary>
    public List<CandleDto> GetFullSeries(string interval)
    {
        var key = interval.ToLowerInvariant();
        return _history.TryGetValue(key, out var list)
            ? list.Select(x => x.Candle).ToList()
            : new List<CandleDto>();
    }

    public Task<TwelveDataResponse?> GetCandlesAsync(
        string symbol,
        string interval,
        int outputSize,
        CancellationToken cancellationToken = default)
    {
        var key = interval.ToLowerInvariant();

        if (!_history.TryGetValue(key, out var all))
        {
            return Task.FromResult<TwelveDataResponse?>(new TwelveDataResponse
            {
                Status = "ok",
                Values = new List<CandleDto>()
            });
        }

        // Look-ahead bias-ın qarşısını al: yalnız kursordan ƏVVƏLKİ candle-lar.
        // Vaxtlar onsuz da parse olunub və sıralı — sadəcə kəsirik.
        var visible = new List<CandleDto>(outputSize);
        var startCollecting = false;

        // Sondan geriyə getmək əvəzinə, sıralı siyahıda yuxarı sərhədi tapırıq.
        var upperIndex = all.Count - 1;
        while (upperIndex >= 0 && all[upperIndex].Time > _cursorUtc)
            upperIndex--;

        var lowerIndex = Math.Max(0, upperIndex - outputSize + 1);

        for (var i = lowerIndex; i <= upperIndex; i++)
            visible.Add(all[i].Candle);

        return Task.FromResult<TwelveDataResponse?>(new TwelveDataResponse
        {
            Status = "ok",
            Values = visible
        });
    }

    private static DateTime ParseTime(CandleDto c)
    {
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd"
        };

        return DateTime.TryParseExact(
            c.DateTime, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t)
            ? t
            : DateTime.MinValue;
    }
}
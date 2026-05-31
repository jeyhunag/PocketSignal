using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Backtest;

/// <summary>
/// Backtest üçün market data servisi. Real API-yə getmir —
/// əvvəlcədən yüklənmiş tarixi candle-lardan müəyyən "pəncərə" qaytarır.
///
/// İş prinsipi: backtest mühərriki SetCursor(time) çağırır, bu servis isə
/// strategiyaya YALNIZ həmin andan ƏVVƏLKİ candle-ları verir (look-ahead bias olmasın deyə).
///
/// Bu servis IMarketDataService-i implement etdiyi üçün strategiya
/// (CoreForexSignalService) heç nə bilmədən onun üzərində işləyə bilir.
/// </summary>
public class ReplayMarketDataService : IMarketDataService
{
    // interval -> bütün tarixi candle-lar (köhnədən yeniyə sıralı)
    private readonly Dictionary<string, List<CandleDto>> _history = new();

    // Backtest mühərrikinin "indiki an" kursoru. Bu andan sonrakı candle-lar gizlədilir.
    private DateTime _cursorUtc = DateTime.MaxValue;

    public void LoadHistory(string interval, List<CandleDto> candles)
    {
        // Köhnədən yeniyə sırala (strategiya bu sıranı gözləyir).
        _history[interval.ToLowerInvariant()] = candles
            .OrderBy(ParseTime)
            .ToList();
    }

    public void SetCursor(DateTime cursorUtc)
    {
        _cursorUtc = cursorUtc;
    }

    /// <summary>
    /// Backtest mühərriki üçün: ən kiçik timeframe-in (adətən 1min) bütün candle-larını qaytarır
    /// ki, onların üzərində addım-addım irəliləyə bilsin.
    /// </summary>
    public List<CandleDto> GetFullSeries(string interval)
    {
        var key = interval.ToLowerInvariant();
        return _history.TryGetValue(key, out var list)
            ? list
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

        // Look-ahead bias-ın qarşısını al: yalnız kursordan ƏVVƏLKİ (və ona bərabər) candle-lar.
        var visible = all
            .Where(c => ParseTime(c) <= _cursorUtc)
            .TakeLast(outputSize)
            .ToList();

        // TwelveData "values"-ı yenidən köhnəyə qaytarır, MapCandles onsuz da yenidən sıralayır,
        // ona görə sıralama kritik deyil, amma orijinal davranışa uyğun saxlayırıq.
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
            c.DateTime,
            formats,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None,
            out var t)
            ? t
            : DateTime.MinValue;
    }
}
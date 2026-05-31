using Microsoft.AspNetCore.Mvc;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.Backtest;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Controllers;

[ApiController]
[Route("api/backtest")]
public class BacktestController : ControllerBase
{
    private readonly IMarketDataService _marketData;

    public BacktestController(IMarketDataService marketData)
    {
        _marketData = marketData;
    }

    /// <summary>
    /// Forex strategiyasını tarixi data üzərində test edir.
    /// Nümunə: GET /api/backtest/forex?symbol=EUR/USD&bars=3000&step=5
    ///
    /// bars  = neçə M1 candle çəkilsin (max ~5000, API limitinə diqqət).
    /// step  = neçə M1 candle-dan bir analiz olunsun (5 = sürətli, 1 = dəqiq amma yavaş).
    /// </summary>
    [HttpGet("forex")]
    public async Task<IActionResult> Forex(
        [FromQuery] string symbol = "EUR/USD",
        [FromQuery] int bars = 3000,
        [FromQuery] int step = 5,
        CancellationToken cancellationToken = default)
    {
        bars = Math.Clamp(bars, 500, 5000);
        step = Math.Clamp(step, 1, 30);

        // M1 = bars qədər; M5 və M15 üçün mütənasib (amma daha az kifayətdir).
        var m5Bars = Math.Clamp(bars / 3, 200, 5000);
        var m15Bars = Math.Clamp(bars / 9, 100, 5000);

        // VACİB: ForexWorker kimi "Forex" API qrupundan key götürmək üçün
        // context-i təyin edirik. Olmasa "Default" qrupda key tapılmır.
        TwelveDataResponse? m15, m5, m1;
        using (MarketDataApiGroupContext.Use("Forex"))
        {
            m15 = await _marketData.GetCandlesAsync(symbol, "15min", m15Bars, cancellationToken);
            m5 = await _marketData.GetCandlesAsync(symbol, "5min", m5Bars, cancellationToken);
            m1 = await _marketData.GetCandlesAsync(symbol, "1min", bars, cancellationToken);
        }

        if (m15?.Values == null || m5?.Values == null || m1?.Values == null ||
            m1.Values.Count < 300)
        {
            return BadRequest(new
            {
                error = "Kifayət qədər tarixi data çəkilmədi.",
                m15 = m15?.Values?.Count ?? 0,
                m5 = m5?.Values?.Count ?? 0,
                m1 = m1?.Values?.Count ?? 0,
                hint = "API limitini və ya symbol formatını yoxlayın (məs: EUR/USD)."
            });
        }

        // Backtest üçün AYRICA replay servisi (real servisə toxunmur, API yemir).
        var replay = new ReplayMarketDataService();
        var engine = new ForexBacktestEngine(replay);

        var report = await engine.RunAsync(
            symbol,
            m15.Values,
            m5.Values,
            m1.Values,
            step,
            cancellationToken);

        return Ok(new
        {
            report.Symbol,
            report.FromUtc,
            report.ToUtc,
            report.TotalTrades,
            report.Wins,
            report.Losses,
            report.WinRatePercent,
            report.TotalR,
            report.ProfitFactor,
            report.MaxDrawdownR,
            report.MaxConsecutiveLosses,
            report.AverageConfidence,
            report.Summary,
            // Son 20 trade-i nümunə kimi göstər (cavab çox böyük olmasın).
            sampleTrades = report.Trades.TakeLast(20)
        });
    }
}
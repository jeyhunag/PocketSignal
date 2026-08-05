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

    /// <summary>Controller işləyirmi? GET /api/backtest/ping</summary>
    [HttpGet("ping")]
    public IActionResult Ping()
    {
        return Ok(new { ok = true, time = DateTime.UtcNow });
    }

    /// <summary>
    /// YALNIZ data çəkməni test edir (backtest döngüsü işləmir).
    /// GET /api/backtest/data?symbol=BTC/USD&bars=600
    /// Bununla data çəkmənin işlədiyini ayrıca yoxlayırıq.
    /// </summary>
    [HttpGet("data")]
    public async Task<IActionResult> Data(
        [FromQuery] string symbol = "BTC/USD",
        [FromQuery] int bars = 600,
        CancellationToken cancellationToken = default)
    {
        try
        {
            bars = Math.Clamp(bars, 200, 5000);
            var m5Bars = Math.Clamp(bars / 3, 200, 5000);
            var m15Bars = Math.Clamp(bars / 9, 100, 5000);

            TwelveDataResponse? m15, m5, m1;
            using (MarketDataApiGroupContext.Use("Forex"))
            {
                m15 = await _marketData.GetCandlesAsync(symbol, "15min", m15Bars, cancellationToken);
                m5 = await _marketData.GetCandlesAsync(symbol, "5min", m5Bars, cancellationToken);
                m1 = await _marketData.GetCandlesAsync(symbol, "1min", bars, cancellationToken);
            }

            return Ok(new
            {
                symbol,
                m15Count = m15?.Values?.Count ?? 0,
                m5Count = m5?.Values?.Count ?? 0,
                m1Count = m1?.Values?.Count ?? 0,
                m15Status = m15?.Status,
                m5Status = m5?.Status,
                m1Status = m1?.Status,
                m1Message = m1?.Message
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error = "Data çəkmə xətası.",
                message = ex.Message,
                type = ex.GetType().Name
            });
        }
    }

    /// <summary>
    /// Forex strategiyasını tarixi data üzərində test edir.
    /// GET /api/backtest/forex?symbol=BTC/USD&bars=600&step=20
    /// </summary>
    [HttpGet("forex")]
    public async Task<IActionResult> Forex(
        [FromQuery] string symbol = "EUR/USD",
        [FromQuery] int bars = 3000,
        [FromQuery] int step = 5,
        [FromQuery] string tf = "m1",
        CancellationToken cancellationToken = default)
    {
        try
        {
            bars = Math.Clamp(bars, 500, 5000);
            step = Math.Clamp(step, 1, 30);

            // === Timeframe seti seçimi ===
            // tf=m1   → köhnə Breaker: 15min/5min/1min (qısa data).
            // tf=m15  → Breaker böyük set: 4h/1h/15min.
            // tf=ema  → EMA Pullback M30: hər üç slot da 30min.
            // tf=core → EMA50 + Williams %R M30 (yeni strategiya, CoreForexSignalService).
            var tfLower = tf.ToLowerInvariant();

            // Tək-timeframe rejimi: ema və core (M30) strategiyaları üçün.
            var isSingleTfMode = tfLower == "ema" || tfLower == "core" || tfLower == "core15" || tfLower == "core5";

            var strategyName = tfLower switch
            {
                "ema" => "ema",
                "core" => "breaker",     // CoreForexSignalService, M30
                "core15" => "breaker",   // CoreForexSignalService, M15
                "core5" => "breaker",    // CoreForexSignalService, M5
                _ => "breaker"
            };

            var singleTf = tfLower == "core15" ? "15min"
                : tfLower == "core5" ? "5min"
                : "30min";

            string tfBig, tfMid, tfSmall;
            if (isSingleTfMode)
            {
                // Strategiya yalnız "1min" slotunu (entry seriyası) oxuyur.
                // Engine üçün hər üç slot da lazımdır, ona görə hamısını eyni veririk.
                tfBig = singleTf;
                tfMid = singleTf;
                tfSmall = singleTf;
            }
            else if (tfLower == "m15")
            {
                tfBig = "4h";
                tfMid = "1h";
                tfSmall = "15min";
            }
            else
            {
                tfBig = "15min";
                tfMid = "5min";
                tfSmall = "1min";
            }

            var midBars = isSingleTfMode ? bars : Math.Clamp(bars / 3, 200, 5000);
            var bigBars = isSingleTfMode ? bars : Math.Clamp(bars / 9, 100, 5000);

            TwelveDataResponse? big, mid, small;
            using (MarketDataApiGroupContext.Use("Forex"))
            {
                big = await _marketData.GetCandlesAsync(symbol, tfBig, bigBars, cancellationToken);
                mid = await _marketData.GetCandlesAsync(symbol, tfMid, midBars, cancellationToken);
                small = await _marketData.GetCandlesAsync(symbol, tfSmall, bars, cancellationToken);
            }

            if (big?.Values == null || mid?.Values == null || small?.Values == null ||
                small.Values.Count < 300)
            {
                return BadRequest(new
                {
                    error = "Kifayət qədər tarixi data çəkilmədi.",
                    big = big?.Values?.Count ?? 0,
                    mid = mid?.Values?.Count ?? 0,
                    small = small?.Values?.Count ?? 0,
                    smallStatus = small?.Status,
                    smallMessage = small?.Message,
                    hint = "Symbol formatını (məs: BTC/USD) və API limitini yoxlayın."
                });
            }

            var replay = new ReplayMarketDataService();
            var engine = new ForexBacktestEngine(replay);

            // Strategiya daxildə "15min/5min/1min" gözləyir, ona görə böyük
            // timeframe datasını həmin slotlara map edirik.
            var report = await engine.RunAsync(
                symbol, big.Values, mid.Values, small.Values, step, strategyName, cancellationToken);

            return Ok(new
            {
                report.Symbol,
                timeframeSet = tfLower == "ema" ? "30min (EMA Pullback)"
                    : tfLower == "core" ? "30min (Şam-trend + Williams %R)"
                    : tfLower == "core15" ? "15min (Şam-trend + Williams %R)"
                    : tfLower == "core5" ? "5min (Şam-trend + Williams %R)"
                    : tfLower == "m15" ? "4h/1h/15min"
                    : "15min/5min/1min",
                strategy = strategyName,
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
                sampleTrades = report.Trades.TakeLast(20)
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new
            {
                error = "Backtest xətası.",
                message = ex.Message,
                type = ex.GetType().Name,
                stack = ex.StackTrace?.Split('\n').Take(6)
            });
        }
    }
}
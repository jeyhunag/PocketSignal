using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data;
using PocketSignal.Api.Data.Entities;
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.Forex;
using PocketSignal.Api.Services.MarketData;
using System.Globalization;
using System.Text.Json;

namespace PocketSignal.Api.Controllers;

[ApiController]
[Route("api/forex-test")]
public class ForexTestController : ControllerBase
{
    private readonly PocketSignalDbContext _dbContext;
    private readonly IMarketDataService _marketDataService;
    private readonly IForexTradeResultTracker _forexTradeResultTracker;

    public ForexTestController(
        PocketSignalDbContext dbContext,
        IMarketDataService marketDataService,
        IForexTradeResultTracker forexTradeResultTracker)
    {
        _dbContext = dbContext;
        _marketDataService = marketDataService;
        _forexTradeResultTracker = forexTradeResultTracker;
    }

    [HttpGet("create-trade")]
    public async Task<IActionResult> CreateTestTrade(
        [FromQuery] string symbol = "GBP/JPY",
        [FromQuery] string direction = "LONG",
        [FromQuery] string expected = "TP1",
        CancellationToken cancellationToken = default)
    {
        direction = direction.Trim().ToUpperInvariant();
        expected = expected.Trim().ToUpperInvariant();

        if (direction != "LONG" && direction != "SHORT")
        {
            return BadRequest(new
            {
                error = "direction yalniz LONG ve ya SHORT ola biler."
            });
        }

        if (expected != "TP1" &&
            expected != "TP2" &&
            expected != "LOSS" &&
            expected != "AMBIGUOUS")
        {
            return BadRequest(new
            {
                error = "expected yalniz TP1, TP2, LOSS ve ya AMBIGUOUS ola biler."
            });
        }

        var response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            20,
            cancellationToken);

        var candles = MapCandles(response, symbol);

        if (candles.Count == 0)
        {
            return BadRequest(new
            {
                error = "Test trade yaratmaq ucun candle tapilmadi."
            });
        }

        var last = candles.Last();

        var plan = BuildTestPlan(
            symbol,
            direction,
            expected,
            last);

        var signal = new ForexSignalEntity
        {
            Id = Guid.NewGuid(),
            Symbol = symbol,
            Direction = direction,
            IsTradable = true,

            EntryPrice = plan.Entry,
            StopLoss = plan.StopLoss,
            TakeProfit1 = plan.TakeProfit1,
            TakeProfit2 = plan.TakeProfit2,

            RiskPips = plan.RiskPips,
            RewardPips1 = plan.RewardPips1,
            RewardPips2 = plan.RewardPips2,
            RiskReward1 = plan.RiskReward1,
            RiskReward2 = plan.RiskReward2,

            Confidence = 99,
            Grade = "TEST",
            Message = $"{symbol} {direction} TEST TRADE",
            InvalidIf = "TEST trade - real signal deyil.",
            ValidForMinutes = 10,

            ReasonsJson = JsonSerializer.Serialize(new List<string>
            {
                "TEST trade yaradildi.",
                $"Expected result: {expected}"
            }),

            StrategyBreakdownJson = "[]",

            Status = "PENDING",
            CreatedAtUtc = plan.CreatedAtUtc
        };

        signal.TradeResult = new ForexTradeResultEntity
        {
            ForexSignalId = signal.Id,
            Symbol = symbol,
            Direction = direction,

            EntryPrice = plan.Entry,
            StopLoss = plan.StopLoss,
            TakeProfit1 = plan.TakeProfit1,
            TakeProfit2 = plan.TakeProfit2,

            Result = "PENDING",
            CreatedAtUtc = plan.CreatedAtUtc,
            ExpiresAtUtc = plan.CreatedAtUtc.AddHours(4),
            Notes = $"TEST trade yaradildi. Expected: {expected}"
        };

        _dbContext.ForexSignals.Add(signal);

        await _dbContext.SaveChangesAsync(cancellationToken);

        await _forexTradeResultTracker.EvaluateOpenTradesAsync(cancellationToken);

        var savedTrade = await _dbContext.ForexTradeResults
            .AsNoTracking()
            .FirstAsync(x => x.ForexSignalId == signal.Id, cancellationToken);

        var savedSignal = await _dbContext.ForexSignals
            .AsNoTracking()
            .FirstAsync(x => x.Id == signal.Id, cancellationToken);

        return Ok(new
        {
            message = "TEST forex trade yaradildi ve evaluate edildi.",
            expected,
            signal = new
            {
                savedSignal.Id,
                savedSignal.Symbol,
                savedSignal.Direction,
                savedSignal.Status,
                savedSignal.EntryPrice,
                savedSignal.StopLoss,
                savedSignal.TakeProfit1,
                savedSignal.TakeProfit2,
                savedSignal.Confidence,
                savedSignal.Grade,
                savedSignal.CreatedAtUtc,
                savedSignal.UpdatedAtUtc
            },
            trade = new
            {
                savedTrade.Id,
                savedTrade.Result,
                savedTrade.EntryPrice,
                savedTrade.StopLoss,
                savedTrade.TakeProfit1,
                savedTrade.TakeProfit2,
                savedTrade.ExitPrice,
                savedTrade.Difference,
                savedTrade.IsTp1Hit,
                savedTrade.IsTp2Hit,
                savedTrade.IsStopLossHit,
                savedTrade.Notes,
                savedTrade.CheckedAtUtc,
                savedTrade.Tp1HitAtUtc,
                savedTrade.Tp2HitAtUtc,
                savedTrade.StopLossHitAtUtc,
                savedTrade.LastNotifiedResult,
                savedTrade.LastNotifiedAtUtc,
                savedTrade.LastNotificationError
            }
        });
    }

    [HttpGet("clear")]
    public async Task<IActionResult> ClearTestTrades(
        CancellationToken cancellationToken = default)
    {
        var testSignals = await _dbContext.ForexSignals
            .Where(x => x.Grade == "TEST")
            .ToListAsync(cancellationToken);

        var count = testSignals.Count;

        _dbContext.ForexSignals.RemoveRange(testSignals);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new
        {
            removed = count,
            message = "TEST forex signal/trade melumatlari silindi."
        });
    }

    private static List<Candle> MapCandles(
        TwelveDataResponse? response,
        string symbol)
    {
        if (response?.Values == null)
            return new List<Candle>();

        var candles = new List<Candle>();

        foreach (var item in response.Values)
        {
            var time = TryParseTime(item.DateTime);

            if (time == null)
                continue;

            candles.Add(new Candle
            {
                Symbol = symbol,
                Time = time.Value,
                Open = item.Open,
                High = item.High,
                Low = item.Low,
                Close = item.Close
            });
        }

        return candles
            .OrderBy(x => x.Time)
            .ToList();
    }

    private static TestTradePlan BuildTestPlan(
        string symbol,
        string direction,
        string expected,
        Candle candle)
    {
        var pipSize = GetPipSize(symbol);

        var candleRange = candle.High - candle.Low;

        if (candleRange <= 0)
            candleRange = pipSize * 20;

        decimal entry;
        decimal stopLoss;
        decimal takeProfit1;
        decimal takeProfit2;

        if (direction == "LONG")
        {
            if (expected == "LOSS")
            {
                entry = candle.High;
                stopLoss = candle.Low;
                takeProfit1 = entry + candleRange;
                takeProfit2 = entry + candleRange * 2;
            }
            else if (expected == "AMBIGUOUS")
            {
                entry = candle.Close;
                stopLoss = candle.Low;
                takeProfit1 = candle.High;
                takeProfit2 = candle.High + pipSize;
            }
            else if (expected == "TP2")
            {
                entry = candle.Low;
                stopLoss = candle.Low - candleRange;
                takeProfit1 = candle.Close;
                takeProfit2 = candle.High;
            }
            else
            {
                entry = candle.Low;
                stopLoss = candle.Low - candleRange;
                takeProfit1 = candle.Close;
                takeProfit2 = candle.High + candleRange;
            }
        }
        else
        {
            if (expected == "LOSS")
            {
                entry = candle.Low;
                stopLoss = candle.High;
                takeProfit1 = entry - candleRange;
                takeProfit2 = entry - candleRange * 2;
            }
            else if (expected == "AMBIGUOUS")
            {
                entry = candle.Close;
                stopLoss = candle.High;
                takeProfit1 = candle.Low;
                takeProfit2 = candle.Low - pipSize;
            }
            else if (expected == "TP2")
            {
                entry = candle.High;
                stopLoss = candle.High + candleRange;
                takeProfit1 = candle.Close;
                takeProfit2 = candle.Low;
            }
            else
            {
                entry = candle.High;
                stopLoss = candle.High + candleRange;
                takeProfit1 = candle.Close;
                takeProfit2 = candle.Low - candleRange;
            }
        }

        entry = RoundPrice(symbol, entry);
        stopLoss = RoundPrice(symbol, stopLoss);
        takeProfit1 = RoundPrice(symbol, takeProfit1);
        takeProfit2 = RoundPrice(symbol, takeProfit2);

        var riskPips = Math.Abs(entry - stopLoss) / pipSize;
        var rewardPips1 = Math.Abs(takeProfit1 - entry) / pipSize;
        var rewardPips2 = Math.Abs(takeProfit2 - entry) / pipSize;

        return new TestTradePlan
        {
            Entry = entry,
            StopLoss = stopLoss,
            TakeProfit1 = takeProfit1,
            TakeProfit2 = takeProfit2,

            RiskPips = Math.Round(riskPips, 1),
            RewardPips1 = Math.Round(rewardPips1, 1),
            RewardPips2 = Math.Round(rewardPips2, 1),

            RiskReward1 = riskPips > 0
                ? Math.Round(rewardPips1 / riskPips, 2)
                : 0,

            RiskReward2 = riskPips > 0
                ? Math.Round(rewardPips2 / riskPips, 2)
                : 0,

            CreatedAtUtc = candle.Time
        };
    }

    private static decimal GetPipSize(string symbol)
    {
        return symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase)
            ? 0.01m
            : 0.0001m;
    }

    private static decimal RoundPrice(
        string symbol,
        decimal price)
    {
        return symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase)
            ? Math.Round(price, 3)
            : Math.Round(price, 5);
    }

    private static DateTime? TryParseTime(string value)
    {
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd"
        };

        if (DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            return DateTime.SpecifyKind(time, DateTimeKind.Utc);
        }

        return null;
    }

    private class TestTradePlan
    {
        public decimal Entry { get; set; }

        public decimal StopLoss { get; set; }

        public decimal TakeProfit1 { get; set; }

        public decimal TakeProfit2 { get; set; }

        public decimal RiskPips { get; set; }

        public decimal RewardPips1 { get; set; }

        public decimal RewardPips2 { get; set; }

        public decimal RiskReward1 { get; set; }

        public decimal RiskReward2 { get; set; }

        public DateTime CreatedAtUtc { get; set; }
    }
}
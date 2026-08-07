using Microsoft.AspNetCore.Mvc;
using PocketSignal.Api.Services;
using PocketSignal.Api.Services.Forex;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ForexController : ControllerBase
{
    private readonly IForexSignalService _forexSignalService;
    private readonly IForexNotificationService _forexNotificationService;
    private readonly IForexTradeResultTracker _forexTradeResultTracker;
    private readonly IForexSignalDatabaseService _forexSignalDatabaseService;

    public ForexController(
        IForexSignalService forexSignalService,
        IForexNotificationService forexNotificationService,
        IForexTradeResultTracker forexTradeResultTracker,
        IForexSignalDatabaseService forexSignalDatabaseService)
    {
        _forexSignalService = forexSignalService;
        _forexNotificationService = forexNotificationService;
        _forexTradeResultTracker = forexTradeResultTracker;
        _forexSignalDatabaseService = forexSignalDatabaseService;
    }

    [HttpGet("signal")]
    public async Task<IActionResult> GetSignal(
        [FromQuery] string symbol = "GBP/JPY",
        CancellationToken cancellationToken = default)
    {
        using (MarketDataApiGroupContext.Use("Forex"))
        {
            var signal = await _forexSignalService.AnalyzeAsync(
                symbol,
                "15min",
                cancellationToken);

            return Ok(signal);
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        [FromQuery] string symbol = "GBP/JPY",
        CancellationToken cancellationToken = default)
    {
        using (MarketDataApiGroupContext.Use("Forex"))
        {
            var signal = await _forexSignalService.AnalyzeAsync(
                symbol,
                "15min",
                cancellationToken);

            var message = ForexMessageFormatter.Format(signal);

            return Content(message, "text/plain; charset=utf-8");
        }
    }

    [HttpGet("notify")]
    public async Task<IActionResult> Notify(
        [FromQuery] string symbol = "GBP/JPY",
        CancellationToken cancellationToken = default)
    {
        using (MarketDataApiGroupContext.Use("Forex"))
        {
            var signal = await _forexSignalService.AnalyzeAsync(
                symbol,
                "15min",
                cancellationToken);

            var result = await _forexNotificationService.NotifyIfValidSignalAsync(
                signal,
                cancellationToken);

            var savedSignalId = await _forexSignalDatabaseService.SaveSignalAsync(
                signal,
                result.Sent,
                result.Message,
                cancellationToken);

            // Cassandra order/trade vermir — trade result tracker çağırılmır.

            return Ok(new
            {
                sent = result.Sent,
                notificationMessage = result.Message,
                savedSignalId,

                symbol = signal.Symbol,
                bias = signal.Bias,
                sellZones = signal.SellZones,
                buyZones = signal.BuyZones,
                decisionPoint = signal.DecisionPoint,
                nearestZone = signal.NearestZone,
                lastPrice = signal.LastPrice,
                confidence = signal.Confidence,
                grade = signal.Grade,
                message = signal.Message,
                reasons = signal.Reasons,
                strategyResults = signal.StrategyResults
            });
        }
    }

    [HttpGet("trade-results")]
    public async Task<IActionResult> GetTradeResults(
        CancellationToken cancellationToken = default)
    {
        await _forexTradeResultTracker.EvaluateOpenTradesAsync(cancellationToken);

        var trades = await _forexTradeResultTracker.GetTodayTradesAsync(
            cancellationToken);

        var result = trades.Select(trade => new
        {
            trade.Id,
            trade.ForexSignalId,
            trade.Symbol,
            trade.Direction,
            trade.EntryPrice,
            trade.StopLoss,
            trade.TakeProfit1,
            trade.TakeProfit2,
            trade.ExitPrice,
            trade.Difference,
            trade.Result,
            trade.IsTp1Hit,
            trade.IsTp2Hit,
            trade.IsStopLossHit,
            trade.Notes,
            trade.CreatedAtUtc,
            trade.ExpiresAtUtc,
            trade.CheckedAtUtc,
            trade.Tp1HitAtUtc,
            trade.Tp2HitAtUtc,
            trade.StopLossHitAtUtc
        }).ToList();

        return Ok(result);
    }

    [HttpGet("trade-results-status")]
    public async Task<IActionResult> GetTradeResultsStatus(
        CancellationToken cancellationToken = default)
    {
        await _forexTradeResultTracker.EvaluateOpenTradesAsync(cancellationToken);

        var message = await _forexTradeResultTracker.GetTodayStatusAsync(
            cancellationToken);

        return Content(message, "text/plain; charset=utf-8");
    }

    [HttpGet("db-signals")]
    public async Task<IActionResult> GetDbSignals(
        [FromQuery] int count = 50,
        CancellationToken cancellationToken = default)
    {
        var signals = await _forexSignalDatabaseService.GetLatestSignalsAsync(
            count,
            cancellationToken);

        var result = signals.Select(signal => new
        {
            signal.Id,
            signal.Symbol,
            signal.Direction,
            signal.IsTradable,
            signal.EntryPrice,
            signal.StopLoss,
            signal.TakeProfit1,
            signal.TakeProfit2,
            signal.RiskPips,
            signal.RewardPips1,
            signal.RewardPips2,
            signal.RiskReward1,
            signal.RiskReward2,
            signal.Confidence,
            signal.Grade,
            signal.Message,
            signal.InvalidIf,
            signal.ValidForMinutes,
            signal.ReasonsJson,
            signal.StrategyBreakdownJson,
            signal.Status,
            signal.CreatedAtUtc,
            signal.UpdatedAtUtc,

            StrategyScores = signal.StrategyScores.Select(strategy => new
            {
                strategy.Id,
                strategy.StrategyName,
                strategy.Direction,
                strategy.Score,
                strategy.MaxScore,
                strategy.IsConfirmed,
                strategy.ReasonsJson,
                strategy.CreatedAtUtc
            }).ToList(),

            TradeResult = signal.TradeResult == null
                ? null
                : new
                {
                    signal.TradeResult.Id,
                    signal.TradeResult.Symbol,
                    signal.TradeResult.Direction,
                    signal.TradeResult.EntryPrice,
                    signal.TradeResult.StopLoss,
                    signal.TradeResult.TakeProfit1,
                    signal.TradeResult.TakeProfit2,
                    signal.TradeResult.ExitPrice,
                    signal.TradeResult.Difference,
                    signal.TradeResult.Result,
                    signal.TradeResult.IsTp1Hit,
                    signal.TradeResult.IsTp2Hit,
                    signal.TradeResult.IsStopLossHit,
                    signal.TradeResult.Notes,
                    signal.TradeResult.CreatedAtUtc,
                    signal.TradeResult.ExpiresAtUtc,
                    signal.TradeResult.CheckedAtUtc,
                    signal.TradeResult.Tp1HitAtUtc,
                    signal.TradeResult.Tp2HitAtUtc,
                    signal.TradeResult.StopLossHitAtUtc
                }
        }).ToList();

        return Ok(result);
    }

    [HttpGet("db-signals-status")]
    public async Task<IActionResult> GetDbSignalsStatus(
        CancellationToken cancellationToken = default)
    {
        var message = await _forexSignalDatabaseService.GetTodayStatusAsync(
            cancellationToken);

        return Content(message, "text/plain; charset=utf-8");
    }
}
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Forex.Strategies;

internal class FalseBreakoutTrapStrategy : IForexStrategy
{
    public string Name => nameof(FalseBreakoutTrapStrategy);

    public int MaxScore => 20;

    public bool IsDirectional => true;

    public ForexStrategyResult Evaluate(
        ForexMarketContext context,
        string direction)
    {
        var result = new ForexStrategyResult
        {
            StrategyName = Name,
            Direction = direction,
            MaxScore = MaxScore
        };

        var m15Candles = context.M15;

        if (m15Candles.Count < 40)
            return result;

        var last = m15Candles[^1];
        var previous = m15Candles[^2];

        var lookback = m15Candles
            .Skip(Math.Max(0, m15Candles.Count - 25))
            .Take(20)
            .ToList();

        if (lookback.Count < 10)
            return result;

        var resistance = lookback.Max(x => x.High);
        var support = lookback.Min(x => x.Low);

        var atr = CalculateAtr(m15Candles, 14);

        if (atr <= 0)
            return result;

        var sweepBuffer = atr * 0.05m;
        var rejectionBodyMin = atr * 0.15m;

        if (direction == "SHORT")
        {
            var sweptResistance = previous.High > resistance + sweepBuffer;
            var closedBackBelowResistance = previous.Close < resistance;
            var bearishConfirmation = last.Close < last.Open &&
                                      (last.Open - last.Close) >= rejectionBodyMin;
            var continuationLower = last.Close < previous.Close;

            if (sweptResistance)
            {
                result.Score += 6;
                result.Reasons.Add("False breakout: resistance ustu liquidity sweep edildi.");
            }

            if (closedBackBelowResistance)
            {
                result.Score += 6;
                result.Reasons.Add("Qiymet resistance altina geri qayitdi, breakout ugursuz gorunur.");
            }

            if (bearishConfirmation)
            {
                result.Score += 5;
                result.Reasons.Add("Bearish confirmation candle formalaşdı.");
            }

            if (continuationLower)
            {
                result.Score += 3;
                result.Reasons.Add("Qiymet sweep-den sonra asagi davam edir.");
            }
        }

        if (direction == "LONG")
        {
            var sweptSupport = previous.Low < support - sweepBuffer;
            var closedBackAboveSupport = previous.Close > support;
            var bullishConfirmation = last.Close > last.Open &&
                                      (last.Close - last.Open) >= rejectionBodyMin;
            var continuationHigher = last.Close > previous.Close;

            if (sweptSupport)
            {
                result.Score += 6;
                result.Reasons.Add("False breakout: support alti liquidity sweep edildi.");
            }

            if (closedBackAboveSupport)
            {
                result.Score += 6;
                result.Reasons.Add("Qiymet support ustune geri qayitdi, breakout ugursuz gorunur.");
            }

            if (bullishConfirmation)
            {
                result.Score += 5;
                result.Reasons.Add("Bullish confirmation candle formalaşdı.");
            }

            if (continuationHigher)
            {
                result.Score += 3;
                result.Reasons.Add("Qiymet sweep-den sonra yuxari davam edir.");
            }
        }

        result.IsConfirmed = result.Score >= 14;

        return result;
    }

    private static decimal CalculateAtr(
        IReadOnlyList<Candle> candles,
        int period)
    {
        if (candles.Count < period + 1)
            return 0;

        var trueRanges = new List<decimal>();

        for (var i = candles.Count - period; i < candles.Count; i++)
        {
            var current = candles[i];
            var previous = candles[i - 1];

            var highLow = current.High - current.Low;
            var highClose = Math.Abs(current.High - previous.Close);
            var lowClose = Math.Abs(current.Low - previous.Close);

            trueRanges.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
        }

        return trueRanges.Average();
    }
}
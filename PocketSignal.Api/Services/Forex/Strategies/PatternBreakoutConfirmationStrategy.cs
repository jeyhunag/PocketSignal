using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Forex.Strategies;

internal class PatternBreakoutConfirmationStrategy : IForexStrategy
{
    public string Name => nameof(PatternBreakoutConfirmationStrategy);

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

        var range = resistance - support;

        if (range <= 0)
            return result;

        var atr = CalculateAtr(m15Candles, 14);

        if (atr <= 0)
            return result;

        var closeBuffer = atr * 0.10m;
        var maxDistanceFromBreakout = atr * 0.80m;

        if (direction == "LONG")
        {
            var closedAboveResistance = last.Close > resistance + closeBuffer;
            var previousWasInside = previous.Close <= resistance;
            var notTooFar = Math.Abs(last.Close - resistance) <= maxDistanceFromBreakout;

            if (closedAboveResistance)
            {
                result.Score += 8;
                result.Reasons.Add("Pattern breakout: qiymet resistance ustunde close etdi.");
            }

            if (previousWasInside)
            {
                result.Score += 4;
                result.Reasons.Add("Breakout yeni aktivlesib, evvelki candle range daxilinde idi.");
            }

            if (notTooFar)
            {
                result.Score += 5;
                result.Reasons.Add("Entry breakout seviyyesinden cox uzaqlasmayib.");
            }

            if (last.Close > last.Open)
            {
                result.Score += 3;
                result.Reasons.Add("Breakout candle bullish close verdi.");
            }
        }

        if (direction == "SHORT")
        {
            var closedBelowSupport = last.Close < support - closeBuffer;
            var previousWasInside = previous.Close >= support;
            var notTooFar = Math.Abs(last.Close - support) <= maxDistanceFromBreakout;

            if (closedBelowSupport)
            {
                result.Score += 8;
                result.Reasons.Add("Pattern breakout: qiymet support altinda close etdi.");
            }

            if (previousWasInside)
            {
                result.Score += 4;
                result.Reasons.Add("Breakout yeni aktivlesib, evvelki candle range daxilinde idi.");
            }

            if (notTooFar)
            {
                result.Score += 5;
                result.Reasons.Add("Entry breakout seviyyesinden cox uzaqlasmayib.");
            }

            if (last.Close < last.Open)
            {
                result.Score += 3;
                result.Reasons.Add("Breakout candle bearish close verdi.");
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
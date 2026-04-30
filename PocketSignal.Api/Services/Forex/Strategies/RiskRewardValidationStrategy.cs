using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Forex.Strategies;

internal class RiskRewardValidationStrategy : IForexStrategy
{
    public string Name => nameof(RiskRewardValidationStrategy);

    public int MaxScore => 15;

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

        if (m15Candles.Count < 30)
            return result;

        var last = m15Candles[^1];

        var lookback = m15Candles
            .Skip(Math.Max(0, m15Candles.Count - 25))
            .Take(20)
            .ToList();

        if (lookback.Count < 10)
            return result;

        var atr = CalculateAtr(m15Candles, 14);

        if (atr <= 0)
            return result;

        var entry = last.Close;
        decimal risk;

        if (direction == "LONG")
        {
            var recentSupport = lookback.Min(x => x.Low);
            risk = entry - recentSupport;
        }
        else if (direction == "SHORT")
        {
            var recentResistance = lookback.Max(x => x.High);
            risk = recentResistance - entry;
        }
        else
        {
            return result;
        }

        if (risk <= 0)
        {
            result.Reasons.Add("Risk/Reward: risk mesafesi hesablanmadi.");
            return result;
        }

        var minRisk = atr * 0.15m;
        var maxRisk = atr * 2.50m;

        if (risk >= minRisk)
        {
            result.Score += 4;
            result.Reasons.Add("Risk/Reward: stop mesafesi cox six deyil.");
        }

        if (risk <= maxRisk)
        {
            result.Score += 5;
            result.Reasons.Add("Risk/Reward: stop mesafesi normal araliqdadir.");
        }

        var projectedReward1 = atr * 1.20m;
        var projectedReward2 = atr * 2.00m;

        var rr1 = projectedReward1 / risk;
        var rr2 = projectedReward2 / risk;

        if (rr1 >= 1.0m)
        {
            result.Score += 3;
            result.Reasons.Add("Risk/Reward: TP1 ucun minimum RR uygundur.");
        }

        if (rr2 >= 1.5m)
        {
            result.Score += 3;
            result.Reasons.Add("Risk/Reward: TP2 ucun RR potensiali uygundur.");
        }

        result.IsConfirmed = result.Score >= 10;

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
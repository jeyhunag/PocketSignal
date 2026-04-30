using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Forex.Strategies;

internal class NarrowRangeInsideBarStrategy : IForexStrategy
{
    public string Name => nameof(NarrowRangeInsideBarStrategy);

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

        if (m15Candles.Count < 20)
            return result;

        var breakout = m15Candles[^1];
        var nr = m15Candles[^2];

        var c1 = m15Candles[^5];
        var c2 = m15Candles[^4];
        var c3 = m15Candles[^3];

        var nrRange = nr.High - nr.Low;
        var r1 = c1.High - c1.Low;
        var r2 = c2.High - c2.Low;
        var r3 = c3.High - c3.Low;

        if (nrRange <= 0)
            return result;

        var isNr4 = nrRange < r1 && nrRange < r2 && nrRange < r3;
        var isInsideBar = nr.High <= c3.High && nr.Low >= c3.Low;

        if (isNr4)
        {
            result.Score += 6;
            result.Reasons.Add("NR4: setup candle evvelki 3 candle-dan daha dar range verdi.");
        }

        if (isInsideBar)
        {
            result.Score += 5;
            result.Reasons.Add("Inside bar: volatility sixilmasi gorunur.");
        }

        if (direction == "LONG")
        {
            var brokeHigh = breakout.Close > nr.High;
            var bullishBreakout = breakout.Close > breakout.Open;

            if (brokeHigh)
            {
                result.Score += 6;
                result.Reasons.Add("Narrow range high ustunde breakout oldu.");
            }

            if (bullishBreakout)
            {
                result.Score += 3;
                result.Reasons.Add("Breakout candle bullish istiqametdedir.");
            }
        }

        if (direction == "SHORT")
        {
            var brokeLow = breakout.Close < nr.Low;
            var bearishBreakout = breakout.Close < breakout.Open;

            if (brokeLow)
            {
                result.Score += 6;
                result.Reasons.Add("Narrow range low altinda breakout oldu.");
            }

            if (bearishBreakout)
            {
                result.Score += 3;
                result.Reasons.Add("Breakout candle bearish istiqametdedir.");
            }
        }

        result.IsConfirmed = result.Score >= 14;

        return result;
    }
}
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Models.Common;

namespace PocketSignal.Api.Services.Binary;

public class BinaryContextFilter
{
    public BinaryContextFilterResult Validate(
        string direction,
        IReadOnlyList<Candle> m15Candles,
        IReadOnlyList<Candle> m5Candles,
        IReadOnlyList<Candle> m1Candles)
    {
        var result = new BinaryContextFilterResult();

        direction = NormalizeDirection(direction);

        if (direction != "LONG" && direction != "SHORT")
        {
            return BinaryContextFilterResult.Block(
                "Binary context filter: direction LONG/SHORT deyil, signal bloklandı.");
        }

        if (m15Candles.Count < 30 || m5Candles.Count < 30 || m1Candles.Count < 30)
        {
            return BinaryContextFilterResult.Block(
                "Binary context filter: kifayet qeder candle yoxdur.");
        }

        var lastM1 = m1Candles[^1];
        var lastM5 = m5Candles[^1];
        var lastM15 = m15Candles[^1];

        var m15Trend = GetTrend(m15Candles);
        var m5Trend = GetTrend(m5Candles);
        var m1Trend = GetTrend(m1Candles);

        var m1Atr = CalculateAtr(m1Candles, 14);
        var m5Atr = CalculateAtr(m5Candles, 14);

        var recentResistance = GetRecentResistance(m1Candles, 40);
        var recentSupport = GetRecentSupport(m1Candles, 40);

        var distanceToResistance = Math.Abs(recentResistance - lastM1.Close);
        var distanceToSupport = Math.Abs(lastM1.Close - recentSupport);

        var isNearResistance = m1Atr > 0 && distanceToResistance <= m1Atr * 1.2m;
        var isNearSupport = m1Atr > 0 && distanceToSupport <= m1Atr * 1.2m;

        var isLateLong = IsLateEntryLong(m1Candles, m1Atr);
        var isLateShort = IsLateEntryShort(m1Candles, m1Atr);

        var hasBullishReversal = HasBullishReversalConfirmation(m1Candles);
        var hasBearishReversal = HasBearishReversalConfirmation(m1Candles);

        var isCounterTrendLong =
            direction == "LONG" &&
            (m15Trend == "SHORT" || m5Trend == "SHORT");

        var isCounterTrendShort =
            direction == "SHORT" &&
            (m15Trend == "LONG" || m5Trend == "LONG");

        result.Reasons.Add($"Binary context: M15 trend = {m15Trend}");
        result.Reasons.Add($"Binary context: M5 trend = {m5Trend}");
        result.Reasons.Add($"Binary context: M1 trend = {m1Trend}");

        if (direction == "LONG")
        {
            if (isNearResistance && !hasBullishReversal)
            {
                result.IsAllowed = false;
                result.Decision = "WAIT";
                result.RiskLevel = "HIGH";
                result.ScorePenalty += 35;
                result.Reasons.Add("LONG bloklandı: qiymet resistance zonasına yaxındır və bullish reversal təsdiqi yoxdur.");
            }

            if (isLateLong)
            {
                result.IsAllowed = false;
                result.Decision = "WAIT";
                result.RiskLevel = "HIGH";
                result.ScorePenalty += 30;
                result.Reasons.Add("LONG bloklandı: entry gecikib, qiymet artıq yuxarı çox hərəkət edib.");
            }

            if (isCounterTrendLong && !hasBullishReversal)
            {
                result.IsAllowed = false;
                result.Decision = "WAIT";
                result.RiskLevel = "HIGH";
                result.ScorePenalty += 40;
                result.Reasons.Add("LONG bloklandı: M15/M5 trend SHORT-dur, amma real bullish reversal təsdiqi yoxdur.");
            }

            if (m15Trend == "LONG" && m5Trend == "LONG" && !isNearResistance && !isLateLong)
            {
                result.Reasons.Add("LONG context uyğundur: M15 və M5 LONG istiqamətini dəstəkləyir.");
            }
        }

        if (direction == "SHORT")
        {
            if (isNearSupport && !hasBearishReversal)
            {
                result.IsAllowed = false;
                result.Decision = "WAIT";
                result.RiskLevel = "HIGH";
                result.ScorePenalty += 35;
                result.Reasons.Add("SHORT bloklandı: qiymet support zonasına yaxındır və bearish reversal təsdiqi yoxdur.");
            }

            if (isLateShort)
            {
                result.IsAllowed = false;
                result.Decision = "WAIT";
                result.RiskLevel = "HIGH";
                result.ScorePenalty += 30;
                result.Reasons.Add("SHORT bloklandı: entry gecikib, qiymet artıq aşağı çox hərəkət edib.");
            }

            if (isCounterTrendShort && !hasBearishReversal)
            {
                result.IsAllowed = false;
                result.Decision = "WAIT";
                result.RiskLevel = "HIGH";
                result.ScorePenalty += 40;
                result.Reasons.Add("SHORT bloklandı: M15/M5 trend LONG-dur, amma real bearish reversal təsdiqi yoxdur.");
            }

            if (m15Trend == "SHORT" && m5Trend == "SHORT" && !isNearSupport && !isLateShort)
            {
                result.Reasons.Add("SHORT context uyğundur: M15 və M5 SHORT istiqamətini dəstəkləyir.");
            }
        }

        if (m1Atr <= 0 || m5Atr <= 0)
        {
            result.IsAllowed = false;
            result.Decision = "WAIT";
            result.RiskLevel = "UNKNOWN";
            result.ScorePenalty += 20;
            result.Reasons.Add("Binary context: ATR hesablana bilmedi, risk düzgün ölçülmədi.");
        }

        if (result.IsAllowed)
        {
            result.Decision = "ALLOW";
            result.RiskLevel = "NORMAL";
            result.Reasons.Add("Binary context filter: signal üçün ciddi bloklayıcı risk tapılmadı.");
        }

        return result;
    }

    private static string NormalizeDirection(string direction)
    {
        if (string.IsNullOrWhiteSpace(direction))
            return "WAIT";

        direction = direction.Trim().ToUpperInvariant();

        if (direction == "CALL" || direction == "BUY" || direction == "UP")
            return "LONG";

        if (direction == "PUT" || direction == "SELL" || direction == "DOWN")
            return "SHORT";

        return direction;
    }

    private static string GetTrend(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 25)
            return "RANGE";

        var recent = candles.TakeLast(20).ToList();

        var firstClose = recent.First().Close;
        var lastClose = recent.Last().Close;

        var recentHigh = recent.Max(x => x.High);
        var recentLow = recent.Min(x => x.Low);

        var firstHalfHigh = recent.Take(10).Max(x => x.High);
        var firstHalfLow = recent.Take(10).Min(x => x.Low);

        var secondHalfHigh = recent.Skip(10).Max(x => x.High);
        var secondHalfLow = recent.Skip(10).Min(x => x.Low);

        var priceMove = lastClose - firstClose;
        var range = recentHigh - recentLow;

        if (range <= 0)
            return "RANGE";

        var moveStrength = Math.Abs(priceMove) / range;

        if (secondHalfHigh > firstHalfHigh && secondHalfLow > firstHalfLow && priceMove > 0 && moveStrength >= 0.25m)
            return "LONG";

        if (secondHalfHigh < firstHalfHigh && secondHalfLow < firstHalfLow && priceMove < 0 && moveStrength >= 0.25m)
            return "SHORT";

        if (lastClose > firstClose && moveStrength >= 0.35m)
            return "LONG";

        if (lastClose < firstClose && moveStrength >= 0.35m)
            return "SHORT";

        return "RANGE";
    }

    private static decimal GetRecentResistance(IReadOnlyList<Candle> candles, int lookback)
    {
        return candles
            .TakeLast(Math.Min(lookback, candles.Count))
            .Max(x => x.High);
    }

    private static decimal GetRecentSupport(IReadOnlyList<Candle> candles, int lookback)
    {
        return candles
            .TakeLast(Math.Min(lookback, candles.Count))
            .Min(x => x.Low);
    }

    private static decimal CalculateAtr(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count < period + 2)
            return 0;

        var selected = candles.TakeLast(period + 1).ToList();

        var trs = new List<decimal>();

        for (var i = 1; i < selected.Count; i++)
        {
            var current = selected[i];
            var previous = selected[i - 1];

            var highLow = current.High - current.Low;
            var highClose = Math.Abs(current.High - previous.Close);
            var lowClose = Math.Abs(current.Low - previous.Close);

            trs.Add(Math.Max(highLow, Math.Max(highClose, lowClose)));
        }

        return trs.Count == 0 ? 0 : trs.Average();
    }

    private static bool IsLateEntryLong(IReadOnlyList<Candle> candles, decimal atr)
    {
        if (candles.Count < 8 || atr <= 0)
            return false;

        var recent = candles.TakeLast(6).ToList();

        var first = recent.First();
        var last = recent.Last();

        var move = last.Close - first.Low;
        var bullishCount = recent.Count(x => x.Close > x.Open);

        return move >= atr * 2.2m && bullishCount >= 4;
    }

    private static bool IsLateEntryShort(IReadOnlyList<Candle> candles, decimal atr)
    {
        if (candles.Count < 8 || atr <= 0)
            return false;

        var recent = candles.TakeLast(6).ToList();

        var first = recent.First();
        var last = recent.Last();

        var move = first.High - last.Close;
        var bearishCount = recent.Count(x => x.Close < x.Open);

        return move >= atr * 2.2m && bearishCount >= 4;
    }

    private static bool HasBullishReversalConfirmation(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 8)
            return false;

        var c1 = candles[^3];
        var c2 = candles[^2];
        var c3 = candles[^1];

        var sweptLow = c2.Low < c1.Low;
        var bullishClose = c3.Close > c3.Open;
        var closeAbovePreviousHigh = c3.Close > c2.High;

        var strongBody = GetBodySize(c3) >= GetAverageBodySize(candles, 10) * 1.15m;

        return sweptLow && bullishClose && closeAbovePreviousHigh && strongBody;
    }

    private static bool HasBearishReversalConfirmation(IReadOnlyList<Candle> candles)
    {
        if (candles.Count < 8)
            return false;

        var c1 = candles[^3];
        var c2 = candles[^2];
        var c3 = candles[^1];

        var sweptHigh = c2.High > c1.High;
        var bearishClose = c3.Close < c3.Open;
        var closeBelowPreviousLow = c3.Close < c2.Low;

        var strongBody = GetBodySize(c3) >= GetAverageBodySize(candles, 10) * 1.15m;

        return sweptHigh && bearishClose && closeBelowPreviousLow && strongBody;
    }

    private static decimal GetBodySize(Candle candle)
    {
        return Math.Abs(candle.Close - candle.Open);
    }

    private static decimal GetAverageBodySize(IReadOnlyList<Candle> candles, int period)
    {
        var selected = candles.TakeLast(Math.Min(period, candles.Count)).ToList();

        if (selected.Count == 0)
            return 0;

        return selected.Average(GetBodySize);
    }
}
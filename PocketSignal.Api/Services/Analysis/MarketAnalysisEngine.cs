using PocketSignal.Api.Models.Analysis;

namespace PocketSignal.Api.Services.Analysis;

public class MarketAnalysisEngine : IMarketAnalysisEngine
{
    public CoreMarketAnalysisResult Analyze(
        string symbol,
        IReadOnlyList<PriceCandle> m15Candles,
        IReadOnlyList<PriceCandle> m5Candles,
        IReadOnlyList<PriceCandle> m1Candles)
    {
        var result = new CoreMarketAnalysisResult
        {
            Symbol = symbol
        };

        if (m15Candles.Count < 30 || m5Candles.Count < 30 || m1Candles.Count < 30)
        {
            result.IsBlocked = true;
            result.BlockReason = "Analiz ucun kifayet qeder candle yoxdur.";
            result.BlockReasons.Add(result.BlockReason);
            return result;
        }

        result.M15 = AnalyzeTimeframe("M15", m15Candles);
        result.M5 = AnalyzeTimeframe("M5", m5Candles);
        result.M1 = AnalyzeTimeframe("M1", m1Candles);

        result.EntryPrice = m1Candles[^1].Close;

        var longScore = 0;
        var shortScore = 0;

        ApplyM15Context(result, ref longScore, ref shortScore);
        ApplyM5Context(result, ref longScore, ref shortScore);
        ApplyM1Trigger(result, m1Candles, ref longScore, ref shortScore);
        ApplyZoneScore(result, ref longScore, ref shortScore);
        ApplyAlignmentBonus(result, ref longScore, ref shortScore);

        longScore = Math.Clamp(longScore, 0, 100);
        shortScore = Math.Clamp(shortScore, 0, 100);

        result.LongScore = longScore;
        result.ShortScore = shortScore;
        result.ScoreGap = Math.Abs(longScore - shortScore);

        var candidate = PickDirection(longScore, shortScore);

        if (candidate == TradeDirection.Wait)
        {
            result.Direction = TradeDirection.Wait;
            result.Confidence = Math.Max(longScore, shortScore);
            result.Grade = "NO_TRADE";
            result.Reasons.Add($"WAIT: LONG {longScore}, SHORT {shortScore}, gap {result.ScoreGap}.");
            result.Reasons.Add("Direction ustunluyu kifayet qeder guclu deyil.");
            return result;
        }

        ApplyCandidateVeto(result, candidate, m1Candles);

        if (result.BlockReasons.Count > 0)
        {
            result.IsBlocked = true;
            result.BlockReason = result.BlockReasons[0];
            result.Direction = TradeDirection.Wait;
            result.Confidence = candidate == TradeDirection.Long ? longScore : shortScore;
            result.Grade = "NO_TRADE";

            result.Reasons.Add($"BLOCKED: LONG {longScore}, SHORT {shortScore}, gap {result.ScoreGap}.");
            result.Reasons.AddRange(result.BlockReasons);

            return result;
        }

        result.Direction = candidate;
        result.Confidence = candidate == TradeDirection.Long ? longScore : shortScore;

        if (candidate == TradeDirection.Long && !IsLongFromValidDiscountZone(result))
            result.Confidence = Math.Min(result.Confidence, 78);

        if (candidate == TradeDirection.Short && !IsShortFromValidPremiumZone(result))
            result.Confidence = Math.Min(result.Confidence, 78);

        if (result.ScoreGap < 15)
            result.Confidence = Math.Min(result.Confidence, 84);

        if (result.M15.Trend == MarketTrend.Range && result.M5.Trend == MarketTrend.Range)
            result.Confidence = Math.Min(result.Confidence, 80);

        result.Grade = GetGrade(result.Confidence);

        if (candidate == TradeDirection.Long)
            result.InvalidPrice = result.M1.LastSwingLow;
        else
            result.InvalidPrice = result.M1.LastSwingHigh;

        result.SuggestedExpiryMinutes = CalculateExpiry(result, m1Candles);

        result.Reasons.Add($"Final score: LONG {longScore}, SHORT {shortScore}, gap {result.ScoreGap}.");
        result.Reasons.Add($"Direction: {result.Direction}, Confidence: {result.Confidence}%, Grade: {result.Grade}.");

        return result;
    }

    private static TimeframeAnalysis AnalyzeTimeframe(
        string timeframe,
        IReadOnlyList<PriceCandle> candles)
    {
        var last = candles[^1];

        var analysis = new TimeframeAnalysis
        {
            Timeframe = timeframe,
            LastClose = last.Close,
            LastSwingHigh = FindLastSwingHigh(candles),
            LastSwingLow = FindLastSwingLow(candles)
        };

        analysis.Trend = DetectTrend(candles, out var strength);
        analysis.TrendStrength = strength;

        analysis.Zones.AddRange(DetectSupportResistanceZones(timeframe, candles));
        analysis.Zones.AddRange(DetectFvgZones(timeframe, candles));

        analysis.Notes.Add($"{timeframe} trend: {analysis.Trend}, strength: {analysis.TrendStrength}.");
        analysis.Notes.Add($"{timeframe} swing high: {analysis.LastSwingHigh}, swing low: {analysis.LastSwingLow}.");

        return analysis;
    }

    private static MarketTrend DetectTrend(
        IReadOnlyList<PriceCandle> candles,
        out int strength)
    {
        var recent = candles.TakeLast(24).ToList();

        var firstClose = recent.First().Close;
        var lastClose = recent.Last().Close;

        var avgRange = AverageRange(recent);
        var fastMa = recent.TakeLast(6).Average(x => x.Close);
        var slowMa = recent.TakeLast(18).Average(x => x.Close);

        var bullishScore = 0;
        var bearishScore = 0;

        if (lastClose > firstClose + avgRange)
            bullishScore += 25;

        if (lastClose < firstClose - avgRange)
            bearishScore += 25;

        if (fastMa > slowMa)
            bullishScore += 20;

        if (fastMa < slowMa)
            bearishScore += 20;

        var higherHighs = 0;
        var higherLows = 0;
        var lowerHighs = 0;
        var lowerLows = 0;

        for (var i = 1; i < recent.Count; i++)
        {
            if (recent[i].High > recent[i - 1].High)
                higherHighs++;

            if (recent[i].Low > recent[i - 1].Low)
                higherLows++;

            if (recent[i].High < recent[i - 1].High)
                lowerHighs++;

            if (recent[i].Low < recent[i - 1].Low)
                lowerLows++;
        }

        bullishScore += higherHighs + higherLows;
        bearishScore += lowerHighs + lowerLows;

        if (bullishScore >= bearishScore + 10)
        {
            strength = Math.Min(100, bullishScore);
            return MarketTrend.Bullish;
        }

        if (bearishScore >= bullishScore + 10)
        {
            strength = Math.Min(100, bearishScore);
            return MarketTrend.Bearish;
        }

        strength = Math.Max(bullishScore, bearishScore);
        return MarketTrend.Range;
    }

    private static void ApplyM15Context(
        CoreMarketAnalysisResult result,
        ref int longScore,
        ref int shortScore)
    {
        if (result.M15.Trend == MarketTrend.Bullish)
        {
            longScore += 28;
            result.Reasons.Add("M15 bullish context LONG istiqametini destekleyir.");
        }
        else if (result.M15.Trend == MarketTrend.Bearish)
        {
            shortScore += 28;
            result.Reasons.Add("M15 bearish context SHORT istiqametini destekleyir.");
        }
        else
        {
            longScore += 8;
            shortScore += 8;
            result.Reasons.Add("M15 RANGE: zona esasli trade axtarilir.");
        }
    }

    private static void ApplyM5Context(
        CoreMarketAnalysisResult result,
        ref int longScore,
        ref int shortScore)
    {
        if (result.M5.Trend == MarketTrend.Bullish)
        {
            longScore += 24;
            result.Reasons.Add("M5 bullish struktur LONG setup ucun uygundur.");
        }
        else if (result.M5.Trend == MarketTrend.Bearish)
        {
            shortScore += 24;
            result.Reasons.Add("M5 bearish struktur SHORT setup ucun uygundur.");
        }
        else
        {
            longScore += 6;
            shortScore += 6;
            result.Reasons.Add("M5 RANGE: daha guclu M1 trigger ve zona lazimdir.");
        }
    }

    private static void ApplyM1Trigger(
        CoreMarketAnalysisResult result,
        IReadOnlyList<PriceCandle> m1Candles,
        ref int longScore,
        ref int shortScore)
    {
        var last = m1Candles[^1];
        var previous = m1Candles[^2];

        var avgRange = AverageRange(m1Candles.TakeLast(20).ToList());

        var bullishEngulf =
            last.IsBullish &&
            previous.IsBearish &&
            last.Close > previous.Open &&
            last.Open <= previous.Close;

        var bearishEngulf =
            last.IsBearish &&
            previous.IsBullish &&
            last.Close < previous.Open &&
            last.Open >= previous.Close;

        var bullishRejection =
            last.IsBullish &&
            last.LowerWick > last.Body * 1.2 &&
            last.Range >= avgRange * 0.6;

        var bearishRejection =
            last.IsBearish &&
            last.UpperWick > last.Body * 1.2 &&
            last.Range >= avgRange * 0.6;

        var previousHigh = m1Candles.TakeLast(8).Take(7).Max(x => x.High);
        var previousLow = m1Candles.TakeLast(8).Take(7).Min(x => x.Low);

        var bullishBos = last.Close > previousHigh;
        var bearishBos = last.Close < previousLow;

        if (bullishEngulf || bullishRejection || bullishBos)
        {
            longScore += 24;
            result.Reasons.Add("M1 bullish entry trigger var.");
        }

        if (bearishEngulf || bearishRejection || bearishBos)
        {
            shortScore += 24;
            result.Reasons.Add("M1 bearish entry trigger var.");
        }

        if (result.M1.Trend == MarketTrend.Bullish)
        {
            longScore += 8;
            result.Reasons.Add("M1 qisa trend LONG istiqametindedir.");
        }

        if (result.M1.Trend == MarketTrend.Bearish)
        {
            shortScore += 8;
            result.Reasons.Add("M1 qisa trend SHORT istiqametindedir.");
        }
    }

    private static void ApplyZoneScore(
        CoreMarketAnalysisResult result,
        ref int longScore,
        ref int shortScore)
    {
        var price = result.EntryPrice;

        var allZones = result.M15.Zones
            .Concat(result.M5.Zones)
            .ToList();

        var zoneDistance = GetZoneDistanceLimit(result);

        var supportZone = allZones
            .Where(x => x.Type is "SUPPORT" or "BULLISH_FVG" or "DEMAND")
            .OrderBy(x => x.DistanceTo(price))
            .FirstOrDefault();

        var resistanceZone = allZones
            .Where(x => x.Type is "RESISTANCE" or "BEARISH_FVG" or "SUPPLY")
            .OrderBy(x => x.DistanceTo(price))
            .FirstOrDefault();

        if (supportZone != null && supportZone.DistanceTo(price) <= zoneDistance)
        {
            longScore += supportZone.Type == "BULLISH_FVG" ? 18 : 15;
            result.Reasons.Add($"Qiymet bullish/support zone yaxinligindadir: {supportZone.Type} {supportZone.Timeframe}.");
        }

        if (resistanceZone != null && resistanceZone.DistanceTo(price) <= zoneDistance)
        {
            shortScore += resistanceZone.Type == "BEARISH_FVG" ? 18 : 15;
            result.Reasons.Add($"Qiymet bearish/resistance zone yaxinligindadir: {resistanceZone.Type} {resistanceZone.Timeframe}.");
        }
    }

    private static void ApplyAlignmentBonus(
        CoreMarketAnalysisResult result,
        ref int longScore,
        ref int shortScore)
    {
        if (result.M15.Trend == MarketTrend.Bullish &&
            result.M5.Trend == MarketTrend.Bullish)
        {
            longScore += 10;
            result.Reasons.Add("M15 ve M5 LONG istiqamette alignedir.");
        }

        if (result.M15.Trend == MarketTrend.Bearish &&
            result.M5.Trend == MarketTrend.Bearish)
        {
            shortScore += 10;
            result.Reasons.Add("M15 ve M5 SHORT istiqamette alignedir.");
        }

        if (result.M5.Trend == MarketTrend.Bullish &&
            result.M1.Trend == MarketTrend.Bullish)
        {
            longScore += 6;
            result.Reasons.Add("M5 ve M1 LONG istiqamette alignedir.");
        }

        if (result.M5.Trend == MarketTrend.Bearish &&
            result.M1.Trend == MarketTrend.Bearish)
        {
            shortScore += 6;
            result.Reasons.Add("M5 ve M1 SHORT istiqamette alignedir.");
        }
    }

    private static TradeDirection PickDirection(int longScore, int shortScore)
    {
        var gap = Math.Abs(longScore - shortScore);

        if (longScore >= 70 && longScore > shortScore && gap >= 8)
            return TradeDirection.Long;

        if (shortScore >= 70 && shortScore > longScore && gap >= 8)
            return TradeDirection.Short;

        return TradeDirection.Wait;
    }

    private static void ApplyCandidateVeto(
        CoreMarketAnalysisResult result,
        TradeDirection candidate,
        IReadOnlyList<PriceCandle> m1Candles)
    {
        if (candidate == TradeDirection.Long)
        {
            if (HasUnfilledBullishFvgBelow(result, m1Candles))
            {
                result.BlockReasons.Add("LONG bloklandi: qiymetin altinda doldurulmamis Bullish FVG/imbalance var. Qiymet FVG-ni doldurmadan LONG chase edilmir.");
            }

            if (IsLongChasingAfterImpulse(result, m1Candles))
            {
                result.BlockReasons.Add("LONG bloklandi: qiymet impulsdan sonra yuxarida qalib, entry gecikmisdir.");
            }

            if (!IsLongFromValidDiscountZone(result))
            {
                result.BlockReasons.Add("LONG bloklandi: qiymet support/demand/FVG retest zonasindan girmir.");
            }

            if (result.M15.Trend == MarketTrend.Bearish &&
                result.M5.Trend != MarketTrend.Bullish)
            {
                result.BlockReasons.Add("LONG bloklandi: M15 bearish, M5 ise LONG tesdiq vermir.");
            }

            if (IsLongLate(result, m1Candles))
            {
                result.BlockReasons.Add("LONG bloklandi: entry gecikib, qiymet M1-de artiq cox yuxari gedib.");
            }

            if (IsNearBearishZone(result) &&
                result.M15.Trend != MarketTrend.Bullish &&
                !HasBullishBreakout(m1Candles))
            {
                result.BlockReasons.Add("LONG bloklandi: qiymet resistance/bearish zone yaxinligindadir.");
            }
        }

        if (candidate == TradeDirection.Short)
        {
            if (HasUnfilledBearishFvgAbove(result, m1Candles))
            {
                result.BlockReasons.Add("SHORT bloklandi: qiymetin ustunde doldurulmamis Bearish FVG/imbalance var. Qiymet yuxari FVG-ni doldurmadan SHORT chase edilmir.");
            }

            if (IsShortChasingAfterImpulse(result, m1Candles))
            {
                result.BlockReasons.Add("SHORT bloklandi: qiymet impulsdan sonra asagida qalib, entry gecikmisdir.");
            }

            if (!IsShortFromValidPremiumZone(result))
            {
                result.BlockReasons.Add("SHORT bloklandi: qiymet resistance/supply/FVG retest zonasindan girmir.");
            }

            if (result.M15.Trend == MarketTrend.Bullish &&
                result.M5.Trend != MarketTrend.Bearish)
            {
                result.BlockReasons.Add("SHORT bloklandi: M15 bullish, M5 ise SHORT tesdiq vermir.");
            }

            if (IsShortLate(result, m1Candles))
            {
                result.BlockReasons.Add("SHORT bloklandi: entry gecikib, qiymet M1-de artiq cox asagi gedib.");
            }

            if (IsNearBullishZone(result) &&
                result.M15.Trend != MarketTrend.Bearish &&
                !HasBearishBreakout(m1Candles))
            {
                result.BlockReasons.Add("SHORT bloklandi: qiymet support/bullish zone yaxinligindadir.");
            }
        }

        if (result.M15.Trend == MarketTrend.Range &&
            result.M5.Trend == MarketTrend.Range &&
            result.M1.Trend == MarketTrend.Range)
        {
            result.BlockReasons.Add("Signal bloklandi: M15, M5 ve M1 hamisi RANGE-dir.");
        }
    }

    private static bool HasUnfilledBullishFvgBelow(
        CoreMarketAnalysisResult result,
        IReadOnlyList<PriceCandle> m1Candles)
    {
        var price = result.EntryPrice;

        var zones = result.M15.Zones
            .Concat(result.M5.Zones)
            .Where(x => x.Type == "BULLISH_FVG" && x.High < price)
            .OrderByDescending(x => x.High)
            .ToList();

        if (zones.Count == 0)
            return false;

        var nearest = zones.First();

        var distance = price - nearest.High;
        var avgRange = AverageRange(m1Candles.TakeLast(30).ToList());

        if (avgRange <= 0)
            return false;

        var isCloseEnoughToPullPrice = distance <= avgRange * 8;

        var wasMitigated = m1Candles
            .TakeLast(25)
            .Any(x => x.Low <= nearest.High && x.High >= nearest.Low);

        return isCloseEnoughToPullPrice && !wasMitigated;
    }

    private static bool HasUnfilledBearishFvgAbove(
        CoreMarketAnalysisResult result,
        IReadOnlyList<PriceCandle> m1Candles)
    {
        var price = result.EntryPrice;

        var zones = result.M15.Zones
            .Concat(result.M5.Zones)
            .Where(x => x.Type == "BEARISH_FVG" && x.Low > price)
            .OrderBy(x => x.Low)
            .ToList();

        if (zones.Count == 0)
            return false;

        var nearest = zones.First();

        var distance = nearest.Low - price;
        var avgRange = AverageRange(m1Candles.TakeLast(30).ToList());

        if (avgRange <= 0)
            return false;

        var isCloseEnoughToPullPrice = distance <= avgRange * 8;

        var wasMitigated = m1Candles
            .TakeLast(25)
            .Any(x => x.High >= nearest.Low && x.Low <= nearest.High);

        return isCloseEnoughToPullPrice && !wasMitigated;
    }

    private static bool IsLongChasingAfterImpulse(
        CoreMarketAnalysisResult result,
        IReadOnlyList<PriceCandle> m1Candles)
    {
        var recent = m1Candles.TakeLast(20).ToList();
        var last = recent[^1];

        var avgRange = AverageRange(recent);

        if (avgRange <= 0)
            return false;

        var impulseLow = recent.Min(x => x.Low);
        var move = last.Close - impulseLow;

        var last10 = m1Candles.TakeLast(10).ToList();
        var rangeHigh = last10.Max(x => x.High);
        var rangeLow = last10.Min(x => x.Low);
        var rangeSize = rangeHigh - rangeLow;

        var isAfterBigMove = move > avgRange * 7;
        var isNowRangingNearTop = rangeSize < avgRange * 4 && last.Close > impulseLow + move * 0.55;

        return isAfterBigMove && isNowRangingNearTop;
    }

    private static bool IsShortChasingAfterImpulse(
        CoreMarketAnalysisResult result,
        IReadOnlyList<PriceCandle> m1Candles)
    {
        var recent = m1Candles.TakeLast(20).ToList();
        var last = recent[^1];

        var avgRange = AverageRange(recent);

        if (avgRange <= 0)
            return false;

        var impulseHigh = recent.Max(x => x.High);
        var move = impulseHigh - last.Close;

        var last10 = m1Candles.TakeLast(10).ToList();
        var rangeHigh = last10.Max(x => x.High);
        var rangeLow = last10.Min(x => x.Low);
        var rangeSize = rangeHigh - rangeLow;

        var isAfterBigMove = move > avgRange * 7;
        var isNowRangingNearBottom = rangeSize < avgRange * 4 && last.Close < impulseHigh - move * 0.55;

        return isAfterBigMove && isNowRangingNearBottom;
    }

    private static bool IsLongFromValidDiscountZone(CoreMarketAnalysisResult result)
    {
        var price = result.EntryPrice;
        var limit = GetZoneDistanceLimit(result);

        var validZone = result.M15.Zones
            .Concat(result.M5.Zones)
            .Any(x =>
                (x.Type == "SUPPORT" || x.Type == "BULLISH_FVG" || x.Type == "DEMAND") &&
                x.DistanceTo(price) <= limit);

        if (validZone)
            return true;

        if (result.M15.Trend == MarketTrend.Bullish &&
            result.M5.Trend == MarketTrend.Bullish &&
            result.M1.Trend == MarketTrend.Bullish)
        {
            return true;
        }

        return false;
    }

    private static bool IsShortFromValidPremiumZone(CoreMarketAnalysisResult result)
    {
        var price = result.EntryPrice;
        var limit = GetZoneDistanceLimit(result);

        var validZone = result.M15.Zones
            .Concat(result.M5.Zones)
            .Any(x =>
                (x.Type == "RESISTANCE" || x.Type == "BEARISH_FVG" || x.Type == "SUPPLY") &&
                x.DistanceTo(price) <= limit);

        if (validZone)
            return true;

        if (result.M15.Trend == MarketTrend.Bearish &&
            result.M5.Trend == MarketTrend.Bearish &&
            result.M1.Trend == MarketTrend.Bearish)
        {
            return true;
        }

        return false;
    }

    private static bool IsLongLate(
        CoreMarketAnalysisResult result,
        IReadOnlyList<PriceCandle> m1Candles)
    {
        var last5 = m1Candles.TakeLast(5).ToList();
        var avgRange = AverageRange(m1Candles.TakeLast(25).ToList());

        var bullishCount = last5.Count(x => x.IsBullish);
        var moveFromLow = result.EntryPrice - last5.Min(x => x.Low);

        return bullishCount >= 4 && moveFromLow > avgRange * 3.2;
    }

    private static bool IsShortLate(
        CoreMarketAnalysisResult result,
        IReadOnlyList<PriceCandle> m1Candles)
    {
        var last5 = m1Candles.TakeLast(5).ToList();
        var avgRange = AverageRange(m1Candles.TakeLast(25).ToList());

        var bearishCount = last5.Count(x => x.IsBearish);
        var moveFromHigh = last5.Max(x => x.High) - result.EntryPrice;

        return bearishCount >= 4 && moveFromHigh > avgRange * 3.2;
    }

    private static bool IsNearBearishZone(CoreMarketAnalysisResult result)
    {
        var price = result.EntryPrice;
        var limit = GetZoneDistanceLimit(result);

        return result.M15.Zones
            .Concat(result.M5.Zones)
            .Where(x => x.Type is "RESISTANCE" or "BEARISH_FVG" or "SUPPLY")
            .Any(x => x.DistanceTo(price) <= limit);
    }

    private static bool IsNearBullishZone(CoreMarketAnalysisResult result)
    {
        var price = result.EntryPrice;
        var limit = GetZoneDistanceLimit(result);

        return result.M15.Zones
            .Concat(result.M5.Zones)
            .Where(x => x.Type is "SUPPORT" or "BULLISH_FVG" or "DEMAND")
            .Any(x => x.DistanceTo(price) <= limit);
    }

    private static bool HasBullishBreakout(IReadOnlyList<PriceCandle> m1Candles)
    {
        var last = m1Candles[^1];
        var prevHigh = m1Candles.TakeLast(10).Take(9).Max(x => x.High);

        return last.IsBullish && last.Close > prevHigh;
    }

    private static bool HasBearishBreakout(IReadOnlyList<PriceCandle> m1Candles)
    {
        var last = m1Candles[^1];
        var prevLow = m1Candles.TakeLast(10).Take(9).Min(x => x.Low);

        return last.IsBearish && last.Close < prevLow;
    }

    private static double GetZoneDistanceLimit(CoreMarketAnalysisResult result)
    {
        var swingRange = Math.Abs(result.M5.LastSwingHigh - result.M5.LastSwingLow);

        if (swingRange <= 0)
            return result.EntryPrice * 0.001;

        return swingRange * 0.14;
    }

    private static int CalculateExpiry(
        CoreMarketAnalysisResult result,
        IReadOnlyList<PriceCandle> m1Candles)
    {
        var avgRange = AverageRange(m1Candles.TakeLast(20).ToList());
        var recentRange = AverageRange(m1Candles.TakeLast(5).ToList());

        var volatilityRatio = avgRange <= 0
            ? 1
            : recentRange / avgRange;

        if (result.M15.Trend == result.M5.Trend &&
            result.M5.Trend != MarketTrend.Range &&
            volatilityRatio < 0.85)
        {
            return 12;
        }

        if (result.M15.Trend == result.M5.Trend &&
            result.M5.Trend != MarketTrend.Range)
        {
            return 10;
        }

        if (volatilityRatio > 1.35)
        {
            return 5;
        }

        return 8;
    }

    private static string GetGrade(int confidence)
    {
        if (confidence >= 92)
            return "A+";

        if (confidence >= 85)
            return "A";

        if (confidence >= 75)
            return "B";

        return "NO_TRADE";
    }

    private static List<MarketZone> DetectSupportResistanceZones(
        string timeframe,
        IReadOnlyList<PriceCandle> candles)
    {
        var zones = new List<MarketZone>();

        var recent = candles.TakeLast(40).ToList();

        var swingHigh = FindLastSwingHigh(recent);
        var swingLow = FindLastSwingLow(recent);

        var avgRange = AverageRange(recent);
        var padding = avgRange * 0.35;

        if (swingLow > 0)
        {
            zones.Add(new MarketZone
            {
                Type = "SUPPORT",
                Timeframe = timeframe,
                Low = swingLow - padding,
                High = swingLow + padding,
                Strength = 70
            });
        }

        if (swingHigh > 0)
        {
            zones.Add(new MarketZone
            {
                Type = "RESISTANCE",
                Timeframe = timeframe,
                Low = swingHigh - padding,
                High = swingHigh + padding,
                Strength = 70
            });
        }

        return zones;
    }

    private static List<MarketZone> DetectFvgZones(
        string timeframe,
        IReadOnlyList<PriceCandle> candles)
    {
        var zones = new List<MarketZone>();

        var recent = candles.TakeLast(45).ToList();

        for (var i = 2; i < recent.Count; i++)
        {
            var c1 = recent[i - 2];
            var c3 = recent[i];

            if (c1.High < c3.Low)
            {
                zones.Add(new MarketZone
                {
                    Type = "BULLISH_FVG",
                    Timeframe = timeframe,
                    Low = c1.High,
                    High = c3.Low,
                    Strength = 75
                });
            }

            if (c1.Low > c3.High)
            {
                zones.Add(new MarketZone
                {
                    Type = "BEARISH_FVG",
                    Timeframe = timeframe,
                    Low = c3.High,
                    High = c1.Low,
                    Strength = 75
                });
            }
        }

        return zones
            .OrderByDescending(x => x.Strength)
            .Take(8)
            .ToList();
    }

    private static double FindLastSwingHigh(IReadOnlyList<PriceCandle> candles)
    {
        var recent = candles.TakeLast(20).ToList();

        return recent.Max(x => x.High);
    }

    private static double FindLastSwingLow(IReadOnlyList<PriceCandle> candles)
    {
        var recent = candles.TakeLast(20).ToList();

        return recent.Min(x => x.Low);
    }

    private static double AverageRange(IReadOnlyList<PriceCandle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.Range);
    }
}
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;
using System.Globalization;

namespace PocketSignal.Api.Services.Forex;

public class CoreForexSignalService : IForexSignalService
{
    private const int MinimumConfidence = 72;
    private const int DirectionConflictDistance = 10;

    private readonly IMarketDataService _marketDataService;

    public CoreForexSignalService(IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<ForexTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var m15Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "15min",
            220,
            cancellationToken);

        var m1Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            320,
            cancellationToken);

        var m15 = MapCandles(m15Response);
        var m1 = MapCandles(m1Response);

        if (m15.Count < 70 || m1.Count < 100)
        {
            return Wait(
                symbol,
                0,
                "NO_TRADE",
                new List<string>
                {
                    "Strategy 2 ucun kifayet qeder M15/M1 candle yoxdur."
                },
                new List<ForexStrategyResult>());
        }

        var m15Trend = DetectTrend(m15);

        var longAnalysis = AnalyzeDirection(
            symbol,
            "LONG",
            m15,
            m1,
            m15Trend);

        var shortAnalysis = AnalyzeDirection(
            symbol,
            "SHORT",
            m15,
            m1,
            m15Trend);

        Console.WriteLine(
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Forex Strategy2 | {symbol} | " +
            $"M15 Trend: {m15Trend} | " +
            $"LONG {longAnalysis.Confidence}% [{longAnalysis.DebugSummary}] | " +
            $"SHORT {shortAnalysis.Confidence}% [{shortAnalysis.DebugSummary}]");

        var best = longAnalysis.Confidence >= shortAnalysis.Confidence
            ? longAnalysis
            : shortAnalysis;

        var opposite = best.Direction == "LONG"
            ? shortAnalysis
            : longAnalysis;

        var strategyResults = BuildStrategyResults(
            longAnalysis,
            shortAnalysis,
            m15Trend);

        if (!best.TradeReady)
        {
            return Wait(
                symbol,
                best.Confidence,
                best.Confidence >= MinimumConfidence ? "WATCHLIST" : "NO_TRADE",
                new List<string>
                {
                    $"M15 trend: {m15Trend}",
                    $"Setup hele tam hazir deyil. Best: {best.Direction} {best.Confidence}%.",
                    $"LONG score: {longAnalysis.Confidence}%",
                    $"SHORT score: {shortAnalysis.Confidence}%"
                }
                .Concat(best.Reasons)
                .Distinct()
                .ToList(),
                strategyResults);
        }

        if (opposite.TradeReady &&
            Math.Abs(best.Confidence - opposite.Confidence) < DirectionConflictDistance)
        {
            return Wait(
                symbol,
                Math.Max(best.Confidence, opposite.Confidence),
                "NO_TRADE",
                new List<string>
                {
                    $"M15 trend: {m15Trend}",
                    $"LONG score: {longAnalysis.Confidence}%",
                    $"SHORT score: {shortAnalysis.Confidence}%",
                    "LONG ve SHORT Strategy 2 setup-lari arasinda ferq azdir. Direction temiz deyil."
                },
                strategyResults);
        }

        if (best.Confidence < MinimumConfidence)
        {
            return Wait(
                symbol,
                best.Confidence,
                "WATCHLIST",
                new List<string>
                {
                    $"M15 trend: {m15Trend}",
                    $"{best.Direction} Strategy 2 setup var, amma confidence minimum seviyeye catmadi.",
                    $"Confidence: {best.Confidence}%, minimum: {MinimumConfidence}%"
                }
                .Concat(best.Reasons)
                .Distinct()
                .ToList(),
                strategyResults);
        }

        var entry = RoundPrice(symbol, best.EntryPrice);
        var stopLoss = RoundPrice(symbol, best.StopLoss);
        var takeProfit1 = RoundPrice(symbol, best.TakeProfit1);
        var takeProfit2 = RoundPrice(symbol, best.TakeProfit2);

        var reasons = new List<string>
        {
            $"Strategy 2 {best.Direction} signal tesdiqlendi.",
            $"M15 trend: {m15Trend}",
            "M15 trend direction tapildi.",
            "M15 FVG optimal trade zone kimi istifade edildi.",
            "Price FVG zone-a pullback/retest etdi.",
            "M1 liquidity grab entry reason verdi.",
            best.HasChoch
                ? "M1 CHoCH/BOS elave confirmation verdi."
                : "M1 CHoCH/BOS yoxdur, liquidity grab esas entry confirmation kimi istifade edildi.",
            best.RiskReason
        };

        reasons.AddRange(best.Reasons);

        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = best.Direction,

            EntryPrice = entry,
            StopLoss = stopLoss,
            TakeProfit1 = takeProfit1,
            TakeProfit2 = takeProfit2,

            RiskPips = Math.Round(best.RiskPips, 1),
            RewardPips1 = Math.Round(best.RewardPips1, 1),
            RewardPips2 = Math.Round(best.RewardPips2, 1),
            RiskReward1 = Math.Round(best.RiskReward1, 2),
            RiskReward2 = Math.Round(best.RiskReward2, 2),

            Confidence = best.Confidence,
            Grade = GetGrade(best.Confidence),

            Message =
                $"{symbol} {best.Direction} Entry: {entry} SL: {stopLoss} TP1: {takeProfit1} TP2: {takeProfit2}",

            InvalidIf = best.Direction == "LONG"
                ? $"M1 candle {RoundPrice(symbol, best.InvalidLevel)} altinda baglansa trade legvdir."
                : $"M1 candle {RoundPrice(symbol, best.InvalidLevel)} ustunde baglansa trade legvdir.",

            ValidForMinutes = GetValidForMinutes(best.Confidence),

            Reasons = reasons.Distinct().ToList(),

            SideAnalyses = new List<SideAnalysis>
            {
                new SideAnalysis
                {
                    Direction = "LONG",
                    Score = longAnalysis.Confidence,
                    Reasons = longAnalysis.Reasons
                },
                new SideAnalysis
                {
                    Direction = "SHORT",
                    Score = shortAnalysis.Confidence,
                    Reasons = shortAnalysis.Reasons
                }
            },

            StrategyResults = strategyResults,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static DirectionAnalysis AnalyzeDirection(
        string symbol,
        string direction,
        List<PriceCandle> m15,
        List<PriceCandle> m1,
        string m15Trend)
    {
        var lastM1 = m1[^1];
        var avgM15Range = AverageRange(m15.TakeLast(40).ToList());
        var avgM1Range = AverageRange(m1.TakeLast(40).ToList());

        var analysis = new DirectionAnalysis
        {
            Direction = direction,
            EntryPrice = (decimal)lastM1.Close
        };

        var reasons = new List<string>();

        if (avgM15Range <= 0 || avgM1Range <= 0)
        {
            analysis.Reasons.Add("Average range hesablanmadi, candle data duzgun deyil.");
            return analysis;
        }

        var trendAligned = IsTrendContextAligned(direction, m15Trend);

        if (trendAligned)
        {
            analysis.Confidence += 20;
            analysis.IsTrendAligned = true;
            reasons.Add($"M15 trend {direction} istiqametindedir.");
        }
        else if (m15Trend == "RANGE")
        {
            analysis.Confidence += 6;
            reasons.Add("M15 trend RANGE-dir. Strategy 2 ucun direction tam guclu deyil.");
        }
        else
        {
            reasons.Add($"M15 trend {direction} ucun uygun deyil. Trend: {m15Trend}.");
        }

        var fvgZones = DetectActiveFvgs(
            m15,
            direction,
            avgM15Range);

        if (fvgZones.Count == 0)
        {
            reasons.Add(direction == "LONG"
                ? "M15 bullish FVG optimal trade zone tapilmadi."
                : "M15 bearish FVG optimal trade zone tapilmadi.");

            analysis.Reasons = reasons;
            analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);

            return analysis;
        }

        var fvg = fvgZones
            .OrderByDescending(x => ScoreZoneNearPrice(lastM1.Close, x, avgM1Range))
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.AgeCandles)
            .First();

        analysis.HasFvg = true;
        analysis.ZoneLow = fvg.Low;
        analysis.ZoneHigh = fvg.High;

        analysis.Confidence += fvg.Score;

        reasons.Add(direction == "LONG"
            ? $"M15 bullish FVG zone tapildi: {FormatPrice(fvg.Low)} - {FormatPrice(fvg.High)}."
            : $"M15 bearish FVG zone tapildi: {FormatPrice(fvg.Low)} - {FormatPrice(fvg.High)}.");

        var retest = FindRecentZoneRetest(
            m1,
            fvg,
            avgM1Range);

        if (retest == null)
        {
            reasons.Add("Price hele M15 FVG zone-a pullback/retest etmeyib.");

            analysis.Reasons = reasons;
            analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);

            return analysis;
        }

        analysis.HasZoneRetest = true;
        analysis.Confidence += retest.Score;

        reasons.Add($"Price M15 FVG zone-a retest etdi. Retest age: {retest.AgeCandles} M1 candle.");

        var liquidityGrab = FindLiquidityGrabInsideZone(
            m1,
            direction,
            fvg,
            avgM1Range);

        if (liquidityGrab == null)
        {
            reasons.Add(direction == "LONG"
                ? "M1 bullish liquidity grab FVG daxilinde hele yoxdur."
                : "M1 bearish liquidity grab FVG daxilinde hele yoxdur.");

            analysis.Reasons = reasons;
            analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);

            return analysis;
        }

        analysis.HasLiquidityGrab = true;
        analysis.Confidence += liquidityGrab.Score;

        reasons.Add(direction == "LONG"
            ? $"M1 bullish liquidity grab tapildi. Level: {FormatPrice(liquidityGrab.Level)}, age: {liquidityGrab.AgeCandles} candle."
            : $"M1 bearish liquidity grab tapildi. Level: {FormatPrice(liquidityGrab.Level)}, age: {liquidityGrab.AgeCandles} candle.");

        var choch = HasChochAfterGrab(
            m1,
            direction,
            liquidityGrab.CandleIndex);

        if (choch)
        {
            analysis.HasChoch = true;
            analysis.Confidence += 10;
            reasons.Add("Liquidity grab-dan sonra M1 CHoCH/BOS confirmation var.");
        }
        else
        {
            reasons.Add("Liquidity grab-dan sonra M1 CHoCH/BOS confirmation hele yoxdur.");
        }

        var entryConfirmation = HasEntryConfirmation(
            m1,
            direction,
            fvg,
            avgM1Range);

        if (entryConfirmation.IsConfirmed)
        {
            analysis.HasEntryConfirmation = true;
            analysis.Confidence += 6;
            reasons.Add(entryConfirmation.Reason);
        }
        else
        {
            reasons.Add(entryConfirmation.Reason);
        }

        var entryStillValid = IsEntryStillNearZone(
            lastM1.Close,
            fvg,
            avgM1Range);

        if (entryStillValid)
        {
            analysis.IsEntryStillValid = true;
            analysis.Confidence += 5;
            reasons.Add("Entry gecikmis deyil, price hele FVG zone/retest zonasina yaxindir.");
        }
        else
        {
            reasons.Add("Entry gecikmis ola biler, price FVG zone-dan uzaqlasib.");
        }

        var riskPlan = BuildRiskPlan(
            symbol,
            direction,
            lastM1,
            fvg,
            liquidityGrab,
            avgM1Range);

        analysis.EntryPrice = riskPlan.Entry;
        analysis.StopLoss = riskPlan.StopLoss;
        analysis.TakeProfit1 = riskPlan.TakeProfit1;
        analysis.TakeProfit2 = riskPlan.TakeProfit2;
        analysis.RiskPips = riskPlan.RiskPips;
        analysis.RewardPips1 = riskPlan.RewardPips1;
        analysis.RewardPips2 = riskPlan.RewardPips2;
        analysis.RiskReward1 = riskPlan.RiskReward1;
        analysis.RiskReward2 = riskPlan.RiskReward2;
        analysis.InvalidLevel = riskPlan.InvalidLevel;
        analysis.IsRiskPlanValid = riskPlan.IsValid;
        analysis.InvalidReason = riskPlan.InvalidReason;
        analysis.RiskReason = riskPlan.Reason;

        if (riskPlan.IsValid)
        {
            analysis.Confidence += 5;
            reasons.Add(riskPlan.Reason);
        }
        else
        {
            reasons.Add(riskPlan.InvalidReason);
        }

        if (liquidityGrab.AgeCandles > 30)
        {
            analysis.Confidence -= 8;
            reasons.Add("Liquidity grab artiq bir az kohneleib.");
        }

        analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);

        analysis.TradeReady =
            analysis.IsTrendAligned &&
            analysis.HasFvg &&
            analysis.HasZoneRetest &&
            analysis.HasLiquidityGrab &&
            analysis.IsEntryStillValid &&
            analysis.IsRiskPlanValid &&
            liquidityGrab.AgeCandles <= 35;

        if (!analysis.TradeReady)
        {
            if (!analysis.IsTrendAligned)
                reasons.Add("No trade: M15 trend direction ile uygun deyil.");

            if (!analysis.IsEntryStillValid)
                reasons.Add("No trade: entry gecikib.");

            if (!analysis.IsRiskPlanValid)
                reasons.Add("No trade: risk plan uygun deyil.");

            if (liquidityGrab.AgeCandles > 35)
                reasons.Add("No trade: liquidity grab cox kohneleib.");
        }

        analysis.Reasons = reasons.Distinct().ToList();

        return analysis;
    }

    private static List<FvgZone> DetectActiveFvgs(
        List<PriceCandle> candles,
        string direction,
        double avgRange)
    {
        var zones = new List<FvgZone>();

        var start = Math.Max(2, candles.Count - 140);

        for (var i = start; i < candles.Count - 1; i++)
        {
            var c1 = candles[i - 2];
            var c2 = candles[i - 1];
            var c3 = candles[i];

            if (direction == "LONG")
            {
                var hasBullishFvg = c1.High < c3.Low;

                if (!hasBullishFvg)
                    continue;

                var low = c1.High;
                var high = c3.Low;
                var size = high - low;

                if (size <= 0 || size < avgRange * 0.05)
                    continue;

                var isUsable = !IsFvgFullyInvalidated(
                    candles,
                    i,
                    low,
                    high,
                    direction);

                if (!isUsable)
                    continue;

                var impulse =
                    c2.IsBullish ||
                    c3.IsBullish ||
                    c3.Close > c1.Close;

                zones.Add(new FvgZone
                {
                    Direction = direction,
                    Low = low,
                    High = high,
                    CreatedIndex = i,
                    CreatedAtUtc = c3.TimeUtc,
                    AgeCandles = candles.Count - 1 - i,
                    Score = CalculateFvgScore(i, candles.Count, size, avgRange, impulse)
                });
            }
            else
            {
                var hasBearishFvg = c1.Low > c3.High;

                if (!hasBearishFvg)
                    continue;

                var low = c3.High;
                var high = c1.Low;
                var size = high - low;

                if (size <= 0 || size < avgRange * 0.05)
                    continue;

                var isUsable = !IsFvgFullyInvalidated(
                    candles,
                    i,
                    low,
                    high,
                    direction);

                if (!isUsable)
                    continue;

                var impulse =
                    c2.IsBearish ||
                    c3.IsBearish ||
                    c3.Close < c1.Close;

                zones.Add(new FvgZone
                {
                    Direction = direction,
                    Low = low,
                    High = high,
                    CreatedIndex = i,
                    CreatedAtUtc = c3.TimeUtc,
                    AgeCandles = candles.Count - 1 - i,
                    Score = CalculateFvgScore(i, candles.Count, size, avgRange, impulse)
                });
            }
        }

        return zones
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.AgeCandles)
            .Take(12)
            .ToList();
    }

    private static bool IsFvgFullyInvalidated(
        List<PriceCandle> candles,
        int createdIndex,
        double low,
        double high,
        string direction)
    {
        for (var i = createdIndex + 1; i < candles.Count; i++)
        {
            var candle = candles[i];

            if (direction == "LONG")
            {
                if (candle.Low <= low)
                    return true;
            }
            else
            {
                if (candle.High >= high)
                    return true;
            }
        }

        return false;
    }

    private static int CalculateFvgScore(
        int createdIndex,
        int candleCount,
        double size,
        double avgRange,
        bool impulse)
    {
        var age = candleCount - 1 - createdIndex;

        var score = 18;

        if (age <= 12)
            score += 6;
        else if (age <= 30)
            score += 4;
        else if (age <= 60)
            score += 2;

        if (size <= avgRange * 2.5)
            score += 4;

        if (impulse)
            score += 4;

        return Math.Clamp(score, 12, 30);
    }

    private static int ScoreZoneNearPrice(
        double price,
        FvgZone zone,
        double avgM1Range)
    {
        var distance = DistanceFromZone(price, zone);

        if (distance <= 0)
            return 30;

        if (distance <= avgM1Range * 3)
            return 24;

        if (distance <= avgM1Range * 8)
            return 16;

        if (distance <= avgM1Range * 15)
            return 8;

        return 0;
    }

    private static RetestInfo? FindRecentZoneRetest(
        List<PriceCandle> m1,
        FvgZone zone,
        double avgRange)
    {
        var tolerance = Math.Max(
            avgRange * 0.35,
            Math.Max(zone.Low, zone.High) * 0.00003);

        var start = Math.Max(0, m1.Count - 80);

        for (var i = m1.Count - 1; i >= start; i--)
        {
            var candle = m1[i];

            if (!OverlapsZone(candle, zone, tolerance))
                continue;

            var age = m1.Count - 1 - i;

            var score = age switch
            {
                <= 5 => 20,
                <= 15 => 17,
                <= 30 => 13,
                <= 50 => 9,
                _ => 6
            };

            return new RetestInfo
            {
                CandleIndex = i,
                AgeCandles = age,
                Score = score
            };
        }

        return null;
    }

    private static LiquidityGrabInfo? FindLiquidityGrabInsideZone(
        List<PriceCandle> m1,
        string direction,
        FvgZone zone,
        double avgRange)
    {
        var tolerance = Math.Max(
            avgRange * 0.35,
            Math.Max(zone.Low, zone.High) * 0.00003);

        var start = Math.Max(30, m1.Count - 90);

        for (var i = m1.Count - 1; i >= start; i--)
        {
            var candle = m1[i];

            if (!OverlapsZone(candle, zone, tolerance))
                continue;

            var referenceStart = Math.Max(0, i - 28);
            var reference = m1
                .Skip(referenceStart)
                .Take(i - referenceStart)
                .ToList();

            if (reference.Count < 10)
                continue;

            if (direction == "LONG")
            {
                var keyLow = reference.Min(x => x.Low);

                var sweptLow = candle.Low < keyLow;
                var closedBackInside = candle.Close > keyLow;

                if (!sweptLow || !closedBackInside)
                    continue;

                var wickBonus = candle.LowerWick >= Math.Max(candle.Body * 0.8, avgRange * 0.2)
                    ? 5
                    : 0;

                var age = m1.Count - 1 - i;

                var score =
                    17 +
                    wickBonus +
                    GetRecencyScore(age);

                return new LiquidityGrabInfo
                {
                    Candle = candle,
                    CandleIndex = i,
                    Level = keyLow,
                    AgeCandles = age,
                    Score = Math.Clamp(score, 14, 28)
                };
            }
            else
            {
                var keyHigh = reference.Max(x => x.High);

                var sweptHigh = candle.High > keyHigh;
                var closedBackInside = candle.Close < keyHigh;

                if (!sweptHigh || !closedBackInside)
                    continue;

                var wickBonus = candle.UpperWick >= Math.Max(candle.Body * 0.8, avgRange * 0.2)
                    ? 5
                    : 0;

                var age = m1.Count - 1 - i;

                var score =
                    17 +
                    wickBonus +
                    GetRecencyScore(age);

                return new LiquidityGrabInfo
                {
                    Candle = candle,
                    CandleIndex = i,
                    Level = keyHigh,
                    AgeCandles = age,
                    Score = Math.Clamp(score, 14, 28)
                };
            }
        }

        return null;
    }

    private static int GetRecencyScore(int ageCandles)
    {
        if (ageCandles <= 5)
            return 6;

        if (ageCandles <= 15)
            return 4;

        if (ageCandles <= 30)
            return 2;

        return 0;
    }

    private static bool HasChochAfterGrab(
        List<PriceCandle> m1,
        string direction,
        int grabIndex)
    {
        var referenceStart = Math.Max(0, grabIndex - 16);

        var reference = m1
            .Skip(referenceStart)
            .Take(grabIndex - referenceStart)
            .ToList();

        if (reference.Count < 6)
            return false;

        if (direction == "LONG")
        {
            var lastSwingHigh = reference.Max(x => x.High);

            for (var i = grabIndex + 1; i < m1.Count; i++)
            {
                if (m1[i].Close > lastSwingHigh)
                    return true;
            }

            return false;
        }

        var lastSwingLow = reference.Min(x => x.Low);

        for (var i = grabIndex + 1; i < m1.Count; i++)
        {
            if (m1[i].Close < lastSwingLow)
                return true;
        }

        return false;
    }

    private static (bool IsConfirmed, string Reason) HasEntryConfirmation(
        List<PriceCandle> m1,
        string direction,
        FvgZone zone,
        double avgRange)
    {
        var recent = m1.TakeLast(6).ToList();

        foreach (var candle in recent)
        {
            if (!OverlapsZone(candle, zone, avgRange * 0.5))
                continue;

            if (candle.Range <= 0)
                continue;

            var closePosition = (candle.Close - candle.Low) / candle.Range;

            if (direction == "LONG")
            {
                var bullishRejection =
                    candle.IsBullish &&
                    candle.LowerWick >= candle.Body * 0.70 &&
                    closePosition >= 0.55;

                var strongBullishClose =
                    candle.IsBullish &&
                    closePosition >= 0.65 &&
                    candle.Range >= avgRange * 0.55;

                if (bullishRejection || strongBullishClose)
                    return (true, "M1 bullish rejection/confirmation FVG daxilinde var.");
            }
            else
            {
                var bearishRejection =
                    candle.IsBearish &&
                    candle.UpperWick >= candle.Body * 0.70 &&
                    closePosition <= 0.45;

                var strongBearishClose =
                    candle.IsBearish &&
                    closePosition <= 0.35 &&
                    candle.Range >= avgRange * 0.55;

                if (bearishRejection || strongBearishClose)
                    return (true, "M1 bearish rejection/confirmation FVG daxilinde var.");
            }
        }

        return direction == "LONG"
            ? (false, "M1 bullish rejection/confirmation hele yoxdur.")
            : (false, "M1 bearish rejection/confirmation hele yoxdur.");
    }

    private static RiskPlan BuildRiskPlan(
        string symbol,
        string direction,
        PriceCandle lastM1,
        FvgZone zone,
        LiquidityGrabInfo grab,
        double avgM1Range)
    {
        var entry = (decimal)lastM1.Close;

        var bufferDouble = Math.Max(
            avgM1Range * 1.2,
            lastM1.Close * 0.00005);

        var buffer = (decimal)bufferDouble;

        decimal stopLoss;
        decimal takeProfit1;
        decimal takeProfit2;
        decimal risk;
        decimal invalidLevel;

        if (direction == "LONG")
        {
            var stopBase = Math.Min(grab.Candle.Low, zone.Low);

            stopLoss = (decimal)stopBase - buffer;
            invalidLevel = (decimal)stopBase;

            if (stopLoss >= entry)
                stopLoss = entry - Math.Abs(buffer);

            risk = entry - stopLoss;

            takeProfit1 = entry + risk * 2m;
            takeProfit2 = entry + risk * 3m;
        }
        else
        {
            var stopBase = Math.Max(grab.Candle.High, zone.High);

            stopLoss = (decimal)stopBase + buffer;
            invalidLevel = (decimal)stopBase;

            if (stopLoss <= entry)
                stopLoss = entry + Math.Abs(buffer);

            risk = stopLoss - entry;

            takeProfit1 = entry - risk * 2m;
            takeProfit2 = entry - risk * 3m;
        }

        var pipSize = GetPipSize(symbol);

        var riskPips = risk / pipSize;
        var rewardPips1 = Math.Abs(takeProfit1 - entry) / pipSize;
        var rewardPips2 = Math.Abs(takeProfit2 - entry) / pipSize;

        var riskReward1 = riskPips > 0
            ? rewardPips1 / riskPips
            : 0;

        var riskReward2 = riskPips > 0
            ? rewardPips2 / riskPips
            : 0;

        var isValid =
            risk > 0 &&
            riskPips >= 3m &&
            riskPips <= 150m &&
            riskReward1 >= 1.8m &&
            riskReward2 >= 2.7m;

        var invalidReason = string.Empty;

        if (!isValid)
        {
            invalidReason =
                $"Risk plan uygun deyil. RiskPips: {Math.Round(riskPips, 1)}, RR1: {Math.Round(riskReward1, 2)}, RR2: {Math.Round(riskReward2, 2)}";
        }

        return new RiskPlan
        {
            Entry = entry,
            StopLoss = stopLoss,
            TakeProfit1 = takeProfit1,
            TakeProfit2 = takeProfit2,
            RiskPips = riskPips,
            RewardPips1 = rewardPips1,
            RewardPips2 = rewardPips2,
            RiskReward1 = riskReward1,
            RiskReward2 = riskReward2,
            InvalidLevel = invalidLevel,
            IsValid = isValid,
            InvalidReason = invalidReason,
            Reason = "SL liquidity grab arxasinda, TP1 1:2, TP2 1:3 hesablandi."
        };
    }

    private static bool IsEntryStillNearZone(
        double price,
        FvgZone zone,
        double avgRange)
    {
        var distance = DistanceFromZone(price, zone);

        if (distance <= 0)
            return true;

        return distance <= avgRange * 3.5;
    }

    private static bool OverlapsZone(
        PriceCandle candle,
        FvgZone zone,
        double tolerance)
    {
        return candle.Low <= zone.High + tolerance &&
               candle.High >= zone.Low - tolerance;
    }

    private static double DistanceFromZone(
        double price,
        FvgZone zone)
    {
        if (price >= zone.Low && price <= zone.High)
            return 0;

        if (price < zone.Low)
            return zone.Low - price;

        return price - zone.High;
    }

    private static string DetectTrend(List<PriceCandle> candles)
    {
        var recent = candles.TakeLast(80).ToList();

        if (recent.Count < 30)
            return "RANGE";

        var swings = FindSwings(
            recent,
            2,
            2);

        var highs = swings
            .Where(x => x.Kind == "HIGH")
            .OrderBy(x => x.Index)
            .TakeLast(2)
            .ToList();

        var lows = swings
            .Where(x => x.Kind == "LOW")
            .OrderBy(x => x.Index)
            .TakeLast(2)
            .ToList();

        if (highs.Count >= 2 && lows.Count >= 2)
        {
            var higherHigh = highs[1].Price > highs[0].Price;
            var higherLow = lows[1].Price > lows[0].Price;

            var lowerHigh = highs[1].Price < highs[0].Price;
            var lowerLow = lows[1].Price < lows[0].Price;

            if (higherHigh && higherLow)
                return "BULLISH";

            if (lowerHigh && lowerLow)
                return "BEARISH";
        }

        var lastClose = recent[^1].Close;
        var firstClose = recent[0].Close;

        var fast = recent.TakeLast(8).Average(x => x.Close);
        var slow = recent.TakeLast(24).Average(x => x.Close);

        var avgRange = AverageRange(recent.TakeLast(30).ToList());

        if (lastClose > firstClose + avgRange * 1.2 && fast > slow)
            return "BULLISH";

        if (lastClose < firstClose - avgRange * 1.2 && fast < slow)
            return "BEARISH";

        var beforeLast = recent.Take(recent.Count - 1).ToList();
        var previousSwings = FindSwings(beforeLast, 2, 2);

        var previousHigh = previousSwings
            .Where(x => x.Kind == "HIGH")
            .OrderBy(x => x.Index)
            .LastOrDefault();

        var previousLow = previousSwings
            .Where(x => x.Kind == "LOW")
            .OrderBy(x => x.Index)
            .LastOrDefault();

        if (previousHigh != null && lastClose > previousHigh.Price)
            return "BULLISH";

        if (previousLow != null && lastClose < previousLow.Price)
            return "BEARISH";

        return "RANGE";
    }

    private static List<SwingPoint> FindSwings(
        List<PriceCandle> candles,
        int left,
        int right)
    {
        var swings = new List<SwingPoint>();

        if (candles.Count < left + right + 1)
            return swings;

        for (var i = left; i < candles.Count - right; i++)
        {
            var isHigh = true;
            var isLow = true;

            for (var j = i - left; j <= i + right; j++)
            {
                if (j == i)
                    continue;

                if (candles[i].High <= candles[j].High)
                    isHigh = false;

                if (candles[i].Low >= candles[j].Low)
                    isLow = false;
            }

            if (isHigh)
            {
                swings.Add(new SwingPoint
                {
                    Index = i,
                    TimeUtc = candles[i].TimeUtc,
                    Price = candles[i].High,
                    Kind = "HIGH"
                });
            }

            if (isLow)
            {
                swings.Add(new SwingPoint
                {
                    Index = i,
                    TimeUtc = candles[i].TimeUtc,
                    Price = candles[i].Low,
                    Kind = "LOW"
                });
            }
        }

        return swings;
    }

    private static bool IsTrendContextAligned(
        string direction,
        string trend)
    {
        if (direction == "LONG" && trend == "BULLISH")
            return true;

        if (direction == "SHORT" && trend == "BEARISH")
            return true;

        return false;
    }

    private static List<ForexStrategyResult> BuildStrategyResults(
        DirectionAnalysis longAnalysis,
        DirectionAnalysis shortAnalysis,
        string m15Trend)
    {
        return new List<ForexStrategyResult>
        {
            new ForexStrategyResult
            {
                StrategyName = "Strategy2_TrendFvgLiquidityGrab",
                Direction = "LONG",
                Score = longAnalysis.Confidence,
                MaxScore = 100,
                IsConfirmed = longAnalysis.TradeReady && longAnalysis.Confidence >= MinimumConfidence,
                Reasons = longAnalysis.Reasons
            },
            new ForexStrategyResult
            {
                StrategyName = "Strategy2_TrendFvgLiquidityGrab",
                Direction = "SHORT",
                Score = shortAnalysis.Confidence,
                MaxScore = 100,
                IsConfirmed = shortAnalysis.TradeReady && shortAnalysis.Confidence >= MinimumConfidence,
                Reasons = shortAnalysis.Reasons
            },
            new ForexStrategyResult
            {
                StrategyName = "M15TrendContext",
                Direction = m15Trend,
                Score = m15Trend == "RANGE" ? 6 : 20,
                MaxScore = 20,
                IsConfirmed = m15Trend != "RANGE",
                Reasons = new List<string>
                {
                    $"M15 trend: {m15Trend}"
                }
            }
        };
    }

    private static ForexTradeSignal Wait(
        string symbol,
        int confidence,
        string grade,
        List<string> reasons,
        List<ForexStrategyResult> strategyResults)
    {
        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            Confidence = Math.Clamp(confidence, 0, 100),
            Grade = grade,
            Message = $"{symbol} FOREX WAIT {Math.Clamp(confidence, 0, 100)}%",
            Reasons = reasons.Distinct().ToList(),
            StrategyResults = strategyResults,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static int GetValidForMinutes(int confidence)
    {
        if (confidence >= 90)
            return 15;

        if (confidence >= 80)
            return 10;

        return 7;
    }

    private static string GetGrade(int confidence)
    {
        if (confidence >= 90)
            return "A+";

        if (confidence >= 82)
            return "A";

        if (confidence >= 72)
            return "B";

        return "NO_TRADE";
    }

    private static List<PriceCandle> MapCandles(TwelveDataResponse? response)
    {
        if (response?.Values == null)
            return new List<PriceCandle>();

        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd"
        };

        var candles = new List<PriceCandle>();

        foreach (var item in response.Values)
        {
            if (!DateTime.TryParseExact(
                    item.DateTime,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var time))
            {
                continue;
            }

            candles.Add(new PriceCandle
            {
                TimeUtc = time,
                Open = (double)item.Open,
                High = (double)item.High,
                Low = (double)item.Low,
                Close = (double)item.Close,
                Volume = 0
            });
        }

        return candles
            .OrderBy(x => x.TimeUtc)
            .ToList();
    }

    private static decimal RoundPrice(string symbol, decimal price)
    {
        var digits = GetDigits(symbol);
        return Math.Round(price, digits);
    }

    private static decimal RoundPrice(string symbol, double price)
    {
        var digits = GetDigits(symbol);
        return Math.Round((decimal)price, digits);
    }

    private static string FormatPrice(double price)
    {
        return price.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static int GetDigits(string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("JPY"))
            return 3;

        if (symbol.Contains("XAU"))
            return 2;

        if (symbol.Contains("BTC") || symbol.Contains("ETH"))
            return 2;

        if (symbol.Contains("USOIL"))
            return 2;

        return 5;
    }

    private static decimal GetPipSize(string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("JPY"))
            return 0.01m;

        if (symbol.Contains("XAU"))
            return 0.10m;

        if (symbol.Contains("BTC"))
            return 1m;

        if (symbol.Contains("ETH"))
            return 0.10m;

        if (symbol.Contains("USOIL"))
            return 0.01m;

        return 0.0001m;
    }

    private static double AverageRange(List<PriceCandle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.Range);
    }

    private sealed class DirectionAnalysis
    {
        public string Direction { get; set; } = string.Empty;

        public int Confidence { get; set; }

        public bool TradeReady { get; set; }

        public bool IsTrendAligned { get; set; }

        public bool HasFvg { get; set; }

        public bool HasZoneRetest { get; set; }

        public bool HasLiquidityGrab { get; set; }

        public bool HasChoch { get; set; }

        public bool HasEntryConfirmation { get; set; }

        public bool IsEntryStillValid { get; set; }

        public bool IsRiskPlanValid { get; set; }

        public double ZoneLow { get; set; }

        public double ZoneHigh { get; set; }

        public decimal EntryPrice { get; set; }

        public decimal StopLoss { get; set; }

        public decimal TakeProfit1 { get; set; }

        public decimal TakeProfit2 { get; set; }

        public decimal RiskPips { get; set; }

        public decimal RewardPips1 { get; set; }

        public decimal RewardPips2 { get; set; }

        public decimal RiskReward1 { get; set; }

        public decimal RiskReward2 { get; set; }

        public decimal InvalidLevel { get; set; }

        public string InvalidReason { get; set; } = string.Empty;

        public string RiskReason { get; set; } = string.Empty;

        public List<string> Reasons { get; set; } = new();

        public string DebugSummary =>
            $"Trend={IsTrendAligned}, FVG={HasFvg}, Retest={HasZoneRetest}, Grab={HasLiquidityGrab}, CHoCH={HasChoch}, Confirm={HasEntryConfirmation}, EntryValid={IsEntryStillValid}, Risk={IsRiskPlanValid}, Ready={TradeReady}";
    }

    private sealed class FvgZone
    {
        public string Direction { get; set; } = string.Empty;

        public double Low { get; set; }

        public double High { get; set; }

        public int CreatedIndex { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public int AgeCandles { get; set; }

        public int Score { get; set; }
    }

    private sealed class LiquidityGrabInfo
    {
        public PriceCandle Candle { get; set; } = new();

        public int CandleIndex { get; set; }

        public double Level { get; set; }

        public int AgeCandles { get; set; }

        public int Score { get; set; }
    }

    private sealed class RetestInfo
    {
        public int CandleIndex { get; set; }

        public int AgeCandles { get; set; }

        public int Score { get; set; }
    }

    private sealed class RiskPlan
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

        public decimal InvalidLevel { get; set; }

        public bool IsValid { get; set; }

        public string InvalidReason { get; set; } = string.Empty;

        public string Reason { get; set; } = string.Empty;
    }

    private sealed class SwingPoint
    {
        public int Index { get; set; }

        public DateTime TimeUtc { get; set; }

        public double Price { get; set; }

        public string Kind { get; set; } = string.Empty;
    }
}
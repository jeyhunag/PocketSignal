using System.Globalization;
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Forex;

public class XauUsdScalpingSignalService : IForexSignalService
{
    private const int MinimumConfidence = 78;
    private const int ConflictDistance = 10;

    private readonly IMarketDataService _marketDataService;

    public XauUsdScalpingSignalService(
        IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<ForexTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        symbol = "XAU/USD";

        var m30Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "30min",
            180,
            cancellationToken);

        var m15Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "15min",
            220,
            cancellationToken);

        var m5Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "5min",
            260,
            cancellationToken);

        var m1Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            360,
            cancellationToken);

        var m30 = MapCandles(m30Response);
        var m15 = MapCandles(m15Response);
        var m5 = MapCandles(m5Response);
        var m1 = MapCandles(m1Response);

        if (m30.Count < 50 || m15.Count < 50 || m5.Count < 80 || m1.Count < 100)
        {
            return Wait(
                symbol,
                0,
                "NO_TRADE",
                new List<string>
                {
                    "XAU/USD strategy ucun kifayet qeder M30/M15/M5/M1 candle yoxdur."
                },
                BuildStrategyResults(null, null));
        }

        var longSetup = AnalyzeDirection(
            symbol,
            "LONG",
            m30,
            m15,
            m5,
            m1);

        var shortSetup = AnalyzeDirection(
            symbol,
            "SHORT",
            m30,
            m15,
            m5,
            m1);

        Console.WriteLine(
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] XAU/USD Gold Scalping | " +
            $"LONG {longSetup.Confidence}% [{longSetup.DebugSummary}] | " +
            $"SHORT {shortSetup.Confidence}% [{shortSetup.DebugSummary}] | " +
            $"Best={(longSetup.Confidence >= shortSetup.Confidence ? "LONG" : "SHORT")}");

        var best = longSetup.Confidence >= shortSetup.Confidence
            ? longSetup
            : shortSetup;

        var opposite = best.Direction == "LONG"
            ? shortSetup
            : longSetup;

        var strategyResults = BuildStrategyResults(
            longSetup,
            shortSetup);

        if (!best.TradeReady)
        {
            return Wait(
                symbol,
                best.Confidence,
                best.Confidence >= MinimumConfidence ? "WATCHLIST" : "NO_TRADE",
                new List<string>
                {
                    $"XAU/USD setup hele tam hazir deyil. Best: {best.Direction} {best.Confidence}%.",
                    $"LONG score: {longSetup.Confidence}%",
                    $"SHORT score: {shortSetup.Confidence}%"
                }
                .Concat(best.Reasons)
                .Distinct()
                .ToList(),
                strategyResults);
        }

        if (opposite.TradeReady &&
            Math.Abs(best.Confidence - opposite.Confidence) < ConflictDistance)
        {
            return Wait(
                symbol,
                Math.Max(best.Confidence, opposite.Confidence),
                "NO_TRADE",
                new List<string>
                {
                    $"LONG score: {longSetup.Confidence}%",
                    $"SHORT score: {shortSetup.Confidence}%",
                    "XAU/USD LONG ve SHORT setup-lari yaxindir. Direction temiz deyil."
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
                    $"{best.Direction} XAU/USD setup var, amma confidence minimum seviyeye catmadi.",
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
            $"XAU/USD Gold Scalping {best.Direction} signal tesdiqlendi.",
            "HTF supply/demand, order block/FVG ve liquidity modeli esas goturuldu.",
            "Price HTF zone-a return/retest etdi.",
            best.HasIfvg
                ? "M1/M5 IFVG confirmation tapildi."
                : "M1/M5 rejection/MSS confirmation tapildi.",
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
                ? $"M5 candle {RoundPrice(symbol, best.InvalidLevel)} altinda baglansa trade legvdir."
                : $"M5 candle {RoundPrice(symbol, best.InvalidLevel)} ustunde baglansa trade legvdir.",

            ValidForMinutes = GetValidForMinutes(best.Confidence),

            Reasons = reasons.Distinct().ToList(),

            SideAnalyses = new List<SideAnalysis>
            {
                new SideAnalysis
                {
                    Direction = "LONG",
                    Score = longSetup.Confidence,
                    Reasons = longSetup.Reasons
                },
                new SideAnalysis
                {
                    Direction = "SHORT",
                    Score = shortSetup.Confidence,
                    Reasons = shortSetup.Reasons
                }
            },

            StrategyResults = strategyResults,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static XauSetupAnalysis AnalyzeDirection(
        string symbol,
        string direction,
        List<PriceCandle> m30,
        List<PriceCandle> m15,
        List<PriceCandle> m5,
        List<PriceCandle> m1)
    {
        var reasons = new List<string>();

        var analysis = new XauSetupAnalysis
        {
            Direction = direction,
            EntryPrice = (decimal)m1[^1].Close
        };

        var avgM30Range = AverageRange(m30.TakeLast(40).ToList());
        var avgM15Range = AverageRange(m15.TakeLast(40).ToList());
        var avgM5Range = AverageRange(m5.TakeLast(40).ToList());
        var avgM1Range = AverageRange(m1.TakeLast(40).ToList());

        if (avgM30Range <= 0 || avgM15Range <= 0 || avgM5Range <= 0 || avgM1Range <= 0)
        {
            analysis.Reasons.Add("Average range hesablanmadi, data duzgun deyil.");
            return analysis;
        }

        var htfProgress = ScoreHtfProgress(
            direction,
            m30,
            m15,
            avgM30Range,
            avgM15Range);

        analysis.Confidence += htfProgress.Score;
        reasons.AddRange(htfProgress.Reasons);

        var htfModel = FindBestHtfModel(
            direction,
            m30,
            m15,
            avgM30Range,
            avgM15Range);

        if (htfModel == null)
        {
            reasons.Add(direction == "LONG"
                ? "HTF bullish model hele tam deyil: M30/M15 sweep+BOS/order block ve ya supply-demand/FVG setup tamamlanmayib."
                : "HTF bearish model hele tam deyil: M30/M15 sweep+BOS/order block ve ya supply-demand/FVG setup tamamlanmayib.");

            analysis.Reasons = reasons.Distinct().ToList();
            analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);

            return analysis;
        }

        analysis.HasHtfSweepBos = true;
        analysis.ZoneLow = htfModel.ZoneLow;
        analysis.ZoneHigh = htfModel.ZoneHigh;

        analysis.Confidence += htfModel.Score;

        reasons.Add(direction == "LONG"
            ? $"{htfModel.Timeframe} bullish HTF model tapildi: {htfModel.ModelName}. Demand/zone: {FormatPrice(htfModel.ZoneLow)} - {FormatPrice(htfModel.ZoneHigh)}."
            : $"{htfModel.Timeframe} bearish HTF model tapildi: {htfModel.ModelName}. Supply/zone: {FormatPrice(htfModel.ZoneLow)} - {FormatPrice(htfModel.ZoneHigh)}.");

        var retest = FindRecentZoneRetest(
            m5,
            htfModel.ZoneLow,
            htfModel.ZoneHigh,
            avgM5Range);

        if (retest == null)
        {
            reasons.Add("Price HTF order block/supply-demand/FVG zone-a hele qayitmayib.");

            analysis.Reasons = reasons.Distinct().ToList();
            analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);

            return analysis;
        }

        analysis.HasZoneRetest = true;
        analysis.Confidence += retest.Score;

        reasons.Add($"Price HTF zone-a retest etdi. Retest age: {retest.AgeCandles} M5 candle.");

        var mss = HasMarketStructureShiftAfterRetest(
            m5,
            direction,
            retest.CandleIndex);

        if (mss)
        {
            analysis.HasMss = true;
            analysis.Confidence += 14;
            reasons.Add("M5 market structure shift / CHoCH confirmation var.");
        }
        else
        {
            reasons.Add("M5 market structure shift hele tam yoxdur.");
        }

        var ifvg = FindInverseFvgConfirmation(
            m1,
            direction,
            htfModel.ZoneLow,
            htfModel.ZoneHigh,
            avgM1Range);

        if (ifvg != null)
        {
            analysis.HasIfvg = true;
            analysis.Confidence += ifvg.Score;
            reasons.Add(direction == "LONG"
                ? $"M1 bullish IFVG confirmation var: {FormatPrice(ifvg.Low)} - {FormatPrice(ifvg.High)}."
                : $"M1 bearish IFVG confirmation var: {FormatPrice(ifvg.Low)} - {FormatPrice(ifvg.High)}.");
        }
        else
        {
            reasons.Add("M1 IFVG confirmation hele yoxdur.");
        }

        var rejection = HasRejectionFromZone(
            m1,
            direction,
            htfModel.ZoneLow,
            htfModel.ZoneHigh,
            avgM1Range);

        if (rejection.IsConfirmed)
        {
            analysis.HasRejection = true;
            analysis.Confidence += 12;
            reasons.Add(rejection.Reason);
        }
        else
        {
            reasons.Add(rejection.Reason);
        }

        var entryStillNearZone = IsPriceNearZone(
            m1[^1].Close,
            htfModel.ZoneLow,
            htfModel.ZoneHigh,
            avgM1Range);

        if (entryStillNearZone)
        {
            analysis.IsEntryStillValid = true;
            analysis.Confidence += 6;
            reasons.Add("Entry gecikmis deyil, price HTF zone/reversal zonasina yaxindir.");
        }
        else
        {
            reasons.Add("Entry gecikmis ola biler, price HTF zone-dan uzaqlasib.");
        }

        var riskPlan = BuildRiskPlan(
            symbol,
            direction,
            m1[^1],
            htfModel,
            avgM1Range,
            avgM5Range);

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
            analysis.Confidence += 8;
            reasons.Add(riskPlan.Reason);
        }
        else
        {
            reasons.Add(riskPlan.InvalidReason);
        }

        if (retest.AgeCandles > 20)
        {
            analysis.Confidence -= 6;
            reasons.Add("Retest bir az kohneleib, scalping ucun risk artdi.");
        }

        analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);

        analysis.TradeReady =
            analysis.HasHtfSweepBos &&
            analysis.HasZoneRetest &&
            analysis.IsEntryStillValid &&
            analysis.IsRiskPlanValid &&
            (analysis.HasIfvg || analysis.HasMss || analysis.HasRejection) &&
            retest.AgeCandles <= 35;

        if (!analysis.TradeReady)
        {
            if (!analysis.IsEntryStillValid)
                reasons.Add("No trade: entry gecikib.");

            if (!analysis.IsRiskPlanValid)
                reasons.Add("No trade: risk plan uygun deyil.");

            if (!analysis.HasIfvg && !analysis.HasMss && !analysis.HasRejection)
                reasons.Add("No trade: LTF confirmation yoxdur.");

            if (retest.AgeCandles > 35)
                reasons.Add("No trade: zone retest cox kohneleib.");
        }

        analysis.Reasons = reasons.Distinct().ToList();

        return analysis;
    }

    private static HtfModel? FindBestHtfModel(
        string direction,
        List<PriceCandle> m30,
        List<PriceCandle> m15,
        double avgM30Range,
        double avgM15Range)
    {
        var models = new List<HtfModel>();

        var m30SweepBos = FindHtfSweepBosOrderBlock(
            m30,
            "M30",
            direction,
            avgM30Range);

        if (m30SweepBos != null)
            models.Add(m30SweepBos);

        var m15SweepBos = FindHtfSweepBosOrderBlock(
            m15,
            "M15",
            direction,
            avgM15Range);

        if (m15SweepBos != null)
            models.Add(m15SweepBos);

        var m30SupplyDemand = FindSupplyDemandBosModel(
            m30,
            "M30",
            direction,
            avgM30Range);

        if (m30SupplyDemand != null)
            models.Add(m30SupplyDemand);

        var m15SupplyDemand = FindSupplyDemandBosModel(
            m15,
            "M15",
            direction,
            avgM15Range);

        if (m15SupplyDemand != null)
            models.Add(m15SupplyDemand);

        var m30ChochFvg = FindChochFvgModel(
            m30,
            "M30",
            direction,
            avgM30Range);

        if (m30ChochFvg != null)
            models.Add(m30ChochFvg);

        var m15ChochFvg = FindChochFvgModel(
            m15,
            "M15",
            direction,
            avgM15Range);

        if (m15ChochFvg != null)
            models.Add(m15ChochFvg);

        return models
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.BosIndex)
            .FirstOrDefault();
    }

    private static (int Score, List<string> Reasons) ScoreHtfProgress(
        string direction,
        List<PriceCandle> m30,
        List<PriceCandle> m15,
        double avgM30Range,
        double avgM15Range)
    {
        var score = 0;
        var reasons = new List<string>();

        var m30Trend = DetectSimpleTrend(m30);
        var m15Trend = DetectSimpleTrend(m15);

        if (direction == "LONG" && m30Trend == "BULLISH")
        {
            score += 8;
            reasons.Add("M30 trend bullishdir, LONG ehtimali izlenir.");
        }
        else if (direction == "SHORT" && m30Trend == "BEARISH")
        {
            score += 8;
            reasons.Add("M30 trend bearishdir, SHORT ehtimali izlenir.");
        }
        else
        {
            reasons.Add($"M30 trend {direction} ucun tam uygun deyil. Trend: {m30Trend}.");
        }

        if (direction == "LONG" && m15Trend == "BULLISH")
        {
            score += 8;
            reasons.Add("M15 trend bullishdir, LONG ehtimali izlenir.");
        }
        else if (direction == "SHORT" && m15Trend == "BEARISH")
        {
            score += 8;
            reasons.Add("M15 trend bearishdir, SHORT ehtimali izlenir.");
        }
        else
        {
            reasons.Add($"M15 trend {direction} ucun tam uygun deyil. Trend: {m15Trend}.");
        }

        if (HasRecentLiquiditySweep(m30, direction, avgM30Range))
        {
            score += 12;
            reasons.Add(direction == "LONG"
                ? "M30-da bullish liquidity sweep elameti var."
                : "M30-da bearish liquidity sweep elameti var.");
        }

        if (HasRecentLiquiditySweep(m15, direction, avgM15Range))
        {
            score += 12;
            reasons.Add(direction == "LONG"
                ? "M15-da bullish liquidity sweep elameti var."
                : "M15-da bearish liquidity sweep elameti var.");
        }

        if (HasAnyFvg(m30, direction, avgM30Range))
        {
            score += 7;
            reasons.Add(direction == "LONG"
                ? "M30 bullish FVG zonasi var."
                : "M30 bearish FVG zonasi var.");
        }

        if (HasAnyFvg(m15, direction, avgM15Range))
        {
            score += 7;
            reasons.Add(direction == "LONG"
                ? "M15 bullish FVG zonasi var."
                : "M15 bearish FVG zonasi var.");
        }

        score = Math.Clamp(score, 0, 55);

        if (score == 0)
        {
            reasons.Add(direction == "LONG"
                ? "HTF LONG ucun ilkin elamet yoxdur."
                : "HTF SHORT ucun ilkin elamet yoxdur.");
        }

        return (score, reasons);
    }

    private static HtfModel? FindHtfSweepBosOrderBlock(
        List<PriceCandle> candles,
        string timeframe,
        string direction,
        double avgRange)
    {
        var start = Math.Max(30, candles.Count - 120);

        for (var i = candles.Count - 12; i >= start; i--)
        {
            var referenceStart = Math.Max(0, i - 24);

            var reference = candles
                .Skip(referenceStart)
                .Take(i - referenceStart)
                .ToList();

            if (reference.Count < 12)
                continue;

            var sweepCandle = candles[i];

            if (direction == "LONG")
            {
                var keyLow = reference.Min(x => x.Low);
                var keyHigh = reference.Max(x => x.High);

                var sweptLow =
                    sweepCandle.Low < keyLow &&
                    sweepCandle.Close > keyLow;

                if (!sweptLow)
                    continue;

                var bosIndex = FindBosIndex(
                    candles,
                    i + 1,
                    Math.Min(candles.Count - 1, i + 12),
                    "LONG",
                    keyHigh);

                if (bosIndex < 0)
                    continue;

                var orderBlock = FindOrderBlock(
                    candles,
                    i,
                    bosIndex,
                    "LONG");

                if (orderBlock == null)
                    continue;

                return new HtfModel
                {
                    Direction = direction,
                    Timeframe = timeframe,
                    ModelName = "Strategy1 Sweep + BOS + OrderBlock",
                    SweepIndex = i,
                    BosIndex = bosIndex,
                    ZoneLow = orderBlock.Low,
                    ZoneHigh = orderBlock.High,
                    SweepPrice = sweepCandle.Low,
                    BosLevel = keyHigh,
                    Score = CalculateHtfScore(sweepCandle, orderBlock, avgRange, i, bosIndex)
                };
            }
            else
            {
                var keyHigh = reference.Max(x => x.High);
                var keyLow = reference.Min(x => x.Low);

                var sweptHigh =
                    sweepCandle.High > keyHigh &&
                    sweepCandle.Close < keyHigh;

                if (!sweptHigh)
                    continue;

                var bosIndex = FindBosIndex(
                    candles,
                    i + 1,
                    Math.Min(candles.Count - 1, i + 12),
                    "SHORT",
                    keyLow);

                if (bosIndex < 0)
                    continue;

                var orderBlock = FindOrderBlock(
                    candles,
                    i,
                    bosIndex,
                    "SHORT");

                if (orderBlock == null)
                    continue;

                return new HtfModel
                {
                    Direction = direction,
                    Timeframe = timeframe,
                    ModelName = "Strategy1 Sweep + BOS + OrderBlock",
                    SweepIndex = i,
                    BosIndex = bosIndex,
                    ZoneLow = orderBlock.Low,
                    ZoneHigh = orderBlock.High,
                    SweepPrice = sweepCandle.High,
                    BosLevel = keyLow,
                    Score = CalculateHtfScore(sweepCandle, orderBlock, avgRange, i, bosIndex)
                };
            }
        }

        return null;
    }

    private static HtfModel? FindSupplyDemandBosModel(
        List<PriceCandle> candles,
        string timeframe,
        string direction,
        double avgRange)
    {
        var recent = candles.TakeLast(90).ToList();

        if (recent.Count < 40)
            return null;

        var swings = FindSwingPoints(
            recent,
            2,
            2);

        if (swings.Count < 4)
            return null;

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

        if (highs.Count < 2 || lows.Count < 2)
            return null;

        if (direction == "LONG")
        {
            var bullishStructure =
                highs[1].Price > highs[0].Price &&
                lows[1].Price > lows[0].Price;

            if (!bullishStructure)
                return null;

            var bosLevel = highs[0].Price;
            var bosIndex = recent.FindLastIndex(x => x.Close > bosLevel);

            if (bosIndex < 10)
                return null;

            var orderBlock = FindOrderBlock(
                recent,
                Math.Max(0, bosIndex - 10),
                bosIndex,
                "LONG");

            if (orderBlock == null)
                return null;

            if (IsZoneMitigatedAfter(recent, orderBlock.Index, orderBlock.Low, orderBlock.High, "LONG"))
                return null;

            return new HtfModel
            {
                Direction = direction,
                Timeframe = timeframe,
                ModelName = "Strategy2 Trend + Unmitigated Demand",
                SweepIndex = orderBlock.Index,
                BosIndex = bosIndex,
                ZoneLow = orderBlock.Low,
                ZoneHigh = orderBlock.High,
                SweepPrice = orderBlock.Low,
                BosLevel = bosLevel,
                Score = 34
            };
        }
        else
        {
            var bearishStructure =
                highs[1].Price < highs[0].Price &&
                lows[1].Price < lows[0].Price;

            if (!bearishStructure)
                return null;

            var bosLevel = lows[0].Price;
            var bosIndex = recent.FindLastIndex(x => x.Close < bosLevel);

            if (bosIndex < 10)
                return null;

            var orderBlock = FindOrderBlock(
                recent,
                Math.Max(0, bosIndex - 10),
                bosIndex,
                "SHORT");

            if (orderBlock == null)
                return null;

            if (IsZoneMitigatedAfter(recent, orderBlock.Index, orderBlock.Low, orderBlock.High, "SHORT"))
                return null;

            return new HtfModel
            {
                Direction = direction,
                Timeframe = timeframe,
                ModelName = "Strategy2 Trend + Unmitigated Supply",
                SweepIndex = orderBlock.Index,
                BosIndex = bosIndex,
                ZoneLow = orderBlock.Low,
                ZoneHigh = orderBlock.High,
                SweepPrice = orderBlock.High,
                BosLevel = bosLevel,
                Score = 34
            };
        }
    }

    private static HtfModel? FindChochFvgModel(
        List<PriceCandle> candles,
        string timeframe,
        string direction,
        double avgRange)
    {
        var recent = candles.TakeLast(100).ToList();

        if (recent.Count < 50)
            return null;

        var before = recent.Take(recent.Count - 10).ToList();
        var previousTrend = DetectSimpleTrend(before);

        if (direction == "LONG" && previousTrend != "BEARISH")
            return null;

        if (direction == "SHORT" && previousTrend != "BULLISH")
            return null;

        var swings = FindSwingPoints(
            before,
            2,
            2);

        if (swings.Count < 4)
            return null;

        if (direction == "LONG")
        {
            var lastHigh = swings
                .Where(x => x.Kind == "HIGH")
                .OrderBy(x => x.Index)
                .LastOrDefault();

            if (lastHigh == null)
                return null;

            var chochIndex = recent.FindLastIndex(x => x.Close > lastHigh.Price);

            if (chochIndex < 5)
                return null;

            var fvg = FindFvgAroundIndex(
                recent,
                direction,
                Math.Max(2, chochIndex - 6),
                Math.Min(recent.Count - 1, chochIndex + 3),
                avgRange);

            if (fvg == null)
                return null;

            return new HtfModel
            {
                Direction = direction,
                Timeframe = timeframe,
                ModelName = "Strategy3 HTF CHoCH + FVG",
                SweepIndex = chochIndex,
                BosIndex = chochIndex,
                ZoneLow = fvg.Low,
                ZoneHigh = fvg.High,
                SweepPrice = fvg.Low,
                BosLevel = lastHigh.Price,
                Score = 36
            };
        }
        else
        {
            var lastLow = swings
                .Where(x => x.Kind == "LOW")
                .OrderBy(x => x.Index)
                .LastOrDefault();

            if (lastLow == null)
                return null;

            var chochIndex = recent.FindLastIndex(x => x.Close < lastLow.Price);

            if (chochIndex < 5)
                return null;

            var fvg = FindFvgAroundIndex(
                recent,
                direction,
                Math.Max(2, chochIndex - 6),
                Math.Min(recent.Count - 1, chochIndex + 3),
                avgRange);

            if (fvg == null)
                return null;

            return new HtfModel
            {
                Direction = direction,
                Timeframe = timeframe,
                ModelName = "Strategy3 HTF CHoCH + FVG",
                SweepIndex = chochIndex,
                BosIndex = chochIndex,
                ZoneLow = fvg.Low,
                ZoneHigh = fvg.High,
                SweepPrice = fvg.High,
                BosLevel = lastLow.Price,
                Score = 36
            };
        }
    }

    private static bool HasRecentLiquiditySweep(
        List<PriceCandle> candles,
        string direction,
        double avgRange)
    {
        var recent = candles.TakeLast(50).ToList();

        if (recent.Count < 25)
            return false;

        var reference = recent.Take(recent.Count - 5).ToList();
        var last5 = recent.TakeLast(5).ToList();

        if (direction == "LONG")
        {
            var keyLow = reference.Min(x => x.Low);

            return last5.Any(x =>
                x.Low < keyLow &&
                x.Close > keyLow &&
                x.LowerWick >= avgRange * 0.15);
        }

        var keyHigh = reference.Max(x => x.High);

        return last5.Any(x =>
            x.High > keyHigh &&
            x.Close < keyHigh &&
            x.UpperWick >= avgRange * 0.15);
    }

    private static bool HasAnyFvg(
        List<PriceCandle> candles,
        string direction,
        double avgRange)
    {
        var recent = candles.TakeLast(70).ToList();

        for (var i = 2; i < recent.Count; i++)
        {
            var c1 = recent[i - 2];
            var c3 = recent[i];

            if (direction == "LONG")
            {
                var size = c3.Low - c1.High;

                if (c1.High < c3.Low && size >= avgRange * 0.04)
                    return true;
            }
            else
            {
                var size = c1.Low - c3.High;

                if (c1.Low > c3.High && size >= avgRange * 0.04)
                    return true;
            }
        }

        return false;
    }

    private static SimpleFvg? FindFvgAroundIndex(
        List<PriceCandle> candles,
        string direction,
        int start,
        int end,
        double avgRange)
    {
        for (var i = Math.Max(2, start); i <= end; i++)
        {
            var c1 = candles[i - 2];
            var c3 = candles[i];

            if (direction == "LONG")
            {
                var size = c3.Low - c1.High;

                if (c1.High < c3.Low && size >= avgRange * 0.04)
                {
                    return new SimpleFvg
                    {
                        Low = c1.High,
                        High = c3.Low
                    };
                }
            }
            else
            {
                var size = c1.Low - c3.High;

                if (c1.Low > c3.High && size >= avgRange * 0.04)
                {
                    return new SimpleFvg
                    {
                        Low = c3.High,
                        High = c1.Low
                    };
                }
            }
        }

        return null;
    }

    private static bool IsZoneMitigatedAfter(
        List<PriceCandle> candles,
        int index,
        double low,
        double high,
        string direction)
    {
        for (var i = index + 1; i < candles.Count - 1; i++)
        {
            if (direction == "LONG" && candles[i].Low <= low)
                return true;

            if (direction == "SHORT" && candles[i].High >= high)
                return true;
        }

        return false;
    }

    private static string DetectSimpleTrend(List<PriceCandle> candles)
    {
        var recent = candles.TakeLast(40).ToList();

        if (recent.Count < 25)
            return "RANGE";

        var first = recent.First().Close;
        var last = recent.Last().Close;

        var fast = recent.TakeLast(8).Average(x => x.Close);
        var slow = recent.TakeLast(21).Average(x => x.Close);
        var avgRange = AverageRange(recent.TakeLast(25).ToList());

        if (last > first + avgRange * 1.2 && fast > slow)
            return "BULLISH";

        if (last < first - avgRange * 1.2 && fast < slow)
            return "BEARISH";

        return "RANGE";
    }

    private static List<SwingPoint> FindSwingPoints(
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
                    Price = candles[i].High,
                    Kind = "HIGH"
                });
            }

            if (isLow)
            {
                swings.Add(new SwingPoint
                {
                    Index = i,
                    Price = candles[i].Low,
                    Kind = "LOW"
                });
            }
        }

        return swings;
    }

    private static int FindBosIndex(
        List<PriceCandle> candles,
        int start,
        int end,
        string direction,
        double level)
    {
        for (var i = start; i <= end; i++)
        {
            if (direction == "LONG" && candles[i].Close > level)
                return i;

            if (direction == "SHORT" && candles[i].Close < level)
                return i;
        }

        return -1;
    }

    private static HtfOrderBlock? FindOrderBlock(
        List<PriceCandle> candles,
        int sweepIndex,
        int bosIndex,
        string direction)
    {
        if (direction == "LONG")
        {
            for (var i = bosIndex - 1; i >= Math.Max(0, sweepIndex - 4); i--)
            {
                if (candles[i].IsBearish)
                {
                    return new HtfOrderBlock
                    {
                        Low = candles[i].Low,
                        High = candles[i].High,
                        Index = i
                    };
                }
            }
        }
        else
        {
            for (var i = bosIndex - 1; i >= Math.Max(0, sweepIndex - 4); i--)
            {
                if (candles[i].IsBullish)
                {
                    return new HtfOrderBlock
                    {
                        Low = candles[i].Low,
                        High = candles[i].High,
                        Index = i
                    };
                }
            }
        }

        return null;
    }

    private static int CalculateHtfScore(
        PriceCandle sweepCandle,
        HtfOrderBlock orderBlock,
        double avgRange,
        int sweepIndex,
        int bosIndex)
    {
        var score = 32;

        if (sweepCandle.Range >= avgRange * 1.1)
            score += 4;

        if ((orderBlock.High - orderBlock.Low) <= avgRange * 1.8)
            score += 4;

        var bosSpeed = bosIndex - sweepIndex;

        if (bosSpeed <= 4)
            score += 5;
        else if (bosSpeed <= 8)
            score += 3;

        return Math.Clamp(score, 25, 45);
    }

    private static RetestInfo? FindRecentZoneRetest(
        List<PriceCandle> candles,
        double zoneLow,
        double zoneHigh,
        double avgRange)
    {
        var tolerance = Math.Max(avgRange * 0.35, 0.20);
        var start = Math.Max(0, candles.Count - 70);

        for (var i = candles.Count - 1; i >= start; i--)
        {
            if (!OverlapsZone(candles[i], zoneLow, zoneHigh, tolerance))
                continue;

            var age = candles.Count - 1 - i;

            var score = age switch
            {
                <= 4 => 18,
                <= 12 => 15,
                <= 25 => 11,
                _ => 7
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

    private static bool HasMarketStructureShiftAfterRetest(
        List<PriceCandle> candles,
        string direction,
        int retestIndex)
    {
        var refStart = Math.Max(0, retestIndex - 12);

        var reference = candles
            .Skip(refStart)
            .Take(retestIndex - refStart)
            .ToList();

        if (reference.Count < 5)
            return false;

        if (direction == "LONG")
        {
            var keyHigh = reference.Max(x => x.High);

            return candles
                .Skip(retestIndex + 1)
                .Any(x => x.Close > keyHigh);
        }

        var keyLow = reference.Min(x => x.Low);

        return candles
            .Skip(retestIndex + 1)
            .Any(x => x.Close < keyLow);
    }

    private static IfvgZone? FindInverseFvgConfirmation(
        List<PriceCandle> candles,
        string direction,
        double htfZoneLow,
        double htfZoneHigh,
        double avgRange)
    {
        var start = Math.Max(2, candles.Count - 70);
        var zones = new List<IfvgZone>();

        for (var i = start; i < candles.Count - 2; i++)
        {
            var c1 = candles[i - 2];
            var c3 = candles[i];

            if (direction == "LONG")
            {
                var hasBearishFvg = c1.Low > c3.High;

                if (!hasBearishFvg)
                    continue;

                var zoneLow = c3.High;
                var zoneHigh = c1.Low;

                var violatedUp = candles
                    .Skip(i + 1)
                    .Any(x => x.Close > zoneHigh);

                if (!violatedUp)
                    continue;

                var recentTouch = candles
                    .TakeLast(12)
                    .Any(x =>
                        x.Low <= zoneHigh + avgRange * 0.35 &&
                        x.Close > zoneLow);

                if (!recentTouch)
                    continue;

                if (!ZoneNearZone(zoneLow, zoneHigh, htfZoneLow, htfZoneHigh, avgRange * 2.0))
                    continue;

                zones.Add(new IfvgZone
                {
                    Low = zoneLow,
                    High = zoneHigh,
                    Score = 18
                });
            }
            else
            {
                var hasBullishFvg = c1.High < c3.Low;

                if (!hasBullishFvg)
                    continue;

                var zoneLow = c1.High;
                var zoneHigh = c3.Low;

                var violatedDown = candles
                    .Skip(i + 1)
                    .Any(x => x.Close < zoneLow);

                if (!violatedDown)
                    continue;

                var recentTouch = candles
                    .TakeLast(12)
                    .Any(x =>
                        x.High >= zoneLow - avgRange * 0.35 &&
                        x.Close < zoneHigh);

                if (!recentTouch)
                    continue;

                if (!ZoneNearZone(zoneLow, zoneHigh, htfZoneLow, htfZoneHigh, avgRange * 2.0))
                    continue;

                zones.Add(new IfvgZone
                {
                    Low = zoneLow,
                    High = zoneHigh,
                    Score = 18
                });
            }
        }

        return zones.LastOrDefault();
    }

    private static (bool IsConfirmed, string Reason) HasRejectionFromZone(
        List<PriceCandle> candles,
        string direction,
        double zoneLow,
        double zoneHigh,
        double avgRange)
    {
        var recent = candles.TakeLast(8).ToList();
        var tolerance = Math.Max(avgRange * 0.45, 0.20);

        foreach (var candle in recent)
        {
            if (!OverlapsZone(candle, zoneLow, zoneHigh, tolerance))
                continue;

            if (candle.Range <= 0)
                continue;

            var closePosition = (candle.Close - candle.Low) / candle.Range;

            if (direction == "LONG")
            {
                var bullishRejection =
                    candle.IsBullish &&
                    candle.LowerWick >= Math.Max(candle.Body * 0.70, avgRange * 0.15) &&
                    closePosition >= 0.58;

                if (bullishRejection)
                    return (true, "M1 bullish rejection HTF demand zone daxilinde var.");
            }
            else
            {
                var bearishRejection =
                    candle.IsBearish &&
                    candle.UpperWick >= Math.Max(candle.Body * 0.70, avgRange * 0.15) &&
                    closePosition <= 0.42;

                if (bearishRejection)
                    return (true, "M1 bearish rejection HTF supply zone daxilinde var.");
            }
        }

        return direction == "LONG"
            ? (false, "M1 bullish rejection hele yoxdur.")
            : (false, "M1 bearish rejection hele yoxdur.");
    }

    private static RiskPlan BuildRiskPlan(
        string symbol,
        string direction,
        PriceCandle lastM1,
        HtfModel htfModel,
        double avgM1Range,
        double avgM5Range)
    {
        var entry = (decimal)lastM1.Close;

        var bufferDouble = Math.Max(
            avgM5Range * 0.55,
            0.80);

        var buffer = (decimal)bufferDouble;

        decimal stopLoss;
        decimal takeProfit1;
        decimal takeProfit2;
        decimal risk;
        decimal invalidLevel;

        if (direction == "LONG")
        {
            var stopBase = Math.Min(htfModel.ZoneLow, htfModel.SweepPrice);

            invalidLevel = (decimal)stopBase;
            stopLoss = invalidLevel - buffer;

            if (stopLoss >= entry)
                stopLoss = entry - Math.Abs(buffer);

            risk = entry - stopLoss;

            takeProfit1 = entry + risk * 2m;
            takeProfit2 = entry + risk * 3m;
        }
        else
        {
            var stopBase = Math.Max(htfModel.ZoneHigh, htfModel.SweepPrice);

            invalidLevel = (decimal)stopBase;
            stopLoss = invalidLevel + buffer;

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
            riskPips >= 8m &&
            riskPips <= 250m &&
            riskReward1 >= 1.8m &&
            riskReward2 >= 2.7m;

        var invalidReason = string.Empty;

        if (!isValid)
        {
            invalidReason =
                $"XAU risk plan uygun deyil. RiskPips: {Math.Round(riskPips, 1)}, RR1: {Math.Round(riskReward1, 2)}, RR2: {Math.Round(riskReward2, 2)}";
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
            Reason = "XAU SL HTF order block/sweep arxasinda, TP1 1:2, TP2 1:3 hesablandi."
        };
    }

    private static bool IsPriceNearZone(
        double price,
        double zoneLow,
        double zoneHigh,
        double avgRange)
    {
        if (price >= zoneLow && price <= zoneHigh)
            return true;

        var distance = price < zoneLow
            ? zoneLow - price
            : price - zoneHigh;

        return distance <= Math.Max(avgRange * 4.0, 1.20);
    }

    private static bool OverlapsZone(
        PriceCandle candle,
        double zoneLow,
        double zoneHigh,
        double tolerance)
    {
        return candle.Low <= zoneHigh + tolerance &&
               candle.High >= zoneLow - tolerance;
    }

    private static bool ZoneNearZone(
        double low1,
        double high1,
        double low2,
        double high2,
        double tolerance)
    {
        return low1 <= high2 + tolerance &&
               high1 >= low2 - tolerance;
    }

    private static List<ForexStrategyResult> BuildStrategyResults(
        XauSetupAnalysis? longSetup,
        XauSetupAnalysis? shortSetup)
    {
        var results = new List<ForexStrategyResult>();

        if (longSetup != null)
        {
            results.Add(new ForexStrategyResult
            {
                StrategyName = "XAU_GoldScalp_HTF_OB_IFVG",
                Direction = "LONG",
                Score = longSetup.Confidence,
                MaxScore = 100,
                IsConfirmed = longSetup.TradeReady && longSetup.Confidence >= MinimumConfidence,
                Reasons = longSetup.Reasons
            });
        }

        if (shortSetup != null)
        {
            results.Add(new ForexStrategyResult
            {
                StrategyName = "XAU_GoldScalp_HTF_OB_IFVG",
                Direction = "SHORT",
                Score = shortSetup.Confidence,
                MaxScore = 100,
                IsConfirmed = shortSetup.TradeReady && shortSetup.Confidence >= MinimumConfidence,
                Reasons = shortSetup.Reasons
            });
        }

        if (results.Count == 0)
        {
            results.Add(new ForexStrategyResult
            {
                StrategyName = "XAU_GoldScalp_HTF_OB_IFVG",
                Direction = "WAIT",
                Score = 0,
                MaxScore = 100,
                IsConfirmed = false,
                Reasons = new List<string>
                {
                    "XAU setup yoxdur."
                }
            });
        }

        return results;
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
            return 10;

        if (confidence >= 82)
            return 7;

        return 5;
    }

    private static string GetGrade(int confidence)
    {
        if (confidence >= 90)
            return "A+";

        if (confidence >= 82)
            return "A";

        if (confidence >= 78)
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

    private static decimal RoundPrice(
        string symbol,
        decimal price)
    {
        var digits = GetDigits(symbol);
        return Math.Round(price, digits);
    }

    private static decimal RoundPrice(
        string symbol,
        double price)
    {
        var digits = GetDigits(symbol);
        return Math.Round((decimal)price, digits);
    }

    private static string FormatPrice(double price)
    {
        return price.ToString("0.##", CultureInfo.InvariantCulture);
    }

    private static int GetDigits(string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("XAU"))
            return 2;

        return 5;
    }

    private static decimal GetPipSize(string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("XAU"))
            return 0.10m;

        return 0.0001m;
    }

    private static double AverageRange(List<PriceCandle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.Range);
    }

    private sealed class XauSetupAnalysis
    {
        public string Direction { get; set; } = string.Empty;

        public int Confidence { get; set; }

        public bool TradeReady { get; set; }

        public bool HasHtfSweepBos { get; set; }

        public bool HasZoneRetest { get; set; }

        public bool HasMss { get; set; }

        public bool HasIfvg { get; set; }

        public bool HasRejection { get; set; }

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
            $"HTF={HasHtfSweepBos}, Retest={HasZoneRetest}, MSS={HasMss}, IFVG={HasIfvg}, Rejection={HasRejection}, EntryValid={IsEntryStillValid}, Risk={IsRiskPlanValid}, Ready={TradeReady}";
    }

    private sealed class HtfModel
    {
        public string Direction { get; set; } = string.Empty;

        public string Timeframe { get; set; } = string.Empty;

        public string ModelName { get; set; } = string.Empty;

        public int SweepIndex { get; set; }

        public int BosIndex { get; set; }

        public double ZoneLow { get; set; }

        public double ZoneHigh { get; set; }

        public double SweepPrice { get; set; }

        public double BosLevel { get; set; }

        public int Score { get; set; }
    }

    private sealed class HtfOrderBlock
    {
        public double Low { get; set; }

        public double High { get; set; }

        public int Index { get; set; }
    }

    private sealed class IfvgZone
    {
        public double Low { get; set; }

        public double High { get; set; }

        public int Score { get; set; }
    }

    private sealed class SimpleFvg
    {
        public double Low { get; set; }

        public double High { get; set; }
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

        public double Price { get; set; }

        public string Kind { get; set; } = string.Empty;
    }
}
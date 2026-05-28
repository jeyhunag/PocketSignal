using System.Globalization;
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Forex;

public class CoreForexSignalService : IForexSignalService
{
    private const int MinimumConfidence = 72;
    private const int ConflictDistance = 10;

    private readonly IMarketDataService _marketDataService;

    public CoreForexSignalService(
        IMarketDataService marketDataService)
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
            180,
            cancellationToken);

        var m5Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "5min",
            220,
            cancellationToken);

        var m1Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            260,
            cancellationToken);

        var m15 = MapCandles(m15Response);
        var m5 = MapCandles(m5Response);
        var m1 = MapCandles(m1Response);

        if (m15.Count < 80 || m5.Count < 100 || m1.Count < 120)
        {
            return Wait(
                symbol,
                0,
                "NO_TRADE",
                new List<string>
                {
                    "Breaker Block strategiyası üçün kifayət qədər M15/M5/M1 candle yoxdur."
                },
                BuildStrategyResults(null, null));
        }

        var longAnalysis = AnalyzeDirection(
            symbol,
            "LONG",
            m15,
            m5,
            m1);

        var shortAnalysis = AnalyzeDirection(
            symbol,
            "SHORT",
            m15,
            m5,
            m1);

        Console.WriteLine(
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Forex Breaker Block M15/M5/M1 | {symbol} | " +
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
            shortAnalysis);

        if (!best.TradeReady)
        {
            return Wait(
                symbol,
                best.Confidence,
                best.Confidence >= MinimumConfidence ? "WATCHLIST" : "NO_TRADE",
                new List<string>
                {
                    $"Breaker Block setup hələ tam hazır deyil. Best: {best.Direction} {best.Confidence}%.",
                    $"LONG score: {longAnalysis.Confidence}%",
                    $"SHORT score: {shortAnalysis.Confidence}%"
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
                    $"LONG score: {longAnalysis.Confidence}%",
                    $"SHORT score: {shortAnalysis.Confidence}%",
                    "LONG və SHORT Breaker Block setup-ları yaxındır. Direction təmiz deyil."
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
                    $"{best.Direction} Breaker Block setup var, amma confidence minimum səviyyəyə çatmadı.",
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
            $"Breaker Block {best.Direction} signal təsdiqləndi.",
            "Failed order block / breaker block tapıldı.",
            "Market structure shift / CHoCH təsdiqi var.",
            "Price breaker zone-a retest etdi.",
            best.HasFvgOverlap
                ? "Breaker block FVG ilə overlap edir."
                : "Breaker block fresh retest əsasında işləyir.",
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
                ? $"M5 candle {RoundPrice(symbol, best.InvalidLevel)} altında bağlansa trade ləğvdir."
                : $"M5 candle {RoundPrice(symbol, best.InvalidLevel)} üstündə bağlansa trade ləğvdir.",

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

    private static BreakerDirectionAnalysis AnalyzeDirection(
        string symbol,
        string direction,
        List<PriceCandle> m15,
        List<PriceCandle> m5,
        List<PriceCandle> m1)
    {
        var reasons = new List<string>();

        var analysis = new BreakerDirectionAnalysis
        {
            Direction = direction,
            EntryPrice = (decimal)m1[^1].Close
        };

        var m15Structure = DetectStructure(m15);
        var m5Structure = DetectStructure(m5);

        if (direction == "LONG")
        {
            if (m15Structure == "BULLISH")
            {
                analysis.Confidence += 10;
                reasons.Add("M15 structure bullishdir.");
            }
            else if (m15Structure == "RANGE")
            {
                analysis.Confidence += 4;
                reasons.Add("M15 structure range-dir.");
            }
            else
            {
                reasons.Add("M15 structure LONG üçün uyğun deyil.");
            }

            if (m5Structure == "BULLISH")
            {
                analysis.Confidence += 8;
                reasons.Add("M5 structure bullishdir.");
            }
            else if (m5Structure == "RANGE")
            {
                analysis.Confidence += 3;
                reasons.Add("M5 structure range-dir.");
            }
            else
            {
                reasons.Add("M5 structure LONG üçün uyğun deyil.");
            }
        }
        else
        {
            if (m15Structure == "BEARISH")
            {
                analysis.Confidence += 10;
                reasons.Add("M15 structure bearishdir.");
            }
            else if (m15Structure == "RANGE")
            {
                analysis.Confidence += 4;
                reasons.Add("M15 structure range-dir.");
            }
            else
            {
                reasons.Add("M15 structure SHORT üçün uyğun deyil.");
            }

            if (m5Structure == "BEARISH")
            {
                analysis.Confidence += 8;
                reasons.Add("M5 structure bearishdir.");
            }
            else if (m5Structure == "RANGE")
            {
                analysis.Confidence += 3;
                reasons.Add("M5 structure range-dir.");
            }
            else
            {
                reasons.Add("M5 structure SHORT üçün uyğun deyil.");
            }
        }

        var breaker = FindBreakerBlock(
            direction,
            m15);

        if (breaker == null)
        {
            reasons.Add(direction == "LONG"
                ? "Bullish Breaker Block tapılmadı."
                : "Bearish Breaker Block tapılmadı.");

            analysis.Reasons = reasons.Distinct().ToList();
            return analysis;
        }

        analysis.HasBreakerBlock = true;
        analysis.ZoneLow = breaker.ZoneLow;
        analysis.ZoneHigh = breaker.ZoneHigh;
        analysis.InvalidLevel = direction == "LONG"
            ? (decimal)breaker.ZoneLow
            : (decimal)breaker.ZoneHigh;

        analysis.Confidence += 28;

        reasons.Add(direction == "LONG"
            ? $"Bullish Breaker Block tapıldı: {FormatPrice(breaker.ZoneLow)} - {FormatPrice(breaker.ZoneHigh)}."
            : $"Bearish Breaker Block tapıldı: {FormatPrice(breaker.ZoneLow)} - {FormatPrice(breaker.ZoneHigh)}.");

        if (breaker.HasBodyBreak)
        {
            analysis.HasBodyBreak = true;
            analysis.Confidence += 12;
            reasons.Add("Zone candle body ilə qırıldı. Wick-only break deyil.");
        }
        else
        {
            reasons.Add("Zone body ilə qırılmayıb. Bu sadəcə liquidity sweep ola bilər.");
        }

        if (breaker.HasStructureShift)
        {
            analysis.HasStructureShift = true;
            analysis.Confidence += 15;
            reasons.Add("Market structure shift / CHoCH təsdiqi var.");
        }
        else
        {
            reasons.Add("Market structure shift / CHoCH təsdiqi yoxdur.");
        }

        var fvg = FindFvgOverlap(
            direction,
            m5,
            breaker.ZoneLow,
            breaker.ZoneHigh);

        if (fvg != null)
        {
            analysis.HasFvgOverlap = true;
            analysis.Confidence += 14;
            reasons.Add(direction == "LONG"
                ? $"Bullish FVG breaker zone ilə overlap edir: {FormatPrice(fvg.Low)} - {FormatPrice(fvg.High)}."
                : $"Bearish FVG breaker zone ilə overlap edir: {FormatPrice(fvg.Low)} - {FormatPrice(fvg.High)}.");
        }
        else
        {
            reasons.Add("Breaker Block ilə FVG overlap tapılmadı.");
        }

        var isFresh = IsZoneFreshAfterBreak(
            m5,
            breaker.ZoneLow,
            breaker.ZoneHigh);

        if (isFresh)
        {
            analysis.IsFresh = true;
            analysis.Confidence += 12;
            reasons.Add("Breaker Block fresh / unmitigated vəziyyətdədir.");
        }
        else
        {
            reasons.Add("Breaker Block artıq retest/mitigation görüb.");
        }

        var retest = FindRetest(
            m5,
            breaker.ZoneLow,
            breaker.ZoneHigh);

        if (retest != null)
        {
            analysis.HasRetest = true;
            analysis.Confidence += 12;
            reasons.Add($"Price Breaker Block zone-a retest etdi. Retest age: {retest.AgeCandles} M5 candle.");
        }
        else
        {
            reasons.Add("Price hələ Breaker Block zone-a retest etməyib.");
        }

        var reaction = HasReactionConfirmation(
            direction,
            m1,
            breaker.ZoneLow,
            breaker.ZoneHigh);

        if (reaction.IsConfirmed)
        {
            analysis.HasReaction = true;
            analysis.Confidence += 12;
            reasons.Add(reaction.Reason);
        }
        else
        {
            reasons.Add(reaction.Reason);
        }

        var riskPlan = BuildRiskPlan(
            symbol,
            direction,
            m1,
            breaker);

        ApplyRiskPlan(
            analysis,
            reasons,
            riskPlan);

        analysis.Confidence = Math.Clamp(
            analysis.Confidence,
            0,
            100);

        analysis.TradeReady =
           analysis.HasBreakerBlock &&
           analysis.HasBodyBreak &&
           analysis.HasStructureShift &&
           analysis.HasRetest &&
           analysis.IsRiskPlanValid &&
           (
               analysis.HasReaction ||
               analysis.HasFvgOverlap
           ) &&
           analysis.Confidence >= MinimumConfidence;

        if (!analysis.TradeReady)
        {
            if (!analysis.HasBodyBreak)
                reasons.Add("No trade: body break yoxdur.");

            if (!analysis.HasStructureShift)
                reasons.Add("No trade: market structure shift yoxdur.");

            if (!analysis.IsFresh)
                reasons.Add("No trade: breaker block fresh deyil.");

            if (!analysis.HasRetest)
                reasons.Add("No trade: breaker zone retest olunmayıb.");

            if (!analysis.HasReaction)
                reasons.Add("No trade: retest reaction confirmation yoxdur.");

            if (!analysis.IsRiskPlanValid)
                reasons.Add("No trade: risk plan uyğun deyil.");
        }

        analysis.Reasons = reasons.Distinct().ToList();

        return analysis;
    }

    private static BreakerBlock? FindBreakerBlock(
        string direction,
        List<PriceCandle> m15)
    {
        var recent = m15.TakeLast(90).ToList();

        if (recent.Count < 40)
            return null;

        if (direction == "LONG")
        {
            for (var i = recent.Count - 12; i >= 12; i--)
            {
                var candle = recent[i];

                if (!candle.IsBearish)
                    continue;

                var previousHigh = recent
                    .Take(i)
                    .TakeLast(20)
                    .Max(x => x.High);

                var bodyBreakIndex = FindBodyBreakIndex(
                    recent,
                    i + 1,
                    Math.Min(recent.Count - 1, i + 10),
                    "LONG",
                    previousHigh);

                if (bodyBreakIndex < 0)
                    continue;

                var hasShift = HasStructureShiftAfter(
                    recent,
                    bodyBreakIndex,
                    "LONG");

                return new BreakerBlock
                {
                    Direction = direction,
                    ZoneLow = candle.Low,
                    ZoneHigh = candle.High,
                    BreakIndex = bodyBreakIndex,
                    HasBodyBreak = true,
                    HasStructureShift = hasShift
                };
            }
        }
        else
        {
            for (var i = recent.Count - 12; i >= 12; i--)
            {
                var candle = recent[i];

                if (!candle.IsBullish)
                    continue;

                var previousLow = recent
                    .Take(i)
                    .TakeLast(20)
                    .Min(x => x.Low);

                var bodyBreakIndex = FindBodyBreakIndex(
                    recent,
                    i + 1,
                    Math.Min(recent.Count - 1, i + 10),
                    "SHORT",
                    previousLow);

                if (bodyBreakIndex < 0)
                    continue;

                var hasShift = HasStructureShiftAfter(
                    recent,
                    bodyBreakIndex,
                    "SHORT");

                return new BreakerBlock
                {
                    Direction = direction,
                    ZoneLow = candle.Low,
                    ZoneHigh = candle.High,
                    BreakIndex = bodyBreakIndex,
                    HasBodyBreak = true,
                    HasStructureShift = hasShift
                };
            }
        }

        return null;
    }

    private static int FindBodyBreakIndex(
        List<PriceCandle> candles,
        int start,
        int end,
        string direction,
        double level)
    {
        for (var i = start; i <= end; i++)
        {
            var candle = candles[i];

            if (direction == "LONG")
            {
                if (candle.Close > level &&
                    Math.Min(candle.Open, candle.Close) > level)
                {
                    return i;
                }
            }
            else
            {
                if (candle.Close < level &&
                    Math.Max(candle.Open, candle.Close) < level)
                {
                    return i;
                }
            }
        }

        return -1;
    }

    private static bool HasStructureShiftAfter(
        List<PriceCandle> candles,
        int breakIndex,
        string direction)
    {
        var before = candles
            .Take(breakIndex)
            .TakeLast(25)
            .ToList();

        if (before.Count < 10)
            return false;

        if (direction == "LONG")
        {
            var high = before.Max(x => x.High);

            return candles
                .Skip(breakIndex)
                .Take(8)
                .Any(x => x.Close > high);
        }

        var low = before.Min(x => x.Low);

        return candles
            .Skip(breakIndex)
            .Take(8)
            .Any(x => x.Close < low);
    }

    private static bool IsZoneFreshAfterBreak(
        List<PriceCandle> m5,
        double zoneLow,
        double zoneHigh)
    {
        var recent = m5.TakeLast(80).ToList();

        var touchedCount = recent.Count(x =>
            x.Low <= zoneHigh &&
            x.High >= zoneLow);

        return touchedCount <= 1;
    }

    private static SimpleZone? FindFvgOverlap(
        string direction,
        List<PriceCandle> candles,
        double zoneLow,
        double zoneHigh)
    {
        var recent = candles.TakeLast(80).ToList();

        for (var i = recent.Count - 1; i >= 2; i--)
        {
            var c1 = recent[i - 2];
            var c3 = recent[i];

            if (direction == "LONG")
            {
                if (c1.High < c3.Low)
                {
                    var fvgLow = c1.High;
                    var fvgHigh = c3.Low;

                    if (ZonesOverlap(
                            fvgLow,
                            fvgHigh,
                            zoneLow,
                            zoneHigh))
                    {
                        return new SimpleZone
                        {
                            Low = fvgLow,
                            High = fvgHigh
                        };
                    }
                }
            }
            else
            {
                if (c1.Low > c3.High)
                {
                    var fvgLow = c3.High;
                    var fvgHigh = c1.Low;

                    if (ZonesOverlap(
                            fvgLow,
                            fvgHigh,
                            zoneLow,
                            zoneHigh))
                    {
                        return new SimpleZone
                        {
                            Low = fvgLow,
                            High = fvgHigh
                        };
                    }
                }
            }
        }

        return null;
    }

    private static RetestInfo? FindRetest(
        List<PriceCandle> m5,
        double zoneLow,
        double zoneHigh)
    {
        var recent = m5.TakeLast(40).ToList();

        for (var i = recent.Count - 1; i >= 0; i--)
        {
            var candle = recent[i];

            var touched =
                candle.Low <= zoneHigh &&
                candle.High >= zoneLow;

            if (!touched)
                continue;

            return new RetestInfo
            {
                CandleIndex = i,
                AgeCandles = recent.Count - 1 - i
            };
        }

        return null;
    }

    private static (bool IsConfirmed, string Reason) HasReactionConfirmation(
        string direction,
        List<PriceCandle> m1,
        double zoneLow,
        double zoneHigh)
    {
        var recent = m1.TakeLast(12).ToList();

        if (recent.Count < 6)
            return (false, "Reaction confirmation üçün kifayət qədər M1 candle yoxdur.");

        foreach (var candle in recent)
        {
            var touched =
                candle.Low <= zoneHigh &&
                candle.High >= zoneLow;

            if (!touched)
                continue;

            if (candle.Range <= 0)
                continue;

            var closePosition = (candle.Close - candle.Low) / candle.Range;

            if (direction == "LONG")
            {
                var bullishReaction =
                    candle.IsBullish &&
                    candle.LowerWick >= Math.Max(candle.Body * 0.70, candle.Range * 0.25) &&
                    closePosition >= 0.55;

                if (bullishReaction)
                {
                    return (
                        true,
                        "Breaker Block retest sonrası M1 bullish reaction confirmation var.");
                }
            }
            else
            {
                var bearishReaction =
                    candle.IsBearish &&
                    candle.UpperWick >= Math.Max(candle.Body * 0.70, candle.Range * 0.25) &&
                    closePosition <= 0.45;

                if (bearishReaction)
                {
                    return (
                        true,
                        "Breaker Block retest sonrası M1 bearish reaction confirmation var.");
                }
            }
        }

        return direction == "LONG"
            ? (false, "Breaker Block retest sonrası M1 bullish reaction confirmation yoxdur.")
            : (false, "Breaker Block retest sonrası M1 bearish reaction confirmation yoxdur.");
    }

    private static RiskPlan BuildRiskPlan(
        string symbol,
        string direction,
        List<PriceCandle> m1,
        BreakerBlock breaker)
    {
        var entry = (decimal)m1[^1].Close;

        var buffer = GetRiskBuffer(
            symbol,
            m1);

        decimal stopLoss;
        decimal invalidLevel;

        if (direction == "LONG")
        {
            invalidLevel = (decimal)breaker.ZoneLow;
            stopLoss = invalidLevel - buffer;

            if (stopLoss >= entry)
                stopLoss = entry - buffer;
        }
        else
        {
            invalidLevel = (decimal)breaker.ZoneHigh;
            stopLoss = invalidLevel + buffer;

            if (stopLoss <= entry)
                stopLoss = entry + buffer;
        }

        var risk = Math.Abs(entry - stopLoss);

        decimal takeProfit1;
        decimal takeProfit2;

        if (direction == "LONG")
        {
            takeProfit1 = entry + risk * 2m;
            takeProfit2 = entry + risk * 3m;
        }
        else
        {
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
            entry > 0 &&
            stopLoss > 0 &&
            takeProfit1 > 0 &&
            takeProfit2 > 0 &&
            riskPips >= GetMinimumRiskPips(symbol) &&
            riskPips <= GetMaximumRiskPips(symbol) &&
            riskReward1 >= 1.8m &&
            riskReward2 >= 2.5m;

        var invalidReason = string.Empty;

        if (!isValid)
        {
            invalidReason =
                $"Risk plan uyğun deyil. RiskPips: {Math.Round(riskPips, 1)}, RR1: {Math.Round(riskReward1, 2)}, RR2: {Math.Round(riskReward2, 2)}";
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
            Reason = "SL Breaker Block zonasının arxasında yerləşdirildi. TP1 1:2, TP2 1:3 risk/reward ilə hesablandı."
        };
    }

    private static decimal GetRiskBuffer(
        string symbol,
        List<PriceCandle> candles)
    {
        var avgRange = AverageRange(
            candles.TakeLast(20).ToList());

        var buffer = (decimal)(avgRange * 1.2);

        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("JPY"))
            return Math.Max(buffer, 0.03m);

        if (symbol.Contains("XAU"))
            return Math.Max(buffer, 0.80m);

        if (symbol.Contains("USOIL"))
            return Math.Max(buffer, 0.08m);

        return Math.Max(buffer, 0.0003m);
    }

    private static void ApplyRiskPlan(
        BreakerDirectionAnalysis analysis,
        List<string> reasons,
        RiskPlan riskPlan)
    {
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
            analysis.Confidence += 7;
            reasons.Add(riskPlan.Reason);
        }
        else
        {
            reasons.Add(riskPlan.InvalidReason);
        }
    }

    private static string DetectStructure(
        List<PriceCandle> candles)
    {
        var recent = candles.TakeLast(60).ToList();

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
            if (highs[1].Price > highs[0].Price &&
                lows[1].Price > lows[0].Price)
            {
                return "BULLISH";
            }

            if (highs[1].Price < highs[0].Price &&
                lows[1].Price < lows[0].Price)
            {
                return "BEARISH";
            }
        }

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

    private static List<ForexStrategyResult> BuildStrategyResults(
        BreakerDirectionAnalysis? longAnalysis,
        BreakerDirectionAnalysis? shortAnalysis)
    {
        var results = new List<ForexStrategyResult>();

        if (longAnalysis != null)
        {
            results.Add(new ForexStrategyResult
            {
                StrategyName = "BREAKER_BLOCK_M15_M5_M1",
                Direction = "LONG",
                Score = longAnalysis.Confidence,
                MaxScore = 100,
                IsConfirmed = longAnalysis.TradeReady && longAnalysis.Confidence >= MinimumConfidence,
                Reasons = longAnalysis.Reasons
            });
        }

        if (shortAnalysis != null)
        {
            results.Add(new ForexStrategyResult
            {
                StrategyName = "BREAKER_BLOCK_M15_M5_M1",
                Direction = "SHORT",
                Score = shortAnalysis.Confidence,
                MaxScore = 100,
                IsConfirmed = shortAnalysis.TradeReady && shortAnalysis.Confidence >= MinimumConfidence,
                Reasons = shortAnalysis.Reasons
            });
        }

        if (results.Count == 0)
        {
            results.Add(new ForexStrategyResult
            {
                StrategyName = "BREAKER_BLOCK_M15_M5_M1",
                Direction = "WAIT",
                Score = 0,
                MaxScore = 100,
                IsConfirmed = false,
                Reasons = new List<string>
                {
                    "Breaker Block setup yoxdur."
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

    private static int GetValidForMinutes(
        int confidence)
    {
        if (confidence >= 90)
            return 15;

        if (confidence >= 80)
            return 10;

        return 7;
    }

    private static string GetGrade(
        int confidence)
    {
        if (confidence >= 90)
            return "A+";

        if (confidence >= 82)
            return "A";

        if (confidence >= 72)
            return "B";

        return "NO_TRADE";
    }

    private static List<PriceCandle> MapCandles(
        TwelveDataResponse? response)
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

    private static string FormatPrice(
        double price)
    {
        return price.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static int GetDigits(
        string symbol)
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

    private static decimal GetPipSize(
        string symbol)
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

    private static decimal GetMinimumRiskPips(
        string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("JPY"))
            return 3m;

        if (symbol.Contains("XAU"))
            return 8m;

        if (symbol.Contains("BTC"))
            return 20m;

        if (symbol.Contains("ETH"))
            return 10m;

        if (symbol.Contains("USOIL"))
            return 5m;

        return 3m;
    }

    private static decimal GetMaximumRiskPips(
        string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("JPY"))
            return 100m;

        if (symbol.Contains("XAU"))
            return 300m;

        if (symbol.Contains("BTC"))
            return 2500m;

        if (symbol.Contains("ETH"))
            return 700m;

        if (symbol.Contains("USOIL"))
            return 150m;

        return 100m;
    }

    private static bool ZonesOverlap(
        double low1,
        double high1,
        double low2,
        double high2)
    {
        return low1 <= high2 &&
               high1 >= low2;
    }

    private static double AverageRange(
        List<PriceCandle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.Range);
    }

    private sealed class BreakerDirectionAnalysis
    {
        public string Direction { get; set; } = string.Empty;

        public int Confidence { get; set; }

        public bool TradeReady { get; set; }

        public bool HasBreakerBlock { get; set; }

        public bool HasBodyBreak { get; set; }

        public bool HasStructureShift { get; set; }

        public bool IsFresh { get; set; }

        public bool HasFvgOverlap { get; set; }

        public bool HasRetest { get; set; }

        public bool HasReaction { get; set; }

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
            $"Breaker={HasBreakerBlock}, BodyBreak={HasBodyBreak}, MSS={HasStructureShift}, Fresh={IsFresh}, FVG={HasFvgOverlap}, Retest={HasRetest}, Reaction={HasReaction}, Risk={IsRiskPlanValid}, Ready={TradeReady}";
    }

    private sealed class BreakerBlock
    {
        public string Direction { get; set; } = string.Empty;

        public double ZoneLow { get; set; }

        public double ZoneHigh { get; set; }

        public int BreakIndex { get; set; }

        public bool HasBodyBreak { get; set; }

        public bool HasStructureShift { get; set; }
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

    private sealed class SimpleZone
    {
        public double Low { get; set; }

        public double High { get; set; }
    }

    private sealed class RetestInfo
    {
        public int CandleIndex { get; set; }

        public int AgeCandles { get; set; }
    }
}
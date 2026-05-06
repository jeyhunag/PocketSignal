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
        var h1Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1h",
            160,
            cancellationToken);

        var m5Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "5min",
            260,
            cancellationToken);

        var h1 = MapCandles(h1Response);
        var m5 = MapCandles(m5Response);

        if (h1.Count < 60 || m5.Count < 100)
        {
            return Wait(
                symbol,
                0,
                "NO_TRADE",
                new List<string>
                {
                    "Flip Level + Bait and Switch strategiyasi ucun kifayet qeder H1/M5 candle yoxdur."
                },
                BuildStrategyResults(null, null, "UNKNOWN"));
        }

        var marketContext = DetectMarketContext(m5);
        var htfContext = DetectMarketContext(h1);

        var longAnalysis = AnalyzeDirection(
            symbol,
            "LONG",
            h1,
            m5,
            marketContext,
            htfContext);

        var shortAnalysis = AnalyzeDirection(
            symbol,
            "SHORT",
            h1,
            m5,
            marketContext,
            htfContext);

        Console.WriteLine(
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Forex Flip+BaitSwitch | {symbol} | " +
            $"M5 Context: {marketContext} | H1 Context: {htfContext} | " +
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
            marketContext);

        if (!best.TradeReady)
        {
            return Wait(
                symbol,
                best.Confidence,
                best.Confidence >= MinimumConfidence ? "WATCHLIST" : "NO_TRADE",
                new List<string>
                {
                    $"Setup hele tam hazir deyil. Best: {best.Direction} {best.Confidence}%.",
                    $"M5 context: {marketContext}",
                    $"H1 context: {htfContext}",
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
                    "LONG ve SHORT setup-lari arasinda ferq azdir. Direction temiz deyil."
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
                    $"{best.Direction} setup var, amma confidence minimum seviyeye catmadi.",
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
            $"Flip Level + Bait and Switch {best.Direction} signal tesdiqlendi.",
            $"M5 context: {marketContext}",
            $"H1 context: {htfContext}",
            "Market context tapildi.",
            "High probability flip level tapildi.",
            "Bait and switch / liquidity trap candle tapildi.",
            "Volume confirmation yoxlanildi.",
            "Higher timeframe alignment yoxlanildi.",
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
        List<PriceCandle> h1,
        List<PriceCandle> m5,
        string marketContext,
        string htfContext)
    {
        var reasons = new List<string>();

        var last = m5[^1];

        var analysis = new DirectionAnalysis
        {
            Direction = direction,
            EntryPrice = (decimal)last.Close
        };

        var avgM5Range = AverageRange(m5.TakeLast(40).ToList());
        var avgH1Range = AverageRange(h1.TakeLast(40).ToList());

        if (avgM5Range <= 0 || avgH1Range <= 0)
        {
            reasons.Add("Average range hesablanmadi, data duzgun deyil.");
            analysis.Reasons = reasons;
            return analysis;
        }

        ScoreMarketContext(
            direction,
            marketContext,
            analysis,
            reasons);

        var flipLevels = BuildCandidateFlipLevels(
            m5,
            direction,
            marketContext,
            avgM5Range);

        if (flipLevels.Count > 0)
        {
            analysis.HasFlipLevel = true;
            analysis.Confidence += Math.Min(18, flipLevels.Max(x => x.Score));
            reasons.Add(direction == "LONG"
                ? $"LONG ucun flip level namizedleri tapildi. Count: {flipLevels.Count}."
                : $"SHORT ucun flip level namizedleri tapildi. Count: {flipLevels.Count}.");
        }
        else
        {
            reasons.Add(direction == "LONG"
                ? "LONG ucun flip level hele tapilmadi."
                : "SHORT ucun flip level hele tapilmadi.");
        }

        var setup = FindBaitAndSwitchSetup(
            m5,
            direction,
            marketContext,
            avgM5Range,
            flipLevels);

        if (setup == null)
        {
            var htfProgress = ScoreHtfProgress(
                h1,
                direction,
                htfContext,
                avgH1Range);

            analysis.Confidence += htfProgress.Score;
            reasons.Add(htfProgress.Reason);

            analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);
            analysis.Reasons = reasons.Distinct().ToList();

            return analysis;
        }

        analysis.HasBaitSwitch = true;
        analysis.FlipLevel = setup.Level;
        analysis.BaitSwitchIndex = setup.BaitSwitchIndex;

        analysis.Confidence += setup.Score;

        reasons.Add(direction == "LONG"
            ? $"Flip level support kimi mudafie olundu: {FormatPrice(setup.Level)}."
            : $"Flip level resistance kimi mudafie olundu: {FormatPrice(setup.Level)}.");

        reasons.Add(direction == "LONG"
            ? "Bait and switch: price level altina gedib geri ustunde baglandi."
            : "Bait and switch: price level ustune gedib geri altinda baglandi.");

        var volumeConfirmation = HasVolumeConfirmation(
            m5,
            direction,
            setup.BaitSwitchIndex);

        if (volumeConfirmation.IsConfirmed)
        {
            analysis.HasVolumeConfirmation = true;
            analysis.Confidence += 20;
            reasons.Add(volumeConfirmation.Reason);
        }
        else
        {
            analysis.Confidence -= 15;
            reasons.Add(volumeConfirmation.Reason);
            reasons.Add("Volume confirmation olmadığı üçün setup real signal səviyyəsinə buraxılmadı.");
        }

        var htfAlignment = CheckHigherTimeframeAlignment(
            h1,
            direction,
            htfContext,
            setup.Level,
            avgH1Range);

        if (htfAlignment.IsAligned)
        {
            analysis.HasHtfAlignment = true;
            analysis.Confidence += htfAlignment.Score;
            reasons.Add(htfAlignment.Reason);
        }
        else
        {
            analysis.Confidence += htfAlignment.Score;
            reasons.Add(htfAlignment.Reason);
        }

        var riskPlan = BuildRiskPlan(
            symbol,
            direction,
            m5,
            setup,
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
            analysis.Confidence += analysis.HasVolumeConfirmation && analysis.HasHtfAlignment
                ? 10
                : 3;

            reasons.Add(riskPlan.Reason);
        }
        else
        {
            reasons.Add(riskPlan.InvalidReason);
        }

        if (setup.AgeCandles <= 2)
        {
            analysis.Confidence += 5;
            reasons.Add("Bait and switch tezedir, entry vaxtinda sayilir.");
        }
        else if (setup.AgeCandles <= 5)
        {
            reasons.Add("Bait and switch yeni sayilir, amma entry gecikmemelidir.");
        }
        else
        {
            analysis.Confidence -= 10;
            reasons.Add("Bait and switch kohneleib, entry gecikmis ola biler.");
        }

        if (!analysis.HasVolumeConfirmation)
        {
            analysis.Confidence = Math.Min(analysis.Confidence, 68);
        }

        if (!analysis.HasHtfAlignment)
        {
            analysis.Confidence = Math.Min(analysis.Confidence, 74);
        }

        if (!analysis.HasVolumeConfirmation || !analysis.HasHtfAlignment)
        {
            reasons.Add("Setup tamam deyil: Volume və HTF alignment tamamlanmadan 72%+ real signal sayılmır.");
        }

        analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);

        analysis.TradeReady =
            analysis.HasMarketContext &&
            analysis.HasFlipLevel &&
            analysis.HasBaitSwitch &&
            analysis.HasVolumeConfirmation &&
            analysis.HasHtfAlignment &&
            analysis.IsRiskPlanValid &&
            setup.AgeCandles <= 5;

        if (!analysis.TradeReady)
        {
            if (!analysis.HasVolumeConfirmation)
                reasons.Add("No trade: volume confirmation yoxdur.");

            if (!analysis.HasHtfAlignment)
                reasons.Add("No trade: higher timeframe alignment yoxdur.");

            if (!analysis.IsRiskPlanValid)
                reasons.Add("No trade: risk plan uygun deyil.");

            if (setup.AgeCandles > 5)
                reasons.Add("No trade: entry gecikib.");
        }

        analysis.Reasons = reasons.Distinct().ToList();

        return analysis;
    }

    private static void ScoreMarketContext(
        string direction,
        string marketContext,
        DirectionAnalysis analysis,
        List<string> reasons)
    {
        if (marketContext == "CHOPPY")
        {
            analysis.Confidence += 5;
            reasons.Add("M5 market choppy-dir. Transcript qaydasi: choppy marketde trade yoxdur, ancaq analiz davam edir.");
            return;
        }

        var contextAllowed = IsDirectionAllowedByContext(
            direction,
            marketContext);

        if (contextAllowed)
        {
            analysis.HasMarketContext = true;
            analysis.Confidence += 15;
            reasons.Add($"Market context {direction} ucun uygundur: {marketContext}.");
        }
        else
        {
            analysis.Confidence += 5;
            reasons.Add($"Market context {direction} ucun temiz deyil: {marketContext}.");
        }
    }

    private static (int Score, string Reason) ScoreHtfProgress(
        List<PriceCandle> h1,
        string direction,
        string htfContext,
        double avgH1Range)
    {
        if (htfContext == "CHOPPY")
        {
            return (
                0,
                "H1 choppy-dir, HTF confirmation yoxdur.");
        }

        if (direction == "LONG" && htfContext == "UPTREND")
        {
            return (
                12,
                "H1 uptrend-dir, LONG ucun HTF ehtimal var.");
        }

        if (direction == "SHORT" && htfContext == "DOWNTREND")
        {
            return (
                12,
                "H1 downtrend-dir, SHORT ucun HTF ehtimal var.");
        }

        if (htfContext == "RANGE")
        {
            var recent = h1.TakeLast(60).ToList();
            var rangeHigh = recent.Max(x => x.High);
            var rangeLow = recent.Min(x => x.Low);
            var last = recent[^1];

            if (direction == "LONG")
            {
                var roomToResistance = rangeHigh - last.Close;

                if (roomToResistance >= avgH1Range * 2)
                {
                    return (
                        10,
                        "H1 range-dir, LONG ucun resistance-a qeder mesafe var.");
                }
            }
            else
            {
                var roomToSupport = last.Close - rangeLow;

                if (roomToSupport >= avgH1Range * 2)
                {
                    return (
                        10,
                        "H1 range-dir, SHORT ucun support-a qeder mesafe var.");
                }
            }
        }

        var htfReversal = HasHtfReversalSweep(
            h1,
            direction,
            avgH1Range);

        if (htfReversal)
        {
            return (
                12,
                direction == "LONG"
                    ? "H1 bullish reversal liquidity sweep elameti var."
                    : "H1 bearish reversal liquidity sweep elameti var.");
        }

        return (
            0,
            $"H1 context {direction} ucun uygun deyil. Context: {htfContext}.");
    }

    private static string DetectMarketContext(List<PriceCandle> candles)
    {
        var recent = candles.TakeLast(80).ToList();

        if (recent.Count < 40)
            return "CHOPPY";

        var swings = FindSwingPoints(
            recent,
            2,
            2);

        var highs = swings
            .Where(x => x.Kind == "HIGH")
            .OrderBy(x => x.Index)
            .TakeLast(3)
            .ToList();

        var lows = swings
            .Where(x => x.Kind == "LOW")
            .OrderBy(x => x.Index)
            .TakeLast(3)
            .ToList();

        if (highs.Count >= 2 && lows.Count >= 2)
        {
            var higherHigh =
                highs[^1].Price > highs[^2].Price;

            var higherLow =
                lows[^1].Price > lows[^2].Price;

            var lowerHigh =
                highs[^1].Price < highs[^2].Price;

            var lowerLow =
                lows[^1].Price < lows[^2].Price;

            if (higherHigh && higherLow)
                return "UPTREND";

            if (lowerHigh && lowerLow)
                return "DOWNTREND";
        }

        var avgRange = AverageRange(recent.TakeLast(40).ToList());
        var rangeHigh = recent.Max(x => x.High);
        var rangeLow = recent.Min(x => x.Low);
        var rangeSize = rangeHigh - rangeLow;

        if (avgRange > 0 && rangeSize <= avgRange * 18)
            return "RANGE";

        return "CHOPPY";
    }

    private static bool IsDirectionAllowedByContext(
        string direction,
        string marketContext)
    {
        if (marketContext == "UPTREND" && direction == "LONG")
            return true;

        if (marketContext == "DOWNTREND" && direction == "SHORT")
            return true;

        if (marketContext == "RANGE")
            return true;

        return false;
    }

    private static BaitSwitchSetup? FindBaitAndSwitchSetup(
        List<PriceCandle> candles,
        string direction,
        string marketContext,
        double avgRange,
        List<FlipLevel> candidateLevels)
    {
        var recent = candles.TakeLast(120).ToList();

        if (recent.Count < 60)
            return null;

        if (candidateLevels.Count == 0)
            return null;

        var searchStart = Math.Max(0, recent.Count - 30);

        for (var i = recent.Count - 1; i >= searchStart; i--)
        {
            var candle = recent[i];

            foreach (var level in candidateLevels)
            {
                var tolerance = Math.Max(avgRange * 0.20, level.Price * 0.00003);

                if (direction == "LONG")
                {
                    var baitSwitch =
                        candle.Low < level.Price - tolerance &&
                        candle.Close > level.Price;

                    if (!baitSwitch)
                        continue;

                    var defendedLevel =
                        Math.Abs(candle.Close - level.Price) <= avgRange * 4 ||
                        candle.Low <= level.Price + avgRange * 0.5;

                    if (!defendedLevel)
                        continue;

                    return new BaitSwitchSetup
                    {
                        Direction = direction,
                        Level = level.Price,
                        BaitSwitchIndex = candles.Count - recent.Count + i,
                        AgeCandles = recent.Count - 1 - i,
                        SweepPrice = candle.Low,
                        EntryPrice = candle.Close,
                        Score = level.Score + 25
                    };
                }
                else
                {
                    var baitSwitch =
                        candle.High > level.Price + tolerance &&
                        candle.Close < level.Price;

                    if (!baitSwitch)
                        continue;

                    var defendedLevel =
                        Math.Abs(candle.Close - level.Price) <= avgRange * 4 ||
                        candle.High >= level.Price - avgRange * 0.5;

                    if (!defendedLevel)
                        continue;

                    return new BaitSwitchSetup
                    {
                        Direction = direction,
                        Level = level.Price,
                        BaitSwitchIndex = candles.Count - recent.Count + i,
                        AgeCandles = recent.Count - 1 - i,
                        SweepPrice = candle.High,
                        EntryPrice = candle.Close,
                        Score = level.Score + 25
                    };
                }
            }
        }

        return null;
    }

    private static List<FlipLevel> BuildCandidateFlipLevels(
        List<PriceCandle> candles,
        string direction,
        string marketContext,
        double avgRange)
    {
        var recent = candles.TakeLast(120).ToList();

        var swings = FindSwingPoints(
            recent,
            2,
            2);

        if (swings.Count < 6)
            return new List<FlipLevel>();

        var levels = new List<FlipLevel>();

        var swingHighs = swings
            .Where(x => x.Kind == "HIGH")
            .OrderByDescending(x => x.Index)
            .Take(10)
            .ToList();

        var swingLows = swings
            .Where(x => x.Kind == "LOW")
            .OrderByDescending(x => x.Index)
            .Take(10)
            .ToList();

        if (direction == "LONG")
        {
            foreach (var high in swingHighs)
            {
                var brokenAbove = recent
                    .Skip(high.Index + 1)
                    .Any(x => x.Close > high.Price);

                if (!brokenAbove)
                    continue;

                levels.Add(new FlipLevel
                {
                    Price = high.Price,
                    Score = marketContext == "UPTREND" ? 20 : 16
                });
            }

            if (marketContext == "RANGE")
            {
                foreach (var low in swingLows)
                {
                    levels.Add(new FlipLevel
                    {
                        Price = low.Price,
                        Score = 18
                    });
                }
            }
        }
        else
        {
            foreach (var low in swingLows)
            {
                var brokenBelow = recent
                    .Skip(low.Index + 1)
                    .Any(x => x.Close < low.Price);

                if (!brokenBelow)
                    continue;

                levels.Add(new FlipLevel
                {
                    Price = low.Price,
                    Score = marketContext == "DOWNTREND" ? 20 : 16
                });
            }

            if (marketContext == "RANGE")
            {
                foreach (var high in swingHighs)
                {
                    levels.Add(new FlipLevel
                    {
                        Price = high.Price,
                        Score = 18
                    });
                }
            }
        }

        return MergeLevels(
                levels,
                avgRange)
            .OrderByDescending(x => x.Score)
            .Take(8)
            .ToList();
    }

    private static List<FlipLevel> MergeLevels(
        List<FlipLevel> levels,
        double avgRange)
    {
        var result = new List<FlipLevel>();
        var tolerance = Math.Max(avgRange * 0.50, 0.0001);

        foreach (var level in levels.OrderByDescending(x => x.Score))
        {
            var existing = result.FirstOrDefault(x =>
                Math.Abs(x.Price - level.Price) <= tolerance);

            if (existing == null)
            {
                result.Add(level);
            }
            else
            {
                existing.Score = Math.Max(existing.Score, level.Score + 2);
            }
        }

        return result;
    }

    private static (bool IsConfirmed, string Reason) HasVolumeConfirmation(
        List<PriceCandle> candles,
        string direction,
        int baitSwitchIndex)
    {
        var recentWithVolume = candles
            .Where(x => x.Volume > 0)
            .ToList();

        if (recentWithVolume.Count < 30)
        {
            return (
                false,
                "Volume confirmation yoxdur: data provider volume melumati qaytarmir ve ya kifayet deyil.");
        }

        var start = Math.Max(0, baitSwitchIndex - 30);

        var volumeWindow = candles
            .Skip(start)
            .Take(Math.Max(1, baitSwitchIndex - start))
            .Where(x => x.Volume > 0)
            .ToList();

        if (volumeWindow.Count < 10)
        {
            return (
                false,
                "Volume confirmation ucun kifayet qeder kecmis volume yoxdur.");
        }

        var avgVolume = volumeWindow.Average(x => x.Volume);

        var confirmationCandles = candles
            .Skip(baitSwitchIndex)
            .Take(2)
            .Where(x => x.Volume > 0)
            .ToList();

        foreach (var candle in confirmationCandles)
        {
            if (direction == "LONG")
            {
                var strongBuying =
                    candle.IsBullish &&
                    candle.Volume >= avgVolume * 1.35;

                if (strongBuying)
                {
                    return (
                        true,
                        "Bait and switch-den sonra guclu buying volume confirmation var.");
                }
            }
            else
            {
                var strongSelling =
                    candle.IsBearish &&
                    candle.Volume >= avgVolume * 1.35;

                if (strongSelling)
                {
                    return (
                        true,
                        "Bait and switch-den sonra guclu selling volume confirmation var.");
                }
            }
        }

        return direction == "LONG"
            ? (false, "Bait and switch-den sonra guclu buying volume confirmation yoxdur.")
            : (false, "Bait and switch-den sonra guclu selling volume confirmation yoxdur.");
    }

    private static (bool IsAligned, int Score, string Reason) CheckHigherTimeframeAlignment(
        List<PriceCandle> h1,
        string direction,
        string htfContext,
        double level,
        double avgRange)
    {
        if (htfContext == "CHOPPY")
        {
            return (
                false,
                0,
                "H1 choppy-dir, higher timeframe alignment yoxdur.");
        }

        if (direction == "LONG" && htfContext == "UPTREND")
        {
            return (
                true,
                20,
                "H1 uptrend-dir, LONG setup higher timeframe ile uygundur.");
        }

        if (direction == "SHORT" && htfContext == "DOWNTREND")
        {
            return (
                true,
                20,
                "H1 downtrend-dir, SHORT setup higher timeframe ile uygundur.");
        }

        if (htfContext == "RANGE")
        {
            var recent = h1.TakeLast(60).ToList();

            var rangeHigh = recent.Max(x => x.High);
            var rangeLow = recent.Min(x => x.Low);
            var last = recent[^1];

            if (direction == "LONG")
            {
                var roomToResistance = rangeHigh - last.Close;

                if (roomToResistance >= avgRange * 2)
                {
                    return (
                        true,
                        16,
                        "H1 range-dir, price supportdan resistance istiqametine hereket ede biler.");
                }

                return (
                    false,
                    4,
                    "H1 range-dir, amma LONG ucun hedefe kifayet qeder mesafe yoxdur.");
            }
            else
            {
                var roomToSupport = last.Close - rangeLow;

                if (roomToSupport >= avgRange * 2)
                {
                    return (
                        true,
                        16,
                        "H1 range-dir, price resistance-dan support istiqametine hereket ede biler.");
                }

                return (
                    false,
                    4,
                    "H1 range-dir, amma SHORT ucun hedefe kifayet qeder mesafe yoxdur.");
            }
        }

        var htfReversal = HasHtfReversalSweep(
            h1,
            direction,
            avgRange);

        if (htfReversal)
        {
            return (
                true,
                18,
                direction == "LONG"
                    ? "H1 bullish reversal liquidity sweep ile uygundur."
                    : "H1 bearish reversal liquidity sweep ile uygundur.");
        }

        return (
            false,
            0,
            $"H1 context {direction} ucun uygun deyil. Context: {htfContext}.");
    }

    private static bool HasHtfReversalSweep(
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
                x.LowerWick >= avgRange * 0.20);
        }

        var keyHigh = reference.Max(x => x.High);

        return last5.Any(x =>
            x.High > keyHigh &&
            x.Close < keyHigh &&
            x.UpperWick >= avgRange * 0.20);
    }

    private static RiskPlan BuildRiskPlan(
        string symbol,
        string direction,
        List<PriceCandle> candles,
        BaitSwitchSetup setup,
        double avgRange)
    {
        var baitCandle = candles[setup.BaitSwitchIndex];
        var entry = (decimal)baitCandle.Close;

        var bufferDouble = Math.Max(
            avgRange * 0.50,
            GetMinimumBuffer(symbol));

        var buffer = (decimal)bufferDouble;

        decimal stopLoss;
        decimal takeProfit1;
        decimal takeProfit2;
        decimal risk;
        decimal invalidLevel;

        if (direction == "LONG")
        {
            invalidLevel = (decimal)setup.SweepPrice;
            stopLoss = invalidLevel - buffer;

            if (stopLoss >= entry)
                stopLoss = entry - Math.Abs(buffer);

            risk = entry - stopLoss;

            var nextKeyLevel = FindNextKeyLevel(
                candles,
                setup.BaitSwitchIndex,
                direction,
                (double)entry);

            takeProfit1 = nextKeyLevel > (double)entry
                ? (decimal)nextKeyLevel
                : entry + risk * 2m;

            if (takeProfit1 < entry + risk * 2m)
                takeProfit1 = entry + risk * 2m;

            takeProfit2 = entry + risk * 3m;
        }
        else
        {
            invalidLevel = (decimal)setup.SweepPrice;
            stopLoss = invalidLevel + buffer;

            if (stopLoss <= entry)
                stopLoss = entry + Math.Abs(buffer);

            risk = stopLoss - entry;

            var nextKeyLevel = FindNextKeyLevel(
                candles,
                setup.BaitSwitchIndex,
                direction,
                (double)entry);

            takeProfit1 = nextKeyLevel < (double)entry && nextKeyLevel > 0
                ? (decimal)nextKeyLevel
                : entry - risk * 2m;

            if (takeProfit1 > entry - risk * 2m)
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
            riskReward1 >= 2.0m &&
            riskReward2 >= 2.0m &&
            riskPips >= GetMinimumRiskPips(symbol) &&
            riskPips <= GetMaximumRiskPips(symbol);

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
            Reason = "Entry bait and switch candle close, SL liquidity sweep arxasinda, target next key level ve minimum RR 2:1 esasinda hesablandi."
        };
    }

    private static double FindNextKeyLevel(
        List<PriceCandle> candles,
        int fromIndex,
        string direction,
        double entry)
    {
        var history = candles
            .Take(Math.Max(1, fromIndex))
            .ToList();

        var swings = FindSwingPoints(
            history,
            2,
            2);

        if (direction == "LONG")
        {
            var nextHigh = swings
                .Where(x => x.Kind == "HIGH" && x.Price > entry)
                .OrderBy(x => x.Price)
                .FirstOrDefault();

            return nextHigh?.Price ?? 0;
        }

        var nextLow = swings
            .Where(x => x.Kind == "LOW" && x.Price < entry)
            .OrderByDescending(x => x.Price)
            .FirstOrDefault();

        return nextLow?.Price ?? 0;
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

    private static List<ForexStrategyResult> BuildStrategyResults(
        DirectionAnalysis? longAnalysis,
        DirectionAnalysis? shortAnalysis,
        string marketContext)
    {
        var results = new List<ForexStrategyResult>();

        if (longAnalysis != null)
        {
            results.Add(new ForexStrategyResult
            {
                StrategyName = "FlipLevel_BaitSwitch_Volume_HTF",
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
                StrategyName = "FlipLevel_BaitSwitch_Volume_HTF",
                Direction = "SHORT",
                Score = shortAnalysis.Confidence,
                MaxScore = 100,
                IsConfirmed = shortAnalysis.TradeReady && shortAnalysis.Confidence >= MinimumConfidence,
                Reasons = shortAnalysis.Reasons
            });
        }

        results.Add(new ForexStrategyResult
        {
            StrategyName = "MarketContext",
            Direction = marketContext,
            Score = marketContext == "CHOPPY" ? 0 : 15,
            MaxScore = 15,
            IsConfirmed = marketContext != "CHOPPY",
            Reasons = new List<string>
            {
                $"Market context: {marketContext}"
            }
        });

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
                Volume = ReadVolume(item)
            });
        }

        return candles
            .OrderBy(x => x.TimeUtc)
            .ToList();
    }

    private static double ReadVolume(object candleDto)
    {
        var property = candleDto
            .GetType()
            .GetProperty("Volume");

        if (property == null)
            return 0;

        var value = property.GetValue(candleDto);

        if (value == null)
            return 0;

        if (value is decimal decimalValue)
            return (double)decimalValue;

        if (value is double doubleValue)
            return doubleValue;

        if (value is int intValue)
            return intValue;

        if (double.TryParse(
                Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Any,
                CultureInfo.InvariantCulture,
                out var parsed))
        {
            return parsed;
        }

        return 0;
    }

    private static decimal RoundPrice(
        string symbol,
        decimal price)
    {
        var digits = GetDigits(symbol);
        return Math.Round(price, digits);
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

    private static double GetMinimumBuffer(string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("JPY"))
            return 0.03;

        if (symbol.Contains("XAU"))
            return 0.80;

        if (symbol.Contains("BTC"))
            return 15;

        if (symbol.Contains("ETH"))
            return 3;

        if (symbol.Contains("USOIL"))
            return 0.05;

        return 0.0003;
    }

    private static decimal GetMinimumRiskPips(string symbol)
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

    private static decimal GetMaximumRiskPips(string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("JPY"))
            return 80m;

        if (symbol.Contains("XAU"))
            return 250m;

        if (symbol.Contains("BTC"))
            return 2000m;

        if (symbol.Contains("ETH"))
            return 500m;

        if (symbol.Contains("USOIL"))
            return 100m;

        return 80m;
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

        public bool HasMarketContext { get; set; }

        public bool HasFlipLevel { get; set; }

        public bool HasBaitSwitch { get; set; }

        public bool HasVolumeConfirmation { get; set; }

        public bool HasHtfAlignment { get; set; }

        public bool IsRiskPlanValid { get; set; }

        public double FlipLevel { get; set; }

        public int BaitSwitchIndex { get; set; }

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
            $"Context={HasMarketContext}, Flip={HasFlipLevel}, BaitSwitch={HasBaitSwitch}, Volume={HasVolumeConfirmation}, HTF={HasHtfAlignment}, Risk={IsRiskPlanValid}, Ready={TradeReady}";
    }

    private sealed class BaitSwitchSetup
    {
        public string Direction { get; set; } = string.Empty;

        public double Level { get; set; }

        public int BaitSwitchIndex { get; set; }

        public int AgeCandles { get; set; }

        public double SweepPrice { get; set; }

        public double EntryPrice { get; set; }

        public int Score { get; set; }
    }

    private sealed class FlipLevel
    {
        public double Price { get; set; }

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
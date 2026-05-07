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
        var response = await _marketDataService.GetCandlesAsync(
            symbol,
            "5min",
            260,
            cancellationToken);

        var candles = MapCandles(response);

        if (candles.Count < 220)
        {
            return Wait(
                symbol,
                0,
                "NO_TRADE",
                new List<string>
                {
                    "Moving Average strategiyasi ucun kifayet qeder M5 candle yoxdur. Minimum 220 candle lazimdir."
                },
                BuildStrategyResults(null, null));
        }

        var longAnalysis = AnalyzeDirection(
            symbol,
            "LONG",
            candles);

        var shortAnalysis = AnalyzeDirection(
            symbol,
            "SHORT",
            candles);

        Console.WriteLine(
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Forex MA Strategy | {symbol} | " +
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
                    "LONG ve SHORT setup-lari yaxindir. Direction temiz deyil."
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
                    $"{best.Direction} Moving Average setup var, amma confidence minimum seviyeye catmadi.",
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
            $"Moving Average {best.Direction} signal tesdiqlendi.",
            best.EntryModel,
            "MA20/MA50 trend ve entry ucun istifade edildi.",
            "Stop Loss 2 ATR esasinda hesablandi.",
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
        List<PriceCandle> candles)
    {
        var reasons = new List<string>();

        var analysis = new DirectionAnalysis
        {
            Direction = direction,
            EntryPrice = (decimal)candles[^1].Close
        };

        var last = candles[^1];

        var ma20 = MovingAverage(candles, 20, candles.Count - 1);
        var ma50 = MovingAverage(candles, 50, candles.Count - 1);
        var ma200 = MovingAverage(candles, 200, candles.Count - 1);

        var ma20Prev = MovingAverage(candles, 20, candles.Count - 6);
        var ma50Prev = MovingAverage(candles, 50, candles.Count - 6);

        var atr = AverageTrueRange(candles.TakeLast(20).ToList(), 14);

        if (ma20 <= 0 || ma50 <= 0 || ma200 <= 0 || atr <= 0)
        {
            reasons.Add("MA20/MA50/MA200 ve ya ATR hesablanmadi.");
            analysis.Reasons = reasons;
            return analysis;
        }

        var isMa50Up = ma50 > ma50Prev;
        var isMa50Down = ma50 < ma50Prev;

        var isMa20Up = ma20 > ma20Prev;
        var isMa20Down = ma20 < ma20Prev;

        var priceAbove20 = last.Close > ma20;
        var priceBelow20 = last.Close < ma20;

        var priceAbove50 = last.Close > ma50;
        var priceBelow50 = last.Close < ma50;

        var priceAbove200 = last.Close > ma200;
        var priceBelow200 = last.Close < ma200;

        var structure = DetectStructure(candles);

        if (direction == "LONG")
        {
            ScoreLongContext(
                analysis,
                reasons,
                structure,
                isMa20Up,
                isMa50Up,
                priceAbove20,
                priceAbove50,
                priceAbove200);

            var swingPullback = DetectLongMa50Pullback(
                candles,
                ma50,
                atr);

            var breakout = DetectLongMa20Breakout(
                candles,
                ma20,
                ma50);

            if (swingPullback.IsConfirmed)
            {
                analysis.HasMa50Pullback = true;
                analysis.EntryModel = "MA50 pullback swing entry.";
                analysis.Confidence += 28;
                reasons.Add(swingPullback.Reason);
            }
            else
            {
                reasons.Add(swingPullback.Reason);
            }

            if (breakout.IsConfirmed)
            {
                analysis.HasMa20Breakout = true;

                if (string.IsNullOrWhiteSpace(analysis.EntryModel))
                    analysis.EntryModel = "MA20 ustunde breakout entry.";

                analysis.Confidence += 24;
                reasons.Add(breakout.Reason);
            }
            else
            {
                reasons.Add(breakout.Reason);
            }

            var riskPlan = BuildRiskPlan(
                symbol,
                direction,
                candles,
                atr);

            ApplyRiskPlan(
                analysis,
                reasons,
                riskPlan);
        }
        else
        {
            ScoreShortContext(
                analysis,
                reasons,
                structure,
                isMa20Down,
                isMa50Down,
                priceBelow20,
                priceBelow50,
                priceBelow200);

            var swingPullback = DetectShortMa50Pullback(
                candles,
                ma50,
                atr);

            var breakout = DetectShortMa20Breakout(
                candles,
                ma20,
                ma50);

            if (swingPullback.IsConfirmed)
            {
                analysis.HasMa50Pullback = true;
                analysis.EntryModel = "MA50 pullback swing entry.";
                analysis.Confidence += 28;
                reasons.Add(swingPullback.Reason);
            }
            else
            {
                reasons.Add(swingPullback.Reason);
            }

            if (breakout.IsConfirmed)
            {
                analysis.HasMa20Breakout = true;

                if (string.IsNullOrWhiteSpace(analysis.EntryModel))
                    analysis.EntryModel = "MA20 altinda breakout entry.";

                analysis.Confidence += 24;
                reasons.Add(breakout.Reason);
            }
            else
            {
                reasons.Add(breakout.Reason);
            }

            var riskPlan = BuildRiskPlan(
                symbol,
                direction,
                candles,
                atr);

            ApplyRiskPlan(
                analysis,
                reasons,
                riskPlan);
        }

        analysis.Confidence = Math.Clamp(
            analysis.Confidence,
            0,
            100);

        analysis.TradeReady =
            analysis.HasTrendDirection &&
            (analysis.HasMa50Pullback || analysis.HasMa20Breakout) &&
            analysis.IsRiskPlanValid;

        if (!analysis.TradeReady)
        {
            if (!analysis.HasTrendDirection)
                reasons.Add("No trade: MA trend direction temiz deyil.");

            if (!analysis.HasMa50Pullback && !analysis.HasMa20Breakout)
                reasons.Add("No trade: MA50 pullback ve ya MA20 breakout entry yoxdur.");

            if (!analysis.IsRiskPlanValid)
                reasons.Add("No trade: risk plan uygun deyil.");
        }

        analysis.Reasons = reasons.Distinct().ToList();

        return analysis;
    }

    private static void ScoreLongContext(
        DirectionAnalysis analysis,
        List<string> reasons,
        string structure,
        bool isMa20Up,
        bool isMa50Up,
        bool priceAbove20,
        bool priceAbove50,
        bool priceAbove200)
    {
        if (isMa50Up && priceAbove50)
        {
            analysis.HasTrendDirection = true;
            analysis.Confidence += 25;
            reasons.Add("MA50 yuxari baxir ve price MA50 ustundedir.");
        }
        else
        {
            reasons.Add("LONG ucun MA50 trend direction tam uygun deyil.");
        }

        if (isMa20Up && priceAbove20)
        {
            analysis.Confidence += 12;
            reasons.Add("MA20 yuxari momentum destekleyir.");
        }
        else
        {
            reasons.Add("MA20 LONG momentum ucun tam guclu deyil.");
        }

        if (priceAbove200)
        {
            analysis.Confidence += 6;
            reasons.Add("Price MA200 ustundedir, uzun trend LONG ucun destekleyicidir.");
        }
        else
        {
            reasons.Add("Price MA200 altindadir, uzun trend LONG ucun zeifdir.");
        }

        if (structure == "UPTREND")
        {
            analysis.Confidence += 12;
            reasons.Add("Price structure higher high / higher low formasindadir.");
        }
        else
        {
            reasons.Add($"Price structure LONG ucun tam uygun deyil: {structure}.");
        }
    }

    private static void ScoreShortContext(
        DirectionAnalysis analysis,
        List<string> reasons,
        string structure,
        bool isMa20Down,
        bool isMa50Down,
        bool priceBelow20,
        bool priceBelow50,
        bool priceBelow200)
    {
        if (isMa50Down && priceBelow50)
        {
            analysis.HasTrendDirection = true;
            analysis.Confidence += 25;
            reasons.Add("MA50 asagi baxir ve price MA50 altindadir.");
        }
        else
        {
            reasons.Add("SHORT ucun MA50 trend direction tam uygun deyil.");
        }

        if (isMa20Down && priceBelow20)
        {
            analysis.Confidence += 12;
            reasons.Add("MA20 asagi momentum destekleyir.");
        }
        else
        {
            reasons.Add("MA20 SHORT momentum ucun tam guclu deyil.");
        }

        if (priceBelow200)
        {
            analysis.Confidence += 6;
            reasons.Add("Price MA200 altindadir, uzun trend SHORT ucun destekleyicidir.");
        }
        else
        {
            reasons.Add("Price MA200 ustundedir, uzun trend SHORT ucun zeifdir.");
        }

        if (structure == "DOWNTREND")
        {
            analysis.Confidence += 12;
            reasons.Add("Price structure lower high / lower low formasindadir.");
        }
        else
        {
            reasons.Add($"Price structure SHORT ucun tam uygun deyil: {structure}.");
        }
    }

    private static (bool IsConfirmed, string Reason) DetectLongMa50Pullback(
        List<PriceCandle> candles,
        double ma50,
        double atr)
    {
        var recent = candles.TakeLast(8).ToList();
        var last = candles[^1];

        var touchedMa50 = recent.Any(x =>
            x.Low <= ma50 + atr * 0.25 &&
            x.Close >= ma50 - atr * 0.10);

        var rejectedUp =
            last.Close > ma50 &&
            last.Close >= last.Open;

        if (touchedMa50 && rejectedUp)
        {
            return (
                true,
                "Price MA50 zonasina geri cekildi ve yuxari reaksiya verdi.");
        }

        return (
            false,
            "MA50 pullback LONG entry hele yoxdur.");
    }

    private static (bool IsConfirmed, string Reason) DetectShortMa50Pullback(
        List<PriceCandle> candles,
        double ma50,
        double atr)
    {
        var recent = candles.TakeLast(8).ToList();
        var last = candles[^1];

        var touchedMa50 = recent.Any(x =>
            x.High >= ma50 - atr * 0.25 &&
            x.Close <= ma50 + atr * 0.10);

        var rejectedDown =
            last.Close < ma50 &&
            last.Close <= last.Open;

        if (touchedMa50 && rejectedDown)
        {
            return (
                true,
                "Price MA50 zonasina geri cekildi ve asagi reaksiya verdi.");
        }

        return (
            false,
            "MA50 pullback SHORT entry hele yoxdur.");
    }

    private static (bool IsConfirmed, string Reason) DetectLongMa20Breakout(
        List<PriceCandle> candles,
        double ma20,
        double ma50)
    {
        var last = candles[^1];
        var previous = candles[^2];

        var reference = candles
            .Skip(Math.Max(0, candles.Count - 22))
            .Take(20)
            .ToList();

        var recentHigh = reference
            .Take(reference.Count - 1)
            .Max(x => x.High);

        var breakout =
            last.Close > recentHigh &&
            previous.Close <= recentHigh &&
            last.Close > ma20 &&
            last.Close > ma50 &&
            ma20 > ma50;

        if (breakout)
        {
            return (
                true,
                "Price MA20 ustunde qalaraq son tepeni breakout etdi.");
        }

        return (
            false,
            "MA20 breakout LONG entry yoxdur.");
    }

    private static (bool IsConfirmed, string Reason) DetectShortMa20Breakout(
        List<PriceCandle> candles,
        double ma20,
        double ma50)
    {
        var last = candles[^1];
        var previous = candles[^2];

        var reference = candles
            .Skip(Math.Max(0, candles.Count - 22))
            .Take(20)
            .ToList();

        var recentLow = reference
            .Take(reference.Count - 1)
            .Min(x => x.Low);

        var breakout =
            last.Close < recentLow &&
            previous.Close >= recentLow &&
            last.Close < ma20 &&
            last.Close < ma50 &&
            ma20 < ma50;

        if (breakout)
        {
            return (
                true,
                "Price MA20 altinda qalaraq son dibi breakout etdi.");
        }

        return (
            false,
            "MA20 breakout SHORT entry yoxdur.");
    }

    private static RiskPlan BuildRiskPlan(
        string symbol,
        string direction,
        List<PriceCandle> candles,
        double atr)
    {
        var last = candles[^1];
        var entry = (decimal)last.Close;

        var recent = candles.TakeLast(14).ToList();

        var buffer = (decimal)(atr * 2.0);

        decimal stopLoss;
        decimal takeProfit1;
        decimal takeProfit2;
        decimal risk;
        decimal invalidLevel;

        if (direction == "LONG")
        {
            var recentLow = (decimal)recent.Min(x => x.Low);

            invalidLevel = recentLow;
            stopLoss = recentLow - buffer;

            if (stopLoss >= entry)
                stopLoss = entry - Math.Abs(buffer);

            risk = entry - stopLoss;

            takeProfit1 = entry + risk * 2m;
            takeProfit2 = entry + risk * 3m;
        }
        else
        {
            var recentHigh = (decimal)recent.Max(x => x.High);

            invalidLevel = recentHigh;
            stopLoss = recentHigh + buffer;

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
            riskPips >= GetMinimumRiskPips(symbol) &&
            riskPips <= GetMaximumRiskPips(symbol) &&
            riskReward1 >= 1.8m &&
            riskReward2 >= 2.5m;

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
            Reason = "SL son swing arxasinda 2 ATR mesafesi ile, TP1 1:2 ve TP2 1:3 risk/reward esasinda hesablandi."
        };
    }

    private static void ApplyRiskPlan(
        DirectionAnalysis analysis,
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
            analysis.Confidence += 13;
            reasons.Add(riskPlan.Reason);
        }
        else
        {
            reasons.Add(riskPlan.InvalidReason);
        }
    }

    private static string DetectStructure(List<PriceCandle> candles)
    {
        var recent = candles.TakeLast(80).ToList();

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
                return "UPTREND";
            }

            if (highs[1].Price < highs[0].Price &&
                lows[1].Price < lows[0].Price)
            {
                return "DOWNTREND";
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

    private static double MovingAverage(
        List<PriceCandle> candles,
        int period,
        int endIndex)
    {
        if (endIndex < 0)
            return 0;

        if (endIndex >= candles.Count)
            endIndex = candles.Count - 1;

        var startIndex = endIndex - period + 1;

        if (startIndex < 0)
            return 0;

        return candles
            .Skip(startIndex)
            .Take(period)
            .Average(x => x.Close);
    }

    private static double AverageTrueRange(
        List<PriceCandle> candles,
        int period)
    {
        if (candles.Count < period + 1)
            return 0;

        var ranges = new List<double>();

        for (var i = 1; i < candles.Count; i++)
        {
            var highLow = candles[i].High - candles[i].Low;
            var highPrevClose = Math.Abs(candles[i].High - candles[i - 1].Close);
            var lowPrevClose = Math.Abs(candles[i].Low - candles[i - 1].Close);

            ranges.Add(
                Math.Max(
                    highLow,
                    Math.Max(
                        highPrevClose,
                        lowPrevClose)));
        }

        return ranges
            .TakeLast(period)
            .Average();
    }

    private static List<ForexStrategyResult> BuildStrategyResults(
        DirectionAnalysis? longAnalysis,
        DirectionAnalysis? shortAnalysis)
    {
        var results = new List<ForexStrategyResult>();

        if (longAnalysis != null)
        {
            results.Add(new ForexStrategyResult
            {
                StrategyName = "MA20_MA50_2ATR",
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
                StrategyName = "MA20_MA50_2ATR",
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
                StrategyName = "MA20_MA50_2ATR",
                Direction = "WAIT",
                Score = 0,
                MaxScore = 100,
                IsConfirmed = false,
                Reasons = new List<string>
                {
                    "Moving Average setup yoxdur."
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

    private static decimal RoundPrice(
        string symbol,
        decimal price)
    {
        var digits = GetDigits(symbol);
        return Math.Round(price, digits);
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

    private sealed class DirectionAnalysis
    {
        public string Direction { get; set; } = string.Empty;

        public int Confidence { get; set; }

        public bool TradeReady { get; set; }

        public bool HasTrendDirection { get; set; }

        public bool HasMa50Pullback { get; set; }

        public bool HasMa20Breakout { get; set; }

        public bool IsRiskPlanValid { get; set; }

        public string EntryModel { get; set; } = string.Empty;

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
            $"Trend={HasTrendDirection}, MA50Pullback={HasMa50Pullback}, MA20Breakout={HasMa20Breakout}, Risk={IsRiskPlanValid}, Ready={TradeReady}";
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
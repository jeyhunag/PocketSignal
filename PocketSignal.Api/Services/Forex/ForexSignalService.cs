using System.Globalization;
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Forex;

public class ForexSignalService : IForexSignalService
{
    private readonly IMarketDataService _marketDataService;

    private const int MinimumScore = 82;

    private readonly List<IForexStrategy> _strategies = new()
{
    new MultiTimeframeConfirmationStrategy(),
    new TrendContinuationStrategy(),
    new ReversalSweepStrategy(),
    new SupportResistanceBounceStrategy(),
    new BreakoutRetestStrategy(),


    new ForexSessionFilterStrategy(),
    new VolatilityFilterStrategy(),
};

    public ForexSignalService(IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<ForexTradeSignal> AnalyzeAsync(
        string symbol,
        string timeframe = "15min",
        CancellationToken cancellationToken = default)
    {
        var h1Response = await _marketDataService.GetCandlesAsync(symbol, "1h", 200, cancellationToken);
        var m15Response = await _marketDataService.GetCandlesAsync(symbol, "15min", 200, cancellationToken);
        var m5Response = await _marketDataService.GetCandlesAsync(symbol, "5min", 200, cancellationToken);

        var h1 = ForexAnalysis.MapCandles(h1Response, symbol);
        var m15 = ForexAnalysis.MapCandles(m15Response, symbol);
        var m5 = ForexAnalysis.MapCandles(m5Response, symbol);

        if (h1.Count < 60 || m15.Count < 60 || m5.Count < 60)
        {
            return CreateWaitSignal(
                symbol,
                0,
                "NO_TRADE",
                new List<string>
                {
                    "Forex analiz ucun kifayet qeder candle data yoxdur."
                },
                new List<ForexStrategyResult>());
        }

        var context = ForexMarketContext.Create(symbol, h1, m15, m5);

        var longResults = new List<ForexStrategyResult>();
        var shortResults = new List<ForexStrategyResult>();
        var allResults = new List<ForexStrategyResult>();

        foreach (var strategy in _strategies)
        {
            if (strategy.IsDirectional)
            {
                var longResult = strategy.Evaluate(context, "LONG");
                var shortResult = strategy.Evaluate(context, "SHORT");

                longResults.Add(longResult);
                shortResults.Add(shortResult);

                allResults.Add(longResult);
                allResults.Add(shortResult);
            }
            else
            {
                var filterResult = strategy.Evaluate(context, "FILTER");

                longResults.Add(filterResult);
                shortResults.Add(filterResult);

                allResults.Add(filterResult);
            }
        }

        var longPreRiskScore = CalculatePercentScore(longResults);
        var shortPreRiskScore = CalculatePercentScore(shortResults);

        var sideAnalyses = new List<SideAnalysis>
        {
            new SideAnalysis
            {
                Direction = "LONG",
                Score = longPreRiskScore,
                Reasons = longResults.SelectMany(x => x.Reasons).ToList()
            },
            new SideAnalysis
            {
                Direction = "SHORT",
                Score = shortPreRiskScore,
                Reasons = shortResults.SelectMany(x => x.Reasons).ToList()
            }
        };

        if (context.H1Bias != "NEUTRAL" &&
            context.M15Bias != "NEUTRAL" &&
            context.H1Bias != context.M15Bias)
        {
            return CreateWaitSignal(
                symbol,
                Math.Max(longPreRiskScore, shortPreRiskScore),
                "NO_TRADE",
                new List<string>
                {
                    $"H1 bias: {context.H1Bias}",
                    $"M15 bias: {context.M15Bias}",
                    "No-trade filter aktivdir: H1 ve M15 istiqametleri ziddir.",
                    "Forex trade ucun bu zona risklidir, signal verilmedi."
                },
                allResults,
                sideAnalyses);
        }

        var bestDirection = longPreRiskScore >= shortPreRiskScore ? "LONG" : "SHORT";

        var bestResults = bestDirection == "LONG"
            ? longResults
            : shortResults;

        var oppositeScore = bestDirection == "LONG"
            ? shortPreRiskScore
            : longPreRiskScore;

        var entry = m5.Last().Close;

        var riskPlan = ForexAnalysis.BuildRiskPlan(
            symbol,
            bestDirection,
            entry,
            m15,
            m5);

        var riskResult = new ForexStrategyResult
        {
            StrategyName = "RiskRewardValidationStrategy",
            Direction = bestDirection,
            Score = riskPlan.IsValid ? 15 : 0,
            MaxScore = 15,
            IsConfirmed = riskPlan.IsValid,
            Reasons = new List<string>
            {
                riskPlan.IsValid
                    ? riskPlan.Reason
                    : riskPlan.InvalidReason
            }
        };

        bestResults.Add(riskResult);
        allResults.Add(riskResult);

        var finalScore = CalculatePercentScore(bestResults);

        sideAnalyses = new List<SideAnalysis>
        {
            new SideAnalysis
            {
                Direction = "LONG",
                Score = bestDirection == "LONG" ? finalScore : longPreRiskScore,
                Reasons = longResults.SelectMany(x => x.Reasons).ToList()
            },
            new SideAnalysis
            {
                Direction = "SHORT",
                Score = bestDirection == "SHORT" ? finalScore : shortPreRiskScore,
                Reasons = shortResults.SelectMany(x => x.Reasons).ToList()
            }
        };

        if (!riskPlan.IsValid)
        {
            return CreateWaitSignal(
                symbol,
                finalScore,
                GetGrade(finalScore),
                new List<string>
                {
                    "Setup tapildi, amma risk plani uygun deyil.",
                    riskPlan.InvalidReason
                },
                allResults,
                sideAnalyses);
        }

        if (finalScore < MinimumScore)
        {
            return CreateWaitSignal(
                symbol,
                Math.Max(finalScore, oppositeScore),
                "NO_TRADE",
                new List<string>
                {
                    $"Best direction: {bestDirection}",
                    $"Best score: {finalScore}",
                    $"Opposite score: {oppositeScore}",
                    $"Minimum lazim olan score: {MinimumScore}",
                    "Multi-strategy confluence kifayet qeder guclu deyil."
                },
                allResults,
                sideAnalyses);
        }

        var reasons = bestResults
            .Where(x => x.Score > 0)
            .SelectMany(x => x.Reasons)
            .Distinct()
            .ToList();

        reasons.Add(riskPlan.Reason);

        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = bestDirection,

            EntryPrice = ForexAnalysis.RoundPrice(symbol, entry),
            StopLoss = ForexAnalysis.RoundPrice(symbol, riskPlan.StopLoss),
            TakeProfit1 = ForexAnalysis.RoundPrice(symbol, riskPlan.TakeProfit1),
            TakeProfit2 = ForexAnalysis.RoundPrice(symbol, riskPlan.TakeProfit2),

            RiskPips = Math.Round(riskPlan.RiskPips, 1),
            RewardPips1 = Math.Round(riskPlan.RewardPips1, 1),
            RewardPips2 = Math.Round(riskPlan.RewardPips2, 1),
            RiskReward1 = Math.Round(riskPlan.RiskReward1, 2),
            RiskReward2 = Math.Round(riskPlan.RiskReward2, 2),

            Confidence = finalScore,
            Grade = GetGrade(finalScore),

            Message =
                $"{symbol} {bestDirection} Entry: {ForexAnalysis.RoundPrice(symbol, entry)} SL: {ForexAnalysis.RoundPrice(symbol, riskPlan.StopLoss)} TP1: {ForexAnalysis.RoundPrice(symbol, riskPlan.TakeProfit1)} TP2: {ForexAnalysis.RoundPrice(symbol, riskPlan.TakeProfit2)}",

            InvalidIf = bestDirection == "LONG"
                ? $"M5 candle {ForexAnalysis.RoundPrice(symbol, riskPlan.StopLoss)} altinda baglansa trade legvdir."
                : $"M5 candle {ForexAnalysis.RoundPrice(symbol, riskPlan.StopLoss)} ustunde baglansa trade legvdir.",

            ValidForMinutes = 10,
            Reasons = reasons,
            SideAnalyses = sideAnalyses,
            StrategyResults = allResults,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static ForexTradeSignal CreateWaitSignal(
        string symbol,
        int confidence,
        string grade,
        List<string> reasons,
        List<ForexStrategyResult> strategyResults,
        List<SideAnalysis>? sideAnalyses = null)
    {
        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            Confidence = confidence,
            Grade = grade,
            Message = $"{symbol} FOREX WAIT",
            Reasons = reasons,
            SideAnalyses = sideAnalyses ?? new List<SideAnalysis>(),
            StrategyResults = strategyResults,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static int CalculatePercentScore(List<ForexStrategyResult> results)
    {
        var maxScore = results.Sum(x => x.MaxScore);

        if (maxScore <= 0)
            return 0;

        var score = results.Sum(x => x.Score);

        return Math.Clamp(
            (int)Math.Round((decimal)score / maxScore * 100m),
            0,
            100);
    }

    private static string GetGrade(int score)
    {
        if (score >= 90)
            return "A+";

        if (score >= 82)
            return "A";

        if (score >= 70)
            return "B";

        return "NO_TRADE";
    }
}

internal interface IForexStrategy
{
    string Name { get; }

    int MaxScore { get; }

    bool IsDirectional { get; }

    ForexStrategyResult Evaluate(
        ForexMarketContext context,
        string direction);
}

internal class MultiTimeframeConfirmationStrategy : IForexStrategy
{
    public string Name => "MultiTimeframeConfirmationStrategy";

    public int MaxScore => 20;

    public bool IsDirectional => true;

    public ForexStrategyResult Evaluate(
        ForexMarketContext context,
        string direction)
    {
        var score = 0;
        var reasons = new List<string>();

        if (context.H1Bias == direction)
        {
            score += 10;
            reasons.Add($"H1 bias {direction} istiqametindedir.");
        }
        else if (context.H1Bias == "NEUTRAL")
        {
            score += 3;
            reasons.Add("H1 bias neytraldir.");
        }
        else
        {
            reasons.Add($"H1 bias {direction} istiqametine uygun deyil.");
        }

        if (context.M15Bias == direction)
        {
            score += 8;
            reasons.Add($"M15 bias {direction} istiqametindedir.");
        }
        else if (context.M15Bias == "NEUTRAL")
        {
            score += 3;
            reasons.Add("M15 bias neytraldir.");
        }
        else
        {
            reasons.Add($"M15 bias {direction} istiqametine uygun deyil.");
        }

        if (context.H1Bias == direction && context.M15Bias == direction)
        {
            score += 2;
            reasons.Add("H1 ve M15 eyni istiqameti tesdiq edir.");
        }

        return ForexStrategyResultFactory.Result(
            Name,
            direction,
            score,
            MaxScore,
            score >= 15,
            reasons);
    }
}

internal class TrendContinuationStrategy : IForexStrategy
{
    public string Name => "TrendContinuationStrategy";

    public int MaxScore => 25;

    public bool IsDirectional => true;

    public ForexStrategyResult Evaluate(
        ForexMarketContext context,
        string direction)
    {
        var score = 0;
        var reasons = new List<string>();

        if (context.H1Bias == direction && context.M15Bias == direction)
        {
            score += 12;
            reasons.Add($"Trend continuation: H1 ve M15 {direction} istiqametinde eynidir.");
        }
        else if (context.H1Bias == direction || context.M15Bias == direction)
        {
            score += 5;
            reasons.Add($"Trend continuation: yalniz bir HTF {direction} istiqametini destekleyir.");
        }

        var sma20 = ForexAnalysis.Sma(
            context.M15.TakeLast(20).Select(x => x.Close).ToList());

        var sma50 = ForexAnalysis.Sma(
            context.M15.TakeLast(50).Select(x => x.Close).ToList());

        if (sma20 > 0 && sma50 > 0)
        {
            if (direction == "LONG" &&
                context.LastClose > sma20 &&
                sma20 > sma50)
            {
                score += 8;
                reasons.Add("M15 momentum LONG trend continuation ucun uygundur.");
            }

            if (direction == "SHORT" &&
                context.LastClose < sma20 &&
                sma20 < sma50)
            {
                score += 8;
                reasons.Add("M15 momentum SHORT trend continuation ucun uygundur.");
            }
        }

        if (ForexAnalysis.IsEntryClean(context.M5))
        {
            score += 5;
            reasons.Add("Trend continuation ucun entry gecikmis deyil.");
        }

        return ForexStrategyResultFactory.Result(
            Name,
            direction,
            score,
            MaxScore,
            score >= 17,
            reasons);
    }
}

internal class ReversalSweepStrategy : IForexStrategy
{
    public string Name => "ReversalSweepStrategy";

    public int MaxScore => 20;

    public bool IsDirectional => true;

    public ForexStrategyResult Evaluate(
        ForexMarketContext context,
        string direction)
    {
        var score = 0;
        var reasons = new List<string>();

        var sweep = ForexAnalysis.HasLiquiditySweep(context.M5, direction);

        if (sweep)
        {
            score += 8;
            reasons.Add(direction == "LONG"
                ? "Reversal sweep: M5 sell-side liquidity sweep tapildi."
                : "Reversal sweep: M5 buy-side liquidity sweep tapildi.");
        }

        var choch = ForexAnalysis.HasChoch(context.M5, direction);

        if (choch)
        {
            score += 7;
            reasons.Add($"Reversal sweep: M5 {direction} CHoCH/BOS tesdiqi var.");
        }

        var priceAction = ForexAnalysis.HasPriceActionConfirmation(context.M5, direction);

        if (priceAction.IsConfirmed)
        {
            score += 5;
            reasons.Add(priceAction.Reason);
        }

        return ForexStrategyResultFactory.Result(
            Name,
            direction,
            score,
            MaxScore,
            score >= 15,
            reasons);
    }
}

internal class SupportResistanceBounceStrategy : IForexStrategy
{
    public string Name => "SupportResistanceBounceStrategy";

    public int MaxScore => 20;

    public bool IsDirectional => true;

    public ForexStrategyResult Evaluate(
        ForexMarketContext context,
        string direction)
    {
        var score = 0;
        var reasons = new List<string>();

        var zone = context.M15Zones
            .Where(x => x.Direction == direction)
            .OrderByDescending(x => x.Time)
            .FirstOrDefault(x => x.Contains(context.LastClose, context.ZoneTolerance));

        if (zone != null)
        {
            score += 12;
            reasons.Add($"Support/Resistance: qiymet M15 {zone.Type} zonasinda/retest zonasindadir.");
        }

        var priceAction = ForexAnalysis.HasPriceActionConfirmation(context.M5, direction);

        if (priceAction.IsConfirmed)
        {
            score += 5;
            reasons.Add("Support/Resistance zonasinda price action tesdiqi var.");
        }

        if (ForexAnalysis.IsEntryClean(context.M5))
        {
            score += 3;
            reasons.Add("Support/Resistance entry gecikmis deyil.");
        }

        return ForexStrategyResultFactory.Result(
            Name,
            direction,
            score,
            MaxScore,
            score >= 14,
            reasons);
    }
}

internal class BreakoutRetestStrategy : IForexStrategy
{
    public string Name => "BreakoutRetestStrategy";

    public int MaxScore => 15;

    public bool IsDirectional => true;

    public ForexStrategyResult Evaluate(
        ForexMarketContext context,
        string direction)
    {
        var score = 0;
        var reasons = new List<string>();

        var choch = ForexAnalysis.HasChoch(context.M5, direction);

        if (choch)
        {
            score += 7;
            reasons.Add($"Breakout/Retest: M5 {direction} BOS/CHoCH var.");
        }

        var breakout = ForexAnalysis.HasRecentBreakout(context.M5, direction);

        if (breakout)
        {
            score += 5;
            reasons.Add($"Breakout/Retest: son M5 strukturunda {direction} breakout gorunur.");
        }

        if (ForexAnalysis.IsEntryClean(context.M5))
        {
            score += 3;
            reasons.Add("Breakout/Retest entry cox uzaqlasmayib.");
        }

        return ForexStrategyResultFactory.Result(
            Name,
            direction,
            score,
            MaxScore,
            score >= 11,
            reasons);
    }
}


internal class ForexSessionFilterStrategy : IForexStrategy
{
    public string Name => "ForexSessionFilterStrategy";

    public int MaxScore => 10;

    public bool IsDirectional => false;

    public ForexStrategyResult Evaluate(
        ForexMarketContext context,
        string direction)
    {
        var utcNow = DateTime.UtcNow;
        var hour = utcNow.Hour;
        var day = utcNow.DayOfWeek;

        var score = 0;
        var reasons = new List<string>();

        if (day == DayOfWeek.Saturday || day == DayOfWeek.Sunday)
        {
            reasons.Add("Session filter: hefte sonudur, forex trade ucun risklidir.");

            return ForexStrategyResultFactory.Result(
                Name,
                "FILTER",
                0,
                MaxScore,
                false,
                reasons);
        }

        if (day == DayOfWeek.Monday && hour < 6)
        {
            reasons.Add("Session filter: bazar ertesi erken saatlar, spread/likvidlik riski ola biler.");

            return ForexStrategyResultFactory.Result(
                Name,
                "FILTER",
                2,
                MaxScore,
                false,
                reasons);
        }

        if (day == DayOfWeek.Friday && hour >= 20)
        {
            reasons.Add("Session filter: cume gec saatlar, hefte sonu oncesi trade riski artir.");

            return ForexStrategyResultFactory.Result(
                Name,
                "FILTER",
                2,
                MaxScore,
                false,
                reasons);
        }

        var isLondonSession = hour >= 7 && hour < 12;
        var isLondonNewYorkOverlap = hour >= 12 && hour < 16;
        var isNewYorkSession = hour >= 16 && hour < 20;
        var isAsiaSession = hour >= 0 && hour < 7;
        var isDeadZone = hour >= 20 && hour <= 23;

        if (isLondonNewYorkOverlap)
        {
            score = 10;
            reasons.Add("Session filter: London/New York overlap aktivdir, forex ucun en guclu likvidlik zonalarindan biridir.");
        }
        else if (isLondonSession)
        {
            score = 9;
            reasons.Add("Session filter: London sessiyasi aktivdir, GBP/JPY ucun uygun vaxtdir.");
        }
        else if (isNewYorkSession)
        {
            score = 7;
            reasons.Add("Session filter: New York sessiyasi aktivdir, trade ucun qebul edile biler vaxtdir.");
        }
        else if (isAsiaSession)
        {
            score = 5;
            reasons.Add("Session filter: Asia sessiyasidir, GBP/JPY hereket ede biler amma fake move riski daha yuksekdir.");
        }
        else if (isDeadZone)
        {
            score = 3;
            reasons.Add("Session filter: gec saatlar/dead zone, likvidlik zeif ola biler.");
        }
        else
        {
            score = 4;
            reasons.Add("Session filter: orta keyfiyyetli saat araligidir.");
        }

        var isConfirmed = score >= 7;

        if (!isConfirmed)
        {
            reasons.Add("Session filter: trade ucun ideal saat deyil, score azaldildi.");
        }

        return ForexStrategyResultFactory.Result(
            Name,
            "FILTER",
            score,
            MaxScore,
            isConfirmed,
            reasons);
    }
}

internal class VolatilityFilterStrategy : IForexStrategy
{
    public string Name => "VolatilityFilterStrategy";

    public int MaxScore => 10;

    public bool IsDirectional => false;

    public ForexStrategyResult Evaluate(
        ForexMarketContext context,
        string direction)
    {
        var score = context.IsVolatilityNormal ? 10 : 0;

        return ForexStrategyResultFactory.Result(
            Name,
            "FILTER",
            score,
            MaxScore,
            context.IsVolatilityNormal,
            new List<string>
            {
                context.VolatilityReason
            });
    }
}

internal class ForexMarketContext
{
    public string Symbol { get; set; } = string.Empty;

    public List<Candle> H1 { get; set; } = new();

    public List<Candle> M15 { get; set; } = new();

    public List<Candle> M5 { get; set; } = new();

    public string H1Bias { get; set; } = "NEUTRAL";

    public string M15Bias { get; set; } = "NEUTRAL";

    public decimal LastClose { get; set; }

    public decimal AvgRangeM5 { get; set; }

    public decimal ZoneTolerance { get; set; }

    public bool IsVolatilityNormal { get; set; }

    public string VolatilityReason { get; set; } = string.Empty;

    public List<PriceZone> M15Zones { get; set; } = new();

    public static ForexMarketContext Create(
        string symbol,
        List<Candle> h1,
        List<Candle> m15,
        List<Candle> m5)
    {
        var lastClose = m5.Last().Close;
        var avgRangeM5 = ForexAnalysis.AverageRange(m5.TakeLast(20).ToList());
        var volatility = ForexAnalysis.IsVolatilityNormal(m5);

        return new ForexMarketContext
        {
            Symbol = symbol,
            H1 = h1,
            M15 = m15,
            M5 = m5,
            H1Bias = ForexAnalysis.GetStructureBias(h1),
            M15Bias = ForexAnalysis.GetStructureBias(m15),
            LastClose = lastClose,
            AvgRangeM5 = avgRangeM5,
            ZoneTolerance = Math.Max(avgRangeM5 * 1.2m, lastClose * 0.00012m),
            IsVolatilityNormal = volatility.IsNormal,
            VolatilityReason = volatility.Reason,
            M15Zones = ForexAnalysis.DetectZones(m15)
        };
    }
}

internal static class ForexStrategyResultFactory
{
    public static ForexStrategyResult Result(
        string strategyName,
        string direction,
        int score,
        int maxScore,
        bool isConfirmed,
        List<string> reasons)
    {
        return new ForexStrategyResult
        {
            StrategyName = strategyName,
            Direction = direction,
            Score = Math.Clamp(score, 0, maxScore),
            MaxScore = maxScore,
            IsConfirmed = isConfirmed,
            Reasons = reasons
        };
    }
}

internal static class ForexAnalysis
{
    public static List<Candle> MapCandles(
        TwelveDataResponse? response,
        string symbol)
    {
        if (response?.Values == null)
            return new List<Candle>();

        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd"
        };

        var candles = new List<Candle>();

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

            candles.Add(new Candle
            {
                Symbol = symbol,
                Time = time,
                Open = item.Open,
                High = item.High,
                Low = item.Low,
                Close = item.Close
            });
        }

        return candles
            .OrderBy(x => x.Time)
            .ToList();
    }

    public static string GetStructureBias(List<Candle> candles)
    {
        var swings = FindSwings(candles, 2, 2);

        var highs = swings
            .Where(x => x.Kind == SwingKind.High)
            .OrderBy(x => x.Time)
            .TakeLast(2)
            .ToList();

        var lows = swings
            .Where(x => x.Kind == SwingKind.Low)
            .OrderBy(x => x.Time)
            .TakeLast(2)
            .ToList();

        if (highs.Count >= 2 && lows.Count >= 2)
        {
            var higherHigh = highs[1].Price > highs[0].Price;
            var higherLow = lows[1].Price > lows[0].Price;

            var lowerHigh = highs[1].Price < highs[0].Price;
            var lowerLow = lows[1].Price < lows[0].Price;

            if (higherHigh && higherLow)
                return "LONG";

            if (lowerHigh && lowerLow)
                return "SHORT";
        }

        var last40 = candles.TakeLast(40).ToList();

        if (last40.Count < 40)
            return "NEUTRAL";

        var first = last40.First().Close;
        var last = last40.Last().Close;

        if (last > first)
            return "LONG";

        if (last < first)
            return "SHORT";

        return "NEUTRAL";
    }

    public static List<SwingPoint> FindSwings(
        List<Candle> candles,
        int left,
        int right)
    {
        var swings = new List<SwingPoint>();

        if (candles.Count < left + right + 1)
            return swings;

        for (int i = left; i < candles.Count - right; i++)
        {
            var isHigh = true;
            var isLow = true;

            for (int j = i - left; j <= i + right; j++)
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
                    Time = candles[i].Time,
                    Price = candles[i].High,
                    Kind = SwingKind.High
                });
            }

            if (isLow)
            {
                swings.Add(new SwingPoint
                {
                    Index = i,
                    Time = candles[i].Time,
                    Price = candles[i].Low,
                    Kind = SwingKind.Low
                });
            }
        }

        return swings;
    }

    public static List<PriceZone> DetectZones(List<Candle> candles)
    {
        var zones = new List<PriceZone>();

        if (candles.Count < 30)
            return zones;

        var recent = candles.TakeLast(100).ToList();

        for (int i = 2; i < recent.Count; i++)
        {
            var c0 = recent[i - 2];
            var c2 = recent[i];

            if (c2.Low > c0.High)
            {
                zones.Add(new PriceZone
                {
                    Type = "Bullish FVG",
                    Direction = "LONG",
                    Time = c2.Time,
                    Low = c0.High,
                    High = c2.Low
                });
            }

            if (c2.High < c0.Low)
            {
                zones.Add(new PriceZone
                {
                    Type = "Bearish FVG",
                    Direction = "SHORT",
                    Time = c2.Time,
                    Low = c2.High,
                    High = c0.Low
                });
            }
        }

        var avgBody = recent
            .Select(x => Math.Abs(x.Close - x.Open))
            .DefaultIfEmpty(0)
            .Average();

        for (int i = 1; i < recent.Count; i++)
        {
            var previous = recent[i - 1];
            var current = recent[i];

            var currentBody = Math.Abs(current.Close - current.Open);

            if (current.Close > current.Open &&
                currentBody > avgBody * 1.5m &&
                previous.Close < previous.Open)
            {
                zones.Add(new PriceZone
                {
                    Type = "Bullish OrderBlock",
                    Direction = "LONG",
                    Time = previous.Time,
                    Low = previous.Low,
                    High = previous.High
                });
            }

            if (current.Close < current.Open &&
                currentBody > avgBody * 1.5m &&
                previous.Close > previous.Open)
            {
                zones.Add(new PriceZone
                {
                    Type = "Bearish OrderBlock",
                    Direction = "SHORT",
                    Time = previous.Time,
                    Low = previous.Low,
                    High = previous.High
                });
            }
        }

        return zones;
    }

    public static bool HasLiquiditySweep(
        List<Candle> candles,
        string direction)
    {
        if (candles.Count < 40)
            return false;

        var reference = candles
            .Take(candles.Count - 3)
            .TakeLast(30)
            .ToList();

        var last3 = candles.TakeLast(3).ToList();

        var keyLow = reference.Min(x => x.Low);
        var keyHigh = reference.Max(x => x.High);

        if (direction == "LONG")
            return last3.Any(x => x.Low < keyLow && x.Close > keyLow);

        return last3.Any(x => x.High > keyHigh && x.Close < keyHigh);
    }

    public static bool HasChoch(
        List<Candle> candles,
        string direction)
    {
        if (candles.Count < 40)
            return false;

        var beforeLast = candles.Take(candles.Count - 1).ToList();
        var swings = FindSwings(beforeLast, 2, 2);
        var lastClose = candles.Last().Close;

        if (direction == "LONG")
        {
            var lastSwingHigh = swings
                .Where(x => x.Kind == SwingKind.High)
                .OrderBy(x => x.Time)
                .LastOrDefault();

            return lastSwingHigh != null &&
                   lastClose > lastSwingHigh.Price;
        }

        var lastSwingLow = swings
            .Where(x => x.Kind == SwingKind.Low)
            .OrderBy(x => x.Time)
            .LastOrDefault();

        return lastSwingLow != null &&
               lastClose < lastSwingLow.Price;
    }

    public static bool HasRecentBreakout(
        List<Candle> candles,
        string direction)
    {
        if (candles.Count < 40)
            return false;

        var reference = candles
            .Take(candles.Count - 3)
            .TakeLast(25)
            .ToList();

        var last = candles.Last();

        var high = reference.Max(x => x.High);
        var low = reference.Min(x => x.Low);

        if (direction == "LONG")
            return last.Close > high;

        return last.Close < low;
    }

    public static (bool IsConfirmed, string Reason) HasPriceActionConfirmation(
        List<Candle> candles,
        string direction)
    {
        if (candles.Count < 3)
            return (false, "Price action ucun kifayet qeder candle yoxdur.");

        var previous = candles[^2];
        var last = candles[^1];

        var body = Math.Abs(last.Close - last.Open);
        var totalRange = last.High - last.Low;

        if (totalRange <= 0)
            return (false, "Son candle range sifirdir.");

        var upperWick = last.High - Math.Max(last.Open, last.Close);
        var lowerWick = Math.Min(last.Open, last.Close) - last.Low;
        var closePosition = (last.Close - last.Low) / totalRange;

        if (direction == "LONG")
        {
            var bullish = last.Close > last.Open;
            var lowerRejection = lowerWick >= body * 1.1m;
            var strongClose = closePosition >= 0.62m;

            var bullishEngulfing =
                previous.Close < previous.Open &&
                last.Close > previous.Open &&
                last.Open <= previous.Close;

            if ((bullish && lowerRejection && strongClose) || bullishEngulfing)
                return (true, "M5 bullish rejection/engulfing price action tesdiqi var.");
        }
        else
        {
            var bearish = last.Close < last.Open;
            var upperRejection = upperWick >= body * 1.1m;
            var strongClose = closePosition <= 0.38m;

            var bearishEngulfing =
                previous.Close > previous.Open &&
                last.Close < previous.Open &&
                last.Open >= previous.Close;

            if ((bearish && upperRejection && strongClose) || bearishEngulfing)
                return (true, "M5 bearish rejection/engulfing price action tesdiqi var.");
        }

        return (false, "Son M5 candle price action tesdiqi vermir.");
    }

    public static (bool IsNormal, string Reason) IsVolatilityNormal(
        List<Candle> candles)
    {
        if (candles.Count < 20)
            return (false, "Volatility analiz ucun kifayet qeder candle yoxdur.");

        var recent = candles.TakeLast(20).ToList();
        var avgRange = AverageRange(recent);
        var lastClose = candles.Last().Close;

        if (lastClose <= 0)
            return (false, "Qiymet duzgun deyil.");

        var rangePercent = avgRange / lastClose * 100m;

        if (rangePercent < 0.01m)
            return (false, "Volatility cox zeifdir.");

        if (rangePercent > 0.25m)
            return (false, "Volatility cox yuksekdir, SL boyuye biler.");

        return (true, "Volatility normal araliqdadir.");
    }

    public static bool IsEntryClean(List<Candle> candles)
    {
        if (candles.Count < 20)
            return false;

        var recent = candles.TakeLast(20).ToList();
        var avgRange = AverageRange(recent);

        var last = candles[^1];
        var fourthBack = candles[^4];

        var move = Math.Abs(last.Close - fourthBack.Close);

        return move <= avgRange * 3.5m;
    }

    public static decimal AverageRange(List<Candle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.High - x.Low);
    }

    public static decimal Sma(List<decimal> values)
    {
        if (values.Count == 0)
            return 0;

        return values.Average();
    }

    public static decimal CalculateAtr(
        List<Candle> candles,
        int period)
    {
        if (candles.Count < period + 1)
            return 0;

        var recent = candles.TakeLast(period + 1).ToList();
        var trueRanges = new List<decimal>();

        for (int i = 1; i < recent.Count; i++)
        {
            var current = recent[i];
            var previous = recent[i - 1];

            var tr1 = current.High - current.Low;
            var tr2 = Math.Abs(current.High - previous.Close);
            var tr3 = Math.Abs(current.Low - previous.Close);

            trueRanges.Add(Math.Max(tr1, Math.Max(tr2, tr3)));
        }

        return trueRanges.Average();
    }

    public static decimal GetPipSize(string symbol)
    {
        return symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase)
            ? 0.01m
            : 0.0001m;
    }

    public static decimal RoundPrice(
        string symbol,
        decimal price)
    {
        return symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase)
            ? Math.Round(price, 3)
            : Math.Round(price, 5);
    }

    public static (
        bool IsValid,
        decimal StopLoss,
        decimal TakeProfit1,
        decimal TakeProfit2,
        decimal RiskPips,
        decimal RewardPips1,
        decimal RewardPips2,
        decimal RiskReward1,
        decimal RiskReward2,
        string Reason,
        string InvalidReason
    ) BuildRiskPlan(
        string symbol,
        string direction,
        decimal entry,
        List<Candle> m15,
        List<Candle> m5)
    {
        var pipSize = GetPipSize(symbol);
        var atr = CalculateAtr(m5, 14);

        if (atr <= 0)
            return Invalid("ATR hesablana bilmedi.");

        var recent = m5.TakeLast(18).ToList();
        var buffer = atr * 0.35m;

        decimal stopLoss;

        if (direction == "LONG")
            stopLoss = recent.Min(x => x.Low) - buffer;
        else
            stopLoss = recent.Max(x => x.High) + buffer;

        var riskDistance = Math.Abs(entry - stopLoss);
        var riskPips = riskDistance / pipSize;

        if (riskPips < 8)
            return Invalid($"Risk mesafesi cox dardir: {Math.Round(riskPips, 1)} pips.");

        if (riskPips > 90)
            return Invalid($"Risk mesafesi cox boyukdur: {Math.Round(riskPips, 1)} pips.");

        decimal takeProfit1;
        decimal takeProfit2;

        if (direction == "LONG")
        {
            takeProfit1 = entry + riskDistance * 2m;
            takeProfit2 = entry + riskDistance * 3m;
        }
        else
        {
            takeProfit1 = entry - riskDistance * 2m;
            takeProfit2 = entry - riskDistance * 3m;
        }

        var rewardPips1 = Math.Abs(takeProfit1 - entry) / pipSize;
        var rewardPips2 = Math.Abs(takeProfit2 - entry) / pipSize;

        return (
            true,
            stopLoss,
            takeProfit1,
            takeProfit2,
            riskPips,
            rewardPips1,
            rewardPips2,
            rewardPips1 / riskPips,
            rewardPips2 / riskPips,
            "Risk plan uygundur. SL son M5 strukturun arxasinda, TP1 1:2, TP2 1:3 RR ile hesablandi.",
            string.Empty
        );

        static (
            bool IsValid,
            decimal StopLoss,
            decimal TakeProfit1,
            decimal TakeProfit2,
            decimal RiskPips,
            decimal RewardPips1,
            decimal RewardPips2,
            decimal RiskReward1,
            decimal RiskReward2,
            string Reason,
            string InvalidReason
        ) Invalid(string reason)
        {
            return (
                false,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                string.Empty,
                reason
            );
        }
    }
}
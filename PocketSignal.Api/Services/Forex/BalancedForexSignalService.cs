using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.Forex.Strategies;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Forex;

public class BalancedForexSignalService : IForexSignalService
{
    private readonly IMarketDataService _marketDataService;

    private const int MinimumTradeScore = 72;
    private const int WatchlistScore = 62;
    private const int MinimumGapBetweenSides = 8;

    private readonly List<IForexStrategy> _strategies = new()
    {
        new MultiTimeframeConfirmationStrategy(),
        new TrendContinuationStrategy(),
        new ReversalSweepStrategy(),
        new SupportResistanceBounceStrategy(),
        new BreakoutRetestStrategy(),

        new PatternBreakoutConfirmationStrategy(),
        new FalseBreakoutTrapStrategy(),
        new NarrowRangeInsideBarStrategy(),

        new VolatilityFilterStrategy(),
    };

    public BalancedForexSignalService(IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<ForexTradeSignal> AnalyzeAsync(
        string symbol,
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

        var longPreContextScore = CalculateAdaptiveScore(longResults);
        var shortPreContextScore = CalculateAdaptiveScore(shortResults);

        var bestDirection = longPreContextScore >= shortPreContextScore
            ? "LONG"
            : "SHORT";

        var bestResults = bestDirection == "LONG"
            ? longResults
            : shortResults;

        var oppositeResults = bestDirection == "LONG"
            ? shortResults
            : longResults;

        var oppositeScore = bestDirection == "LONG"
            ? shortPreContextScore
            : longPreContextScore;

        var scoreGap = Math.Abs(longPreContextScore - shortPreContextScore);

        var contextResult = BuildBalancedContextResult(
            context,
            bestDirection,
            bestResults,
            oppositeResults,
            longPreContextScore,
            shortPreContextScore);

        bestResults.Add(contextResult);
        allResults.Add(contextResult);

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

        var finalScore = CalculateAdaptiveScore(bestResults);

        var sideAnalyses = new List<SideAnalysis>
        {
            new SideAnalysis
            {
                Direction = "LONG",
                Score = bestDirection == "LONG" ? finalScore : longPreContextScore,
                Reasons = longResults.SelectMany(x => x.Reasons).ToList()
            },
            new SideAnalysis
            {
                Direction = "SHORT",
                Score = bestDirection == "SHORT" ? finalScore : shortPreContextScore,
                Reasons = shortResults.SelectMany(x => x.Reasons).ToList()
            }
        };

        var htfConflict =
            context.H1Bias != "NEUTRAL" &&
            context.M15Bias != "NEUTRAL" &&
            context.H1Bias != context.M15Bias;

        var hasStrongTrigger = HasStrongDirectionalTrigger(bestResults);

        if (htfConflict && !hasStrongTrigger)
        {
            return CreateWaitSignal(
                symbol,
                Math.Max(finalScore, oppositeScore),
                "NO_TRADE",
                new List<string>
                {
                    $"Best direction: {bestDirection}",
                    $"H1 bias: {context.H1Bias}",
                    $"M15 bias: {context.M15Bias}",
                    "Forex filter: H1 ve M15 ziddir.",
                    "Ziddiyyetli HTF ucun reversal/breakout trigger kifayet qeder guclu deyil.",
                    "Bu zona risklidir, trade acilmadi."
                },
                allResults,
                sideAnalyses);
        }

        if (scoreGap < MinimumGapBetweenSides)
        {
            return CreateWaitSignal(
                symbol,
                Math.Max(finalScore, oppositeScore),
                "NO_TRADE",
                new List<string>
                {
                    $"LONG score: {longPreContextScore}",
                    $"SHORT score: {shortPreContextScore}",
                    $"Score gap: {scoreGap}",
                    $"Minimum lazim olan gap: {MinimumGapBetweenSides}",
                    "LONG ve SHORT arasinda ustunluk cox azdir, istiqamet aydin deyil."
                },
                allResults,
                sideAnalyses);
        }

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

        if (finalScore < WatchlistScore)
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
                    $"Watchlist score: {WatchlistScore}",
                    "Forex setup hele zeifdir."
                },
                allResults,
                sideAnalyses);
        }

        if (finalScore < MinimumTradeScore)
        {
            return CreateWaitSignal(
                symbol,
                finalScore,
                "WATCHLIST",
                new List<string>
                {
                    $"Watchlist direction: {bestDirection}",
                    $"Watchlist score: {finalScore}",
                    $"Telegram ucun lazim olan score: {MinimumTradeScore}",
                    "Setup formalasir, amma real trade ucun hele tam hazir deyil.",
                    riskPlan.Reason
                },
                allResults,
                sideAnalyses);
        }

        var reasons = bestResults
            .Where(x => x.Score > 0)
            .SelectMany(x => x.Reasons)
            .Distinct()
            .ToList();

        reasons.Insert(0, $"Forex balanced engine: {bestDirection} signal tesdiqlendi.");
        reasons.Add(riskPlan.Reason);

        var roundedEntry = ForexAnalysis.RoundPrice(symbol, entry);
        var roundedSl = ForexAnalysis.RoundPrice(symbol, riskPlan.StopLoss);
        var roundedTp1 = ForexAnalysis.RoundPrice(symbol, riskPlan.TakeProfit1);
        var roundedTp2 = ForexAnalysis.RoundPrice(symbol, riskPlan.TakeProfit2);

        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = bestDirection,

            EntryPrice = roundedEntry,
            StopLoss = roundedSl,
            TakeProfit1 = roundedTp1,
            TakeProfit2 = roundedTp2,

            RiskPips = Math.Round(riskPlan.RiskPips, 1),
            RewardPips1 = Math.Round(riskPlan.RewardPips1, 1),
            RewardPips2 = Math.Round(riskPlan.RewardPips2, 1),
            RiskReward1 = Math.Round(riskPlan.RiskReward1, 2),
            RiskReward2 = Math.Round(riskPlan.RiskReward2, 2),

            Confidence = finalScore,
            Grade = GetGrade(finalScore),

            Message =
                $"{symbol} {bestDirection} Entry: {roundedEntry} SL: {roundedSl} TP1: {roundedTp1} TP2: {roundedTp2}",

            InvalidIf = bestDirection == "LONG"
                ? $"M5 candle {roundedSl} altinda baglansa trade legvdir."
                : $"M5 candle {roundedSl} ustunde baglansa trade legvdir.",

            ValidForMinutes = GetValidForMinutes(finalScore, htfConflict),
            Reasons = reasons,
            SideAnalyses = sideAnalyses,
            StrategyResults = allResults,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static ForexStrategyResult BuildBalancedContextResult(
        ForexMarketContext context,
        string bestDirection,
        List<ForexStrategyResult> bestResults,
        List<ForexStrategyResult> oppositeResults,
        int longScore,
        int shortScore)
    {
        var score = 0;
        var reasons = new List<string>();

        var htfConflict =
            context.H1Bias != "NEUTRAL" &&
            context.M15Bias != "NEUTRAL" &&
            context.H1Bias != context.M15Bias;

        var htfAligned =
            context.H1Bias == bestDirection &&
            context.M15Bias == bestDirection;

        var h1Aligned =
            context.H1Bias == bestDirection;

        var m15Aligned =
            context.M15Bias == bestDirection;

        var hasTrigger = HasStrongDirectionalTrigger(bestResults);
        var oppositeHasTrigger = HasStrongDirectionalTrigger(oppositeResults);

        if (htfAligned)
        {
            score += 22;
            reasons.Add("Balanced context: H1 ve M15 eyni istiqameti tesdiq edir.");
        }
        else if (h1Aligned && hasTrigger)
        {
            score += 18;
            reasons.Add("Balanced context: H1 istiqameti ve trigger signal direction ile uygundur.");
        }
        else if (m15Aligned && hasTrigger)
        {
            score += 16;
            reasons.Add("Balanced context: M15 istiqameti ve trigger signal direction ile uygundur.");
        }
        else if (m15Aligned && !htfConflict)
        {
            score += 14;
            reasons.Add("Balanced context: M15 istiqameti signal direction ile uygundur.");
        }
        else if (htfConflict && hasTrigger)
        {
            score += 12;
            reasons.Add("Balanced context: HTF ziddiyyeti var, amma trigger gorunur. Risk orta-yuksekdir.");
        }
        else if (context.H1Bias == "NEUTRAL" || context.M15Bias == "NEUTRAL")
        {
            score += 10;
            reasons.Add("Balanced context: HTF tam aydin deyil, amma no-trade deyil.");
        }
        else
        {
            score += 6;
            reasons.Add("Balanced context: HTF uygun deyil, score azaldildi.");
        }

        var scoreGap = Math.Abs(longScore - shortScore);

        if (scoreGap >= 20)
        {
            score += 6;
            reasons.Add($"Balanced context: direction ustunluyu cox gucludur. Gap: {scoreGap}");
        }
        else if (scoreGap >= 14)
        {
            score += 5;
            reasons.Add($"Balanced context: direction ustunluyu gucludur. Gap: {scoreGap}");
        }
        else if (scoreGap >= 8)
        {
            score += 3;
            reasons.Add($"Balanced context: direction ustunluyu normaldir. Gap: {scoreGap}");
        }
        else
        {
            reasons.Add($"Balanced context: LONG/SHORT ferqi azdir. Gap: {scoreGap}");
        }

        if (oppositeHasTrigger && scoreGap < 12)
        {
            score -= 3;
            reasons.Add("Balanced context: opposite direction-da da trigger var, risk artdi.");
        }

        if (context.IsVolatilityNormal)
        {
            score += 4;
            reasons.Add("Balanced context: volatility trade ucun normaldir.");
        }
        else
        {
            score -= 3;
            reasons.Add("Balanced context: volatility ideal deyil.");
        }

        return new ForexStrategyResult
        {
            StrategyName = "BalancedContextFilter",
            Direction = bestDirection,
            Score = Math.Clamp(score, 0, 25),
            MaxScore = 25,
            IsConfirmed = score >= 14,
            Reasons = reasons
        };
    }

    private static bool HasStrongDirectionalTrigger(List<ForexStrategyResult> results)
    {
        return results.Any(x =>
            x.IsConfirmed &&
            (
                x.StrategyName.Contains("Reversal", StringComparison.OrdinalIgnoreCase) ||
                x.StrategyName.Contains("Breakout", StringComparison.OrdinalIgnoreCase) ||
                x.StrategyName.Contains("SupportResistance", StringComparison.OrdinalIgnoreCase) ||
                x.StrategyName.Contains("Pattern", StringComparison.OrdinalIgnoreCase) ||
                x.StrategyName.Contains("FalseBreakout", StringComparison.OrdinalIgnoreCase) ||
                x.StrategyName.Contains("NarrowRange", StringComparison.OrdinalIgnoreCase)
            ));
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
            Confidence = Math.Clamp(confidence, 0, 100),
            Grade = grade,
            Message = $"{symbol} FOREX WAIT",
            Reasons = reasons,
            SideAnalyses = sideAnalyses ?? new List<SideAnalysis>(),
            StrategyResults = strategyResults,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static int CalculateAdaptiveScore(List<ForexStrategyResult> results)
    {
        if (results.Count == 0)
            return 0;

        var meaningfulResults = results
            .Where(x =>
                x.MaxScore > 0 &&
                (
                    x.IsConfirmed ||
                    x.Score >= x.MaxScore * 0.40m ||
                    x.StrategyName.Contains("Volatility", StringComparison.OrdinalIgnoreCase) ||
                    x.StrategyName.Contains("RiskReward", StringComparison.OrdinalIgnoreCase) ||
                    x.StrategyName.Contains("BalancedContext", StringComparison.OrdinalIgnoreCase)
                ))
            .ToList();

        if (meaningfulResults.Count == 0)
            return 0;

        var score = meaningfulResults.Sum(x => x.Score);
        var maxScore = meaningfulResults.Sum(x => x.MaxScore);

        if (maxScore <= 0)
            return 0;

        var percent = (int)Math.Round((decimal)score / maxScore * 100m);

        var confirmedCount = meaningfulResults.Count(x =>
            x.IsConfirmed &&
            !x.StrategyName.Contains("Volatility", StringComparison.OrdinalIgnoreCase) &&
            !x.StrategyName.Contains("RiskReward", StringComparison.OrdinalIgnoreCase));

        if (confirmedCount >= 3)
            percent += 5;
        else if (confirmedCount <= 1)
            percent -= 6;

        var hasRiskReward = meaningfulResults.Any(x =>
            x.StrategyName.Contains("RiskReward", StringComparison.OrdinalIgnoreCase) &&
            x.IsConfirmed);

        if (hasRiskReward)
            percent += 4;

        var hasVolatility = meaningfulResults.Any(x =>
            x.StrategyName.Contains("Volatility", StringComparison.OrdinalIgnoreCase) &&
            x.IsConfirmed);

        if (hasVolatility)
            percent += 2;

        return Math.Clamp(percent, 0, 100);
    }

    private static string GetGrade(int score)
    {
        if (score >= 90)
            return "A+";

        if (score >= 82)
            return "A";

        if (score >= 72)
            return "B";

        if (score >= 62)
            return "WATCHLIST";

        return "NO_TRADE";
    }

    private static int GetValidForMinutes(int score, bool htfConflict)
    {
        if (htfConflict)
            return 7;

        if (score >= 90)
            return 15;

        if (score >= 82)
            return 10;

        return 7;
    }
}
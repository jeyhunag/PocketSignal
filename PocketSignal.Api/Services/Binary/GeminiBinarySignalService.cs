using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.MarketData;
using System.Globalization;
using System.Text;

namespace PocketSignal.Api.Services.Binary;

public class GeminiBinarySignalService : ISmartSignalService
{
    private readonly IMarketDataService _marketDataService;
    private readonly GeminiBinaryClient _geminiClient;
    private readonly ISignalResultTracker _signalResultTracker;
    private readonly IConfiguration _configuration;
    private readonly ILogger<GeminiBinarySignalService> _logger;

    public GeminiBinarySignalService(
        IMarketDataService marketDataService,
        GeminiBinaryClient geminiClient,
        ISignalResultTracker signalResultTracker,
        IConfiguration configuration,
        ILogger<GeminiBinarySignalService> logger)
    {
        _marketDataService = marketDataService;
        _geminiClient = geminiClient;
        _signalResultTracker = signalResultTracker;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<SmartTradeSignal> AnalyzeAsync(
        string symbol,
        string timeframe = "15min",
        CancellationToken cancellationToken = default)
    {
        var minimumConfidence =
            _configuration.GetValue<int?>("Gemini:MinimumConfidence") ?? 88;

        var stopBeforeConsecutiveLoss =
            _configuration.GetValue<int?>("Gemini:StopBeforeConsecutiveLoss") ?? 5;

        if (stopBeforeConsecutiveLoss < 2)
            stopBeforeConsecutiveLoss = 5;

        var todayTrades = _signalResultTracker.GetTodayTrades();

        var consecutiveLosses = CountConsecutiveLosses(todayTrades);
        var blockAtLossCount = stopBeforeConsecutiveLoss - 1;

        if (consecutiveLosses >= blockAtLossCount)
        {
            return Wait(
                symbol,
                5,
                0,
                $"Risk qoruması aktivdir: {consecutiveLosses} ardıcıl LOSS var. {stopBeforeConsecutiveLoss}-ci Martingale signalı bloklandı.");
        }

        var m5Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "5min",
            140,
            cancellationToken);

        var m1Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            220,
            cancellationToken);

        var m5 = MapCandles(m5Response);
        var m1 = MapCandles(m1Response);

        if (m5.Count < 60 || m1.Count < 100)
        {
            return Wait(
                symbol,
                5,
                0,
                "Gemini AI strategiyası üçün kifayət qədər M5/M1 candle yoxdur.");
        }

        var lastClose = RoundPrice(symbol, (decimal)m1[^1].Close);

        var localMarketScore = EstimateMarketQualityScore(
            m5,
            m1);

        var prompt = BuildPrompt(
            symbol,
            m5,
            m1,
            todayTrades,
            consecutiveLosses,
            minimumConfidence,
            stopBeforeConsecutiveLoss,
            localMarketScore);

        var aiDecision = await _geminiClient.AnalyzeAsync(
            prompt,
            cancellationToken);

        if (aiDecision == null)
        {
            Console.WriteLine(
                $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Gemini Binary AI | {symbol} | " +
                $"WAIT {localMarketScore}% | Expiry=0m | Risk=UNKNOWN | LossStreak={consecutiveLosses} | " +
                "Reason=Gemini cavab vermədi. Local market quality score göstərildi.");

            return Wait(
                symbol,
                localMarketScore,
                lastClose,
                "Gemini cavab vermədi və ya JSON parse olunmadı. Console-da local market quality score göstərildi.");
        }

        aiDecision.Direction = NormalizeDirection(aiDecision.Direction);

        var displayConfidence = NormalizeDisplayConfidence(aiDecision);

        Console.WriteLine(
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Gemini Binary AI | {symbol} | " +
            $"{aiDecision.Direction} {displayConfidence}% | Expiry={aiDecision.ExpiryMinutes}m | " +
            $"Risk={aiDecision.RiskLevel} | LossStreak={consecutiveLosses} | Reason={aiDecision.Reason}");

        if (aiDecision.Direction != "LONG" &&
            aiDecision.Direction != "SHORT")
        {
            return Wait(
                symbol,
                displayConfidence,
                lastClose,
                $"Gemini WAIT: {aiDecision.Reason}");
        }

        if (displayConfidence < minimumConfidence)
        {
            return Wait(
                symbol,
                displayConfidence,
                lastClose,
                $"Gemini confidence minimumdan aşağıdır. AI: {displayConfidence}%, Minimum: {minimumConfidence}%. Reason: {aiDecision.Reason}");
        }

        if (aiDecision.ExpiryMinutes < 3 || aiDecision.ExpiryMinutes > 25)
        {
            return Wait(
                symbol,
                displayConfidence,
                lastClose,
                $"Gemini expiry düzgün deyil. Expiry: {aiDecision.ExpiryMinutes}. İcazə verilən: 3-25 dəqiqə.");
        }

        if (aiDecision.ValidForSeconds <= 0 || aiDecision.ValidForSeconds > 60)
        {
            aiDecision.ValidForSeconds = 30;
        }

        var grade = GetGrade(displayConfidence);

        return new SmartTradeSignal
        {
            Symbol = symbol,
            Direction = aiDecision.Direction,
            ExpiryMinutes = aiDecision.ExpiryMinutes,
            ExpiryReason = aiDecision.Reason,
            Confidence = displayConfidence,
            Grade = grade,
            Message = $"{symbol} {aiDecision.Direction} {displayConfidence}% | Gemini AI",
            EntryType = "GEMINI_AI",
            ValidForSeconds = aiDecision.ValidForSeconds,
            LastClose = lastClose,
            InvalidIf = string.IsNullOrWhiteSpace(aiDecision.InvalidIf)
                ? BuildDefaultInvalidIf(symbol, aiDecision.Direction, lastClose)
                : aiDecision.InvalidIf.Trim(),
            Reasons = new List<string>
            {
                $"Gemini AI decision: {aiDecision.Direction}",
                $"AI confidence: {displayConfidence}%",
                $"Risk level: {aiDecision.RiskLevel}",
                $"Consecutive losses today: {consecutiveLosses}",
                aiDecision.Reason
            },
            CreatedAtUtc = DateTime.UtcNow,
            SideAnalyses = new List<SideAnalysis>
            {
                new SideAnalysis
                {
                    Direction = aiDecision.Direction,
                    Score = displayConfidence,
                    Reasons = new List<string>
                    {
                        aiDecision.Reason,
                        $"Local market quality score: {localMarketScore}%"
                    }
                }
            }
        };
    }

    private static string BuildPrompt(
        string symbol,
        List<PriceCandle> m5,
        List<PriceCandle> m1,
        List<SignalTradeRecord> todayTrades,
        int consecutiveLosses,
        int minimumConfidence,
        int stopBeforeConsecutiveLoss,
        int localMarketScore)
    {
        var completed = todayTrades
            .Where(x => x.Result == "WIN" || x.Result == "LOSS" || x.Result == "DRAW")
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(20)
            .ToList();

        var totalCompleted = completed.Count;
        var wins = completed.Count(x => x.Result == "WIN");
        var losses = completed.Count(x => x.Result == "LOSS");

        var winRate = totalCompleted > 0
            ? Math.Round((decimal)wins / totalCompleted * 100m, 1)
            : 0;

        var sb = new StringBuilder();

        sb.AppendLine("You are an AI market analyst for Binary Options on Pocket Option.");
        sb.AppendLine("You must analyze the given M5 and M1 OHLC candle data yourself.");
        sb.AppendLine("Return ONLY valid JSON. Do not return markdown. Do not explain outside JSON.");
        sb.AppendLine();
        sb.AppendLine("Main goal:");
        sb.AppendLine("- Avoid low quality signals.");
        sb.AppendLine("- Avoid 5 consecutive losing trades.");
        sb.AppendLine("- If the setup is not very clean, return WAIT.");
        sb.AppendLine("- Even when you return WAIT, confidence must show the setup quality score.");
        sb.AppendLine("- Do not return confidence 0 unless candle data is invalid or unreadable.");
        sb.AppendLine();
        sb.AppendLine("Trading context:");
        sb.AppendLine($"- Symbol: {symbol}");
        sb.AppendLine("- Broker payout: 92%");
        sb.AppendLine("- Martingale steps: 1, 2.2, 4.7, 10, 21.5");
        sb.AppendLine($"- Current consecutive losses today: {consecutiveLosses}");
        sb.AppendLine($"- Stop rule: if {stopBeforeConsecutiveLoss - 1} losses already happened, the next signal must be WAIT.");
        sb.AppendLine($"- Minimum confidence for real LONG/SHORT signal: {minimumConfidence}%");
        sb.AppendLine("- Expiry must be between 3 and 25 minutes.");
        sb.AppendLine();
        sb.AppendLine("Binary strategy rules:");
        sb.AppendLine("1. Check M5 market structure, liquidity zones, recent swing highs/lows.");
        sb.AppendLine("2. For LONG: prefer M5 sell-side liquidity sweep, M1 sweep below liquidity, return inside range, bullish rejection/FVG/imbalance.");
        sb.AppendLine("3. For SHORT: prefer M5 buy-side liquidity sweep, M1 sweep above liquidity, return inside range, bearish rejection/FVG/imbalance.");
        sb.AppendLine("4. Reject late entries where price already moved far away from entry zone.");
        sb.AppendLine("5. Reject choppy/ranging fake setups.");
        sb.AppendLine("6. Reject when LONG and SHORT both look possible.");
        sb.AppendLine("7. If volatility is too low or too aggressive, return WAIT.");
        sb.AppendLine("8. If confidence is below minimum, return WAIT.");
        sb.AppendLine();
        sb.AppendLine("Confidence rule:");
        sb.AppendLine("- You must always return a real confidence score based on your own analysis.");
        sb.AppendLine("- Do not copy the backend local score.");
        sb.AppendLine("- If direction is LONG or SHORT, confidence must represent real trade probability/quality.");
        sb.AppendLine("- If direction is WAIT, confidence must still show the best setup quality score.");
        sb.AppendLine("- WAIT confidence can be 20-76 for weak/medium setups.");
        sb.AppendLine("- WAIT confidence can be 77-84 only when setup is close but still not clean enough.");
        sb.AppendLine("- Do not return confidence 0 unless candle data is invalid or unreadable.");
        sb.AppendLine("- If you are unsure, return WAIT with your real estimated confidence.");
        sb.AppendLine("- If direction is LONG or SHORT, confidence should be 85-100 only for very clean setups.");
        sb.AppendLine("- If direction is WAIT, confidence should still be 20-84 and represent the best setup quality.");
        sb.AppendLine("- Example: WAIT with weak setup can be 35-55.");
        sb.AppendLine("- Example: WAIT with almost-ready setup can be 65-84.");
        sb.AppendLine("- Never return WAIT 0 unless data is invalid.");
        sb.AppendLine();
        sb.AppendLine("Output JSON schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"direction\": \"LONG\" | \"SHORT\" | \"WAIT\",");
        sb.AppendLine("  \"confidence\": 0-100,");
        sb.AppendLine("  \"expiryMinutes\": 0 or 3-25,");
        sb.AppendLine("  \"validForSeconds\": 0-60,");
        sb.AppendLine("  \"reason\": \"short reason\",");
        sb.AppendLine("  \"invalidIf\": \"short invalidation rule\",");
        sb.AppendLine("  \"riskLevel\": \"LOW\" | \"MEDIUM\" | \"HIGH\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Recent completed trades today:");
        sb.AppendLine($"- Total completed: {totalCompleted}");
        sb.AppendLine($"- Wins: {wins}");
        sb.AppendLine($"- Losses: {losses}");
        sb.AppendLine($"- Win rate: {winRate}%");

        foreach (var trade in completed.Take(10))
        {
            sb.AppendLine(
                $"- {trade.CreatedAtUtc:HH:mm} UTC | {trade.Symbol} {trade.Direction} {trade.Confidence}% {trade.ExpiryMinutes}m => {trade.Result}");
        }

        sb.AppendLine();
        sb.AppendLine("M5 candles, oldest to newest:");
        sb.AppendLine(BuildCandlesBlock(m5.TakeLast(80).ToList()));

        sb.AppendLine();
        sb.AppendLine("M1 candles, oldest to newest:");
        sb.AppendLine(BuildCandlesBlock(m1.TakeLast(120).ToList()));

        return sb.ToString();
    }

    private static string BuildCandlesBlock(List<PriceCandle> candles)
    {
        var sb = new StringBuilder();

        sb.AppendLine("time,open,high,low,close");

        foreach (var candle in candles)
        {
            sb.AppendLine(
                $"{candle.TimeUtc:yyyy-MM-dd HH:mm},{Format(candle.Open)},{Format(candle.High)},{Format(candle.Low)},{Format(candle.Close)}");
        }

        return sb.ToString();
    }

    private static int NormalizeDisplayConfidence(
        GeminiBinaryDecision aiDecision)
    {
        return Math.Clamp(aiDecision.Confidence, 0, 100);
    }

    private static int EstimateMarketQualityScore(
        List<PriceCandle> m5,
        List<PriceCandle> m1)
    {
        if (m5.Count < 30 || m1.Count < 40)
            return 10;

        var score = 30;

        var recentM1 = m1.TakeLast(30).ToList();
        var recentM5 = m5.TakeLast(30).ToList();

        var last = recentM1[^1];
        var lastClose = last.Close;

        var avgM1Range = AverageRange(recentM1);
        var avgM5Range = AverageRange(recentM5);

        if (avgM1Range <= 0 || avgM5Range <= 0 || lastClose <= 0)
            return 15;

        var volatilityPercent = avgM1Range / lastClose * 100.0;

        if (volatilityPercent >= 0.015 && volatilityPercent <= 0.12)
        {
            score += 12;
        }
        else if (volatilityPercent >= 0.008 && volatilityPercent <= 0.18)
        {
            score += 6;
        }
        else
        {
            score -= 5;
        }

        if (HasRecentSweep(m1, "LONG") || HasRecentSweep(m1, "SHORT"))
            score += 12;

        if (HasRecentSweep(m5, "LONG") || HasRecentSweep(m5, "SHORT"))
            score += 8;

        if (HasRecentFvg(m1))
            score += 10;

        if (HasRecentRejection(m1))
            score += 8;

        var trendStrength = EstimateTrendStrength(recentM1);

        if (trendStrength >= 0.8)
            score += 8;
        else if (trendStrength >= 0.4)
            score += 4;

        if (IsTooChoppy(recentM1))
            score -= 10;

        return Math.Clamp(score, 20, 84);
    }

    private static bool HasRecentSweep(
        List<PriceCandle> candles,
        string direction)
    {
        if (candles.Count < 40)
            return false;

        var reference = candles
            .Take(candles.Count - 5)
            .TakeLast(30)
            .ToList();

        var last5 = candles
            .TakeLast(5)
            .ToList();

        if (reference.Count < 20)
            return false;

        var keyLow = reference.Min(x => x.Low);
        var keyHigh = reference.Max(x => x.High);

        if (direction == "LONG")
        {
            return last5.Any(x =>
                x.Low < keyLow &&
                x.Close > keyLow);
        }

        return last5.Any(x =>
            x.High > keyHigh &&
            x.Close < keyHigh);
    }

    private static bool HasRecentFvg(List<PriceCandle> candles)
    {
        var recent = candles.TakeLast(25).ToList();

        if (recent.Count < 5)
            return false;

        for (var i = 2; i < recent.Count; i++)
        {
            var c1 = recent[i - 2];
            var c3 = recent[i];

            var bullishFvg = c1.High < c3.Low;
            var bearishFvg = c1.Low > c3.High;

            if (bullishFvg || bearishFvg)
                return true;
        }

        return false;
    }

    private static bool HasRecentRejection(List<PriceCandle> candles)
    {
        var recent = candles.TakeLast(8).ToList();

        foreach (var candle in recent)
        {
            var range = candle.High - candle.Low;

            if (range <= 0)
                continue;

            var body = Math.Abs(candle.Close - candle.Open);
            var upperWick = candle.High - Math.Max(candle.Open, candle.Close);
            var lowerWick = Math.Min(candle.Open, candle.Close) - candle.Low;

            var strongLowerRejection =
                lowerWick >= Math.Max(body * 1.1, range * 0.35);

            var strongUpperRejection =
                upperWick >= Math.Max(body * 1.1, range * 0.35);

            if (strongLowerRejection || strongUpperRejection)
                return true;
        }

        return false;
    }

    private static double EstimateTrendStrength(List<PriceCandle> candles)
    {
        if (candles.Count < 20)
            return 0;

        var firstHalf = candles.Take(candles.Count / 2).ToList();
        var secondHalf = candles.Skip(candles.Count / 2).ToList();

        var firstAvg = firstHalf.Average(x => x.Close);
        var secondAvg = secondHalf.Average(x => x.Close);

        var avgRange = AverageRange(candles);

        if (avgRange <= 0)
            return 0;

        return Math.Abs(secondAvg - firstAvg) / avgRange;
    }

    private static bool IsTooChoppy(List<PriceCandle> candles)
    {
        if (candles.Count < 20)
            return false;

        var directionChanges = 0;

        for (var i = 1; i < candles.Count; i++)
        {
            var previousDirection = candles[i - 1].Close >= candles[i - 1].Open
                ? 1
                : -1;

            var currentDirection = candles[i].Close >= candles[i].Open
                ? 1
                : -1;

            if (previousDirection != currentDirection)
                directionChanges++;
        }

        return directionChanges >= candles.Count * 0.70;
    }

    private static double AverageRange(List<PriceCandle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.High - x.Low);
    }

    private static int CountConsecutiveLosses(List<SignalTradeRecord> trades)
    {
        var ordered = trades
            .Where(x => x.Result == "WIN" || x.Result == "LOSS" || x.Result == "DRAW")
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToList();

        var count = 0;

        foreach (var trade in ordered)
        {
            if (trade.Result == "LOSS")
            {
                count++;
                continue;
            }

            break;
        }

        return count;
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
                Volume = item.Volume.HasValue ? (double)item.Volume.Value : 0
            });
        }

        return candles
            .OrderBy(x => x.TimeUtc)
            .ToList();
    }

    private static SmartTradeSignal Wait(
        string symbol,
        int confidence,
        decimal lastClose,
        string reason)
    {
        confidence = Math.Clamp(confidence, 0, 100);

        return new SmartTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            ExpiryMinutes = 0,
            ExpiryReason = reason,
            Confidence = confidence,
            Grade = "NO_TRADE",
            Message = $"{symbol} Gemini WAIT {confidence}% | {reason}",
            EntryType = "GEMINI_WAIT",
            ValidForSeconds = 0,
            LastClose = lastClose,
            InvalidIf = string.Empty,
            Reasons = new List<string>
            {
                reason
            },
            CreatedAtUtc = DateTime.UtcNow,
            SideAnalyses = new List<SideAnalysis>
            {
                new SideAnalysis
                {
                    Direction = "WAIT",
                    Score = confidence,
                    Reasons = new List<string>
                    {
                        reason
                    }
                }
            }
        };
    }

    private static string BuildDefaultInvalidIf(
        string symbol,
        string direction,
        decimal lastClose)
    {
        var buffer = GetInvalidBuffer(symbol, lastClose);

        if (direction == "LONG")
        {
            var level = lastClose - buffer;
            return $"M1 candle {RoundPrice(symbol, level)} altında bağlansa signal ləğvdir.";
        }

        if (direction == "SHORT")
        {
            var level = lastClose + buffer;
            return $"M1 candle {RoundPrice(symbol, level)} üstündə bağlansa signal ləğvdir.";
        }

        return string.Empty;
    }

    private static decimal GetInvalidBuffer(
        string symbol,
        decimal price)
    {
        if (symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase))
            return 0.03m;

        if (price <= 0)
            return 0.0003m;

        return Math.Max(price * 0.00025m, 0.0003m);
    }

    private static string GetGrade(int confidence)
    {
        if (confidence >= 94)
            return "A+";

        if (confidence >= 90)
            return "A";

        if (confidence >= 88)
            return "B";

        return "NO_TRADE";
    }

    private static decimal RoundPrice(
        string symbol,
        decimal price)
    {
        if (symbol.Contains("JPY", StringComparison.OrdinalIgnoreCase))
            return Math.Round(price, 3);

        return Math.Round(price, 5);
    }

    private static string NormalizeDirection(string? direction)
    {
        direction = direction?.Trim().ToUpperInvariant();

        return direction switch
        {
            "LONG" => "LONG",
            "SHORT" => "SHORT",
            _ => "WAIT"
        };
    }

    private static string Format(double value)
    {
        return value.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
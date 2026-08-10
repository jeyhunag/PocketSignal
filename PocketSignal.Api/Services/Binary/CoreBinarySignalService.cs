using System.Globalization;
using Microsoft.Extensions.Caching.Memory;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Binary;

public class CoreBinarySignalService : ISmartSignalService
{
    private const int MinimumConfidence = 70;
    private const int ConflictScoreDistance = 10;

    private const int M5Candles = 80;
    private const int M1CandlesForSetup = 140;
    private const int M1CandlesForTrend = 200;

    private static readonly TimeSpan WinSymbolCooldown = TimeSpan.FromMinutes(7);
    private static readonly TimeSpan LossSymbolCooldown = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan DrawSymbolCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SameDirectionCooldown = TimeSpan.FromMinutes(12);
    private static readonly TimeSpan SameSetupCooldown = TimeSpan.FromMinutes(20);
    private static readonly TimeSpan AfterTwoLossGlobalCooldown = TimeSpan.FromMinutes(15);

    private readonly IMarketDataService _marketDataService;
    private readonly ISignalResultTracker _signalResultTracker;
    private readonly IMemoryCache _cache;

    public CoreBinarySignalService(
        IMarketDataService marketDataService,
        ISignalResultTracker signalResultTracker,
        IMemoryCache cache)
    {
        _marketDataService = marketDataService;
        _signalResultTracker = signalResultTracker;
        _cache = cache;
    }

    public async Task<SmartTradeSignal> AnalyzeAsync(
        string symbol,
        string timeframe = "15min",
        CancellationToken cancellationToken = default)
    {
        var todayTrades = _signalResultTracker.GetTodayTrades();

        var preBlock = CheckPreMarketRiskFilters(
            symbol,
            todayTrades);

        if (preBlock.IsBlocked)
        {
            return Wait(
                symbol,
                preBlock.Confidence,
                0,
                preBlock.Reason);
        }

        var m5Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "5min",
            M5Candles,
            cancellationToken);

        var m1Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            M1CandlesForTrend,
            cancellationToken);

        var m5 = MapCandles(m5Response);
        var m1All = MapCandles(m1Response);

        if (m5.Count < 50 || m1All.Count < 120)
        {
            return Wait(
                symbol,
                0,
                0,
                "Yeni Binary Core strategiyası üçün kifayət qədər M5/M1 candle yoxdur.");
        }

        var m1Setup = m1All
            .TakeLast(M1CandlesForSetup)
            .ToList();

        var m1Trend = m1All
            .TakeLast(M1CandlesForTrend)
            .ToList();

        var marketContext = BuildMarketContext(
            symbol,
            m5,
            m1Setup,
            m1Trend);

        var longAnalysis = AnalyzeDirection(
            symbol,
            "LONG",
            marketContext);

        var shortAnalysis = AnalyzeDirection(
            symbol,
            "SHORT",
            marketContext);

        Console.WriteLine(
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Binary Core V3 | {symbol} | " +
            $"M5Trend={marketContext.M5Trend}, M1Trend={marketContext.M1Trend}, Chop={marketContext.IsChoppy}, Vol={marketContext.VolatilityState} | " +
            $"LONG {longAnalysis.Confidence}% [{longAnalysis.DebugSummary}] | " +
            $"SHORT {shortAnalysis.Confidence}% [{shortAnalysis.DebugSummary}]");

        var best = longAnalysis.Confidence >= shortAnalysis.Confidence
            ? longAnalysis
            : shortAnalysis;

        var opposite = best.Direction == "LONG"
            ? shortAnalysis
            : longAnalysis;

        var lastClose = RoundPrice(
            symbol,
            marketContext.LastClose);

        if (!best.TradeReady)
        {
            return Wait(
                symbol,
                best.Confidence,
                lastClose,
                $"Setup hələ tam hazır deyil. Best: {best.Direction} {best.Confidence}%. Model: {best.ModelName}.",
                longAnalysis,
                shortAnalysis);
        }

        if (opposite.TradeReady &&
            Math.Abs(best.Confidence - opposite.Confidence) < ConflictScoreDistance)
        {
            return Wait(
                symbol,
                Math.Max(best.Confidence, opposite.Confidence),
                lastClose,
                $"LONG və SHORT score çox yaxındır. LONG: {longAnalysis.Confidence}%, SHORT: {shortAnalysis.Confidence}%. Direction təmiz deyil.",
                longAnalysis,
                shortAnalysis);
        }

        if (best.Confidence < MinimumConfidence)
        {
            return Wait(
                symbol,
                best.Confidence,
                lastClose,
                $"Setup var, amma confidence minimumdan aşağıdır. Confidence: {best.Confidence}%, Minimum: {MinimumConfidence}%.",
                longAnalysis,
                shortAnalysis);
        }

        var postBlock = CheckPostAnalysisDuplicateFilters(
            symbol,
            best,
            todayTrades);

        if (postBlock.IsBlocked)
        {
            return Wait(
                symbol,
                best.Confidence,
                lastClose,
                postBlock.Reason,
                longAnalysis,
                shortAnalysis);
        }

        var invalidIf = best.Direction == "LONG"
            ? $"M1 candle {RoundPrice(symbol, best.InvalidPrice)} altında bağlansa signal ləğvdir."
            : $"M1 candle {RoundPrice(symbol, best.InvalidPrice)} üstündə bağlansa signal ləğvdir.";

        SaveDuplicateLocks(
            symbol,
            best);

        return new SmartTradeSignal
        {
            Symbol = symbol,
            Direction = best.Direction,
            ExpiryMinutes = best.ExpiryMinutes,
            ExpiryReason = best.ExpiryReason,
            Confidence = best.Confidence,
            Grade = GetGrade(best.Confidence),
            Message = $"{symbol} {best.Direction} {best.Confidence}% | {best.ExpiryMinutes} dəqiqə",
            EntryType = best.ModelName,
            ValidForSeconds = 20,
            LastClose = RoundPrice(symbol, best.EntryPrice),
            InvalidIf = invalidIf,
            Reasons = best.Reasons.Distinct().ToList(),
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
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private (bool IsBlocked, string Reason, int Confidence) CheckPreMarketRiskFilters(
        string symbol,
        List<SignalTradeRecord> todayTrades)
    {
        var nowUtc = DateTime.UtcNow;

        var consecutiveLosses = CountConsecutiveLosses(todayTrades);

        if (consecutiveLosses >= 4)
        {
            return (
                true,
                $"Risk qoruması aktivdir: {consecutiveLosses} ardıcıl LOSS var. 5-ci Martingale signalı bloklandı.",
                0);
        }

        if (consecutiveLosses >= 2)
        {
            var lastLoss = todayTrades
                .Where(x => x.Result == "LOSS")
                .OrderByDescending(GetTradeEndTimeUtc)
                .FirstOrDefault();

            if (lastLoss != null)
            {
                var endTime = GetTradeEndTimeUtc(lastLoss);

                if (nowUtc - endTime < AfterTwoLossGlobalCooldown)
                {
                    var left = AfterTwoLossGlobalCooldown - (nowUtc - endTime);

                    return (
                        true,
                        $"Global risk cooldown aktivdir: {consecutiveLosses} ardıcıl LOSS var. Təxminən {Math.Ceiling(left.TotalMinutes)} dəqiqə sonra yenidən analiz.",
                        0);
                }
            }
        }

        var pendingSameSymbol = todayTrades.Any(x =>
            x.Symbol == symbol &&
            x.Result == "PENDING");

        if (pendingSameSymbol)
        {
            return (
                true,
                $"{symbol} üzrə hələ PENDING trade var. Yeni signal bloklandı.",
                0);
        }

        var lastCompletedForSymbol = todayTrades
            .Where(x =>
                x.Symbol == symbol &&
                (x.Result == "WIN" || x.Result == "LOSS" || x.Result == "DRAW"))
            .OrderByDescending(GetTradeEndTimeUtc)
            .FirstOrDefault();

        if (lastCompletedForSymbol != null)
        {
            var endTime = GetTradeEndTimeUtc(lastCompletedForSymbol);
            var elapsed = nowUtc - endTime;

            var cooldown = lastCompletedForSymbol.Result switch
            {
                "WIN" => WinSymbolCooldown,
                "LOSS" => LossSymbolCooldown,
                "DRAW" => DrawSymbolCooldown,
                _ => TimeSpan.Zero
            };

            if (cooldown > TimeSpan.Zero && elapsed < cooldown)
            {
                var left = cooldown - elapsed;

                return (
                    true,
                    $"{symbol} üzrə {lastCompletedForSymbol.Result} sonrası cooldown aktivdir. Təxminən {Math.Ceiling(left.TotalMinutes)} dəqiqə sonra yenidən analiz.",
                    0);
            }
        }

        return (false, string.Empty, 0);
    }

    private (bool IsBlocked, string Reason) CheckPostAnalysisDuplicateFilters(
        string symbol,
        BinaryDirectionAnalysis best,
        List<SignalTradeRecord> todayTrades)
    {
        var nowUtc = DateTime.UtcNow;

        var sameDirectionRecent = todayTrades.Any(x =>
            x.Symbol == symbol &&
            x.Direction == best.Direction &&
            x.CreatedAtUtc >= nowUtc.Subtract(SameDirectionCooldown));

        if (sameDirectionRecent)
        {
            return (
                true,
                $"{symbol} {best.Direction} üzrə son {SameDirectionCooldown.TotalMinutes:0} dəqiqədə artıq signal olub. Təkrar signal bloklandı.");
        }

        var setupKey = BuildSetupCacheKey(
            symbol,
            best.Direction,
            best.ModelName,
            best.EntryPrice);

        if (_cache.TryGetValue(setupKey, out _))
        {
            return (
                true,
                $"Eyni setup təkrarlandı: {symbol} {best.Direction} {best.ModelName}. Duplicate setup filter aktivdir.");
        }

        return (false, string.Empty);
    }

    private void SaveDuplicateLocks(
        string symbol,
        BinaryDirectionAnalysis best)
    {
        var setupKey = BuildSetupCacheKey(
            symbol,
            best.Direction,
            best.ModelName,
            best.EntryPrice);

        _cache.Set(
            setupKey,
            true,
            new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = SameSetupCooldown
            });
    }

    private static string BuildSetupCacheKey(
        string symbol,
        string direction,
        string model,
        decimal entryPrice)
    {
        var normalizedSymbol = NormalizeSymbol(symbol);
        var zone = GetEntryZone(normalizedSymbol, entryPrice);

        return $"binary-core-v3:setup:{normalizedSymbol}:{direction}:{model}:{zone}";
    }

    private static string NormalizeSymbol(string symbol)
    {
        return symbol
            .Replace("/", "_")
            .Replace("-", "_")
            .Replace(" ", "")
            .ToUpperInvariant();
    }

    private static string GetEntryZone(
        string normalizedSymbol,
        decimal entryPrice)
    {
        var zoneSize = normalizedSymbol.Contains("JPY")
            ? 0.025m
            : 0.00025m;

        if (entryPrice <= 0)
            return "0";

        var zone = Math.Round(
            entryPrice / zoneSize,
            0,
            MidpointRounding.AwayFromZero);

        return zone.ToString("0", CultureInfo.InvariantCulture);
    }

    private static int CountConsecutiveLosses(
        List<SignalTradeRecord> todayTrades)
    {
        var completed = todayTrades
            .Where(x => x.Result == "WIN" || x.Result == "LOSS" || x.Result == "DRAW")
            .OrderByDescending(GetTradeEndTimeUtc)
            .ToList();

        var count = 0;

        foreach (var trade in completed)
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

    private static DateTime GetTradeEndTimeUtc(
        SignalTradeRecord trade)
    {
        if (trade.DueAtUtc != default)
            return trade.DueAtUtc;

        if (trade.ExpiryMinutes > 0)
            return trade.CreatedAtUtc.AddMinutes(trade.ExpiryMinutes);

        return trade.CreatedAtUtc;
    }

    private static BinaryMarketContext BuildMarketContext(
        string symbol,
        List<PriceCandle> m5,
        List<PriceCandle> m1Setup,
        List<PriceCandle> m1TrendCandles)
    {
        var avgM5Range = AverageRange(m5.TakeLast(30).ToList());
        var avgM1Range = AverageRange(m1Setup.TakeLast(40).ToList());

        var m5Trend = DetectTrend(
            m5,
            30);

        var m1Trend = DetectTrend(
            m1TrendCandles,
            80);

        var m1ShortTrend = DetectTrend(
            m1Setup,
            30);

        var volatilityState = DetectVolatilityState(
            m1Setup,
            avgM1Range);

        var isChoppy = DetectChoppyMarket(
            m1Setup);

        var last = m1Setup[^1];

        return new BinaryMarketContext
        {
            Symbol = symbol,
            M5 = m5,
            M1Setup = m1Setup,
            M1TrendCandles = m1TrendCandles,
            LastClose = (decimal)last.Close,
            AvgM5Range = avgM5Range,
            AvgM1Range = avgM1Range,
            M5Trend = m5Trend,
            M1Trend = m1Trend,
            M1ShortTrend = m1ShortTrend,
            VolatilityState = volatilityState,
            IsChoppy = isChoppy
        };
    }

    private static BinaryDirectionAnalysis AnalyzeDirection(
        string symbol,
        string direction,
        BinaryMarketContext context)
    {
        var analysis = new BinaryDirectionAnalysis
        {
            Direction = direction,
            EntryPrice = context.LastClose,
            InvalidPrice = context.LastClose,
            ModelName = "NO_MODEL"
        };

        if (context.AvgM1Range <= 0 || context.AvgM5Range <= 0)
        {
            analysis.Reasons.Add("Average range hesablanmadı.");
            return analysis;
        }

        var trendScore = ScoreTrendContext(
            direction,
            context,
            analysis);

        analysis.Confidence += trendScore;

        var trendContinuation = AnalyzeTrendContinuation(
            direction,
            context);

        var sweepReversal = AnalyzeSweepReversal(
            direction,
            context);

        var breakoutRetest = AnalyzeBreakoutRetest(
            direction,
            context);

        var models = new List<BinarySetupModel>
        {
            trendContinuation,
            sweepReversal,
            breakoutRetest
        };

        var bestModel = models
            .OrderByDescending(x => x.Score)
            .First();

        analysis.ModelName = bestModel.Name;
        analysis.Confidence += bestModel.Score;
        analysis.Reasons.AddRange(bestModel.Reasons);
        analysis.InvalidPrice = bestModel.InvalidPrice;
        analysis.EntryPrice = context.LastClose;

        // === V4: İndikator scoring (API sorğusu YOX, lokal hesablama) ===
        var indicators = IndicatorScorer.Score(
            direction,
            context.M1TrendCandles,
            context.M5);

        analysis.Confidence += indicators.Score;
        analysis.Reasons.AddRange(indicators.Reasons);
        analysis.Indicators = indicators;

        if (bestModel.IsConfirmed)
        {
            analysis.HasModelConfirmation = true;
        }

        var priceAction = HasPriceActionConfirmation(
            direction,
            context.M1Setup);

        if (priceAction.IsConfirmed)
        {
            analysis.Confidence += 8;
            analysis.HasPriceAction = true;
            analysis.Reasons.Add(priceAction.Reason);
        }
        else
        {
            analysis.Reasons.Add(priceAction.Reason);
        }

        if (context.VolatilityState == "NORMAL")
        {
            analysis.Confidence += 8;
            analysis.Reasons.Add("Volatility normaldır.");
        }
        else if (context.VolatilityState == "LOW")
        {
            analysis.Confidence -= 6;
            analysis.Reasons.Add("Volatility zəifdir, expiry daha uzun seçilməlidir.");
        }
        else if (context.VolatilityState == "HIGH")
        {
            analysis.Confidence -= 8;
            analysis.Reasons.Add("Volatility çox yüksəkdir, fake move riski var.");
        }

        if (context.IsChoppy)
        {
            analysis.Confidence -= 10;
            analysis.Reasons.Add("M1 bazar çox qarışıq/choppy görünür.");
        }

        var directionConflict = HasDirectionConflict(
            direction,
            context);

        if (directionConflict)
        {
            analysis.Confidence -= 12;
            analysis.Reasons.Add("M5 və M1 direction arasında konflikt var.");
        }

        analysis.Confidence = Math.Clamp(
            analysis.Confidence,
            0,
            100);

        analysis.ExpiryMinutes = CalculateExpiry(
            direction,
            context,
            analysis,
            bestModel);

        analysis.ExpiryReason =
            $"Core V3 expiry: {analysis.ExpiryMinutes} dəqiqə seçildi. Model: {analysis.ModelName}. Volatility: {context.VolatilityState}.";

        var tier = IndicatorScorer.EvaluateTier(
                    analysis.HasModelConfirmation,
                    analysis.HasPriceAction,
                    directionConflict,
                    context.IsChoppy,
                    analysis.Confidence,
                    indicators,
                    MinimumConfidence);

        analysis.TradeReady = tier.TradeReady;
        analysis.Reasons.Add($"Tier: {tier.Tier} — {tier.Reason}");

        if (!analysis.TradeReady)
        {
            if (!analysis.HasModelConfirmation)
                analysis.Reasons.Add("No trade: trend/sweep/breakout modeli təsdiqlənmədi.");

            if (!analysis.HasPriceAction)
                analysis.Reasons.Add("No trade: M1 price action təsdiqi yoxdur.");

            if (directionConflict)
                analysis.Reasons.Add("No trade: direction konflikti var.");

            if (context.IsChoppy)
                analysis.Reasons.Add("No trade: bazar choppy-dir.");
        }

        analysis.Reasons = analysis.Reasons
            .Distinct()
            .ToList();

        return analysis;
    }

    private static int ScoreTrendContext(
        string direction,
        BinaryMarketContext context,
        BinaryDirectionAnalysis analysis)
    {
        var score = 0;

        if (direction == "LONG")
        {
            if (context.M5Trend == "UP")
            {
                score += 14;
                analysis.Reasons.Add("M5 trend LONG istiqamətini dəstəkləyir.");
            }
            else if (context.M5Trend == "NEUTRAL")
            {
                score += 6;
                analysis.Reasons.Add("M5 trend neytraldır.");
            }
            else
            {
                analysis.Reasons.Add("M5 trend LONG üçün uyğun deyil.");
            }

            if (context.M1Trend == "UP")
            {
                score += 16;
                analysis.Reasons.Add("M1 200 candle trend LONG istiqamətindədir.");
            }
            else if (context.M1Trend == "NEUTRAL")
            {
                score += 7;
                analysis.Reasons.Add("M1 200 candle trend neytraldır.");
            }
            else
            {
                analysis.Reasons.Add("M1 200 candle trend LONG üçün uyğun deyil.");
            }

            if (context.M1ShortTrend == "UP")
            {
                score += 8;
                analysis.Reasons.Add("M1 qısa trend LONG istiqamətindədir.");
            }
        }
        else
        {
            if (context.M5Trend == "DOWN")
            {
                score += 14;
                analysis.Reasons.Add("M5 trend SHORT istiqamətini dəstəkləyir.");
            }
            else if (context.M5Trend == "NEUTRAL")
            {
                score += 6;
                analysis.Reasons.Add("M5 trend neytraldır.");
            }
            else
            {
                analysis.Reasons.Add("M5 trend SHORT üçün uyğun deyil.");
            }

            if (context.M1Trend == "DOWN")
            {
                score += 16;
                analysis.Reasons.Add("M1 200 candle trend SHORT istiqamətindədir.");
            }
            else if (context.M1Trend == "NEUTRAL")
            {
                score += 7;
                analysis.Reasons.Add("M1 200 candle trend neytraldır.");
            }
            else
            {
                analysis.Reasons.Add("M1 200 candle trend SHORT üçün uyğun deyil.");
            }

            if (context.M1ShortTrend == "DOWN")
            {
                score += 8;
                analysis.Reasons.Add("M1 qısa trend SHORT istiqamətindədir.");
            }
        }

        return Math.Clamp(score, 0, 38);
    }

    private static BinarySetupModel AnalyzeTrendContinuation(
        string direction,
        BinaryMarketContext context)
    {
        var score = 0;
        var reasons = new List<string>();

        var recent = context.M1Setup.TakeLast(30).ToList();
        var last = recent[^1];

        var ma8 = recent.TakeLast(8).Average(x => x.Close);
        var ma21 = recent.TakeLast(21).Average(x => x.Close);

        var pullbackCandles = recent.TakeLast(8).ToList();

        if (direction == "LONG")
        {
            var trendOk =
                context.M5Trend != "DOWN" &&
                context.M1Trend == "UP" &&
                ma8 >= ma21;

            if (trendOk)
            {
                score += 24;
                reasons.Add("Trend continuation LONG: M1 200 trend yuxarıdır və MA8 MA21 üzərindədir.");
            }

            var pullback =
                pullbackCandles.Any(x => x.Low <= ma21 + context.AvgM1Range * 0.45) &&
                last.Close > ma8 &&
                last.IsBullish;

            if (pullback)
            {
                score += 24;
                reasons.Add("Trend continuation LONG: MA21 zonasına pullback və bullish reaksiya var.");
            }

            var invalid = (decimal)recent.TakeLast(12).Min(x => x.Low);

            return new BinarySetupModel
            {
                Name = "TREND_CONTINUATION",
                Direction = direction,
                Score = Math.Clamp(score, 0, 55),
                IsConfirmed = score >= 38,
                InvalidPrice = invalid,
                Reasons = reasons
            };
        }
        else
        {
            var trendOk =
                context.M5Trend != "UP" &&
                context.M1Trend == "DOWN" &&
                ma8 <= ma21;

            if (trendOk)
            {
                score += 24;
                reasons.Add("Trend continuation SHORT: M1 200 trend aşağıdır və MA8 MA21 altındadır.");
            }

            var pullback =
                pullbackCandles.Any(x => x.High >= ma21 - context.AvgM1Range * 0.45) &&
                last.Close < ma8 &&
                last.IsBearish;

            if (pullback)
            {
                score += 24;
                reasons.Add("Trend continuation SHORT: MA21 zonasına pullback və bearish reaksiya var.");
            }

            var invalid = (decimal)recent.TakeLast(12).Max(x => x.High);

            return new BinarySetupModel
            {
                Name = "TREND_CONTINUATION",
                Direction = direction,
                Score = Math.Clamp(score, 0, 55),
                IsConfirmed = score >= 38,
                InvalidPrice = invalid,
                Reasons = reasons
            };
        }
    }

    private static BinarySetupModel AnalyzeSweepReversal(
        string direction,
        BinaryMarketContext context)
    {
        var score = 0;
        var reasons = new List<string>();

        var recent = context.M1Setup.TakeLast(80).ToList();

        if (recent.Count < 40)
        {
            return new BinarySetupModel
            {
                Name = "SWEEP_REVERSAL",
                Direction = direction,
                Score = 0,
                IsConfirmed = false,
                InvalidPrice = context.LastClose,
                Reasons = new List<string> { "Sweep reversal üçün kifayət qədər candle yoxdur." }
            };
        }

        var reference = recent
            .Take(recent.Count - 5)
            .TakeLast(45)
            .ToList();

        var last8 = recent.TakeLast(8).ToList();

        var keyHigh = reference.Max(x => x.High);
        var keyLow = reference.Min(x => x.Low);

        if (direction == "LONG")
        {
            var sweep = last8
                .Select((c, index) => new { Candle = c, Index = index })
                .Where(x =>
                    x.Candle.Low < keyLow &&
                    x.Candle.Close > keyLow)
                .LastOrDefault();

            if (sweep != null)
            {
                score += 30;
                reasons.Add("Sweep reversal LONG: M1 sell-side liquidity sweep və range içinə qayıdış var.");

                if (sweep.Candle.LowerWick >= Math.Max(sweep.Candle.Body * 0.8, context.AvgM1Range * 0.2))
                {
                    score += 10;
                    reasons.Add("Sweep candle bullish rejection verir.");
                }
            }

            var bullishShift = HasStructureShift(recent, "LONG");

            if (bullishShift)
            {
                score += 15;
                reasons.Add("Sweep reversal LONG: M1 bullish structure shift var.");
            }

            return new BinarySetupModel
            {
                Name = "SWEEP_REVERSAL",
                Direction = direction,
                Score = Math.Clamp(score, 0, 55),
                IsConfirmed = score >= 38,
                InvalidPrice = (decimal)keyLow,
                Reasons = reasons
            };
        }
        else
        {
            var sweep = last8
                .Select((c, index) => new { Candle = c, Index = index })
                .Where(x =>
                    x.Candle.High > keyHigh &&
                    x.Candle.Close < keyHigh)
                .LastOrDefault();

            if (sweep != null)
            {
                score += 30;
                reasons.Add("Sweep reversal SHORT: M1 buy-side liquidity sweep və range içinə qayıdış var.");

                if (sweep.Candle.UpperWick >= Math.Max(sweep.Candle.Body * 0.8, context.AvgM1Range * 0.2))
                {
                    score += 10;
                    reasons.Add("Sweep candle bearish rejection verir.");
                }
            }

            var bearishShift = HasStructureShift(recent, "SHORT");

            if (bearishShift)
            {
                score += 15;
                reasons.Add("Sweep reversal SHORT: M1 bearish structure shift var.");
            }

            return new BinarySetupModel
            {
                Name = "SWEEP_REVERSAL",
                Direction = direction,
                Score = Math.Clamp(score, 0, 55),
                IsConfirmed = score >= 38,
                InvalidPrice = (decimal)keyHigh,
                Reasons = reasons
            };
        }
    }

    private static BinarySetupModel AnalyzeBreakoutRetest(
        string direction,
        BinaryMarketContext context)
    {
        var score = 0;
        var reasons = new List<string>();

        var recent = context.M1Setup.TakeLast(50).ToList();
        var last = recent[^1];

        var reference = recent
            .Take(recent.Count - 5)
            .TakeLast(30)
            .ToList();

        var high = reference.Max(x => x.High);
        var low = reference.Min(x => x.Low);

        if (direction == "LONG")
        {
            var breakout = recent
                .TakeLast(8)
                .Any(x => x.Close > high);

            if (breakout)
            {
                score += 24;
                reasons.Add("Breakout retest LONG: son M1 range high breakout olub.");
            }

            var retest =
                last.Low <= high + context.AvgM1Range * 0.55 &&
                last.Close > high &&
                last.IsBullish;

            if (retest)
            {
                score += 24;
                reasons.Add("Breakout retest LONG: breakout level retest və bullish bağlanış var.");
            }

            return new BinarySetupModel
            {
                Name = "BREAKOUT_RETEST",
                Direction = direction,
                Score = Math.Clamp(score, 0, 50),
                IsConfirmed = score >= 38,
                InvalidPrice = (decimal)high,
                Reasons = reasons
            };
        }
        else
        {
            var breakout = recent
                .TakeLast(8)
                .Any(x => x.Close < low);

            if (breakout)
            {
                score += 24;
                reasons.Add("Breakout retest SHORT: son M1 range low breakout olub.");
            }

            var retest =
                last.High >= low - context.AvgM1Range * 0.55 &&
                last.Close < low &&
                last.IsBearish;

            if (retest)
            {
                score += 24;
                reasons.Add("Breakout retest SHORT: breakout level retest və bearish bağlanış var.");
            }

            return new BinarySetupModel
            {
                Name = "BREAKOUT_RETEST",
                Direction = direction,
                Score = Math.Clamp(score, 0, 50),
                IsConfirmed = score >= 38,
                InvalidPrice = (decimal)low,
                Reasons = reasons
            };
        }
    }

    private static (bool IsConfirmed, string Reason) HasPriceActionConfirmation(
        string direction,
        List<PriceCandle> candles)
    {
        if (candles.Count < 3)
            return (false, "Price action üçün kifayət qədər candle yoxdur.");

        var previous = candles[^2];
        var last = candles[^1];

        if (last.Range <= 0)
            return (false, "Son M1 candle range sıfırdır.");

        var closePosition = (last.Close - last.Low) / last.Range;

        if (direction == "LONG")
        {
            var bullishRejection =
                last.IsBullish &&
                last.LowerWick >= Math.Max(last.Body * 0.65, last.Range * 0.25) &&
                closePosition >= 0.55;

            var bullishEngulf =
                previous.IsBearish &&
                last.IsBullish &&
                last.Close > previous.Open;

            var bullishBreak =
                last.IsBullish &&
                last.Close > previous.High;

            if (bullishRejection || bullishEngulf || bullishBreak)
                return (true, "M1 bullish price action confirmation var.");

            return (false, "M1 bullish price action confirmation yoxdur.");
        }
        else
        {
            var bearishRejection =
                last.IsBearish &&
                last.UpperWick >= Math.Max(last.Body * 0.65, last.Range * 0.25) &&
                closePosition <= 0.45;

            var bearishEngulf =
                previous.IsBullish &&
                last.IsBearish &&
                last.Close < previous.Open;

            var bearishBreak =
                last.IsBearish &&
                last.Close < previous.Low;

            if (bearishRejection || bearishEngulf || bearishBreak)
                return (true, "M1 bearish price action confirmation var.");

            return (false, "M1 bearish price action confirmation yoxdur.");
        }
    }

    private static bool HasStructureShift(
        List<PriceCandle> candles,
        string direction)
    {
        if (candles.Count < 25)
            return false;

        var previous = candles
            .Take(candles.Count - 3)
            .TakeLast(20)
            .ToList();

        var last = candles[^1];

        if (direction == "LONG")
        {
            var high = previous.Max(x => x.High);
            return last.Close > high;
        }

        var low = previous.Min(x => x.Low);
        return last.Close < low;
    }

    private static bool HasDirectionConflict(
        string direction,
        BinaryMarketContext context)
    {
        if (direction == "LONG")
        {
            return context.M5Trend == "DOWN" &&
                   context.M1Trend == "DOWN";
        }

        return context.M5Trend == "UP" &&
               context.M1Trend == "UP";
    }

    private static int CalculateExpiry(
        string direction,
        BinaryMarketContext context,
        BinaryDirectionAnalysis analysis,
        BinarySetupModel model)
    {
        var recent = context.M1Setup.TakeLast(12).ToList();

        var directional = direction == "LONG"
            ? recent.Count(x => x.IsBullish)
            : recent.Count(x => x.IsBearish);

        var opposite = direction == "LONG"
            ? recent.Count(x => x.IsBearish)
            : recent.Count(x => x.IsBullish);

        var lastMove = Math.Abs(recent[^1].Close - recent[0].Close);
        var impulseRatio = context.AvgM1Range > 0
            ? lastMove / context.AvgM1Range
            : 0;

        var minutes = model.Name switch
        {
            "TREND_CONTINUATION" => 12,
            "SWEEP_REVERSAL" => 7,
            "BREAKOUT_RETEST" => 9,
            _ => 10
        };

        if (analysis.Confidence >= 92)
            minutes -= 2;
        else if (analysis.Confidence >= 85)
            minutes -= 1;
        else if (analysis.Confidence < 80)
            minutes += 3;

        if (directional >= 8 && impulseRatio >= 3.0)
            minutes -= 2;
        else if (directional >= 7)
            minutes -= 1;

        if (opposite >= 6)
            minutes += 2;

        if (context.VolatilityState == "LOW")
            minutes += 5;

        if (context.VolatilityState == "HIGH")
            minutes -= 2;

        if (context.IsChoppy)
            minutes += 3;

        return Math.Clamp(minutes, 3, 25);
    }

    private static string DetectTrend(
        List<PriceCandle> candles,
        int lookback)
    {
        var recent = candles
            .TakeLast(lookback)
            .ToList();

        if (recent.Count < 25)
            return "NEUTRAL";

        var first = recent.First();
        var last = recent.Last();

        var avgRange = AverageRange(recent);

        if (avgRange <= 0)
            return "NEUTRAL";

        var fast = recent.TakeLast(9).Average(x => x.Close);
        var slow = recent.TakeLast(26).Average(x => x.Close);

        var bullishCount = recent.TakeLast(12).Count(x => x.IsBullish);
        var bearishCount = recent.TakeLast(12).Count(x => x.IsBearish);

        var netMove = last.Close - first.Close;

        if (fast > slow &&
            netMove > avgRange * 1.2 &&
            bullishCount >= 6)
        {
            return "UP";
        }

        if (fast < slow &&
            netMove < -avgRange * 1.2 &&
            bearishCount >= 6)
        {
            return "DOWN";
        }

        return "NEUTRAL";
    }

    private static string DetectVolatilityState(
        List<PriceCandle> candles,
        double avgRange)
    {
        if (candles.Count < 30)
            return "UNKNOWN";

        var lastClose = candles[^1].Close;

        if (lastClose <= 0 || avgRange <= 0)
            return "UNKNOWN";

        var volatilityPercent = avgRange / lastClose * 100.0;

        if (volatilityPercent < 0.006)
            return "LOW";

        if (volatilityPercent > 0.075)
            return "HIGH";

        return "NORMAL";
    }

    private static bool DetectChoppyMarket(
        List<PriceCandle> candles)
    {
        var recent = candles.TakeLast(24).ToList();

        if (recent.Count < 20)
            return false;

        var directionChanges = 0;

        for (var i = 1; i < recent.Count; i++)
        {
            var previous = recent[i - 1].Close >= recent[i - 1].Open ? 1 : -1;
            var current = recent[i].Close >= recent[i].Open ? 1 : -1;

            if (previous != current)
                directionChanges++;
        }

        var avgRange = AverageRange(recent);

        if (avgRange <= 0)
            return false;

        var totalMove = Math.Abs(recent[^1].Close - recent[0].Close);
        var moveRatio = totalMove / avgRange;

        return directionChanges >= 16 && moveRatio <= 2.5;
    }

    private static SmartTradeSignal Wait(
        string symbol,
        int confidence,
        decimal lastClose,
        string reason,
        BinaryDirectionAnalysis? longAnalysis = null,
        BinaryDirectionAnalysis? shortAnalysis = null)
    {
        confidence = Math.Clamp(confidence, 0, 100);

        var reasons = new List<string>
        {
            reason
        };

        if (longAnalysis != null)
        {
            reasons.Add($"LONG score: {longAnalysis.Confidence}%");
            reasons.AddRange(longAnalysis.Reasons.Take(8));
        }

        if (shortAnalysis != null)
        {
            reasons.Add($"SHORT score: {shortAnalysis.Confidence}%");
            reasons.AddRange(shortAnalysis.Reasons.Take(8));
        }

        var sideAnalyses = new List<SideAnalysis>();

        if (longAnalysis != null)
        {
            sideAnalyses.Add(new SideAnalysis
            {
                Direction = "LONG",
                Score = longAnalysis.Confidence,
                Reasons = longAnalysis.Reasons
            });
        }

        if (shortAnalysis != null)
        {
            sideAnalyses.Add(new SideAnalysis
            {
                Direction = "SHORT",
                Score = shortAnalysis.Confidence,
                Reasons = shortAnalysis.Reasons
            });
        }

        return new SmartTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            ExpiryMinutes = 0,
            ExpiryReason = reason,
            Confidence = confidence,
            Grade = "NO_TRADE",
            Message = $"{symbol} WAIT {confidence}%",
            EntryType = "NO_ENTRY",
            ValidForSeconds = 0,
            LastClose = lastClose,
            InvalidIf = string.Empty,
            Reasons = reasons.Distinct().ToList(),
            SideAnalyses = sideAnalyses,
            CreatedAtUtc = DateTime.UtcNow
        };
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

    private static string GetGrade(
        int confidence)
    {
        if (confidence >= 92)
            return "A+";

        if (confidence >= 85)
            return "A";

        if (confidence >= 77)
            return "B";

        return "NO_TRADE";
    }

    private static double AverageRange(
        List<PriceCandle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.Range);
    }

    private sealed class BinaryMarketContext
    {
        public string Symbol { get; set; } = string.Empty;

        public List<PriceCandle> M5 { get; set; } = new();

        public List<PriceCandle> M1Setup { get; set; } = new();

        public List<PriceCandle> M1TrendCandles { get; set; } = new();

        public decimal LastClose { get; set; }

        public double AvgM5Range { get; set; }

        public double AvgM1Range { get; set; }

        public string M5Trend { get; set; } = "NEUTRAL";

        public string M1Trend { get; set; } = "NEUTRAL";

        public string M1ShortTrend { get; set; } = "NEUTRAL";

        public string VolatilityState { get; set; } = "UNKNOWN";

        public bool IsChoppy { get; set; }
    }

    private sealed class BinaryDirectionAnalysis
    {
        public string Direction { get; set; } = string.Empty;

        public int Confidence { get; set; }

        public bool TradeReady { get; set; }

        public bool HasModelConfirmation { get; set; }

        public bool HasPriceAction { get; set; }

        public decimal EntryPrice { get; set; }

        public decimal InvalidPrice { get; set; }

        public int ExpiryMinutes { get; set; }

        public string ExpiryReason { get; set; } = string.Empty;

        public string ModelName { get; set; } = string.Empty;

        public List<string> Reasons { get; set; } = new();
        public IndicatorScorer.IndicatorResult? Indicators { get; set; }

        public string DebugSummary =>
            $"Model={ModelName}, ModelOk={HasModelConfirmation}, PA={HasPriceAction}, Ready={TradeReady}";
    }

    private sealed class BinarySetupModel
    {
        public string Name { get; set; } = string.Empty;

        public string Direction { get; set; } = string.Empty;

        public int Score { get; set; }

        public bool IsConfirmed { get; set; }

        public decimal InvalidPrice { get; set; }

        public List<string> Reasons { get; set; } = new();
    }
}
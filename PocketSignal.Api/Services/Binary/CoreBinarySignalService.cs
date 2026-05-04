using System.Globalization;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Binary;

public class CoreBinarySignalService : ISmartSignalService
{
    private const int MinimumConfidence = 82;
    private const int ConflictScoreDistance = 12;

    private readonly IMarketDataService _marketDataService;

    public CoreBinarySignalService(IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<SmartTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
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

        if (m5.Count < 40 || m1.Count < 60)
        {
            return Wait(
                symbol,
                0,
                0,
                "Liquidity Sweep + FVG strategiyasi ucun kifayet qeder M5/M1 candle yoxdur.");
        }

        var longAnalysis = AnalyzeDirection(
            symbol,
            "LONG",
            m5,
            m1);

        var shortAnalysis = AnalyzeDirection(
            symbol,
            "SHORT",
            m5,
            m1);

        Console.WriteLine(
            $"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Binary Core | {symbol} | " +
            $"LONG {longAnalysis.Confidence}% [{longAnalysis.DebugSummary}] | " +
            $"SHORT {shortAnalysis.Confidence}% [{shortAnalysis.DebugSummary}]");

        var best = longAnalysis.Confidence >= shortAnalysis.Confidence
            ? longAnalysis
            : shortAnalysis;

        var opposite = best.Direction == "LONG"
            ? shortAnalysis
            : longAnalysis;

        var lastClose = RoundPrice(symbol, (decimal)m1[^1].Close);

        if (!best.TradeReady)
        {
            return Wait(
                symbol,
                best.Confidence,
                lastClose,
                $"Setup hele tam hazir deyil. Best: {best.Direction} {best.Confidence}%.",
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
                $"LONG ve SHORT setup-lari yaxindir. LONG: {longAnalysis.Confidence}%, SHORT: {shortAnalysis.Confidence}%.",
                longAnalysis,
                shortAnalysis);
        }

        if (best.Confidence < MinimumConfidence)
        {
            return Wait(
                symbol,
                best.Confidence,
                lastClose,
                $"Sweep + FVG setup var, amma confidence kifayet deyil. Confidence: {best.Confidence}%, Minimum: {MinimumConfidence}%.",
                longAnalysis,
                shortAnalysis);
        }

        var invalidIf = best.Direction == "LONG"
            ? $"M1 candle {RoundPrice(symbol, best.InvalidPrice)} altinda baglansa signal legvdir."
            : $"M1 candle {RoundPrice(symbol, best.InvalidPrice)} ustunde baglansa signal legvdir.";

        return new SmartTradeSignal
        {
            Symbol = symbol,
            Direction = best.Direction,
            ExpiryMinutes = best.ExpiryMinutes,
            ExpiryReason = best.ExpiryReason,
            Confidence = best.Confidence,
            Grade = GetGrade(best.Confidence),
            Message = $"{symbol} {best.Direction} {best.Confidence}% | {best.ExpiryMinutes} deqiqe",
            EntryType = "NEXT_M1_CANDLE_OPEN_OR_NOW_IF_VALID",
            ValidForSeconds = 20,
            LastClose = RoundPrice(symbol, best.EntryPrice),
            InvalidIf = invalidIf,
            Reasons = best.Reasons,
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

    private static BinaryDirectionAnalysis AnalyzeDirection(
        string symbol,
        string direction,
        List<PriceCandle> m5,
        List<PriceCandle> m1)
    {
        var reasons = new List<string>();
        var last = m1[^1];

        var avgRangeM5 = AverageRange(m5.TakeLast(30).ToList());
        var avgRangeM1 = AverageRange(m1.TakeLast(30).ToList());

        if (avgRangeM5 <= 0 || avgRangeM1 <= 0)
        {
            return new BinaryDirectionAnalysis
            {
                Direction = direction,
                Confidence = 0,
                TradeReady = false,
                EntryPrice = (decimal)last.Close,
                InvalidPrice = (decimal)last.Close,
                Reasons = new List<string>
                {
                    "Average range hesablanmadi, candle data duzgun deyil."
                }
            };
        }

        var side = direction == "LONG"
            ? "SELL_SIDE"
            : "BUY_SIDE";

        var liquidityLevels = FindLiquidityLevels(
            m5,
            side,
            avgRangeM5);

        var analysis = new BinaryDirectionAnalysis
        {
            Direction = direction,
            EntryPrice = (decimal)last.Close,
            InvalidPrice = (decimal)last.Close
        };

        if (liquidityLevels.Count == 0)
        {
            reasons.Add(direction == "LONG"
                ? "M5 sell-side liquidity tapilmadi."
                : "M5 buy-side liquidity tapilmadi.");

            analysis.Confidence = 0;
            analysis.TradeReady = false;
            analysis.Reasons = reasons;

            return analysis;
        }

        var closestLiquidity = liquidityLevels
            .OrderBy(x => Math.Abs(last.Close - x.Price))
            .First();

        analysis.HasLiquidity = true;

        var liquidityScore = Math.Min(22, 10 + closestLiquidity.Strength);
        analysis.Confidence += liquidityScore;

        reasons.Add(direction == "LONG"
            ? $"M5 sell-side liquidity var: {closestLiquidity.Price}."
            : $"M5 buy-side liquidity var: {closestLiquidity.Price}.");

        var distanceToLiquidity = Math.Abs(last.Close - closestLiquidity.Price);

        if (distanceToLiquidity <= avgRangeM1 * 8)
        {
            analysis.Confidence += 6;
            reasons.Add("Qiymet M5 liquidity zonasina yaxindir, setup formalaşa bilər.");
        }
        else
        {
            reasons.Add("M5 liquidity var, amma qiymet hele o zonadan uzaqdir.");
        }

        var sweep = FindRecentSweep(
            m1,
            liquidityLevels,
            direction,
            avgRangeM1);

        if (sweep == null)
        {
            reasons.Add(direction == "LONG"
                ? "M1 sell-side sweep hele yoxdur."
                : "M1 buy-side sweep hele yoxdur.");

            analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);
            analysis.TradeReady = false;
            analysis.Reasons = reasons;

            return analysis;
        }

        analysis.HasSweep = true;
        analysis.HasReturnInside = true;

        analysis.Confidence += sweep.Score;

        reasons.Add(direction == "LONG"
            ? $"M1 sell-side sweep oldu: level {sweep.Level.Price}, age {sweep.AgeCandles} candle."
            : $"M1 buy-side sweep oldu: level {sweep.Level.Price}, age {sweep.AgeCandles} candle.");

        reasons.Add("Price sweep-den sonra range icine geri qayitdi.");

        var fvg = FindBestFvgAfterSweep(
            m1,
            sweep.CandleIndex,
            direction,
            avgRangeM1);

        if (fvg == null)
        {
            reasons.Add(direction == "LONG"
                ? "Sweep-den sonra bullish FVG hele yaranmayib."
                : "Sweep-den sonra bearish FVG hele yaranmayib.");

            analysis.InvalidPrice = direction == "LONG"
                ? (decimal)sweep.Candle.Low
                : (decimal)sweep.Candle.High;

            analysis.Confidence = Math.Clamp(analysis.Confidence, 0, 100);
            analysis.TradeReady = false;
            analysis.Reasons = reasons;

            return analysis;
        }

        analysis.HasFvg = true;
        analysis.IsFvgFresh = fvg.IsFresh;

        if (fvg.IsFresh)
        {
            analysis.Confidence += 20;
            reasons.Add(direction == "LONG"
                ? $"Sweep-den sonra fresh bullish FVG tapildi: {fvg.Low} - {fvg.High}."
                : $"Sweep-den sonra fresh bearish FVG tapildi: {fvg.Low} - {fvg.High}.");
        }
        else
        {
            analysis.Confidence += 8;
            reasons.Add(direction == "LONG"
                ? $"Bullish FVG var, amma daha once mitigated ola biler: {fvg.Low} - {fvg.High}."
                : $"Bearish FVG var, amma daha once mitigated ola biler: {fvg.Low} - {fvg.High}.");
        }

        var proximity = GetFvgProximity(
            last.Close,
            fvg,
            avgRangeM1);

        if (proximity.IsInside)
        {
            analysis.Confidence += 15;
            analysis.IsPriceNearFvg = true;
            reasons.Add("Price FVG entry zone icindedir.");
        }
        else if (proximity.IsNear)
        {
            analysis.Confidence += 12;
            analysis.IsPriceNearFvg = true;
            reasons.Add($"Price FVG entry zone yaxinligindadir. Distance: {proximity.Distance}.");
        }
        else if (proximity.IsAcceptable)
        {
            analysis.Confidence += 6;
            analysis.IsPriceNearFvg = false;
            reasons.Add($"Price FVG-den bir az uzaqdir. Distance: {proximity.Distance}.");
        }
        else
        {
            reasons.Add($"Price FVG entry zone-dan uzaqdir. Distance: {proximity.Distance}.");
        }

        var confirmation = HasM1Confirmation(
            m1,
            direction);

        if (confirmation.IsConfirmed)
        {
            analysis.Confidence += 7;
            analysis.HasConfirmation = true;
            reasons.Add(confirmation.Reason);
        }
        else
        {
            reasons.Add(confirmation.Reason);
        }

        var entryClean = IsEntryClean(
            last,
            fvg,
            avgRangeM1);

        if (entryClean)
        {
            analysis.Confidence += 5;
            reasons.Add("Entry gecikmis deyil, qiymet FVG-den cox uzaqlasmayib.");
        }
        else
        {
            reasons.Add("Entry bir az gecikmis ola biler, qiymet FVG-den uzaqlasib.");
        }

        analysis.Confidence = Math.Clamp(
            analysis.Confidence,
            0,
            100);

        analysis.EntryPrice = (decimal)last.Close;

        analysis.InvalidPrice = direction == "LONG"
            ? (decimal)Math.Min(sweep.Candle.Low, fvg.Low)
            : (decimal)Math.Max(sweep.Candle.High, fvg.High);

        var expiry = CalculateExpiry(
            direction,
            m1,
            sweep,
            fvg,
            proximity,
            avgRangeM1,
            analysis.Confidence,
            analysis.HasConfirmation,
            entryClean);

        analysis.ExpiryMinutes = expiry.Minutes;
        analysis.ExpiryReason = expiry.Reason;

        reasons.Add(expiry.Reason);

        analysis.TradeReady =
            analysis.HasLiquidity &&
            analysis.HasSweep &&
            analysis.HasReturnInside &&
            analysis.HasFvg &&
            analysis.IsFvgFresh &&
            analysis.IsPriceNearFvg &&
            sweep.AgeCandles <= 35;

        if (!analysis.TradeReady)
        {
            if (!analysis.IsFvgFresh)
                reasons.Add("No trade: FVG fresh/unmitigated deyil.");

            if (!analysis.IsPriceNearFvg)
                reasons.Add("No trade: Price FVG entry zone yaxinliginda deyil.");

            if (sweep.AgeCandles > 35)
                reasons.Add("No trade: Sweep artiq gecikmisdir.");
        }

        analysis.Reasons = reasons.Distinct().ToList();

        return analysis;
    }

    private static List<LiquidityLevel> FindLiquidityLevels(
        List<PriceCandle> m5,
        string side,
        double avgRange)
    {
        var levels = new List<LiquidityLevel>();

        var recent = m5
            .TakeLast(80)
            .ToList();

        if (recent.Count < 20)
            return levels;

        var tolerance = Math.Max(
            avgRange * 0.35,
            recent[^1].Close * 0.00008);

        var swings = FindSwings(
                recent,
                2,
                2)
            .Where(x =>
                side == "BUY_SIDE"
                    ? x.Kind == "HIGH"
                    : x.Kind == "LOW")
            .ToList();

        foreach (var swing in swings)
        {
            levels.Add(new LiquidityLevel
            {
                Side = side,
                Price = swing.Price,
                TimeUtc = swing.TimeUtc,
                Strength = 8,
                Reasons = new List<string>
                {
                    side == "BUY_SIDE"
                        ? "M5 swing high liquidity."
                        : "M5 swing low liquidity."
                }
            });
        }

        foreach (var level in levels)
        {
            var equalCount = levels.Count(x =>
                !ReferenceEquals(x, level) &&
                Math.Abs(x.Price - level.Price) <= tolerance);

            if (equalCount > 0)
            {
                level.Strength += Math.Min(8, equalCount * 3);
                level.Reasons.Add("Equal high/low cluster liquidity.");
            }

            var ageMinutes = Math.Abs(
                (recent[^1].TimeUtc - level.TimeUtc).TotalMinutes);

            if (ageMinutes <= 180)
            {
                level.Strength += 2;
                level.Reasons.Add("Recent M5 liquidity.");
            }
        }

        var extremeCandle = side == "BUY_SIDE"
            ? recent.OrderByDescending(x => x.High).First()
            : recent.OrderBy(x => x.Low).First();

        var extremePrice = side == "BUY_SIDE"
            ? extremeCandle.High
            : extremeCandle.Low;

        if (!levels.Any(x => Math.Abs(x.Price - extremePrice) <= tolerance))
        {
            levels.Add(new LiquidityLevel
            {
                Side = side,
                Price = extremePrice,
                TimeUtc = extremeCandle.TimeUtc,
                Strength = 10,
                Reasons = new List<string>
                {
                    side == "BUY_SIDE"
                        ? "M5 recent range high liquidity."
                        : "M5 recent range low liquidity."
                }
            });
        }

        var distinct = new List<LiquidityLevel>();

        foreach (var level in levels
                     .OrderByDescending(x => x.Strength)
                     .ThenByDescending(x => x.TimeUtc))
        {
            if (distinct.Any(x => Math.Abs(x.Price - level.Price) <= tolerance))
                continue;

            distinct.Add(level);
        }

        return distinct
            .Take(10)
            .ToList();
    }

    private static SweepInfo? FindRecentSweep(
        List<PriceCandle> m1,
        List<LiquidityLevel> levels,
        string direction,
        double avgRange)
    {
        var startIndex = Math.Max(
            0,
            m1.Count - 80);

        for (var i = m1.Count - 1; i >= startIndex; i--)
        {
            var candle = m1[i];
            SweepInfo? bestAtCandle = null;

            foreach (var level in levels)
            {
                var swept = direction == "LONG"
                    ? candle.Low < level.Price
                    : candle.High > level.Price;

                if (!swept)
                    continue;

                var returnInsideIndex = FindReturnInsideIndex(
                    m1,
                    i,
                    level.Price,
                    direction);

                if (returnInsideIndex < 0)
                    continue;

                var ageCandles = m1.Count - 1 - i;

                var wick = direction == "LONG"
                    ? candle.LowerWick
                    : candle.UpperWick;

                var rejectionBonus = wick >= Math.Max(candle.Body * 0.8, avgRange * 0.20)
                    ? 5
                    : 0;

                var quickReturnBonus = returnInsideIndex == i
                    ? 5
                    : 2;

                var recencyBonus = ageCandles switch
                {
                    <= 5 => 8,
                    <= 15 => 5,
                    <= 30 => 2,
                    _ => 0
                };

                var levelBonus = Math.Min(
                    6,
                    level.Strength / 2);

                var score =
                    15 +
                    rejectionBonus +
                    quickReturnBonus +
                    recencyBonus +
                    levelBonus;

                var candidate = new SweepInfo
                {
                    Candle = candle,
                    CandleIndex = i,
                    ReturnInsideIndex = returnInsideIndex,
                    Level = level,
                    AgeCandles = ageCandles,
                    Score = Math.Clamp(score, 0, 32)
                };

                if (bestAtCandle == null || candidate.Score > bestAtCandle.Score)
                    bestAtCandle = candidate;
            }

            if (bestAtCandle != null)
                return bestAtCandle;
        }

        return null;
    }

    private static int FindReturnInsideIndex(
        List<PriceCandle> m1,
        int sweepIndex,
        double level,
        string direction)
    {
        var maxIndex = Math.Min(
            m1.Count - 1,
            sweepIndex + 2);

        for (var i = sweepIndex; i <= maxIndex; i++)
        {
            var closedInside = direction == "LONG"
                ? m1[i].Close > level
                : m1[i].Close < level;

            if (closedInside)
                return i;
        }

        return -1;
    }

    private static FvgZone? FindBestFvgAfterSweep(
        List<PriceCandle> m1,
        int sweepIndex,
        string direction,
        double avgRange)
    {
        var zones = new List<FvgZone>();

        var startIndex = Math.Max(
            2,
            sweepIndex + 1);

        for (var i = startIndex; i < m1.Count; i++)
        {
            var c1 = m1[i - 2];
            var c2 = m1[i - 1];
            var c3 = m1[i];

            if (direction == "LONG")
            {
                var hasBullishFvg = c1.High < c3.Low;

                if (!hasBullishFvg)
                    continue;

                var low = c1.High;
                var high = c3.Low;
                var size = high - low;

                if (size <= 0)
                    continue;

                if (size < avgRange * 0.02)
                    continue;

                var displacement =
                    c2.IsBullish ||
                    c3.IsBullish ||
                    c2.Close > c1.Close ||
                    c3.Close > c2.Close;

                var zone = new FvgZone
                {
                    Low = low,
                    High = high,
                    CreatedIndex = i,
                    CreatedAtUtc = c3.TimeUtc,
                    AgeCandles = m1.Count - 1 - i
                };

                zone.IsFresh = !IsFvgMitigatedBeforeLast(
                    zone,
                    m1,
                    direction);

                zone.Score = CalculateFvgScore(
                    zone,
                    avgRange);

                if (displacement)
                    zone.Score += 3;

                zones.Add(zone);
            }
            else
            {
                var hasBearishFvg = c1.Low > c3.High;

                if (!hasBearishFvg)
                    continue;

                var low = c3.High;
                var high = c1.Low;
                var size = high - low;

                if (size <= 0)
                    continue;

                if (size < avgRange * 0.02)
                    continue;

                var displacement =
                    c2.IsBearish ||
                    c3.IsBearish ||
                    c2.Close < c1.Close ||
                    c3.Close < c2.Close;

                var zone = new FvgZone
                {
                    Low = low,
                    High = high,
                    CreatedIndex = i,
                    CreatedAtUtc = c3.TimeUtc,
                    AgeCandles = m1.Count - 1 - i
                };

                zone.IsFresh = !IsFvgMitigatedBeforeLast(
                    zone,
                    m1,
                    direction);

                zone.Score = CalculateFvgScore(
                    zone,
                    avgRange);

                if (displacement)
                    zone.Score += 3;

                zones.Add(zone);
            }
        }

        return zones
            .OrderByDescending(x => x.IsFresh)
            .ThenBy(x => x.AgeCandles)
            .ThenByDescending(x => x.Score)
            .FirstOrDefault();
    }

    private static bool IsFvgMitigatedBeforeLast(
        FvgZone zone,
        List<PriceCandle> candles,
        string direction)
    {
        for (var i = zone.CreatedIndex + 1; i < candles.Count - 1; i++)
        {
            if (direction == "LONG" && candles[i].Low <= zone.Low)
                return true;

            if (direction == "SHORT" && candles[i].High >= zone.High)
                return true;
        }

        return false;
    }

    private static int CalculateFvgScore(
        FvgZone zone,
        double avgRange)
    {
        var score = zone.IsFresh
            ? 20
            : 8;

        score += zone.AgeCandles switch
        {
            <= 5 => 6,
            <= 15 => 4,
            <= 30 => 2,
            _ => 0
        };

        var size = zone.High - zone.Low;

        if (size <= avgRange * 3)
            score += 3;

        return Math.Clamp(score, 0, 30);
    }

    private static ZoneProximity GetFvgProximity(
        double price,
        FvgZone zone,
        double avgRange)
    {
        var isInside =
            price >= zone.Low &&
            price <= zone.High;

        if (isInside)
        {
            return new ZoneProximity
            {
                IsInside = true,
                IsNear = true,
                IsAcceptable = true,
                Distance = 0
            };
        }

        var distance = price < zone.Low
            ? zone.Low - price
            : price - zone.High;

        var nearLimit = Math.Max(
            avgRange * 0.75,
            price * 0.00003);

        var acceptableLimit = Math.Max(
            avgRange * 1.25,
            price * 0.00005);

        return new ZoneProximity
        {
            IsInside = false,
            IsNear = distance <= nearLimit,
            IsAcceptable = distance <= acceptableLimit,
            Distance = distance
        };
    }

    private static (bool IsConfirmed, string Reason) HasM1Confirmation(
        List<PriceCandle> candles,
        string direction)
    {
        if (candles.Count < 3)
            return (false, "M1 confirmation ucun kifayet qeder candle yoxdur.");

        var previous = candles[^2];
        var last = candles[^1];

        if (last.Range <= 0)
            return (false, "Son M1 candle range sifirdir.");

        var closePosition = (last.Close - last.Low) / last.Range;

        if (direction == "LONG")
        {
            var bullishRejection =
                last.IsBullish &&
                last.LowerWick >= last.Body * 0.70 &&
                closePosition >= 0.55;

            var bullishEngulfing =
                previous.IsBearish &&
                last.IsBullish &&
                last.Close > previous.Open;

            var bullishBreak =
                last.IsBullish &&
                last.Close > previous.High;

            if (bullishRejection || bullishEngulfing || bullishBreak)
                return (true, "M1 bullish confirmation/rejection var.");

            return (false, "M1 bullish confirmation hele yoxdur.");
        }

        var bearishRejection =
            last.IsBearish &&
            last.UpperWick >= last.Body * 0.70 &&
            closePosition <= 0.45;

        var bearishEngulfing =
            previous.IsBullish &&
            last.IsBearish &&
            last.Close < previous.Open;

        var bearishBreak =
            last.IsBearish &&
            last.Close < previous.Low;

        if (bearishRejection || bearishEngulfing || bearishBreak)
            return (true, "M1 bearish confirmation/rejection var.");

        return (false, "M1 bearish confirmation hele yoxdur.");
    }

    private static bool IsEntryClean(
        PriceCandle last,
        FvgZone fvg,
        double avgRange)
    {
        var middle = (fvg.Low + fvg.High) / 2.0;
        var distance = Math.Abs(last.Close - middle);

        return distance <= avgRange * 3.0;
    }

    private static (int Minutes, string Reason) CalculateExpiry(
        string direction,
        List<PriceCandle> m1,
        SweepInfo sweep,
        FvgZone fvg,
        ZoneProximity proximity,
        double avgRange,
        int confidence,
        bool hasConfirmation,
        bool entryClean)
    {
        var reasons = new List<string>();
        var last = m1[^1];

        if (avgRange <= 0 || last.Close <= 0)
        {
            return (
                Minutes: 7,
                Reason: "Expiry default 7 deqiqe secildi: volatility hesablanmadi.");
        }

        var recent5 = m1.TakeLast(5).ToList();
        var recent10 = m1.TakeLast(10).ToList();
        var recent20 = m1.TakeLast(20).ToList();

        var minutes = 10;

        var volatilityPercent = avgRange / last.Close * 100.0;

        var move3 = m1.Count >= 4
            ? Math.Abs(m1[^1].Close - m1[^4].Close)
            : 0;

        var move7 = m1.Count >= 8
            ? Math.Abs(m1[^1].Close - m1[^8].Close)
            : 0;

        var impulseRatio3 = avgRange > 0
            ? move3 / avgRange
            : 0;

        var impulseRatio7 = avgRange > 0
            ? move7 / avgRange
            : 0;

        var directional5 = direction == "LONG"
            ? recent5.Count(x => x.IsBullish)
            : recent5.Count(x => x.IsBearish);

        var opposite5 = direction == "LONG"
            ? recent5.Count(x => x.IsBearish)
            : recent5.Count(x => x.IsBullish);

        var recentAvgRange = AverageRange(recent10);
        var olderAvgRange = AverageRange(recent20.Take(10).ToList());

        var volatilityRatio = olderAvgRange > 0
            ? recentAvgRange / olderAvgRange
            : 1.0;

        if (confidence >= 94 && sweep.AgeCandles <= 5 && fvg.AgeCandles <= 6 && hasConfirmation)
        {
            minutes -= 4;
            reasons.Add("Cox guclu ve teze setup var, qisa expiry uygundur.");
        }
        else if (confidence >= 88 && sweep.AgeCandles <= 12 && fvg.AgeCandles <= 15)
        {
            minutes -= 2;
            reasons.Add("Guclu setup var, qisa-orta expiry secildi.");
        }
        else if (confidence < 86)
        {
            minutes += 3;
            reasons.Add("Confidence minimuma yaxindir, qiymete daha cox vaxt verildi.");
        }

        if (proximity.IsInside)
        {
            minutes -= 1;
            reasons.Add("Price FVG entry zone icindedir, entry yaxindir.");
        }
        else if (proximity.IsNear)
        {
            minutes += 1;
            reasons.Add("Price FVG yaxinligindadir, orta expiry secildi.");
        }
        else
        {
            minutes += 3;
            reasons.Add("Price FVG-den bir az uzaqdir, daha uzun expiry lazim ola biler.");
        }

        if (hasConfirmation)
        {
            minutes -= 2;
            reasons.Add("M1 confirmation var, hereket daha tez isleye biler.");
        }
        else
        {
            minutes += 2;
            reasons.Add("M1 confirmation zeifdir, trade-a daha cox vaxt verildi.");
        }

        if (directional5 >= 4 && impulseRatio3 >= 2.0)
        {
            minutes -= 3;
            reasons.Add("Son M1 candle-larda istiqametli impulse gucludur, qisa expiry secildi.");
        }
        else if (directional5 >= 3)
        {
            minutes -= 1;
            reasons.Add("Son M1 candle-lar direction-i destekleyir.");
        }
        else if (directional5 <= 2)
        {
            minutes += 3;
            reasons.Add("M1 direction hele yavasdir, expiry uzadildi.");
        }

        if (opposite5 >= 3)
        {
            minutes += 2;
            reasons.Add("Son candle-larda qarisiq hereket var, expiry bir az uzadildi.");
        }

        if (volatilityPercent < 0.006)
        {
            minutes += 8;
            reasons.Add("Volatility cox zeifdir, maksimuma yaxin uzun expiry lazimdir.");
        }
        else if (volatilityPercent < 0.012)
        {
            minutes += 5;
            reasons.Add("Volatility zeifdir, daha uzun expiry secildi.");
        }
        else if (volatilityPercent < 0.020)
        {
            minutes += 2;
            reasons.Add("Volatility sakitdir, orta-uzun expiry secildi.");
        }
        else if (volatilityPercent > 0.055)
        {
            minutes -= 3;
            reasons.Add("Volatility yuksekdir, qisa expiry daha uygundur.");
        }
        else if (volatilityPercent > 0.038)
        {
            minutes -= 2;
            reasons.Add("Volatility aktivdir, qisa-orta expiry secildi.");
        }

        if (volatilityRatio < 0.70)
        {
            minutes += 3;
            reasons.Add("Son M1 volatility evvelkinden zeifdir, qiymete elave vaxt verildi.");
        }
        else if (volatilityRatio > 1.40)
        {
            minutes -= 2;
            reasons.Add("Son M1 volatility artib, hereket daha tez netice vere biler.");
        }

        if (sweep.AgeCandles <= 3)
        {
            minutes -= 2;
            reasons.Add("Sweep cox tezedir.");
        }
        else if (sweep.AgeCandles <= 10)
        {
            reasons.Add("Sweep tezedir.");
        }
        else if (sweep.AgeCandles <= 20)
        {
            minutes += 2;
            reasons.Add("Sweep bir az vaxt kecib, expiry uzadildi.");
        }
        else
        {
            minutes += 5;
            reasons.Add("Sweep kohnelemeye baslayib, hereket ucun daha genis vaxt verildi.");
        }

        if (fvg.AgeCandles <= 3)
        {
            minutes -= 1;
            reasons.Add("FVG cox tezedir.");
        }
        else if (fvg.AgeCandles <= 12)
        {
            reasons.Add("FVG aktiv ve tezedir.");
        }
        else if (fvg.AgeCandles <= 25)
        {
            minutes += 2;
            reasons.Add("FVG bir az kohneleib, expiry uzadildi.");
        }
        else
        {
            minutes += 4;
            reasons.Add("FVG kohne zone-dur, daha uzun expiry secildi.");
        }

        if (!entryClean)
        {
            minutes += 4;
            reasons.Add("Entry gecikmis ola biler, buna gore expiry uzadildi.");
        }
        else
        {
            reasons.Add("Entry temizdir, price FVG-den cox uzaqlasmayib.");
        }

        if (impulseRatio7 >= 4.5 && directional5 >= 4)
        {
            minutes += 2;
            reasons.Add("Move artiq xeyli gedib, pullback riski ucun expiry bir az uzadildi.");
        }

        minutes = ClampToAllowedExpiry(minutes);

        return (
            Minutes: minutes,
            Reason:
                $"Smart binary expiry: {minutes} deqiqe secildi. " +
                string.Join(" ", reasons.Distinct()));
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
            reasons.AddRange(longAnalysis.Reasons.Take(6));
        }

        if (shortAnalysis != null)
        {
            reasons.Add($"SHORT score: {shortAnalysis.Confidence}%");
            reasons.AddRange(shortAnalysis.Reasons.Take(6));
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
            Reasons = reasons,
            SideAnalyses = sideAnalyses,
            CreatedAtUtc = DateTime.UtcNow
        };
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

    private static int ClampToAllowedExpiry(int minutes)
    {
        return Math.Clamp(minutes, 3, 25);
    }

    private static double AverageRange(List<PriceCandle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.Range);
    }

    private sealed class BinaryDirectionAnalysis
    {
        public string Direction { get; set; } = string.Empty;

        public int Confidence { get; set; }

        public bool TradeReady { get; set; }

        public bool HasLiquidity { get; set; }

        public bool HasSweep { get; set; }

        public bool HasReturnInside { get; set; }

        public bool HasFvg { get; set; }

        public bool IsFvgFresh { get; set; }

        public bool IsPriceNearFvg { get; set; }

        public bool HasConfirmation { get; set; }

        public decimal EntryPrice { get; set; }

        public decimal InvalidPrice { get; set; }

        public int ExpiryMinutes { get; set; }

        public string ExpiryReason { get; set; } = string.Empty;

        public List<string> Reasons { get; set; } = new();

        public string DebugSummary =>
            $"Liquidity={HasLiquidity}, Sweep={HasSweep}, Return={HasReturnInside}, FVG={HasFvg}, Fresh={IsFvgFresh}, NearFVG={IsPriceNearFvg}, Confirm={HasConfirmation}, Ready={TradeReady}";
    }

    private sealed class LiquidityLevel
    {
        public string Side { get; set; } = string.Empty;

        public double Price { get; set; }

        public DateTime TimeUtc { get; set; }

        public int Strength { get; set; }

        public List<string> Reasons { get; set; } = new();
    }

    private sealed class SweepInfo
    {
        public PriceCandle Candle { get; set; } = new();

        public int CandleIndex { get; set; }

        public int ReturnInsideIndex { get; set; }

        public LiquidityLevel Level { get; set; } = new();

        public int AgeCandles { get; set; }

        public int Score { get; set; }
    }

    private sealed class FvgZone
    {
        public double Low { get; set; }

        public double High { get; set; }

        public int CreatedIndex { get; set; }

        public DateTime CreatedAtUtc { get; set; }

        public int AgeCandles { get; set; }

        public bool IsFresh { get; set; }

        public int Score { get; set; }
    }

    private sealed class SwingPoint
    {
        public int Index { get; set; }

        public DateTime TimeUtc { get; set; }

        public double Price { get; set; }

        public string Kind { get; set; } = string.Empty;
    }

    private sealed class ZoneProximity
    {
        public bool IsInside { get; set; }

        public bool IsNear { get; set; }

        public bool IsAcceptable { get; set; }

        public double Distance { get; set; }
    }
}
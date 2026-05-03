using System.Globalization;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Binary;

public class CoreBinarySignalService : ISmartSignalService
{
    private const int MinimumConfidence = 82;

    private readonly IMarketDataService _marketDataService;

    public CoreBinarySignalService(IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<SmartTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var m15Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "15min",
            200,
            cancellationToken);

        var m1Response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            300,
            cancellationToken);

        var m15 = MapCandles(m15Response);
        var m1 = MapCandles(m1Response);

        if (m15.Count < 60 || m1.Count < 80)
        {
            return Wait(
                symbol,
                0,
                "Analiz ucun kifayet qeder M15/M1 candle yoxdur.");
        }

        var currentTime = m1[^1].TimeUtc;

        var session = GetCurrentSessionContext(currentTime);

        if (session == null)
        {
            return Wait(
                symbol,
                0,
                $"CRT session tapilmadi. Candle time: {currentTime:yyyy-MM-dd HH:mm:ss}");
        }

        var rangeCandles = m15
            .Where(x =>
                x.TimeUtc >= session.PreviousSessionStartUtc &&
                x.TimeUtc < session.PreviousSessionEndUtc)
            .ToList();

        if (rangeCandles.Count < 4)
        {
            return Wait(
                symbol,
                0,
                $"Evvelki session ucun kifayet qeder M15 candle yoxdur. Previous session: {session.PreviousSessionName} {session.PreviousSessionStartUtc:HH:mm}-{session.PreviousSessionEndUtc:HH:mm}");
        }

        var upperRange = rangeCandles
            .OrderByDescending(x => x.High)
            .First();

        var lowerRange = rangeCandles
            .OrderBy(x => x.Low)
            .First();

        var currentSessionM1 = m1
            .Where(x => x.TimeUtc >= session.CurrentSessionStartUtc)
            .ToList();

        if (currentSessionM1.Count < 10)
        {
            return Wait(
                symbol,
                0,
                $"Cari session ucun kifayet qeder M1 candle yoxdur. Current session: {session.Name} {session.CurrentSessionStartUtc:HH:mm}-{session.CurrentSessionEndUtc:HH:mm}");
        }

        var shortSetup = AnalyzeCrtDirection(
            symbol,
            "SHORT",
            upperRange,
            lowerRange,
            currentSessionM1,
            session);

        var longSetup = AnalyzeCrtDirection(
            symbol,
            "LONG",
            upperRange,
            lowerRange,
            currentSessionM1,
            session);

        var setups = new List<CrtBinarySetup>();

        if (longSetup != null)
            setups.Add(longSetup);

        if (shortSetup != null)
            setups.Add(shortSetup);

        if (setups.Count == 0)
        {
            return Wait(
                symbol,
                0,
                $"CRT setup yoxdur. Session: {session.Name}. Sweep + return inside range + IFVG/CISD tesdiqi tapilmadi.");
        }

        var best = setups
            .OrderByDescending(x => x.Confidence)
            .First();

        var opposite = setups
            .Where(x => x.Direction != best.Direction)
            .OrderByDescending(x => x.Confidence)
            .FirstOrDefault();

        if (opposite != null &&
            Math.Abs(best.Confidence - opposite.Confidence) < 10)
        {
            return Wait(
                symbol,
                Math.Max(best.Confidence, opposite.Confidence),
                $"LONG ve SHORT CRT setup-lari yaxindir. Direction temiz deyil. LONG: {longSetup?.Confidence ?? 0}, SHORT: {shortSetup?.Confidence ?? 0}");
        }

        if (best.Confidence < MinimumConfidence)
        {
            return Wait(
                symbol,
                best.Confidence,
                $"CRT setup var, amma confidence kifayet deyil. Confidence: {best.Confidence}%, Minimum: {MinimumConfidence}%.");
        }

        var grade = GetGrade(best.Confidence);

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
            Grade = grade,
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
                    Direction = best.Direction,
                    Score = best.Confidence,
                    Reasons = best.Reasons
                }
            },
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static CrtBinarySetup? AnalyzeCrtDirection(
        string symbol,
        string direction,
        PriceCandle upperRange,
        PriceCandle lowerRange,
        List<PriceCandle> currentSessionM1,
        CrtSessionContext session)
    {
        var last = currentSessionM1[^1];

        var rangeHigh = upperRange.High;
        var rangeLow = lowerRange.Low;

        var rangeSize = rangeHigh - rangeLow;

        if (rangeSize <= 0)
            return null;

        var sweepCandle = direction == "SHORT"
            ? currentSessionM1.FirstOrDefault(x => x.High > rangeHigh)
            : currentSessionM1.FirstOrDefault(x => x.Low < rangeLow);

        if (sweepCandle == null)
            return null;

        var afterSweep = currentSessionM1
            .Where(x => x.TimeUtc >= sweepCandle.TimeUtc)
            .ToList();

        if (afterSweep.Count < 5)
            return null;

        var returnedInsideRange = direction == "SHORT"
            ? afterSweep.Any(x => x.Close < rangeHigh && x.Close > rangeLow)
            : afterSweep.Any(x => x.Close > rangeLow && x.Close < rangeHigh);

        if (!returnedInsideRange)
            return null;

        var ifvg = direction == "SHORT"
            ? DetectBearishIfvgAfterSweep(afterSweep)
            : DetectBullishIfvgAfterSweep(afterSweep);

        if (ifvg == null)
            return null;

        var cisd = direction == "SHORT"
            ? HasBearishCisd(afterSweep)
            : HasBullishCisd(afterSweep);

        if (!cisd)
            return null;

        var rejection = direction == "SHORT"
            ? HasBearishRejectionFromZone(afterSweep, ifvg.Low, ifvg.High)
            : HasBullishRejectionFromZone(afterSweep, ifvg.Low, ifvg.High);

        if (!rejection)
            return null;

        var priceNearIfvg = last.Close >= ifvg.Low && last.Close <= ifvg.High;

        var avgRange = AverageRange(currentSessionM1.TakeLast(30).ToList());

        if (avgRange <= 0)
            return null;

        var tooFarFromZone = direction == "SHORT"
            ? last.Close < ifvg.Low - avgRange * 4
            : last.Close > ifvg.High + avgRange * 4;

        if (tooFarFromZone)
            return null;

        var confidence = 0;
        var reasons = new List<string>();

        confidence += 20;
        reasons.Add($"CRT range: {session.PreviousSessionName} high/low M15 candle-lari esas goturuldu.");

        confidence += 20;
        reasons.Add(direction == "SHORT"
            ? "Manipulation: previous session high sweep edildi."
            : "Manipulation: previous session low sweep edildi.");

        confidence += 15;
        reasons.Add("Price sweep-den sonra CRT range icine qayitdi.");

        confidence += 20;
        reasons.Add(direction == "SHORT"
            ? "M1 bearish IFVG formalaşdı."
            : "M1 bullish IFVG formalaşdı.");

        confidence += 15;
        reasons.Add(direction == "SHORT"
            ? "M1 bearish CISD confirmation var."
            : "M1 bullish CISD confirmation var.");

        confidence += 10;
        reasons.Add("M1 rejection candle entry confirmation verdi.");

        if (priceNearIfvg)
        {
            confidence += 5;
            reasons.Add("Price IFVG entry zone daxilindedir.");
        }

        confidence = Math.Clamp(confidence, 0, 100);

        var expiry = CalculateExpiry(
            direction,
            currentSessionM1,
            avgRange);

        var entry = (decimal)last.Close;

        var invalidPrice = direction == "SHORT"
            ? (decimal)Math.Max(ifvg.High, sweepCandle.High)
            : (decimal)Math.Min(ifvg.Low, sweepCandle.Low);

        return new CrtBinarySetup
        {
            Symbol = symbol,
            Direction = direction,
            Confidence = confidence,
            EntryPrice = entry,
            InvalidPrice = invalidPrice,
            ExpiryMinutes = expiry,
            ExpiryReason = $"CRT model: {session.Name} manipulation + M1 IFVG + CISD esasinda {expiry} deqiqe secildi.",
            Reasons = reasons
        };
    }

    private static IfvgZone? DetectBearishIfvgAfterSweep(List<PriceCandle> candles)
    {
        var bullishFvgs = new List<IfvgZone>();

        for (var i = 2; i < candles.Count; i++)
        {
            var c1 = candles[i - 2];
            var c3 = candles[i];

            if (c1.High < c3.Low)
            {
                bullishFvgs.Add(new IfvgZone
                {
                    Direction = "BULLISH_FVG",
                    Low = c1.High,
                    High = c3.Low,
                    CreatedIndex = i
                });
            }
        }

        foreach (var fvg in bullishFvgs.OrderByDescending(x => x.CreatedIndex))
        {
            var after = candles
                .Skip(fvg.CreatedIndex + 1)
                .ToList();

            var violatedDown = after.Any(x => x.Close < fvg.Low);

            if (violatedDown)
            {
                return new IfvgZone
                {
                    Direction = "BEARISH_IFVG",
                    Low = fvg.Low,
                    High = fvg.High,
                    CreatedIndex = fvg.CreatedIndex
                };
            }
        }

        return null;
    }

    private static IfvgZone? DetectBullishIfvgAfterSweep(List<PriceCandle> candles)
    {
        var bearishFvgs = new List<IfvgZone>();

        for (var i = 2; i < candles.Count; i++)
        {
            var c1 = candles[i - 2];
            var c3 = candles[i];

            if (c1.Low > c3.High)
            {
                bearishFvgs.Add(new IfvgZone
                {
                    Direction = "BEARISH_FVG",
                    Low = c3.High,
                    High = c1.Low,
                    CreatedIndex = i
                });
            }
        }

        foreach (var fvg in bearishFvgs.OrderByDescending(x => x.CreatedIndex))
        {
            var after = candles
                .Skip(fvg.CreatedIndex + 1)
                .ToList();

            var violatedUp = after.Any(x => x.Close > fvg.High);

            if (violatedUp)
            {
                return new IfvgZone
                {
                    Direction = "BULLISH_IFVG",
                    Low = fvg.Low,
                    High = fvg.High,
                    CreatedIndex = fvg.CreatedIndex
                };
            }
        }

        return null;
    }

    private static bool HasBearishCisd(List<PriceCandle> candles)
    {
        if (candles.Count < 8)
            return false;

        var recent = candles.TakeLast(12).ToList();

        for (var i = 3; i < recent.Count; i++)
        {
            var previousBullish = recent
                .Skip(Math.Max(0, i - 3))
                .Take(3)
                .Where(x => x.IsBullish)
                .ToList();

            if (previousBullish.Count == 0)
                continue;

            var bullishLow = previousBullish.Min(x => x.Low);
            var current = recent[i];

            if (current.IsBearish && current.Close < bullishLow)
                return true;
        }

        return false;
    }

    private static bool HasBullishCisd(List<PriceCandle> candles)
    {
        if (candles.Count < 8)
            return false;

        var recent = candles.TakeLast(12).ToList();

        for (var i = 3; i < recent.Count; i++)
        {
            var previousBearish = recent
                .Skip(Math.Max(0, i - 3))
                .Take(3)
                .Where(x => x.IsBearish)
                .ToList();

            if (previousBearish.Count == 0)
                continue;

            var bearishHigh = previousBearish.Max(x => x.High);
            var current = recent[i];

            if (current.IsBullish && current.Close > bearishHigh)
                return true;
        }

        return false;
    }

    private static bool HasBearishRejectionFromZone(
        List<PriceCandle> candles,
        double zoneLow,
        double zoneHigh)
    {
        var recent = candles.TakeLast(5).ToList();

        foreach (var candle in recent)
        {
            var touchedZone = candle.High >= zoneLow && candle.Low <= zoneHigh;
            var bearish = candle.IsBearish;
            var upperRejection = candle.UpperWick >= candle.Body * 1.1;
            var strongClose = candle.Close <= candle.Low + candle.Range * 0.40;

            if (touchedZone && bearish && (upperRejection || strongClose))
                return true;
        }

        return false;
    }

    private static bool HasBullishRejectionFromZone(
        List<PriceCandle> candles,
        double zoneLow,
        double zoneHigh)
    {
        var recent = candles.TakeLast(5).ToList();

        foreach (var candle in recent)
        {
            var touchedZone = candle.High >= zoneLow && candle.Low <= zoneHigh;
            var bullish = candle.IsBullish;
            var lowerRejection = candle.LowerWick >= candle.Body * 1.1;
            var strongClose = candle.Close >= candle.Low + candle.Range * 0.60;

            if (touchedZone && bullish && (lowerRejection || strongClose))
                return true;
        }

        return false;
    }

    private static int CalculateExpiry(
        string direction,
        List<PriceCandle> candles,
        double avgRange)
    {
        var recent = candles.TakeLast(8).ToList();

        var directionalCount = direction == "LONG"
            ? recent.Count(x => x.IsBullish)
            : recent.Count(x => x.IsBearish);

        var lastMove = direction == "LONG"
            ? recent[^1].Close - recent.Min(x => x.Low)
            : recent.Max(x => x.High) - recent[^1].Close;

        if (avgRange <= 0)
            return 5;

        if (directionalCount >= 5 && lastMove > avgRange * 4)
            return 3;

        if (directionalCount >= 4)
            return 5;

        return 7;
    }

    private static CrtSessionContext? GetCurrentSessionContext(DateTime currentTime)
    {
        var date = currentTime.Date;

        var asiaStart = date.AddHours(0);
        var asiaEnd = date.AddHours(7);

        var londonStart = date.AddHours(7);
        var londonEnd = date.AddHours(12);

        var newYorkStart = date.AddHours(12);
        var newYorkEnd = date.AddHours(17);

        var londonCloseStart = date.AddHours(17);
        var londonCloseEnd = date.AddHours(20);

        var lateSessionStart = date.AddHours(20);
        var lateSessionEnd = date.AddDays(1);

        if (currentTime >= londonStart && currentTime < londonEnd)
        {
            return new CrtSessionContext
            {
                Name = "London",
                PreviousSessionName = "Asia",
                PreviousSessionStartUtc = asiaStart,
                PreviousSessionEndUtc = asiaEnd,
                CurrentSessionStartUtc = londonStart,
                CurrentSessionEndUtc = londonEnd
            };
        }

        if (currentTime >= newYorkStart && currentTime < newYorkEnd)
        {
            return new CrtSessionContext
            {
                Name = "New York",
                PreviousSessionName = "London",
                PreviousSessionStartUtc = londonStart,
                PreviousSessionEndUtc = londonEnd,
                CurrentSessionStartUtc = newYorkStart,
                CurrentSessionEndUtc = newYorkEnd
            };
        }

        if (currentTime >= londonCloseStart && currentTime < londonCloseEnd)
        {
            return new CrtSessionContext
            {
                Name = "London Close",
                PreviousSessionName = "New York",
                PreviousSessionStartUtc = newYorkStart,
                PreviousSessionEndUtc = newYorkEnd,
                CurrentSessionStartUtc = londonCloseStart,
                CurrentSessionEndUtc = londonCloseEnd
            };
        }

        if (currentTime >= lateSessionStart && currentTime < lateSessionEnd)
        {
            return new CrtSessionContext
            {
                Name = "Late Session",
                PreviousSessionName = "London Close",
                PreviousSessionStartUtc = londonCloseStart,
                PreviousSessionEndUtc = londonCloseEnd,
                CurrentSessionStartUtc = lateSessionStart,
                CurrentSessionEndUtc = lateSessionEnd
            };
        }

        if (currentTime < asiaEnd)
        {
            var previousDate = date.AddDays(-1);

            return new CrtSessionContext
            {
                Name = "Asia",
                PreviousSessionName = "New York",
                PreviousSessionStartUtc = previousDate.AddHours(12),
                PreviousSessionEndUtc = previousDate.AddHours(17),
                CurrentSessionStartUtc = asiaStart,
                CurrentSessionEndUtc = asiaEnd
            };
        }

        return null;
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

    private static SmartTradeSignal Wait(
        string symbol,
        int confidence,
        string reason)
    {
        return new SmartTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            ExpiryMinutes = 0,
            ExpiryReason = reason,
            Confidence = confidence,
            Grade = "NO_TRADE",
            Message = $"{symbol} WAIT",
            EntryType = "NO_ENTRY",
            ValidForSeconds = 0,
            LastClose = 0,
            InvalidIf = string.Empty,
            Reasons = new List<string>
            {
                reason
            },
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

    private static double AverageRange(List<PriceCandle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.Range);
    }

    private sealed class CrtSessionContext
    {
        public string Name { get; set; } = string.Empty;
        public string PreviousSessionName { get; set; } = string.Empty;
        public DateTime PreviousSessionStartUtc { get; set; }
        public DateTime PreviousSessionEndUtc { get; set; }
        public DateTime CurrentSessionStartUtc { get; set; }
        public DateTime CurrentSessionEndUtc { get; set; }
    }

    private sealed class IfvgZone
    {
        public string Direction { get; set; } = string.Empty;
        public double Low { get; set; }
        public double High { get; set; }
        public int CreatedIndex { get; set; }
    }

    private sealed class CrtBinarySetup
    {
        public string Symbol { get; set; } = string.Empty;
        public string Direction { get; set; } = string.Empty;
        public int Confidence { get; set; }
        public decimal EntryPrice { get; set; }
        public decimal InvalidPrice { get; set; }
        public int ExpiryMinutes { get; set; }
        public string ExpiryReason { get; set; } = string.Empty;
        public List<string> Reasons { get; set; } = new();
    }
}
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.MarketData;
using System.Globalization;

namespace PocketSignal.Api.Services.Binary;

public class SmartMoneySignalService : ISmartSignalService
{
    private readonly IMarketDataService _marketDataService;

    private const int MinimumScore = 82;

    public SmartMoneySignalService(IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<SmartTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var m15Response = await _marketDataService.GetCandlesAsync(symbol, "15min", 150, cancellationToken);
        var m5Response = await _marketDataService.GetCandlesAsync(symbol, "5min", 150, cancellationToken);
        var m1Response = await _marketDataService.GetCandlesAsync(symbol, "1min", 150, cancellationToken);

        var m15 = MapCandles(m15Response, symbol);
        var m5 = MapCandles(m5Response, symbol);
        var m1 = MapCandles(m1Response, symbol);

        if (m15.Count < 50 || m5.Count < 50 || m1.Count < 50)
        {
            return new SmartTradeSignal
            {
                Symbol = symbol,
                Direction = "WAIT",
                Confidence = 0,
                Grade = "NO_TRADE",
                Message = $"{symbol} WAIT",
                Reasons = new List<string>
                {
                    "Analiz ucun kifayet qeder candle data yoxdur."
                }
            };
        }

        var m15Bias = GetStructureBias(m15);
        var m5Bias = GetStructureBias(m5);

        var longScore = ScoreDirection("LONG", symbol, m15, m5, m1, m15Bias, m5Bias);
        var shortScore = ScoreDirection("SHORT", symbol, m15, m5, m1, m15Bias, m5Bias);

        var best = longScore.Score >= shortScore.Score ? longScore : shortScore;
        var lastClose = m1.Last().Close;

        // Sərt No-Trade Filter: M15 və M5 ziddirsə, əməliyyat yoxdur
        if (m15Bias != "NEUTRAL" && m5Bias != "NEUTRAL" && m15Bias != m5Bias)
        {
            return new SmartTradeSignal
            {
                Symbol = symbol,
                Direction = "WAIT",
                ExpiryMinutes = 0,
                ExpiryReason = "Expiry secilmedi: M15 ve M5 istiqametleri ziddir.",
                Confidence = Math.Max(longScore.Score, shortScore.Score),
                Grade = "NO_TRADE",
                Message = $"{symbol} WAIT",
                EntryType = "NO_ENTRY",
                ValidForSeconds = 0,
                LastClose = lastClose,
                InvalidIf = "",
                Reasons = new List<string>
                {
                    $"M15 bias: {m15Bias}",
                    $"M5 bias: {m5Bias}",
                    "No-trade filter aktivdir: M15 ve M5 istiqametleri bir-birine ziddir.",
                    "Binary option ucun bu zona risklidir, signal verilmedi."
                },
                SideAnalyses = new List<SideAnalysis>
                {
                    new SideAnalysis
                    {
                        Direction = "LONG",
                        Score = longScore.Score,
                        Reasons = longScore.Reasons
                    },
                    new SideAnalysis
                    {
                        Direction = "SHORT",
                        Score = shortScore.Score,
                        Reasons = shortScore.Reasons
                    }
                },
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        if (best.Score < MinimumScore)
        {
            var waitReasons = new List<string>
            {
                $"LONG score: {longScore.Score}",
                $"SHORT score: {shortScore.Score}",
                $"Minimum lazim olan score: {MinimumScore}",
                "A/A+ keyfiyyetinde setup yoxdur.",
                "Martingel ucun zeif signal gosterilmedi."
            };

            return new SmartTradeSignal
            {
                Symbol = symbol,
                Direction = "WAIT",
                ExpiryMinutes = 0,
                ExpiryReason = "Expiry secilmedi: minimum score tamamlanmadi.",
                Confidence = Math.Max(longScore.Score, shortScore.Score),
                Grade = "NO_TRADE",
                Message = $"{symbol} WAIT",
                EntryType = "NO_ENTRY",
                ValidForSeconds = 0,
                LastClose = lastClose,
                Reasons = waitReasons,
                SideAnalyses = new List<SideAnalysis>
                {
                    new SideAnalysis
                    {
                        Direction = "LONG",
                        Score = longScore.Score,
                        Reasons = longScore.Reasons
                    },
                    new SideAnalysis
                    {
                        Direction = "SHORT",
                        Score = shortScore.Score,
                        Reasons = shortScore.Reasons
                    }
                },
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        var expiryDecision = SelectExpiry(best);
        var grade = GetGrade(best.Score);

        return new SmartTradeSignal
        {
            Symbol = symbol,
            Direction = best.Direction,
            ExpiryMinutes = expiryDecision.Minutes,
            ExpiryReason = expiryDecision.Reason,
            Confidence = Math.Min(best.Score, 100),
            Grade = grade,
            Message = $"{symbol} {best.Direction} {expiryDecision.Minutes} deqiqelik ac",
            EntryType = "NEXT_M1_CANDLE_OPEN_OR_NOW_IF_VALID",
            ValidForSeconds = 25,
            LastClose = lastClose,
            InvalidIf = best.InvalidIf,
            Reasons = best.Reasons,
            SideAnalyses = new List<SideAnalysis>
            {
                new SideAnalysis
                {
                    Direction = "LONG",
                    Score = longScore.Score,
                    Reasons = longScore.Reasons
                },
                new SideAnalysis
                {
                    Direction = "SHORT",
                    Score = shortScore.Score,
                    Reasons = shortScore.Reasons
                }
            },
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static List<Candle> MapCandles(TwelveDataResponse? response, string symbol)
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

    private static DirectionScore ScoreDirection(
        string direction,
        string symbol,
        List<Candle> m15,
        List<Candle> m5,
        List<Candle> m1,
        string m15Bias,
        string m5Bias)
    {
        var score = 0;
        var reasons = new List<string>();

        if (m15Bias == direction)
        {
            score += 20;
            reasons.Add($"M15 bias {direction} istiqametindedir.");
        }
        else if (m15Bias == "NEUTRAL")
        {
            score += 5;
            reasons.Add("M15 bias neytraldir, amma setup davam edir.");
        }
        else
        {
            score -= 25;
            reasons.Add($"M15 bias {direction} istiqametine uygun deyil.");
        }

        if (m5Bias == direction)
        {
            score += 12;
            reasons.Add($"M5 struktur {direction} istiqametini destekleyir.");
        }
        else if (m5Bias == "NEUTRAL")
        {
            score += 4;
            reasons.Add("M5 struktur tam aydin deyil.");
        }
        else
        {
            score -= 15;
            reasons.Add($"M5 struktur {direction} ucun ziddir.");
        }

        var sweep = HasLiquiditySweep(m1, direction);
        if (sweep)
        {
            score += 18;
            reasons.Add(direction == "LONG"
                ? "M1 sell-side liquidity sweep tapildi."
                : "M1 buy-side liquidity sweep tapildi.");
        }

        var choch = HasChoch(m1, direction);
        if (choch)
        {
            score += 16;
            reasons.Add($"M1 {direction} CHoCH/BOS tesdiqi var.");
        }

        var zones = DetectM5Zones(m5);
        var avgRangeM1 = AverageRange(m1.TakeLast(20).ToList());
        var lastClose = m1.Last().Close;
        var tolerance = Math.Max(avgRangeM1 * 0.7m, lastClose * 0.00005m);

        var retestedZone = zones
            .Where(x => x.Direction == direction)
            .OrderByDescending(x => x.Time)
            .FirstOrDefault(x => x.Contains(lastClose, tolerance));

        var hasM5Zone = retestedZone != null;

        if (retestedZone != null)
        {
            score += 14;
            reasons.Add($"Qiymet M5 {retestedZone.Type} zonasinda/retest zonasindadir.");
        }

        var priceAction = HasPriceActionConfirmation(m1, direction);
        if (priceAction.IsConfirmed)
        {
            score += 10;
            reasons.Add(priceAction.Reason);
        }

        var volatility = IsVolatilityNormal(m1);
        if (volatility.IsNormal)
        {
            score += 8;
            reasons.Add(volatility.Reason);
        }
        else
        {
            score -= 10;
            reasons.Add(volatility.Reason);
        }

        var entryClean = IsEntryClean(m1);
        if (entryClean)
        {
            score += 8;
            reasons.Add("Entry gecikmis deyil, qiymet cox uzaqlasmayib.");
        }
        else
        {
            score -= 8;
            reasons.Add("Qiymet artiq cox hereket edib, entry gecikmis ola biler.");
        }

        var invalidLevel = direction == "LONG"
            ? m1.TakeLast(12).Min(x => x.Low)
            : m1.TakeLast(12).Max(x => x.High);

        var invalidIf = direction == "LONG"
            ? $"M1 candle {invalidLevel} altinda baglansa signal legvdir."
            : $"M1 candle {invalidLevel} ustunde baglansa signal legvdir.";

        return new DirectionScore
        {
            Direction = direction,
            Score = Math.Clamp(score, 0, 100),

            IsM15Aligned = m15Bias == direction,
            IsM5Aligned = m5Bias == direction,

            HasM5Zone = hasM5Zone,
            HasLiquiditySweep = sweep,
            HasChoch = choch,
            HasPriceAction = priceAction.IsConfirmed,
            IsVolatilityNormal = volatility.IsNormal,
            IsEntryClean = entryClean,

            InvalidIf = invalidIf,
            Reasons = reasons
        };
    }

    private static string GetStructureBias(List<Candle> candles)
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

        var last30 = candles.TakeLast(30).ToList();

        if (last30.Count < 30)
            return "NEUTRAL";

        var first = last30.First().Close;
        var last = last30.Last().Close;

        if (last > first)
            return "LONG";

        if (last < first)
            return "SHORT";

        return "NEUTRAL";
    }

    private static List<SwingPoint> FindSwings(List<Candle> candles, int left, int right)
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

    private static bool HasLiquiditySweep(List<Candle> candles, string direction)
    {
        if (candles.Count < 40)
            return false;

        var reference = candles
            .Take(candles.Count - 3)
            .TakeLast(30)
            .ToList();

        var last3 = candles
            .TakeLast(3)
            .ToList();

        if (reference.Count < 20)
            return false;

        var keyLow = reference.Min(x => x.Low);
        var keyHigh = reference.Max(x => x.High);

        if (direction == "LONG")
        {
            return last3.Any(x => x.Low < keyLow && x.Close > keyLow);
        }

        return last3.Any(x => x.High > keyHigh && x.Close < keyHigh);
    }

    private static bool HasChoch(List<Candle> candles, string direction)
    {
        if (candles.Count < 40)
            return false;

        var beforeLast = candles
            .Take(candles.Count - 1)
            .ToList();

        var swings = FindSwings(beforeLast, 2, 2);

        var lastClose = candles.Last().Close;

        if (direction == "LONG")
        {
            var lastSwingHigh = swings
                .Where(x => x.Kind == SwingKind.High)
                .OrderBy(x => x.Time)
                .LastOrDefault();

            return lastSwingHigh != null && lastClose > lastSwingHigh.Price;
        }
        else
        {
            var lastSwingLow = swings
                .Where(x => x.Kind == SwingKind.Low)
                .OrderBy(x => x.Time)
                .LastOrDefault();

            return lastSwingLow != null && lastClose < lastSwingLow.Price;
        }
    }

    private static List<PriceZone> DetectM5Zones(List<Candle> candles)
    {
        var zones = new List<PriceZone>();

        if (candles.Count < 30)
            return zones;

        var recent = candles.TakeLast(80).ToList();

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

            var currentBullishImpulse =
                current.Close > current.Open &&
                currentBody > avgBody * 1.5m &&
                previous.Close < previous.Open;

            if (currentBullishImpulse)
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

            var currentBearishImpulse =
                current.Close < current.Open &&
                currentBody > avgBody * 1.5m &&
                previous.Close > previous.Open;

            if (currentBearishImpulse)
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

    private static (bool IsConfirmed, string Reason) HasPriceActionConfirmation(
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
            var lowerRejection = lowerWick >= body * 1.2m;
            var strongClose = closePosition >= 0.65m;

            var bullishEngulfing =
                previous.Close < previous.Open &&
                last.Close > previous.Open &&
                last.Open <= previous.Close;

            if (bullish && lowerRejection && strongClose || bullishEngulfing)
            {
                return (true, "M1 bullish rejection/engulfing price action tesdiqi var.");
            }
        }
        else
        {
            var bearish = last.Close < last.Open;
            var upperRejection = upperWick >= body * 1.2m;
            var strongClose = closePosition <= 0.35m;

            var bearishEngulfing =
                previous.Close > previous.Open &&
                last.Close < previous.Open &&
                last.Open >= previous.Close;

            if (bearish && upperRejection && strongClose || bearishEngulfing)
            {
                return (true, "M1 bearish rejection/engulfing price action tesdiqi var.");
            }
        }

        return (false, "Son candle price action tesdiqi vermir.");
    }

    private static (bool IsNormal, string Reason) IsVolatilityNormal(List<Candle> candles)
    {
        if (candles.Count < 20)
            return (false, "Volatility analiz ucun kifayet qeder candle yoxdur.");

        var recent = candles.TakeLast(20).ToList();
        var avgRange = AverageRange(recent);
        var lastClose = candles.Last().Close;

        if (lastClose <= 0)
            return (false, "Qiymet duzgun deyil.");

        var rangePercent = avgRange / lastClose * 100m;

        if (rangePercent < 0.005m)
            return (false, "Volatility cox zeifdir, binary entry ucun risklidir.");

        if (rangePercent > 0.08m)
            return (false, "Volatility cox yuksekdir, entry gecike biler.");

        return (true, "Volatility normal araliqdadir.");
    }

    private static bool IsEntryClean(List<Candle> candles)
    {
        if (candles.Count < 20)
            return false;

        var recent = candles.TakeLast(20).ToList();
        var avgRange = AverageRange(recent);

        var last = candles[^1];
        var fourthBack = candles[^4];

        var move = Math.Abs(last.Close - fourthBack.Close);

        return move <= avgRange * 3m;
    }

    private static decimal AverageRange(List<Candle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.High - x.Low);
    }

    private static (int Minutes, string Reason) SelectExpiry(DirectionScore score)
    {
        // Ən ideal binary entry: zona + M1 təsdiq + təmiz giriş.
        // Belə setup-da 5 dəqiqə daha uyğundur, çünki entry dəqiqdir.
        if (score.Score >= 82 &&
            score.HasM5Zone &&
            score.HasLiquiditySweep &&
            score.HasChoch &&
            score.HasPriceAction &&
            score.IsEntryClean)
        {
            return (
                5,
                "M5 zona, M1 liquidity sweep, CHoCH ve price action eyni anda tesdiq verdi. Qisa 5 deqiqelik expiry uygundur."
            );
        }

        // Çox güclü böyük timeframe setup.
        // Entry M1-də tam ideal deyilsə, amma HTF çox güclüdürsə, daha uzun vaxt veririk.
        if (score.Score >= 94 &&
            score.IsM15Aligned &&
            score.IsM5Aligned &&
            score.HasM5Zone &&
            score.IsVolatilityNormal)
        {
            return (
                20,
                "Cox guclu M15/M5 confluence var. Daha boyuk hereket gozlenildiyi ucun 20 deqiqelik expiry secildi."
            );
        }

        // Güclü HTF setup, amma çox uzun gözləməyə ehtiyac yoxdur.
        if (score.Score >= 90 &&
            score.IsM15Aligned &&
            score.IsM5Aligned &&
            score.HasM5Zone &&
            score.IsVolatilityNormal)
        {
            return (
                15,
                "Setup daha cox M15/M5 strukturuna esaslanir. Qisa expiry yerine 15 deqiqelik expiry daha guvenlidir."
            );
        }

        // M15 və M5 uyğun, M1 struktur təsdiqi də var.
        if (score.Score >= 86 &&
            score.IsM15Aligned &&
            score.IsM5Aligned &&
            score.HasM5Zone &&
            score.HasChoch &&
            score.IsVolatilityNormal)
        {
            return (
                10,
                "M15 ve M5 eyni istiqametdedir, M5 zona ve M1 struktur tesdiqi var. 10 deqiqelik expiry daha uygundur."
            );
        }

        // Default seçim: minimum score keçilib, amma yuxarıdakı xüsusi şərtlərə düşməyib.
        return (
            5,
            "Default secim: setup qisa muddetli binary entry ucun 5 deqiqelik expiry ile qiymetlendirildi."
        );
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
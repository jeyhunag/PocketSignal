using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;
using System.Globalization;

namespace PocketSignal.Api.Services.Forex;

public class CoreForexSignalService : IForexSignalService
{
    private const int MinimumConfidence = 72;

    private readonly IMarketDataService _marketDataService;

    public CoreForexSignalService(IMarketDataService marketDataService)
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
                "NO_TRADE",
                new List<string>
                {
                    "Unicorn model ucun kifayet qeder M15/M1 candle yoxdur."
                },
                new List<ForexStrategyResult>());
        }

        var m15Trend = DetectTrend(m15);

        var longCandidate = FindUnicornCandidate(
            symbol,
            "LONG",
            m15,
            m1,
            m15Trend);

        var shortCandidate = FindUnicornCandidate(
            symbol,
            "SHORT",
            m15,
            m1,
            m15Trend);

        var candidates = new List<UnicornCandidate>();

        if (longCandidate != null)
            candidates.Add(longCandidate);

        if (shortCandidate != null)
            candidates.Add(shortCandidate);

        if (candidates.Count == 0)
        {
            var strategyResults = BuildEmptyStrategyResults(m15Trend);

            return Wait(
                symbol,
                0,
                "NO_TRADE",
                new List<string>
                {
                    $"M15 trend: {m15Trend}",
                    "Fresh FVG + Breaker Block overlap tapilmadi.",
                    "Unicorn model setup yoxdur."
                },
                strategyResults);
        }

        var best = candidates
            .OrderByDescending(x => x.Confidence)
            .First();

        var opposite = candidates
            .Where(x => x.Direction != best.Direction)
            .OrderByDescending(x => x.Confidence)
            .FirstOrDefault();

        if (opposite != null &&
            Math.Abs(best.Confidence - opposite.Confidence) < 8)
        {
            var results = BuildStrategyResults(best, opposite, m15Trend);

            return Wait(
                symbol,
                Math.Max(best.Confidence, opposite.Confidence),
                "NO_TRADE",
                new List<string>
                {
                    $"M15 trend: {m15Trend}",
                    $"LONG score: {(longCandidate?.Confidence ?? 0)}",
                    $"SHORT score: {(shortCandidate?.Confidence ?? 0)}",
                    "LONG ve SHORT unicorn setup-lari arasinda ferq azdir. Direction temiz deyil."
                },
                results);
        }

        if (!best.IsPriceInEntryZone)
        {
            var results = BuildStrategyResults(best, opposite, m15Trend);

            return Wait(
                symbol,
                best.Confidence,
                "WATCHLIST",
                new List<string>
                {
                    $"M15 trend: {m15Trend}",
                    $"{best.Direction} unicorn zone tapildi, amma price hele entry zone-a qayitmayib.",
                    $"Zone: {FormatPrice(best.ZoneLow)} - {FormatPrice(best.ZoneHigh)}"
                },
                results);
        }

        if (!best.HasEntryConfirmation)
        {
            var results = BuildStrategyResults(best, opposite, m15Trend);

            return Wait(
                symbol,
                best.Confidence,
                "WATCHLIST",
                new List<string>
                {
                    $"M15 trend: {m15Trend}",
                    $"{best.Direction} unicorn zone retest oldu, amma M1 rejection/confirmation yoxdur.",
                    "Transcript qaydasina gore boyuk zone-larda confirmation gozlemek lazimdir."
                },
                results);
        }

        if (!best.IsFreshFvg)
        {
            var results = BuildStrategyResults(best, opposite, m15Trend);

            return Wait(
                symbol,
                best.Confidence,
                "NO_TRADE",
                new List<string>
                {
                    $"{best.Direction} setup bloklandi: FVG fresh deyil / artiq mitigated olub.",
                    "Transcript qaydasi: FVG bir defe test olunandan sonra gelecek trade ucun istifade olunmur."
                },
                results);
        }

        if (!best.IsRiskPlanValid)
        {
            var results = BuildStrategyResults(best, opposite, m15Trend);

            return Wait(
                symbol,
                best.Confidence,
                "NO_TRADE",
                new List<string>
                {
                    $"{best.Direction} setup tapildi, amma risk plan uygun deyil.",
                    best.InvalidReason
                },
                results);
        }

        if (best.Confidence < MinimumConfidence)
        {
            var results = BuildStrategyResults(best, opposite, m15Trend);

            return Wait(
                symbol,
                best.Confidence,
                "WATCHLIST",
                new List<string>
                {
                    $"{best.Direction} unicorn setup var, amma confidence minimum seviyeye catmadi.",
                    $"Confidence: {best.Confidence}%, minimum: {MinimumConfidence}%"
                },
                results);
        }

        var reasons = new List<string>
        {
            $"Unicorn model {best.Direction} signal tesdiqlendi.",
            $"M15 trend: {m15Trend}",
            "Breaker Block + fresh FVG overlap high-confluence zone yaratdi.",
            "Price unicorn zone-a qayitdi.",
            "M1 rejection/confirmation entry verdi.",
            best.RiskReason
        };

        reasons.AddRange(best.Reasons);

        var strategyResultsFinal = BuildStrategyResults(best, opposite, m15Trend);

        var entry = RoundPrice(symbol, best.EntryPrice);
        var stopLoss = RoundPrice(symbol, best.StopLoss);
        var takeProfit1 = RoundPrice(symbol, best.TakeProfit1);
        var takeProfit2 = RoundPrice(symbol, best.TakeProfit2);

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
                ? $"M1 candle {RoundPrice(symbol, best.ZoneLow)} altinda baglansa trade legvdir."
                : $"M1 candle {RoundPrice(symbol, best.ZoneHigh)} ustunde baglansa trade legvdir.",

            ValidForMinutes = GetValidForMinutes(best.Confidence),
            Reasons = reasons.Distinct().ToList(),
            SideAnalyses = new List<SideAnalysis>
            {
                new SideAnalysis
                {
                    Direction = best.Direction,
                    Score = best.Confidence,
                    Reasons = best.Reasons
                }
            },
            StrategyResults = strategyResultsFinal,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static UnicornCandidate? FindUnicornCandidate(
        string symbol,
        string direction,
        List<PriceCandle> m15,
        List<PriceCandle> m1,
        string m15Trend)
    {
        var fvgZones = DetectFreshFvgs(m15, direction);

        if (fvgZones.Count == 0)
            return null;

        var candidates = new List<UnicornCandidate>();

        foreach (var fvg in fvgZones)
        {
            var breaker = FindBreakerBlockForFvg(m15, direction, fvg);

            if (breaker == null)
                continue;

            var overlapLow = Math.Max(fvg.Low, breaker.Low);
            var overlapHigh = Math.Min(fvg.High, breaker.High);

            if (overlapLow >= overlapHigh)
                continue;

            var candidate = BuildCandidate(
                symbol,
                direction,
                m15,
                m1,
                m15Trend,
                fvg,
                breaker,
                overlapLow,
                overlapHigh);

            candidates.Add(candidate);
        }

        return candidates
            .OrderByDescending(x => x.Confidence)
            .FirstOrDefault();
    }

    private static UnicornCandidate BuildCandidate(
        string symbol,
        string direction,
        List<PriceCandle> m15,
        List<PriceCandle> m1,
        string m15Trend,
        Zone fvg,
        Zone breaker,
        double zoneLow,
        double zoneHigh)
    {
        var lastM1 = m1[^1];
        var entryDouble = lastM1.Close;

        var avgM1Range = AverageRange(m1.TakeLast(30).ToList());
        var tolerance = avgM1Range * 1.2;

        var priceTouchedZone = direction == "LONG"
            ? lastM1.Low <= zoneHigh + tolerance && lastM1.Close >= zoneLow - tolerance
            : lastM1.High >= zoneLow - tolerance && lastM1.Close <= zoneHigh + tolerance;

        var priceInsideZone =
            entryDouble >= zoneLow - tolerance &&
            entryDouble <= zoneHigh + tolerance;

        var rejection = direction == "LONG"
            ? HasBullishRejectionFromZone(m1, zoneLow, zoneHigh, avgM1Range)
            : HasBearishRejectionFromZone(m1, zoneLow, zoneHigh, avgM1Range);

        var confidence = 0;
        var reasons = new List<string>();

        confidence += 25;
        reasons.Add("Fresh FVG tapildi.");

        confidence += 25;
        reasons.Add("Breaker Block FVG ile overlap edir.");

        if (priceTouchedZone || priceInsideZone)
        {
            confidence += 20;
            reasons.Add("Price unicorn zone-a qayidib/retest edib.");
        }

        if (rejection)
        {
            confidence += 20;
            reasons.Add("M1 rejection/confirmation var.");
        }

        if (IsTrendContextAligned(direction, m15Trend))
        {
            confidence += 10;
            reasons.Add("M15 trend direction ile uygundur.");
        }
        else if (m15Trend == "RANGE")
        {
            confidence += 5;
            reasons.Add("M15 range-dir, zone reaction daha vacibdir.");
        }
        else
        {
            confidence -= 8;
            reasons.Add("M15 trend direction ile tam uygun deyil.");
        }

        var stopBuffer = avgM1Range * 0.8;

        if (stopBuffer <= 0)
            stopBuffer = entryDouble * 0.0005;

        var entry = (decimal)entryDouble;
        var zoneLowDecimal = (decimal)zoneLow;
        var zoneHighDecimal = (decimal)zoneHigh;
        var buffer = (decimal)stopBuffer;

        decimal stopLoss;
        decimal takeProfit1;
        decimal takeProfit2;
        decimal risk;

        if (direction == "LONG")
        {
            stopLoss = zoneLowDecimal - buffer;

            if (stopLoss >= entry)
                stopLoss = entry - Math.Abs(buffer);

            risk = entry - stopLoss;

            takeProfit1 = entry + risk * 2m;
            takeProfit2 = entry + risk * 3m;
        }
        else
        {
            stopLoss = zoneHighDecimal + buffer;

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

        var riskReward1 = riskPips > 0 ? rewardPips1 / riskPips : 0;
        var riskReward2 = riskPips > 0 ? rewardPips2 / riskPips : 0;

        var isRiskValid =
            risk > 0 &&
            riskPips >= 5 &&
            riskReward1 >= 1.5m &&
            riskReward2 >= 2.2m;

        var invalidReason = string.Empty;

        if (!isRiskValid)
        {
            invalidReason =
                $"Risk plan uygun deyil. RiskPips: {Math.Round(riskPips, 1)}, RR1: {Math.Round(riskReward1, 2)}, RR2: {Math.Round(riskReward2, 2)}";
        }

        if (riskPips > 100)
        {
            confidence -= 10;
            invalidReason = $"Risk mesafesi cox boyukdur: {Math.Round(riskPips, 1)} pips.";
            isRiskValid = false;
        }

        confidence = Math.Clamp(confidence, 0, 100);

        return new UnicornCandidate
        {
            Direction = direction,
            Fvg = fvg,
            Breaker = breaker,
            ZoneLow = zoneLow,
            ZoneHigh = zoneHigh,
            EntryPrice = entry,
            StopLoss = stopLoss,
            TakeProfit1 = takeProfit1,
            TakeProfit2 = takeProfit2,
            RiskPips = riskPips,
            RewardPips1 = rewardPips1,
            RewardPips2 = rewardPips2,
            RiskReward1 = riskReward1,
            RiskReward2 = riskReward2,
            Confidence = confidence,
            IsFreshFvg = fvg.IsFresh,
            IsPriceInEntryZone = priceTouchedZone || priceInsideZone,
            HasEntryConfirmation = rejection,
            IsRiskPlanValid = isRiskValid,
            InvalidReason = invalidReason,
            RiskReason = "SL unicorn zone arxasinda, TP1 1:2, TP2 1:3 hesablandi.",
            Reasons = reasons
        };
    }

    private static List<Zone> DetectFreshFvgs(
        List<PriceCandle> candles,
        string direction)
    {
        var zones = new List<Zone>();

        for (var i = 2; i < candles.Count - 1; i++)
        {
            var c1 = candles[i - 2];
            var c3 = candles[i];

            if (direction == "LONG")
            {
                if (c1.High < c3.Low && c3.Close > c3.Open)
                {
                    var zone = new Zone
                    {
                        Type = "BULLISH_FVG",
                        Direction = "LONG",
                        Low = c1.High,
                        High = c3.Low,
                        CreatedIndex = i,
                        IsFresh = IsFvgFresh(candles, i, c1.High, c3.Low)
                    };

                    if (zone.IsFresh)
                        zones.Add(zone);
                }
            }

            if (direction == "SHORT")
            {
                if (c1.Low > c3.High && c3.Close < c3.Open)
                {
                    var zone = new Zone
                    {
                        Type = "BEARISH_FVG",
                        Direction = "SHORT",
                        Low = c3.High,
                        High = c1.Low,
                        CreatedIndex = i,
                        IsFresh = IsFvgFresh(candles, i, c3.High, c1.Low)
                    };

                    if (zone.IsFresh)
                        zones.Add(zone);
                }
            }
        }

        return zones
            .OrderByDescending(x => x.CreatedIndex)
            .Take(10)
            .ToList();
    }

    private static bool IsFvgFresh(
        List<PriceCandle> candles,
        int createdIndex,
        double low,
        double high)
    {
        for (var i = createdIndex + 1; i < candles.Count - 1; i++)
        {
            var candle = candles[i];

            var touched =
                candle.Low <= high &&
                candle.High >= low;

            if (touched)
                return false;
        }

        return true;
    }

    private static Zone? FindBreakerBlockForFvg(
        List<PriceCandle> candles,
        string direction,
        Zone fvg)
    {
        var start = Math.Max(0, fvg.CreatedIndex - 12);

        if (direction == "LONG")
        {
            for (var i = fvg.CreatedIndex - 1; i >= start; i--)
            {
                var c = candles[i];

                if (!c.IsBearish)
                    continue;

                var brokenAbove = candles
                    .Skip(i + 1)
                    .Take(fvg.CreatedIndex - i)
                    .Any(x => x.Close > c.High);

                if (!brokenAbove)
                    continue;

                return new Zone
                {
                    Type = "BULLISH_BREAKER",
                    Direction = "LONG",
                    Low = c.Low,
                    High = c.High,
                    CreatedIndex = i,
                    IsFresh = true
                };
            }
        }

        if (direction == "SHORT")
        {
            for (var i = fvg.CreatedIndex - 1; i >= start; i--)
            {
                var c = candles[i];

                if (!c.IsBullish)
                    continue;

                var brokenBelow = candles
                    .Skip(i + 1)
                    .Take(fvg.CreatedIndex - i)
                    .Any(x => x.Close < c.Low);

                if (!brokenBelow)
                    continue;

                return new Zone
                {
                    Type = "BEARISH_BREAKER",
                    Direction = "SHORT",
                    Low = c.Low,
                    High = c.High,
                    CreatedIndex = i,
                    IsFresh = true
                };
            }
        }

        return null;
    }

    private static bool HasBullishRejectionFromZone(
        List<PriceCandle> m1,
        double zoneLow,
        double zoneHigh,
        double avgRange)
    {
        var recent = m1.TakeLast(4).ToList();

        foreach (var candle in recent)
        {
            var touchedZone = candle.Low <= zoneHigh && candle.High >= zoneLow;
            var bullish = candle.Close > candle.Open;
            var lowerRejection = candle.LowerWick >= candle.Body * 1.2;
            var strongClose = candle.Close >= candle.Low + candle.Range * 0.62;

            if (touchedZone && bullish && (lowerRejection || strongClose) && candle.Range >= avgRange * 0.5)
                return true;
        }

        return false;
    }

    private static bool HasBearishRejectionFromZone(
        List<PriceCandle> m1,
        double zoneLow,
        double zoneHigh,
        double avgRange)
    {
        var recent = m1.TakeLast(4).ToList();

        foreach (var candle in recent)
        {
            var touchedZone = candle.Low <= zoneHigh && candle.High >= zoneLow;
            var bearish = candle.Close < candle.Open;
            var upperRejection = candle.UpperWick >= candle.Body * 1.2;
            var strongClose = candle.Close <= candle.Low + candle.Range * 0.38;

            if (touchedZone && bearish && (upperRejection || strongClose) && candle.Range >= avgRange * 0.5)
                return true;
        }

        return false;
    }

    private static string DetectTrend(List<PriceCandle> candles)
    {
        var recent = candles.TakeLast(24).ToList();

        var firstClose = recent.First().Close;
        var lastClose = recent.Last().Close;

        var fast = recent.TakeLast(6).Average(x => x.Close);
        var slow = recent.TakeLast(18).Average(x => x.Close);

        var avgRange = AverageRange(recent);

        if (lastClose > firstClose + avgRange && fast > slow)
            return "BULLISH";

        if (lastClose < firstClose - avgRange && fast < slow)
            return "BEARISH";

        return "RANGE";
    }

    private static bool IsTrendContextAligned(
        string direction,
        string trend)
    {
        if (direction == "LONG" && trend == "BULLISH")
            return true;

        if (direction == "SHORT" && trend == "BEARISH")
            return true;

        return false;
    }

    private static List<ForexStrategyResult> BuildStrategyResults(
        UnicornCandidate best,
        UnicornCandidate? opposite,
        string m15Trend)
    {
        var results = new List<ForexStrategyResult>
        {
            new ForexStrategyResult
            {
                StrategyName = "UnicornModel",
                Direction = best.Direction,
                Score = best.Confidence,
                MaxScore = 100,
                IsConfirmed = best.Confidence >= MinimumConfidence,
                Reasons = best.Reasons
            },
            new ForexStrategyResult
            {
                StrategyName = "M15Context",
                Direction = best.Direction,
                Score = IsTrendContextAligned(best.Direction, m15Trend) ? 20 : 8,
                MaxScore = 20,
                IsConfirmed = IsTrendContextAligned(best.Direction, m15Trend),
                Reasons = new List<string>
                {
                    $"M15 trend: {m15Trend}"
                }
            }
        };

        if (opposite != null)
        {
            results.Add(new ForexStrategyResult
            {
                StrategyName = "OppositeUnicornModel",
                Direction = opposite.Direction,
                Score = opposite.Confidence,
                MaxScore = 100,
                IsConfirmed = opposite.Confidence >= MinimumConfidence,
                Reasons = opposite.Reasons
            });
        }

        return results;
    }

    private static List<ForexStrategyResult> BuildEmptyStrategyResults(
        string m15Trend)
    {
        return new List<ForexStrategyResult>
        {
            new ForexStrategyResult
            {
                StrategyName = "UnicornModel",
                Direction = "WAIT",
                Score = 0,
                MaxScore = 100,
                IsConfirmed = false,
                Reasons = new List<string>
                {
                    "Unicorn setup yoxdur."
                }
            },
            new ForexStrategyResult
            {
                StrategyName = "M15Context",
                Direction = "WAIT",
                Score = 0,
                MaxScore = 20,
                IsConfirmed = false,
                Reasons = new List<string>
                {
                    $"M15 trend: {m15Trend}"
                }
            }
        };
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
            Message = $"{symbol} FOREX WAIT",
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

    private static decimal RoundPrice(string symbol, decimal price)
    {
        var digits = GetDigits(symbol);
        return Math.Round(price, digits);
    }

    private static decimal RoundPrice(string symbol, double price)
    {
        var digits = GetDigits(symbol);
        return Math.Round((decimal)price, digits);
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

    private static double AverageRange(List<PriceCandle> candles)
    {
        if (candles.Count == 0)
            return 0;

        return candles.Average(x => x.Range);
    }

    private sealed class Zone
    {
        public string Type { get; set; } = "";
        public string Direction { get; set; } = "";
        public double Low { get; set; }
        public double High { get; set; }
        public int CreatedIndex { get; set; }
        public bool IsFresh { get; set; }
    }

    private sealed class UnicornCandidate
    {
        public string Direction { get; set; } = "";

        public Zone Fvg { get; set; } = new();

        public Zone Breaker { get; set; } = new();

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

        public int Confidence { get; set; }

        public bool IsFreshFvg { get; set; }

        public bool IsPriceInEntryZone { get; set; }

        public bool HasEntryConfirmation { get; set; }

        public bool IsRiskPlanValid { get; set; }

        public string InvalidReason { get; set; } = "";

        public string RiskReason { get; set; } = "";

        public List<string> Reasons { get; set; } = new();
    }
}
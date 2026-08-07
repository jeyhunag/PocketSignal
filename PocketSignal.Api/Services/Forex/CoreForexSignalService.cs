using System.Globalization;
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Forex;

/// <summary>
/// CASSANDRA — Bias + Zona sistemi (yalnız XAU/USD, M15 analiz).
///
/// ŞAHMAT MƏNTİQİ:
///   • Qərar Nöqtəsi = ŞAH. Bu qırılsa bias dəyişir.
///   • Zonalar = PİYADALAR. Bias istiqamətindəki güclü support/resistance səviyyələri.
///   • Bias SELL-dirsə, ŞAH qırılana qədər yalnız SELL zonaları işlək (BUY yox).
///   • Bias BUY-dursa, ŞAH qırılana qədər yalnız BUY zonaları işlək (SELL yox).
///
/// TƏKMİLLƏŞDİRMƏLƏR (v2):
///   1. Zona keyfiyyəti — swing səviyyələri toleransla qruplaşdırılır;
///      neçə dəfə toxunulubsa o qədər güclü (touch count).
///   2. Şah — struktur qırılması əsasında (ən son təsdiqlənmiş HH/LL).
///   3. Parametrlər M15-ə uyğun.
///   4. Bias dəyişimi ForexTradeSignal-da işarələnir (BiasChanged).
/// </summary>
public class CoreForexSignalService : IForexSignalService
{
    private const string TargetSymbol = "XAU/USD";

    private const int CandleCount = 300;

    // Neçə zona göstərilsin — real support/resistance neçə varsa (maksimum bu qədər).
    private const int MaxZones = 5;

    // Biasa TƏRS zona üçün minimum toxunuş sayı — bundan az olsa göstərilmir.
    // 3+ toxunuş = həqiqətən güclü səviyyə (2 çox zəifdir, uydurma olar).
    private const int MinCounterTouches = 3;

    // ==================== TIMEFRAME PARAMETRLƏRİ ====================
    // M15 optimaldır (toxunulmur). M1/M5 M15 keyfiyyətinə uyğunlaşdırılıb:
    // kiçik TF-də daha çox şam + daha geniş swing (noise-a qarşı) + daha dar tolerans.
    private sealed record TfParams(
        string Interval,
        int TrendLookback,
        int SwingLeft,
        int SwingRight,
        decimal ZoneTolerancePct);

    private static TfParams GetTfParams(string timeframe)
    {
        return timeframe switch
        {
            "1min" => new TfParams("1min", 120, 5, 5, 0.0008m),
            "5min" => new TfParams("5min", 80, 5, 5, 0.0012m),
            _ => new TfParams("15min", 60, 4, 4, 0.0015m)   // M15 — OPTIMAL, toxunulmaz
        };
    }


    private readonly IMarketDataService _marketDataService;

    public CoreForexSignalService(
        IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<ForexTradeSignal> AnalyzeAsync(
        string symbol,
        string timeframe = "15min",
        CancellationToken cancellationToken = default)
    {
        // Gələn symbol analiz olunur (XAU/USD, EUR/USD və s.) — hansı seçilibsə.
        if (string.IsNullOrWhiteSpace(symbol))
            symbol = TargetSymbol;

        // Timeframe-ə görə parametrlər (M15 optimal, M1/M5 uyğunlaşdırılıb).
        var tf = GetTfParams(timeframe);

        var response = await _marketDataService.GetCandlesAsync(
            symbol,
            tf.Interval,
            CandleCount,
            cancellationToken);

        var candles = MapCandles(response, symbol);

        if (candles.Count < tf.TrendLookback + 30)
            return Wait(symbol, $"Cassandra analizi üçün kifayət qədər {tf.Interval} candle yoxdur.");

        var lastPrice = candles[^1].Close;

        // ===== SWING nöqtələri =====
        var swings = FindSwings(candles, tf.SwingLeft, tf.SwingRight);

        var swingHighs = swings.Where(x => x.Kind == "HIGH").OrderBy(x => x.Index).ToList();
        var swingLows = swings.Where(x => x.Kind == "LOW").OrderBy(x => x.Index).ToList();

        if (swingHighs.Count < 3 || swingLows.Count < 3)
            return Wait(symbol, "Cassandra: kifayət qədər swing nöqtəsi tapılmadı.");

        // ===== 1) BIAS (struktur qırılması əsasında) =====
        var bias = DetermineBias(candles, swingHighs, swingLows, tf.TrendLookback);

        // ===== 2) ZONALAR — real support/resistance (swing əsaslı) =====
        // Sadə və dürüst: zona = qiymətin dəfələrlə dönüş etdiyi swing səviyyəsi.
        // Yaxın swing-lər bir zonaya qruplaşdırılır. Neçə real zona varsa o qədər.
        // Uydurma yoxdur — zona yoxdursa göstərilmir.
        var sellZones = new List<decimal>();
        var buyZones = new List<decimal>();

        var highClusters = ClusterLevelsTol(swingHighs.Select(x => x.Price).ToList(), lastPrice, tf.ZoneTolerancePct);
        var lowClusters = ClusterLevelsTol(swingLows.Select(x => x.Price).ToList(), lastPrice, tf.ZoneTolerancePct);

        if (bias == "SELL")
        {
            // SELL zonaları: qiymətdən YUXARIDAKI resistance səviyyələri.
            sellZones = highClusters
                .Where(c => c.Price >= lastPrice)
                .OrderBy(c => Math.Abs(c.Price - lastPrice))
                .Take(MaxZones)
                .Select(c => c.Price)
                .OrderBy(p => p)
                .ToList();
        }
        else if (bias == "BUY")
        {
            // BUY zonaları: qiymətdən AŞAĞIDAKI support səviyyələri.
            buyZones = lowClusters
                .Where(c => c.Price <= lastPrice)
                .OrderBy(c => Math.Abs(c.Price - lastPrice))
                .Take(MaxZones)
                .Select(c => c.Price)
                .OrderByDescending(p => p)
                .ToList();
        }

        // ===== 3) QƏRAR NÖQTƏSİ (ŞAH) — piyadalardan KƏNARDA + GÜCLÜ =====
        // ŞAH "son müdafiə xətti"dir və ən güclü (çox toxunulan) səviyyə olmalıdır.
        //   BUY bias: şah bütün BUY zonalarından AŞAĞIDA. Qiymət oranı qırsa → bias SELL.
        //   SELL bias: şah bütün SELL zonalarından YUXARIDA. Qiymət oranı qırsa → bias BUY.
        var decisionPoint = DetermineDecisionPoint(
            bias, buyZones, sellZones, lowClusters, highClusters, lastPrice, swingLows, swingHighs);

        // ===== ƏN YAXIN ZONA =====
        var activeZones = bias == "SELL" ? sellZones : buyZones;
        var nearestZone = activeZones.Count > 0
            ? activeZones.OrderBy(p => Math.Abs(p - lastPrice)).First()
            : decisionPoint;

        // ===== BIASA TƏRS ZONA (ən güclü əks səviyyə) =====
        // BUY bias: yuxarıdakı ən çox toxunulan resistance (güclü tepki → aşağı reaksiya).
        // SELL bias: aşağıdakı ən çox toxunulan support (güclü tepki → yuxarı reaksiya).
        // Yalnız MinCounterTouches+ toxunuş varsa göstərilir (uydurma yoxdur).
        decimal counterZone = 0;
        if (bias == "BUY")
        {
            var strongest = highClusters
                .Where(c => c.Price > lastPrice && c.Touches >= MinCounterTouches)
                .OrderByDescending(c => c.Touches)
                .ThenBy(c => Math.Abs(c.Price - lastPrice))
                .FirstOrDefault();
            if (strongest != null)
                counterZone = strongest.Price;
        }
        else if (bias == "SELL")
        {
            var strongest = lowClusters
                .Where(c => c.Price < lastPrice && c.Touches >= MinCounterTouches)
                .OrderByDescending(c => c.Touches)
                .ThenBy(c => Math.Abs(c.Price - lastPrice))
                .FirstOrDefault();
            if (strongest != null)
                counterZone = strongest.Price;
        }

        // ===== Mətn (Cassandra formatı) =====
        var note = BuildBiasNote(symbol, bias, sellZones, buyZones, decisionPoint, nearestZone, counterZone);

        var direction = bias == "SELL" ? "SHORT"
            : bias == "BUY" ? "LONG"
            : "WAIT";

        // Cassandra order/trade sistemi DEYİL — yalnız bias/zona məlumatı.
        // DB-də və köhnə trade-tracker-də "trade" kimi qeydə düşməsin deyə
        // Direction həmişə WAIT saxlanır (əsl istiqamət Bias sahəsindədir).
        var dbDirection = "WAIT";

        var reasons = new List<string>
        {
            $"Cassandra Bias: {bias}.",
            $"Qərar nöqtəsi (şah): {FormatPrice(symbol, decisionPoint)}.",
            $"Ən yaxın zona: {FormatPrice(symbol, nearestZone)}.",
            bias == "SELL"
                ? "Qiymət satış tərəfinin nəzarətindədir."
                : bias == "BUY"
                    ? "Qiymət alış tərəfinin nəzarətindədir."
                    : "Bias neytraldır."
        };

        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = dbDirection,

            Bias = bias,
            SellZones = sellZones,
            BuyZones = buyZones,
            DecisionPoint = RoundPrice(symbol, decisionPoint),
            NearestZone = RoundPrice(symbol, nearestZone),
            CounterZone = counterZone > 0 ? RoundPrice(symbol, counterZone) : 0,
            Timeframe = tf.Interval,
            LastPrice = RoundPrice(symbol, lastPrice),
            BiasNote = note,

            Confidence = bias == "NEUTRAL" ? 0 : 75,
            Grade = bias == "NEUTRAL" ? "NO_TRADE" : "B",
            Message = $"{symbol} | Bias: {bias} | Ən yaxın zona: {FormatPrice(symbol, nearestZone)}",
            Reasons = reasons,
            SideAnalyses = new List<SideAnalysis>(),
            StrategyResults = new List<ForexStrategyResult>
            {
                new ForexStrategyResult
                {
                    StrategyName = "CASSANDRA_XAU_M15",
                    Direction = direction == "WAIT" ? "FILTER" : direction,
                    Score = bias == "NEUTRAL" ? 0 : 75,
                    MaxScore = 100,
                    IsConfirmed = bias != "NEUTRAL",
                    Reasons = reasons
                }
            },
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    // ==================== BIAS (struktur qırılması) ====================

    /// <summary>
    /// Bias: SELL / BUY / NEUTRAL.
    /// Struktur: son swing HIGH/LOW-lara baxır.
    ///   Higher-High + Higher-Low → BUY (yüksələn struktur).
    ///   Lower-High + Lower-Low → SELL (enən struktur).
    /// Struktur qeyri-müəyyəndirsə qiymət trendinə (lastClose vs pastClose) əsaslanır.
    /// </summary>
    private static string DetermineBias(
        List<Candle> candles,
        List<SwingPoint> swingHighs,
        List<SwingPoint> swingLows,
        int trendLookback)
    {
        var recentHighs = swingHighs.TakeLast(2).ToList();
        var recentLows = swingLows.TakeLast(2).ToList();

        var structureTrend = "FLAT";
        if (recentHighs.Count >= 2 && recentLows.Count >= 2)
        {
            var hh = recentHighs[1].Price > recentHighs[0].Price;
            var hl = recentLows[1].Price > recentLows[0].Price;
            var lh = recentHighs[1].Price < recentHighs[0].Price;
            var ll = recentLows[1].Price < recentLows[0].Price;

            if (hh && hl)
                structureTrend = "UP";
            else if (lh && ll)
                structureTrend = "DOWN";
        }

        var lastClose = candles[^1].Close;
        var lookback = Math.Min(trendLookback, candles.Count - 1);
        var pastClose = candles[^(lookback + 1)].Close;
        var priceTrend = lastClose > pastClose ? "UP" : lastClose < pastClose ? "DOWN" : "FLAT";

        // Struktur əsasdır; qiymət trendi təsdiq/köməkçidir.
        if (structureTrend == "UP" && priceTrend != "DOWN")
            return "BUY";
        if (structureTrend == "DOWN" && priceTrend != "UP")
            return "SELL";

        if (priceTrend == "UP")
            return "BUY";
        if (priceTrend == "DOWN")
            return "SELL";

        return "NEUTRAL";
    }

    // ==================== QƏRAR NÖQTƏSİ (ŞAH) ====================

    /// <summary>
    /// ŞAH = son müdafiə xətti. Piyadalardan (zonalardan) KƏNARDA və GÜCLÜ (çox toxunulan).
    ///   BUY bias: bütün BUY zonalarından AŞAĞIDA olan ən güclü support.
    ///             Qiymət oranı aşağı qırsa → alıcılar məğlub → bias SELL.
    ///   SELL bias: bütün SELL zonalarından YUXARIDA olan ən güclü resistance.
    ///             Qiymət oranı yuxarı qırsa → satıcılar məğlub → bias BUY.
    /// </summary>
    private static decimal DetermineDecisionPoint(
        string bias,
        List<decimal> buyZones,
        List<decimal> sellZones,
        List<LevelCluster> lowClusters,
        List<LevelCluster> highClusters,
        decimal lastPrice,
        List<SwingPoint> swingLows,
        List<SwingPoint> swingHighs)
    {
        if (bias == "BUY")
        {
            // Ən aşağı BUY zonası — şah bundan da aşağıda olmalı.
            var lowestZone = buyZones.Count > 0 ? buyZones.Min() : lastPrice;

            // Bu zonadan aşağıdakı cluster-lər arasından ən güclüsü (çox toxunulan).
            var candidates = lowClusters
                .Where(c => c.Price < lowestZone)
                .ToList();

            if (candidates.Count > 0)
            {
                // Əvvəlcə güc (touch), sonra yaxınlıq.
                return candidates
                    .OrderByDescending(c => c.Touches)
                    .ThenByDescending(c => c.Price)
                    .First()
                    .Price;
            }

            // Fallback: ən aşağı swing low.
            return swingLows.Count > 0 ? swingLows.Min(x => x.Price) : lowestZone;
        }

        if (bias == "SELL")
        {
            // Ən yuxarı SELL zonası — şah bundan da yuxarıda olmalı.
            var highestZone = sellZones.Count > 0 ? sellZones.Max() : lastPrice;

            var candidates = highClusters
                .Where(c => c.Price > highestZone)
                .ToList();

            if (candidates.Count > 0)
            {
                return candidates
                    .OrderByDescending(c => c.Touches)
                    .ThenBy(c => c.Price)
                    .First()
                    .Price;
            }

            return swingHighs.Count > 0 ? swingHighs.Max(x => x.Price) : highestZone;
        }

        // NEUTRAL
        return lastPrice;
    }

    // ==================== ZONA QRUPLAŞDIRMA (touch count) ====================

    /// <summary>
    /// Yaxın qiymət səviyyələrini bir zonaya qruplaşdırır.
    /// Hər zona: orta qiymət + neçə swing ora düşüb (Touches).
    /// Çox toxunulan səviyyə = güclü support/resistance.
    /// </summary>
    private static List<LevelCluster> ClusterLevelsTol(
        List<decimal> levels,
        decimal referencePrice,
        decimal zoneTolerancePct)
    {
        var result = new List<LevelCluster>();
        if (levels.Count == 0)
            return result;

        var tolerance = referencePrice * zoneTolerancePct;
        if (tolerance <= 0)
            tolerance = 1m;

        var sorted = levels.OrderBy(x => x).ToList();

        var currentGroup = new List<decimal> { sorted[0] };

        foreach (var price in sorted.Skip(1))
        {
            if (Math.Abs(price - currentGroup.Average()) <= tolerance)
            {
                currentGroup.Add(price);
            }
            else
            {
                result.Add(new LevelCluster
                {
                    Price = currentGroup.Average(),
                    Touches = currentGroup.Count
                });
                currentGroup = new List<decimal> { price };
            }
        }

        result.Add(new LevelCluster
        {
            Price = currentGroup.Average(),
            Touches = currentGroup.Count
        });

        // Güclüdən zəifə (çox toxunulan öndə).
        return result.OrderByDescending(x => x.Touches).ToList();
    }

    // ==================== SWING ====================

    private static List<SwingPoint> FindSwings(
        List<Candle> candles,
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
                swings.Add(new SwingPoint { Index = i, Price = candles[i].High, Kind = "HIGH" });

            if (isLow)
                swings.Add(new SwingPoint { Index = i, Price = candles[i].Low, Kind = "LOW" });
        }

        return swings;
    }

    // ==================== MƏTN ====================

    private static string BuildBiasNote(
        string symbol,
        string bias,
        List<decimal> sellZones,
        List<decimal> buyZones,
        decimal decisionPoint,
        decimal nearestZone,
        decimal counterZone)
    {
        var lines = new List<string>();

        if (bias == "SELL")
        {
            lines.Add("Qiymət hazırda satış tərəfinin nəzarətindədir. Əsas plan: yuxarı zonalara reaksiya gəldikdə SELL fürsətini izləmək.");
            lines.Add("");
            lines.Add("🔴 SELL zonaları:");
            foreach (var z in sellZones)
                lines.Add($"• Sell zone: {FormatPrice(symbol, z)}");
        }
        else if (bias == "BUY")
        {
            lines.Add("Qiymət hazırda alış tərəfinin nəzarətindədir. Əsas plan: aşağı zonalara reaksiya gəldikdə BUY fürsətini izləmək.");
            lines.Add("");
            lines.Add("🟢 BUY zonaları:");
            foreach (var z in buyZones)
                lines.Add($"• Buy zone: {FormatPrice(symbol, z)}");
        }
        else
        {
            lines.Add("Bias neytraldır. Təmiz istiqamət olana qədər gözləmək tövsiyə olunur.");
        }

        lines.Add($"• Qərar Nöqtəsi (şah): {FormatPrice(symbol, decisionPoint)}");

        // Biasa tərs zona — yalnız güclü tapılıbsa.
        if (counterZone > 0)
        {
            var counterLabel = bias == "BUY" ? "Sell zone (Biasa tərs)" : "Buy zone (Biasa tərs)";
            lines.Add($"• {counterLabel}: {FormatPrice(symbol, counterZone)}");
        }
        lines.Add("");
        lines.Add($"🎯 Ən yaxın zona: {FormatPrice(symbol, nearestZone)}");
        lines.Add("⚠️ Riskinizi düzgün idarə edin.");

        return string.Join("\n", lines);
    }

    // ==================== KÖMƏKÇİ ====================

    private static List<Candle> MapCandles(TwelveDataResponse? response, string symbol)
    {
        if (response?.Values == null)
            return new List<Candle>();

        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };
        var candles = new List<Candle>();

        foreach (var item in response.Values)
        {
            if (!DateTime.TryParseExact(
                    item.DateTime, formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var time))
            {
                continue;
            }

            candles.Add(new Candle
            {
                Time = time,
                Symbol = symbol,
                Open = (decimal)item.Open,
                High = (decimal)item.High,
                Low = (decimal)item.Low,
                Close = (decimal)item.Close
            });
        }

        return candles.OrderBy(x => x.Time).ToList();
    }

    private static ForexTradeSignal Wait(string symbol, string reason)
    {
        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            Bias = "NEUTRAL",
            Confidence = 0,
            Grade = "NO_TRADE",
            Message = $"{symbol} Cassandra WAIT",
            BiasNote = reason,
            Reasons = new List<string> { reason },
            StrategyResults = new List<ForexStrategyResult>
            {
                new ForexStrategyResult
                {
                    StrategyName = "CASSANDRA_XAU_M15",
                    Direction = "FILTER",
                    Score = 0,
                    MaxScore = 100,
                    IsConfirmed = false,
                    Reasons = new List<string> { reason }
                }
            },
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static int GetDigits(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        if (s.Contains("JPY")) return 3;
        if (s.Contains("XAU")) return 2;
        if (s.Contains("BTC") || s.Contains("ETH")) return 2;
        if (s.Contains("USOIL")) return 2;
        return 5;   // standart forex (EUR/USD, GBP/USD və s.)
    }

    private static decimal RoundPrice(string symbol, decimal price)
        => Math.Round(price, GetDigits(symbol));

    private static string FormatPrice(string symbol, decimal price)
    {
        var digits = GetDigits(symbol);
        var fmt = "0." + new string('0', digits);
        return price.ToString(fmt, CultureInfo.InvariantCulture);
    }

    private sealed class SwingPoint
    {
        public int Index { get; set; }
        public decimal Price { get; set; }
        public string Kind { get; set; } = string.Empty;
    }

    private sealed class LevelCluster
    {
        public decimal Price { get; set; }
        public int Touches { get; set; }
    }
}
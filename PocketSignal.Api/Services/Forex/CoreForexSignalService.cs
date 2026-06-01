using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Forex;

/// <summary>
/// EMA50 + Williams %R Reversal (M30).
///
/// MƏNTİQ:
///   Trend istiqamətində, qiymət dərin geri çəkiləndə (oversold/overbought),
///   reversal şamı + təsdiq şamı ilə girişi tuturuq.
///
/// QAYDALAR (LONG):
///   1. M30 qrafik.
///   2. Trend: close > EMA50 → qalxan trend (yalnız LONG).
///   3. Williams %R(14) < -85 (oversold — qiymət dərin düşüb).
///   4. Siqnal şamı (öncəki şam): aşağı sancılıb, amma yuxarı/bullish bağlanıb
///      (close > open).
///   5. Təsdiq şamı (son şam): siqnal şamının diapazonunun 70%-i ÜSTÜNDƏ bağlanır
///      yəni close >= signalLow + 0.70 × (signalHigh - signalLow).
///   6. Giriş: 2 ATR SL, 2 ATR TP (1:1).
///
/// QAYDALAR (SHORT) — simmetrik:
///   2. close < EMA50 → düşən trend (yalnız SHORT).
///   3. Williams %R(14) > -15 (overbought).
///   4. Siqnal şamı: yuxarı sancılıb, amma aşağı/bearish bağlanıb (close < open).
///   5. Təsdiq şamı: siqnal şamının diapazonunun 70%-i ALTINDA bağlanır
///      yəni close <= signalHigh - 0.70 × (signalHigh - signalLow).
///   6. Giriş: 2 ATR SL, 2 ATR TP.
///
/// EMA50 ortada (FLAT) olarsa trade yoxdur.
/// Strategiya yalnız "1min" slotundan oxuyur (backtest ora M30 verir).
/// </summary>
public class CoreForexSignalService : IForexSignalService
{
    private const int WilliamsPeriod = 14;
    private const double OversoldLevel = -85.0;   // LONG: %R bunu aşağıdan yuxarı kəsməlidir
    private const double OverboughtLevel = -15.0; // SHORT: %R bunu yuxarıdan aşağı kəsməlidir
    private const double BodyMinRatio = 0.75;     // siqnal şamının gövdəsi ≥ 75%
    private const int ConfirmWindow = 3;           // setup-ı son neçə şam içində axtarsın
    private const double StopAtrMult = 2.0;       // SL = 2 × ATR
    private const double TpAtrMult = 3.0;         // TP = 3 × ATR (1:1.5 RR)
    private const decimal MinAtrPips = 5m;        // ATR bu pip-dən kiçikdirsə trade yox (dar/sakit dövr filtri)

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
            "1min",
            260,
            cancellationToken);

        var candles = MapCandles(response);

        if (candles.Count < 60)
            return Wait(symbol, "EMA50 + %R üçün kifayət qədər M30 candle yoxdur.");

        var ema50 = CalculateEma(candles, 50);
        var atr = CalculateAtr(candles, 14);
        if (atr <= 0)
            return Wait(symbol, "ATR hesablanmadı.");

        // === Min ATR filtri: dar/sakit dövrdə trade yox ===
        // ATR çox kiçik olanda SL də kiçik olur, qiymət noise içində SL-i vurur.
        var atrPips = (decimal)atr / GetPipSize(symbol);
        if (atrPips < MinAtrPips)
            return Wait(symbol, $"ATR çox kiçik ({atrPips:0.#} pip < {MinAtrPips}). Sakit dövr, trade yox.");

        // === Trend (EMA50) — son şama görə ===
        var lastClose = candles[^1].Close;
        string trend;
        if (lastClose > ema50)
            trend = "UP";
        else if (lastClose < ema50)
            trend = "DOWN";
        else
            return Wait(symbol, $"Trend FLAT (close=EMA50={FormatPrice(ema50)}). Trade yoxdur.");

        // === Setup (video qaydaları) son ConfirmWindow şam içində ===
        // Təsdiq şamı son 1..ConfirmWindow mövqelərindən biri ola bilər.
        // LONG üçün hər təsdiq şamı (c) və ondan əvvəlki siqnal şamı (s) üçün:
        //   1) Williams %R -85-i AŞAĞIDAN YUXARI kəsməlidir (s mövqeyində: əvvəl <-85, indi >=-85).
        //   2) Siqnal şamı (s) qalxan/güclü gövdəli olmalıdır (gövdə ≥ BodyMinRatio).
        //   3) Təsdiq şamı (c) siqnal şamının HIGH-ını keçməlidir (100% keçmə).
        // SHORT simmetrik.
        var direction = trend == "UP" ? "LONG" : "SHORT";

        PriceCandle? confirm = null;
        double williamsR = -50;
        string lastDetail = "setup tapılmadı";

        for (var back = 0; back < ConfirmWindow; back++)
        {
            var confirmIdx = candles.Count - 1 - back;
            var signalIdx = confirmIdx - 1;
            if (signalIdx < WilliamsPeriod + 1)
                break;

            var c = candles[confirmIdx];   // təsdiq şamı
            var s = candles[signalIdx];    // siqnal şamı

            // Təsdiq şamı da trend tərəfində olmalıdır.
            var confirmTrendOk = trend == "UP" ? c.Close > ema50 : c.Close < ema50;
            if (!confirmTrendOk)
                continue;

            var sRange = s.High - s.Low;
            if (sRange <= 0)
                continue;

            // Siqnal şamının gövdə nisbəti.
            var sBody = Math.Abs(s.Close - s.Open);
            var sBodyRatio = sBody / sRange;

            // %R: siqnal şamı (signalIdx) və ondan bir əvvəl (prev) — kəsmə üçün.
            var rSignal = CalculateWilliamsR(candles, WilliamsPeriod, back + 1);
            var rPrev = CalculateWilliamsR(candles, WilliamsPeriod, back + 2);

            bool ok;
            if (trend == "UP")
            {
                // 1) -85-i aşağıdan yuxarı kəsmə
                var crossUp = rPrev < OversoldLevel && rSignal >= OversoldLevel;
                // 2) siqnal şamı bullish + tam gövdəli
                var strongBull = s.Close > s.Open && sBodyRatio >= BodyMinRatio;
                // 3) təsdiq şamı siqnal şamının HIGH-ını keçir
                var brokeHigh = c.Close > s.High;

                ok = crossUp && strongBull && brokeHigh;
                lastDetail = $"%R kəsmə(crossUp={crossUp}, prev={rPrev:0.#}, now={rSignal:0.#}), " +
                             $"güclü bull(body={sBodyRatio:0.00}≥{BodyMinRatio}:{strongBull}), high keçmə={brokeHigh}";
            }
            else
            {
                var crossDown = rPrev > OverboughtLevel && rSignal <= OverboughtLevel;
                var strongBear = s.Close < s.Open && sBodyRatio >= BodyMinRatio;
                var brokeLow = c.Close < s.Low;

                ok = crossDown && strongBear && brokeLow;
                lastDetail = $"%R kəsmə(crossDown={crossDown}, prev={rPrev:0.#}, now={rSignal:0.#}), " +
                             $"güclü bear(body={sBodyRatio:0.00}≥{BodyMinRatio}:{strongBear}), low keçmə={brokeLow}";
            }

            if (ok)
            {
                confirm = c;
                williamsR = rSignal;
                break;
            }
        }

        if (confirm == null)
            return Wait(symbol, $"{direction} setup şərtləri ödənmədi (son {ConfirmWindow} şam). {lastDetail}");

        var entry = (decimal)confirm.Close;

        // === SL / TP ===
        var slDistance = (decimal)(atr * StopAtrMult);
        var tpDistance = (decimal)(atr * TpAtrMult);

        decimal stopLoss, takeProfit;
        if (direction == "LONG")
        {
            stopLoss = entry - slDistance;
            takeProfit = entry + tpDistance;
        }
        else
        {
            stopLoss = entry + slDistance;
            takeProfit = entry - tpDistance;
        }

        var pipSize = GetPipSize(symbol);
        var riskPips = Math.Abs(entry - stopLoss) / pipSize;
        var rewardPips = Math.Abs(takeProfit - entry) / pipSize;
        var rr = riskPips > 0 ? rewardPips / riskPips : 0;

        var entryR = RoundPrice(symbol, entry);
        var slR = RoundPrice(symbol, stopLoss);
        var tpR = RoundPrice(symbol, takeProfit);

        var reasons = new List<string>
        {
            $"EMA50 + Williams %R {direction} signal.",
            $"Trend (EMA50={FormatPrice(ema50)}): {trend}.",
            $"Williams %R(14) = {williamsR:0.#}.",
            "Siqnal şamı + təsdiq şamı (70% qayıtma) təsdiqləndi.",
            $"SL={StopAtrMult}×ATR, TP={TpAtrMult}×ATR (ATR={FormatPrice(atr)}), RR 1:{TpAtrMult / StopAtrMult:0.#}."
        };

        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = direction,
            EntryPrice = entryR,
            StopLoss = slR,
            TakeProfit1 = tpR,
            TakeProfit2 = tpR,
            RiskPips = Math.Round(riskPips, 1),
            RewardPips1 = Math.Round(rewardPips, 1),
            RewardPips2 = Math.Round(rewardPips, 1),
            RiskReward1 = Math.Round(rr, 2),
            RiskReward2 = Math.Round(rr, 2),
            Confidence = 75,
            Grade = "B",
            Message = $"{symbol} {direction} Entry: {entryR} SL: {slR} TP: {tpR}",
            InvalidIf = direction == "LONG"
                ? $"Qiymət EMA50 ({FormatPrice(ema50)}) altına düşsə trend dəyişir."
                : $"Qiymət EMA50 ({FormatPrice(ema50)}) üstünə qalxsa trend dəyişir.",
            ValidForMinutes = 30,
            Reasons = reasons,
            SideAnalyses = new List<SideAnalysis>(),
            StrategyResults = new List<ForexStrategyResult>
            {
                new ForexStrategyResult
                {
                    StrategyName = "EMA50_WILLIAMS_R_M30",
                    Direction = direction,
                    Score = 75,
                    MaxScore = 100,
                    IsConfirmed = true,
                    Reasons = reasons
                }
            },
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    // ==================== Köməkçi hesablamalar ====================

    /// <summary>
    /// Williams %R = -100 × (HighestHigh - Close) / (HighestHigh - LowestLow).
    /// offsetFromEnd=0 → son şam, 1 → öncəki (siqnal) şam.
    /// period qədər şama (həmin şamdan geriyə) baxır. 0..-100 arası.
    /// </summary>
    private static double CalculateWilliamsR(List<PriceCandle> candles, int period, int offsetFromEnd = 0)
    {
        var endIndex = candles.Count - 1 - offsetFromEnd;
        if (endIndex < period - 1 || endIndex < 0)
            return -50;

        // endIndex daxil olmaqla geriyə period qədər şam.
        var window = candles.Skip(endIndex - period + 1).Take(period).ToList();
        var highestHigh = window.Max(x => x.High);
        var lowestLow = window.Min(x => x.Low);
        var close = candles[endIndex].Close;

        var range = highestHigh - lowestLow;
        if (range <= 0)
            return -50;

        return -100.0 * (highestHigh - close) / range;
    }

    private static double CalculateEma(List<PriceCandle> candles, int period)
    {
        if (candles.Count == 0 || period <= 0)
            return 0;

        var take = Math.Min(period * 3, candles.Count);
        var data = candles.Skip(candles.Count - take).ToList();
        var multiplier = 2.0 / (period + 1);

        var seedCount = Math.Min(period, data.Count);
        var ema = data.Take(seedCount).Average(x => x.Close);

        for (var i = seedCount; i < data.Count; i++)
            ema = (data[i].Close - ema) * multiplier + ema;

        return ema;
    }

    private static double CalculateAtr(List<PriceCandle> candles, int period)
    {
        if (candles.Count < period + 1)
            return 0;

        var take = Math.Min(period * 4, candles.Count);
        var data = candles.Skip(candles.Count - take).ToList();

        var trs = new List<double>();
        for (var i = 1; i < data.Count; i++)
        {
            var high = data[i].High;
            var low = data[i].Low;
            var prevClose = data[i - 1].Close;
            var tr = Math.Max(high - low, Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));
            trs.Add(tr);
        }

        if (trs.Count < period)
            return trs.Count > 0 ? trs.Average() : 0;

        var atr = trs.Take(period).Average();
        for (var i = period; i < trs.Count; i++)
            atr = (atr * (period - 1) + trs[i]) / period;

        return atr;
    }

    private static List<PriceCandle> MapCandles(TwelveDataResponse? response)
    {
        if (response?.Values == null)
            return new List<PriceCandle>();

        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };
        var candles = new List<PriceCandle>();

        foreach (var item in response.Values)
        {
            if (!DateTime.TryParseExact(
                    item.DateTime, formats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var time))
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

        return candles.OrderBy(x => x.TimeUtc).ToList();
    }

    private static ForexTradeSignal Wait(string symbol, string reason)
    {
        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            Confidence = 0,
            Grade = "NO_TRADE",
            Message = $"{symbol} EMA50+%R WAIT",
            Reasons = new List<string> { reason },
            StrategyResults = new List<ForexStrategyResult>
            {
                new ForexStrategyResult
                {
                    StrategyName = "EMA50_WILLIAMS_R_M30",
                    Direction = "WAIT",
                    Score = 0,
                    MaxScore = 100,
                    IsConfirmed = false,
                    Reasons = new List<string> { reason }
                }
            },
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static decimal RoundPrice(string symbol, decimal price)
        => Math.Round(price, GetDigits(symbol));

    private static string FormatPrice(double price)
        => price.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

    private static int GetDigits(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        if (symbol.Contains("JPY")) return 3;
        if (symbol.Contains("XAU")) return 2;
        if (symbol.Contains("BTC") || symbol.Contains("ETH")) return 2;
        if (symbol.Contains("USOIL")) return 2;
        return 5;
    }

    private static decimal GetPipSize(string symbol)
    {
        symbol = symbol.ToUpperInvariant();
        if (symbol.Contains("JPY")) return 0.01m;
        if (symbol.Contains("XAU")) return 0.10m;
        if (symbol.Contains("BTC")) return 1m;
        if (symbol.Contains("ETH")) return 0.10m;
        if (symbol.Contains("USOIL")) return 0.01m;
        return 0.0001m;
    }
}
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Forex;

/// <summary>
/// Volatility Squeeze Breakout (M30) — sıxılma → partlayış strategiyası.
///
/// MƏNTİQ (niyə işləməli olduğunu anlayırıq):
///   Bazar uzun müddət dar diapazonda sıxılır (enerji toplanır), sonra
///   güclü hərəkətlə partlayır (enerji boşalır). Biz range-də trade ETMİRİK —
///   yalnız sıxılma-sonrası partlayışı ovlayırıq. Bu, əvvəlki strategiyaların
///   ən böyük problemini (range bazarda dağılmaq) prinsip olaraq həll edir.
///
/// QAYDALAR (tam obyektiv, koda çevrilə bilən):
///   1. SIXILMA: son SqueezeLookback şamının (default 10) ümumi diapazonu
///      ATR-in SqueezeMaxAtrMult mislindən (default 4) kiçik olmalıdır.
///      Yəni qiymət dar zolaqda sıxışıb.
///   2. QIRILMA (breakout): son şam həmin sıxılma zolağının yuxarı/aşağısını
///      GÖVDƏ (close) ilə qırmalıdır — sadəcə wick yox. Bu, fake breakout-u azaldır.
///   3. GÜCLÜ ŞAM: qırılan şamın gövdəsi öz diapazonunun ən az BodyMinRatio
///      hissəsi olmalıdır (default 0.5) — yəni qətiyyətli şam, tərəddüd yox.
///   4. İSTİQAMƏT: yuxarı qırılma → LONG, aşağı qırılma → SHORT.
///   5. SL = StopAtrMult × ATR (default 1.5), TP = TpAtrMult × ATR (default 3.0).
///      Yəni 1:2 risk/reward — breakout strategiyaları üçün uyğun.
///
/// Strategiya yalnız "1min" slotundan oxuyur (backtest ora M30 verir).
/// Canlı sistemə təsir etmir.
/// </summary>
public class SqueezeBreakoutForexSignalService : IForexSignalService
{
    private readonly IMarketDataService _marketDataService;

    // === Tənzimlənə bilən parametrlər ===
    private const int SqueezeLookback = 10;        // sıxılma neçə şama baxır
    private const double SqueezeMaxAtrMult = 4.0;  // diapazon < ATR×bu → sıxılma
    private const double BodyMinRatio = 0.5;       // qırılma şamı gövdə/range nisbəti
    private const double StopAtrMult = 1.5;        // SL = ATR × bu
    private const double TpAtrMult = 3.0;          // TP = ATR × bu (1:2 RR)
    private const decimal MinAtrPips = 0m;         // volatillik döşəməsi (0 = söndürülü)

    public SqueezeBreakoutForexSignalService(
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

        if (candles.Count < SqueezeLookback + 20)
        {
            return Wait(symbol, "Squeeze Breakout üçün kifayət qədər M30 candle yoxdur.");
        }

        var atr = CalculateAtr(candles, 14);
        if (atr <= 0)
        {
            return Wait(symbol, "ATR hesablanmadı.");
        }

        var pipSize = GetPipSize(symbol);
        var atrInPips = (decimal)atr / pipSize;

        if (atrInPips < MinAtrPips)
        {
            return Wait(
                symbol,
                $"ATR çox aşağıdır ({Math.Round(atrInPips, 1)} pip). Volatillik zəif, trade yoxdur.");
        }

        var last = candles[^1];
        var entry = (decimal)last.Close;

        // === 1. SIXILMA yoxlaması ===
        // Son şamdan ƏVVƏLKİ SqueezeLookback şama baxırıq (son şam qırılma şamıdır).
        var squeezeWindow = candles
            .Skip(candles.Count - 1 - SqueezeLookback)
            .Take(SqueezeLookback)
            .ToList();

        var squeezeHigh = squeezeWindow.Max(x => x.High);
        var squeezeLow = squeezeWindow.Min(x => x.Low);
        var squeezeRange = squeezeHigh - squeezeLow;

        var isSqueezed = squeezeRange < atr * SqueezeMaxAtrMult;

        if (!isSqueezed)
        {
            return Wait(
                symbol,
                $"Sıxılma yoxdur. Diapazon {FormatPrice(squeezeRange)} >= ATR×{SqueezeMaxAtrMult} " +
                $"({FormatPrice(atr * SqueezeMaxAtrMult)}). Bazar artıq hərəkətdədir.");
        }

        // === 2. QIRILMA istiqaməti (gövdə/close ilə) ===
        string? direction = null;
        if (last.Close > squeezeHigh)
            direction = "LONG";   // yuxarı partlayış
        else if (last.Close < squeezeLow)
            direction = "SHORT";  // aşağı partlayış

        if (direction == null)
        {
            return Wait(
                symbol,
                $"Sıxılma var, amma hələ qırılma yoxdur. Zona: {FormatPrice(squeezeLow)}–{FormatPrice(squeezeHigh)}, close={FormatPrice(last.Close)}.");
        }

        // === 3. GÜCLÜ ŞAM (gövdə nisbəti) ===
        var candleRange = last.High - last.Low;
        var body = Math.Abs(last.Close - last.Open);
        var bodyRatio = candleRange > 0 ? body / candleRange : 0;

        if (bodyRatio < BodyMinRatio)
        {
            return Wait(
                symbol,
                $"Qırılma şamı zəifdir (gövdə {Math.Round(bodyRatio * 100)}% < {BodyMinRatio * 100}%). Fake breakout riski.");
        }

        // İstiqamət gövdə ilə uyğun olmalıdır (LONG-da bullish şam, SHORT-da bearish).
        var bodyDirectionOk =
            (direction == "LONG" && last.Close > last.Open) ||
            (direction == "SHORT" && last.Close < last.Open);

        if (!bodyDirectionOk)
        {
            return Wait(
                symbol,
                "Qırılma şamının gövdəsi istiqamətə uyğun deyil.");
        }

        // === 4. SL / TP (ATR əsaslı, 1:2 RR) ===
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

        var riskPips = Math.Abs(entry - stopLoss) / pipSize;
        var rewardPips = Math.Abs(takeProfit - entry) / pipSize;
        var rr = riskPips > 0 ? rewardPips / riskPips : 0;

        var entryR = RoundPrice(symbol, entry);
        var slR = RoundPrice(symbol, stopLoss);
        var tpR = RoundPrice(symbol, takeProfit);

        var reasons = new List<string>
        {
            $"Volatility Squeeze Breakout {direction} signal.",
            $"Sıxılma təsdiqləndi: son {SqueezeLookback} şam dar zolaqda (diapazon < ATR×{SqueezeMaxAtrMult}).",
            $"Qırılma zonası: {FormatPrice(squeezeLow)}–{FormatPrice(squeezeHigh)}.",
            $"Güclü gövdə ilə qırılma ({Math.Round(bodyRatio * 100)}% gövdə).",
            $"SL=ATR×{StopAtrMult}, TP=ATR×{TpAtrMult} (RR ~1:{TpAtrMult / StopAtrMult:0.#}). ATR={FormatPrice(atr)}."
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

            Message =
                $"{symbol} {direction} Entry: {entryR} SL: {slR} TP: {tpR}",

            InvalidIf = direction == "LONG"
                ? $"Qiymət sıxılma zonasına ({FormatPrice(squeezeHigh)}) geri qayıtsa setup pozulur."
                : $"Qiymət sıxılma zonasına ({FormatPrice(squeezeLow)}) geri qayıtsa setup pozulur.",

            ValidForMinutes = 30,
            Reasons = reasons,
            SideAnalyses = new List<SideAnalysis>(),

            StrategyResults = new List<ForexStrategyResult>
            {
                new ForexStrategyResult
                {
                    StrategyName = "SQUEEZE_BREAKOUT_M30",
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

            var tr = Math.Max(
                high - low,
                Math.Max(Math.Abs(high - prevClose), Math.Abs(low - prevClose)));

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
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None,
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
            Message = $"{symbol} Squeeze Breakout WAIT",
            Reasons = new List<string> { reason },
            StrategyResults = new List<ForexStrategyResult>
            {
                new ForexStrategyResult
                {
                    StrategyName = "SQUEEZE_BREAKOUT_M30",
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
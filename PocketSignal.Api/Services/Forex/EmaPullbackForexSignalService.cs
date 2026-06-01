using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Forex;

/// <summary>
/// EMA Pullback M30 strategiyası — sadə, test edilə bilən.
///
/// Qaydalar:
///   1. M30 (30 dəqiqəlik) qrafik — tək timeframe.
///   2. Trend filtri: EMA50.
///        - Qiymət (son close) EMA50-dən YUXARI → qalxan trend → yalnız LONG.
///        - Qiymət EMA50-dən AŞAĞI → düşən trend → yalnız SHORT.
///   3. Giriş: son şamın wick/gövdəsi EMA20-yə dəyibsə (pullback).
///        - LONG: candle.Low <= EMA20 <= candle.High (toxunma) VƏ trend UP.
///        - SHORT: candle.Low <= EMA20 <= candle.High (toxunma) VƏ trend DOWN.
///   4. SL = 2 × ATR(14), TP = 2 × ATR(14)  → 1:1 risk/reward.
///        - LONG:  SL = entry − 2·ATR, TP = entry + 2·ATR
///        - SHORT: SL = entry + 2·ATR, TP = entry − 2·ATR
///
/// Qeyd: Strategiya yalnız "1min" slotundan data oxuyur. Backtest-də o slota
/// M30 datası verilir (tf=ema rejimi). Canlı sistemə təsir etmir.
/// </summary>
public class EmaPullbackForexSignalService : IForexSignalService
{
    private readonly IMarketDataService _marketDataService;

    // Volatillik filtri: ATR bu pip dəyərindən kiçikdirsə trade yoxdur.
    // Hazırda söndürülüb (0) — yeni giriş şərtini təmiz test etmək üçün.
    // Lazım olsa 3, 4, 5 et.
    private const decimal MinAtrPips = 0m;

    public EmaPullbackForexSignalService(
        IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public async Task<ForexTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        // Tək timeframe oxuyuruq. Backtest "1min" slotuna M30 datası qoyur.
        var response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            260,
            cancellationToken);

        var candles = MapCandles(response);

        // EMA50 + EMA20 + ATR(14) üçün kifayət data lazımdır.
        if (candles.Count < 60)
        {
            return Wait(symbol, "EMA Pullback üçün kifayət qədər M30 candle yoxdur.");
        }

        var ema20 = CalculateEma(candles, 20);
        var ema50 = CalculateEma(candles, 50);
        var atr = CalculateAtr(candles, 14);

        // Əvvəlki şamın EMA20-si (cross/keçid yoxlaması üçün).
        // Son şamı çıxarıb EMA20-ni yenidən hesablayırıq.
        var prevEma20 = CalculateEma(candles.Take(candles.Count - 1).ToList(), 20);

        if (atr <= 0)
        {
            return Wait(symbol, "ATR hesablanmadı.");
        }

        // === V2: Volatillik filtri — yalnız ATR yüksək olanda trade ===
        // ATR-i pip-ə çeviririk. Əgər minimumdan kiçikdirsə (bazar sıxışıb,
        // dar volatillik), signal vermirik. Sakit bazarda 2 ATR SL/TP çox
        // kiçik olur və noise içində SL-ə dəyir.
        var pipSize = GetPipSize(symbol);
        var atrInPips = (decimal)atr / pipSize;

        if (atrInPips < MinAtrPips)
        {
            return Wait(
                symbol,
                $"ATR çox aşağıdır ({Math.Round(atrInPips, 1)} pip < {MinAtrPips} pip). Volatillik zəif, trade yoxdur.");
        }

        var last = candles[^1];
        var entry = (decimal)last.Close;

        // === Trend filtri (EMA50) ===
        string trend;
        if (last.Close > ema50)
            trend = "UP";
        else if (last.Close < ema50)
            trend = "DOWN";
        else
            trend = "FLAT";

        if (trend == "FLAT")
            return Wait(symbol, "Trend FLAT-dır (qiymət EMA50 üzərindədir). Trade yoxdur.");

        // === V2 (B variantı): EMA20 KEÇİDİ (cross) ===
        // LONG:  əvvəlki şam EMA20 altında bağlanmışdı, indiki üstündə bağlandı.
        // SHORT: əvvəlki şam EMA20 üstündə idi, indiki altında bağlandı.
        var prev = candles[^2];
        string direction;
        bool entrySignal;

        if (trend == "UP")
        {
            direction = "LONG";
            entrySignal = prev.Close < prevEma20 && last.Close > ema20;
        }
        else
        {
            direction = "SHORT";
            entrySignal = prev.Close > prevEma20 && last.Close < ema20;
        }

        if (!entrySignal)
        {
            return Wait(
                symbol,
                $"EMA20 keçid (cross) şərti ödənmədi. EMA20={FormatPrice(ema20)}, " +
                $"close={FormatPrice(last.Close)}, prevClose={FormatPrice(prev.Close)}, trend={trend}.");
        }

        // === SL / TP: 2 ATR / 2 ATR ===
        var distance = (decimal)(atr * 2.0);

        decimal stopLoss;
        decimal takeProfit;

        if (direction == "LONG")
        {
            stopLoss = entry - distance;
            takeProfit = entry + distance;
        }
        else
        {
            stopLoss = entry + distance;
            takeProfit = entry - distance;
        }

        var riskPips = Math.Abs(entry - stopLoss) / pipSize;
        var rewardPips = Math.Abs(takeProfit - entry) / pipSize;

        var entryR = RoundPrice(symbol, entry);
        var slR = RoundPrice(symbol, stopLoss);
        var tpR = RoundPrice(symbol, takeProfit);

        var reasons = new List<string>
        {
            $"EMA Pullback M30 {direction} signal.",
            $"Trend (EMA50) {trend}: yalnız {direction} icazəlidir.",
            $"Son şam EMA20-ni kəsdi (cross), trend istiqamətində bağlandı (EMA20={FormatPrice(ema20)}).",
            $"SL/TP = 2×ATR (ATR={FormatPrice(atr)}), risk/reward 1:1."
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
            RiskReward1 = 1m,
            RiskReward2 = 1m,

            Confidence = 75,
            Grade = "B",

            Message =
                $"{symbol} {direction} Entry: {entryR} SL: {slR} TP: {tpR}",

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
                    StrategyName = "EMA_PULLBACK_M30",
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

    private static double CalculateEma(
        List<PriceCandle> candles,
        int period)
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

    private static double CalculateAtr(
        List<PriceCandle> candles,
        int period)
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

        return candles
            .OrderBy(x => x.TimeUtc)
            .ToList();
    }

    private static ForexTradeSignal Wait(
        string symbol,
        string reason)
    {
        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            Confidence = 0,
            Grade = "NO_TRADE",
            Message = $"{symbol} EMA Pullback WAIT",
            Reasons = new List<string> { reason },
            StrategyResults = new List<ForexStrategyResult>
            {
                new ForexStrategyResult
                {
                    StrategyName = "EMA_PULLBACK_M30",
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

    private static decimal RoundPrice(string symbol, double price)
        => Math.Round((decimal)price, GetDigits(symbol));

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
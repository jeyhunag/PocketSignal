using PocketSignal.Api.Models.Analysis;

namespace PocketSignal.Api.Services.Indicators;

/// <summary>
/// Binary Core V4 üçün təkrar istifadə oluna bilən texniki indikator kitabxanası.
/// Bütün metodlar saf (pure) və static-dir — heç bir xarici asılılıq yoxdur.
/// Candle siyahısı KÖHNƏDƏN YENİYƏ doğru sıralanmalıdır (sonuncu = ən yeni).
/// </summary>
public static class TechnicalIndicators
{
    /// <summary>
    /// Exponential Moving Average. Son candle-lara daha çox çəki verir,
    /// ona görə sadə ortalamadan daha tez reaksiya verir.
    /// </summary>
    public static double Ema(IReadOnlyList<PriceCandle> candles, int period)
    {
        if (candles.Count == 0 || period <= 0)
            return 0;

        var take = Math.Min(period * 3, candles.Count);
        var data = candles.Skip(candles.Count - take).ToList();

        var multiplier = 2.0 / (period + 1);

        // İlk EMA dəyəri kimi sadə ortalama götürürük (seeding).
        var seedCount = Math.Min(period, data.Count);
        var ema = data.Take(seedCount).Average(x => x.Close);

        for (var i = seedCount; i < data.Count; i++)
            ema = (data[i].Close - ema) * multiplier + ema;

        return ema;
    }

    /// <summary>
    /// RSI (Relative Strength Index) — Wilder smoothing ilə.
    /// 0–100 arası. 70+ overbought, 30- oversold.
    /// </summary>
    public static double Rsi(IReadOnlyList<PriceCandle> candles, int period = 14)
    {
        if (candles.Count < period + 1)
            return 50;

        var take = Math.Min(period * 4, candles.Count);
        var data = candles.Skip(candles.Count - take).ToList();

        double gain = 0, loss = 0;

        // İlk period üçün ortalama gain/loss.
        for (var i = 1; i <= period; i++)
        {
            var change = data[i].Close - data[i - 1].Close;
            if (change >= 0) gain += change;
            else loss -= change;
        }

        var avgGain = gain / period;
        var avgLoss = loss / period;

        // Wilder smoothing qalan candle-lar üçün.
        for (var i = period + 1; i < data.Count; i++)
        {
            var change = data[i].Close - data[i - 1].Close;
            var up = change > 0 ? change : 0;
            var down = change < 0 ? -change : 0;

            avgGain = (avgGain * (period - 1) + up) / period;
            avgLoss = (avgLoss * (period - 1) + down) / period;
        }

        if (avgLoss == 0)
            return 100;

        var rs = avgGain / avgLoss;
        return 100 - 100 / (1 + rs);
    }

    /// <summary>
    /// ATR (Average True Range) — Wilder smoothing ilə.
    /// Real volatility ölçüsü: gap və wick-ləri də nəzərə alır.
    /// </summary>
    public static double Atr(IReadOnlyList<PriceCandle> candles, int period = 14)
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

    /// <summary>
    /// Linear regression slope (qiymət dəyişmə sürəti).
    /// Normallaşdırılıb: nəticə "candle başına neçə ATR" şəklində.
    /// Müsbət = yüksələn, mənfi = enən. Trendin GÜCÜNÜ ədədlə verir.
    /// </summary>
    public static double NormalizedSlope(IReadOnlyList<PriceCandle> candles, int period, double atr)
    {
        if (candles.Count < period || period < 2 || atr <= 0)
            return 0;

        var data = candles.Skip(candles.Count - period).ToList();
        var n = data.Count;

        double sumX = 0, sumY = 0, sumXy = 0, sumXx = 0;
        for (var i = 0; i < n; i++)
        {
            double x = i;
            var y = data[i].Close;
            sumX += x;
            sumY += y;
            sumXy += x * y;
            sumXx += x * x;
        }

        var denom = n * sumXx - sumX * sumX;
        if (denom == 0)
            return 0;

        var slope = (n * sumXy - sumX * sumY) / denom;
        return slope / atr; // ATR-ə normallaşdırılmış slope
    }

    /// <summary>
    /// Son candle bağlanışının N-candle aralığında harada olduğunu qaytarır.
    /// 0 = aralığın dibində, 1 = zirvəsində. Extended/exhausted move aşkarlamaq üçün.
    /// </summary>
    public static double PricePositionInRange(IReadOnlyList<PriceCandle> candles, int period)
    {
        if (candles.Count < 2)
            return 0.5;

        var data = candles.Skip(Math.Max(0, candles.Count - period)).ToList();
        var high = data.Max(x => x.High);
        var low = data.Min(x => x.Low);

        if (high - low <= 0)
            return 0.5;

        var last = data[^1].Close;
        return (last - low) / (high - low);
    }

    /// <summary>
    /// Bullish/bearish RSI divergence aşkarlama (sadə versiya).
    /// Qiymət yeni extreme edir, RSI etmir → reversal siqnalı.
    /// </summary>
    public static bool HasBullishDivergence(IReadOnlyList<PriceCandle> candles, int period = 14)
    {
        if (candles.Count < period * 2)
            return false;

        var recent = candles.Skip(candles.Count - period).ToList();
        var older = candles.Skip(candles.Count - period * 2).Take(period).ToList();

        var recentLow = recent.Min(x => x.Low);
        var olderLow = older.Min(x => x.Low);

        // Qiymət daha aşağı low edib...
        if (recentLow >= olderLow)
            return false;

        var recentRsi = Rsi(candles, period);
        var olderRsi = Rsi(older, period);

        // ...amma RSI daha yüksəkdir (divergence).
        return recentRsi > olderRsi && recentRsi < 45;
    }

    public static bool HasBearishDivergence(IReadOnlyList<PriceCandle> candles, int period = 14)
    {
        if (candles.Count < period * 2)
            return false;

        var recent = candles.Skip(candles.Count - period).ToList();
        var older = candles.Skip(candles.Count - period * 2).Take(period).ToList();

        var recentHigh = recent.Max(x => x.High);
        var olderHigh = older.Max(x => x.High);

        if (recentHigh <= olderHigh)
            return false;

        var recentRsi = Rsi(candles, period);
        var olderRsi = Rsi(older, period);

        return recentRsi < olderRsi && recentRsi > 55;
    }
}
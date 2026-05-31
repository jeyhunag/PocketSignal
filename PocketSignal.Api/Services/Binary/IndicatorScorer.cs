using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Services.Indicators;

namespace PocketSignal.Api.Services.Binary;

/// <summary>
/// Binary Core V4 — indikator əsaslı scoring qatı.
/// Bu class mövcud CoreBinarySignalService-in trend/scoring hissəsini
/// EMA/RSI/ATR/momentum ilə gücləndirir VƏ daha çox signal çıxması üçün
/// "tiered" (səviyyəli) qərar sistemi təklif edir.
///
/// İSTİFADƏ: AnalyzeDirection içində ScoreTrendContext-dən SONRA çağır:
///   var ind = IndicatorScorer.Score(direction, context.M1TrendCandles, context.M5);
///   analysis.Confidence += ind.Score;
///   analysis.Reasons.AddRange(ind.Reasons);
/// Sonra TradeReady-ni EvaluateTier ilə əvəz et (aşağıdakı izaha bax).
/// </summary>
public static class IndicatorScorer
{
    public sealed class IndicatorResult
    {
        public int Score { get; set; }
        public double Rsi { get; set; }
        public double Atr { get; set; }
        public double Slope { get; set; }
        public double PricePosition { get; set; }
        public bool MomentumAligned { get; set; }
        public bool ExtendedMove { get; set; }
        public bool HasDivergence { get; set; }
        public List<string> Reasons { get; set; } = new();
    }

    /// <summary>
    /// İstiqamət (LONG/SHORT) üçün indikator skoru hesablayır.
    /// M1 candle-lar əsas, M5 candle-lar daha geniş kontekst üçün.
    /// </summary>
    public static IndicatorResult Score(
        string direction,
        IReadOnlyList<PriceCandle> m1,
        IReadOnlyList<PriceCandle> m5)
    {
        var result = new IndicatorResult();

        if (m1.Count < 30)
        {
            result.Reasons.Add("İndikatorlar üçün kifayət qədər candle yoxdur.");
            return result;
        }

        var emaFast = TechnicalIndicators.Ema(m1, 9);
        var emaMid = TechnicalIndicators.Ema(m1, 21);
        var emaSlow = TechnicalIndicators.Ema(m1, 50);

        var rsi = TechnicalIndicators.Rsi(m1, 14);
        var atr = TechnicalIndicators.Atr(m1, 14);
        var slope = TechnicalIndicators.NormalizedSlope(m1, 20, atr);
        var pricePos = TechnicalIndicators.PricePositionInRange(m1, 40);

        result.Rsi = rsi;
        result.Atr = atr;
        result.Slope = slope;
        result.PricePosition = pricePos;

        var isLong = direction == "LONG";

        // --- 1. EMA düzülüşü (alignment): trendin sağlamlığı (maks ~16) ---
        if (isLong)
        {
            if (emaFast > emaMid && emaMid > emaSlow)
            {
                result.Score += 16;
                result.Reasons.Add("EMA 9>21>50 — güclü yüksələn düzülüş.");
            }
            else if (emaFast > emaMid)
            {
                result.Score += 8;
                result.Reasons.Add("EMA 9>21 — qısamüddətli yüksəlmə.");
            }
            else
            {
                result.Reasons.Add("EMA düzülüşü LONG üçün zəifdir.");
            }
        }
        else
        {
            if (emaFast < emaMid && emaMid < emaSlow)
            {
                result.Score += 16;
                result.Reasons.Add("EMA 9<21<50 — güclü enən düzülüş.");
            }
            else if (emaFast < emaMid)
            {
                result.Score += 8;
                result.Reasons.Add("EMA 9<21 — qısamüddətli enmə.");
            }
            else
            {
                result.Reasons.Add("EMA düzülüşü SHORT üçün zəifdir.");
            }
        }

        // --- 2. Momentum slope: trendin gücü (maks ~12) ---
        // 0.15 ATR/candle = ciddi momentum hesab edilir.
        var slopeStrength = Math.Min(Math.Abs(slope) / 0.15, 1.0);
        var momentumOk = isLong ? slope > 0.03 : slope < -0.03;
        result.MomentumAligned = momentumOk;

        if (momentumOk)
        {
            var add = (int)Math.Round(12 * slopeStrength);
            result.Score += add;
            result.Reasons.Add($"Momentum {direction} istiqamətindədir (slope güc {slopeStrength:P0}).");
        }
        else
        {
            result.Score -= 6;
            result.Reasons.Add("Momentum istiqaməti dəstəkləmir.");
        }

        // --- 3. RSI: overbought/oversold + təsdiq (maks ~10) ---
        if (isLong)
        {
            if (rsi is > 50 and < 70)
            {
                result.Score += 10;
                result.Reasons.Add($"RSI {rsi:F0} — sağlam yüksəliş zonası.");
            }
            else if (rsi >= 70)
            {
                result.Score -= 8;
                result.ExtendedMove = true;
                result.Reasons.Add($"RSI {rsi:F0} — overbought, geri çəkilmə riski.");
            }
            else if (rsi is >= 40 and <= 50)
            {
                result.Score += 4;
                result.Reasons.Add($"RSI {rsi:F0} — neytral, dönüş gözlənilir.");
            }
            else
            {
                result.Reasons.Add($"RSI {rsi:F0} — LONG üçün zəif.");
            }
        }
        else
        {
            if (rsi is < 50 and > 30)
            {
                result.Score += 10;
                result.Reasons.Add($"RSI {rsi:F0} — sağlam enmə zonası.");
            }
            else if (rsi <= 30)
            {
                result.Score -= 8;
                result.ExtendedMove = true;
                result.Reasons.Add($"RSI {rsi:F0} — oversold, geri sıçrayış riski.");
            }
            else if (rsi is >= 50 and <= 60)
            {
                result.Score += 4;
                result.Reasons.Add($"RSI {rsi:F0} — neytral, dönüş gözlənilir.");
            }
            else
            {
                result.Reasons.Add($"RSI {rsi:F0} — SHORT üçün zəif.");
            }
        }

        // --- 4. Extended move filtri: qiymət aralığın ucundadırsa cəza ---
        if (isLong && pricePos > 0.9)
        {
            result.Score -= 6;
            result.ExtendedMove = true;
            result.Reasons.Add("Qiymət aralığın zirvəsində — gec entry riski.");
        }
        else if (!isLong && pricePos < 0.1)
        {
            result.Score -= 6;
            result.ExtendedMove = true;
            result.Reasons.Add("Qiymət aralığın dibində — gec entry riski.");
        }

        // --- 5. Divergence: reversal setup-ları üçün bonus ---
        if (isLong && TechnicalIndicators.HasBullishDivergence(m1))
        {
            result.Score += 6;
            result.HasDivergence = true;
            result.Reasons.Add("Bullish RSI divergence aşkarlandı.");
        }
        else if (!isLong && TechnicalIndicators.HasBearishDivergence(m1))
        {
            result.Score += 6;
            result.HasDivergence = true;
            result.Reasons.Add("Bearish RSI divergence aşkarlandı.");
        }

        // --- 6. M5 ilə uyğunluq (daha böyük şəkil) ---
        if (m5.Count >= 30)
        {
            var m5Slope = TechnicalIndicators.NormalizedSlope(
                m5, 20, TechnicalIndicators.Atr(m5, 14));
            var m5Aligned = isLong ? m5Slope > 0 : m5Slope < 0;

            if (m5Aligned)
            {
                result.Score += 6;
                result.Reasons.Add("M5 momentum eyni istiqamətdədir.");
            }
        }

        result.Score = Math.Clamp(result.Score, -20, 50);
        return result;
    }

    /// <summary>
    /// SƏVİYYƏLİ (tiered) qərar — bu, gündə daha çox signal çıxması üçün açardır.
    /// Köhnə sistem "5 şərtin HAMISI" tələb edirdi. Bu, 3 səviyyə verir:
    ///   A_PLUS: bütün təsdiqlər var (ən güclü)
    ///   A:      əsas təsdiqlər + indikator dəstəyi
    ///   B:      ya model, ya güclü indikator dəstəyi (daha çox signal)
    ///   NONE:   heç biri
    /// </summary>
    public static (bool TradeReady, string Tier, string Reason) EvaluateTier(
        bool hasModelConfirmation,
        bool hasPriceAction,
        bool directionConflict,
        bool isChoppy,
        int confidence,
        IndicatorResult indicators,
        int minConfidence)
    {
        // Choppy bazarda heç vaxt trade etmirik (təhlükəsizlik).
        if (isChoppy)
            return (false, "NONE", "Bazar choppy-dir, trade yoxdur.");

        // Direction konflikti kritik problemdir.
        if (directionConflict)
            return (false, "NONE", "Direction konflikti var.");

        var indStrong = indicators.Score >= 30 && indicators.MomentumAligned && !indicators.ExtendedMove;
        var indDecent = indicators.Score >= 18 && indicators.MomentumAligned;

        // A+ : klassik tam təsdiq + güclü indikatorlar
        if (hasModelConfirmation && hasPriceAction && indStrong && confidence >= minConfidence + 8)
            return (true, "A_PLUS", "Tam təsdiq: model + price action + güclü indikatorlar.");

        // A : model + (price action VƏ YA güclü indikator)
        if (hasModelConfirmation && (hasPriceAction || indStrong) && confidence >= minConfidence)
            return (true, "A", "Model təsdiqi + əlavə dəstək.");

        // B : price action + güclü indikator (model olmasa belə) — DAHA ÇOX SIGNAL
        if (hasPriceAction && indStrong && confidence >= minConfidence)
            return (true, "B", "Price action + güclü indikator dəstəyi.");

        // B : çox güclü indikator stack (momentum + EMA + RSI hamısı uyğun)
        if (indStrong && indDecent && confidence >= minConfidence + 3)
            return (true, "B", "İndikatorlar çox güclüdür (model olmadan).");

        return (false, "NONE", "Setup kifayət qədər güclü deyil.");
    }
}
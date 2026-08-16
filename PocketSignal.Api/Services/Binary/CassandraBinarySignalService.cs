using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Services.Forex;

namespace PocketSignal.Api.Services.Binary;

/// <summary>
/// CASSANDRA binary sistemi.
/// Forex Cassandra məntiqinin (bias + zonalar + şah + tərs zona + dondurma) eynisini
/// istifadə edir, nəticəni binary modelinə (SmartTradeSignal) çevirir.
///
/// Köhnə binary (Gemini, SmartMoney) əvəzinə bu işləyir.
/// Timeframe (1min/5min/15min) admin paneldən seçilir.
/// </summary>
public class CassandraBinarySignalService : ISmartSignalService
{
    private readonly CoreForexSignalService _cassandra;

    public CassandraBinarySignalService(
        CoreForexSignalService cassandra)
    {
        _cassandra = cassandra;
    }

    public async Task<SmartTradeSignal> AnalyzeAsync(
        string symbol,
        string timeframe = "15min",
        CancellationToken cancellationToken = default)
    {
        // Forex Cassandra məntiqini işə sal (bias/zona/şah/tərs zona + dondurma daxil).
        var fx = await _cassandra.AnalyzeAsync(symbol, timeframe, cancellationToken);

        // Bias → Direction. NEUTRAL/WAIT olsa binary WAIT.
        var direction = fx.Bias == "BUY" ? "LONG"
            : fx.Bias == "SELL" ? "SHORT"
            : "WAIT";

        // Expiry (binary üçün) timeframe-ə görə.
        var expiryMinutes = timeframe switch
        {
            "1min" => 5,
            "5min" => 15,
            _ => 30
        };

        return new SmartTradeSignal
        {
            Symbol = fx.Symbol,
            Direction = direction,
            ExpiryMinutes = direction == "WAIT" ? 0 : expiryMinutes,
            ExpiryReason = $"Cassandra {timeframe} bias əsaslı.",
            Confidence = fx.Confidence,
            Grade = fx.Grade,
            Message = fx.Message,
            EntryType = direction == "WAIT" ? "WAIT" : "ZONE",
            ValidForSeconds = expiryMinutes * 60,
            LastClose = fx.LastPrice,
            InvalidIf = fx.Bias == "BUY"
                ? "Qərar nöqtəsi (şah) aşağı qırılsa bias dəyişir."
                : "Qərar nöqtəsi (şah) yuxarı qırılsa bias dəyişir.",
            Reasons = BuildReasons(fx),
            CreatedAtUtc = DateTime.UtcNow,
            SideAnalyses = new List<Models.Common.SideAnalysis>(),

            // Cassandra sahələri (şəkil üçün).
            Bias = fx.Bias,
            SellZones = fx.SellZones,
            BuyZones = fx.BuyZones,
            DecisionPoint = fx.DecisionPoint,
            CounterZone = fx.CounterZone,
            NearestZone = fx.NearestZone,
            LastPrice = fx.LastPrice,
            BiasNote = fx.BiasNote,
            Timeframe = fx.Timeframe
        };
    }

    private static List<string> BuildReasons(Models.Forex.ForexTradeSignal fx)
    {
        // Cassandra note-u (bias, zonalar, şah, tərs zona) reasons kimi ötürülür.
        var reasons = new List<string>();

        if (!string.IsNullOrWhiteSpace(fx.BiasNote))
        {
            foreach (var line in fx.BiasNote.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(line))
                    reasons.Add(line.Trim());
            }
        }
        else if (fx.Reasons != null)
        {
            reasons.AddRange(fx.Reasons);
        }

        return reasons;
    }
}
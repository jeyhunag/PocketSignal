using PocketSignal.Api.Models.Common;

namespace PocketSignal.Api.Models.Forex;

public class ForexTradeSignal
{
    public string Symbol { get; set; } = string.Empty;

    // LONG, SHORT, WAIT
    public string Direction { get; set; } = "WAIT";

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

    public string Grade { get; set; } = "NO_TRADE";

    public string Message { get; set; } = string.Empty;

    public string InvalidIf { get; set; } = string.Empty;

    public int ValidForMinutes { get; set; }

    public List<string> Reasons { get; set; } = new();

    public List<SideAnalysis> SideAnalyses { get; set; } = new();

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public List<ForexStrategyResult> StrategyResults { get; set; } = new();

    // ==================== CASSANDRA sahələri ====================
    // Bias: SELL / BUY / NEUTRAL (bazarın ümumi istiqaməti).
    public string Bias { get; set; } = "NEUTRAL";

    // SELL zonaları (resistance — qiymət ora qalxanda satış gözlənilir).
    public List<decimal> SellZones { get; set; } = new();

    // BUY zonaları (support — qiymət ora düşəndə alış gözlənilir).
    public List<decimal> BuyZones { get; set; } = new();

    // Qərar nöqtəsi: bu səviyyə qırılsa bias dəyişir.
    public decimal DecisionPoint { get; set; }

    // Hazırkı qiymətə ən yaxın zona.
    public decimal NearestZone { get; set; }

    // Hazırkı (son) qiymət.
    public decimal LastPrice { get; set; }

    // Cassandra formatlı analiz mətni (şəkil altında göndərilir).
    public string BiasNote { get; set; } = string.Empty;

    // Biasa TƏRS zona (əks istiqamətdə ən güclü səviyyə — güclü tepki gözlənilir).
    // BUY bias-da yuxarıdakı güclü resistance, SELL bias-da aşağıdakı güclü support.
    // Yalnız həqiqətən güclü tərs zona varsa >0 olur, yoxdursa 0.
    public decimal CounterZone { get; set; }

    // Analiz timeframe-i ("1min", "5min", "15min") — şəkil eyni TF ilə çəkilsin.
    public string Timeframe { get; set; } = "15min";

    // Zona gücü: hər zona qiyməti → neçə dəfə toxunulub (touch count).
    // Güclü zona = çox toxunulub = daha etibarlı giriş.
    public Dictionary<string, int> ZoneStrengths { get; set; } = new();

    // Hər zona üçün RR planı (Entry, SL, TP1 1:1, TP2 1:2).
    // Treyder hansı zonadan trade açmağın təhlükəsiz olduğunu görsün.
    public List<ZoneTradePlan> TradePlans { get; set; } = new();
}

/// <summary>
/// Bir zona üçün əməliyyat planı: giriş, stop, take-profit, risk/reward.
/// </summary>
public class ZoneTradePlan
{
    public decimal Zone { get; set; }          // Giriş (zona qiyməti)
    public string Strength { get; set; } = "";  // GÜCLÜ / orta / zəif
    public decimal StopLoss { get; set; }       // Stop (zona qırılma təsdiqi)
    public decimal TakeProfit1 { get; set; }    // 1:1
    public decimal TakeProfit2 { get; set; }    // 1:2
    public decimal RiskDistance { get; set; }   // Entry - SL (mütləq)
    public decimal RiskReward { get; set; }      // RR nisbəti (reward / risk)
}
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
}
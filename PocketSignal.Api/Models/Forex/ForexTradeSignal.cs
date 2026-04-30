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
}
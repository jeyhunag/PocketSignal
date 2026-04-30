namespace PocketSignal.Api.Data.Entities;

public class ForexSignalEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Symbol { get; set; } = string.Empty;

    // LONG, SHORT, WAIT
    public string Direction { get; set; } = "WAIT";

    public bool IsTradable { get; set; }

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

    public string ReasonsJson { get; set; } = "[]";

    public string StrategyBreakdownJson { get; set; } = "[]";

    // PENDING, WIN, WIN_TP2, LOSS, AMBIGUOUS, EXPIRED, WAIT
    public string Status { get; set; } = "WAIT";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime? UpdatedAtUtc { get; set; }

    public List<ForexStrategyScoreEntity> StrategyScores { get; set; } = new();

    public ForexTradeResultEntity? TradeResult { get; set; }
}
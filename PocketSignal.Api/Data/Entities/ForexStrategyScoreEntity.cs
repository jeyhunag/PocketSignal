namespace PocketSignal.Api.Data.Entities;

public class ForexStrategyScoreEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ForexSignalId { get; set; }

    public string StrategyName { get; set; } = string.Empty;

    // LONG, SHORT, WAIT, FILTER
    public string Direction { get; set; } = "WAIT";

    public int Score { get; set; }

    public int MaxScore { get; set; }

    public bool IsConfirmed { get; set; }

    public string ReasonsJson { get; set; } = "[]";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public ForexSignalEntity? ForexSignal { get; set; }
}
namespace PocketSignal.Api.Models.Binary;

public class BinaryContextFilterResult
{
    public bool IsAllowed { get; set; } = true;

    public int ScorePenalty { get; set; }

    public string Decision { get; set; } = "ALLOW";

    public string RiskLevel { get; set; } = "NORMAL";

    public List<string> Reasons { get; set; } = new();

    public static BinaryContextFilterResult Allow(string reason)
    {
        return new BinaryContextFilterResult
        {
            IsAllowed = true,
            Decision = "ALLOW",
            RiskLevel = "NORMAL",
            ScorePenalty = 0,
            Reasons = new List<string> { reason }
        };
    }

    public static BinaryContextFilterResult Block(string reason, string riskLevel = "HIGH")
    {
        return new BinaryContextFilterResult
        {
            IsAllowed = false,
            Decision = "WAIT",
            RiskLevel = riskLevel,
            ScorePenalty = 100,
            Reasons = new List<string> { reason }
        };
    }
}
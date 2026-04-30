namespace PocketSignal.Api.Models;

public class ForexStrategyResult
{
    public string StrategyName { get; set; } = string.Empty;

    // LONG, SHORT, FILTER
    public string Direction { get; set; } = "FILTER";

    public int Score { get; set; }

    public int MaxScore { get; set; }

    public bool IsConfirmed { get; set; }

    public List<string> Reasons { get; set; } = new();
}
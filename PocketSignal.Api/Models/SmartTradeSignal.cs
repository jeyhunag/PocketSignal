namespace PocketSignal.Api.Models;

public class SmartTradeSignal
{
    public string Symbol { get; set; } = string.Empty;

    // LONG, SHORT, WAIT
    public string Direction { get; set; } = "WAIT";

    public int ExpiryMinutes { get; set; }
    public string ExpiryReason { get; set; } = string.Empty;

    public int Confidence { get; set; }

    public string Grade { get; set; } = "NO_TRADE";

    public string Message { get; set; } = string.Empty;

    public string EntryType { get; set; } = "WAIT";

    public int ValidForSeconds { get; set; }

    public decimal LastClose { get; set; }

    public string InvalidIf { get; set; } = string.Empty;

    public List<string> Reasons { get; set; } = new();

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public List<SideAnalysis> SideAnalyses { get; set; } = new();
}
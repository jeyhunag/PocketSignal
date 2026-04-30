namespace PocketSignal.Api.Models.Binary;

public class SignalTradeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Symbol { get; set; } = string.Empty;

    public string Direction { get; set; } = string.Empty; // LONG, SHORT

    public int ExpiryMinutes { get; set; }

    public int Confidence { get; set; }

    public string Grade { get; set; } = string.Empty;

    public decimal EntryPrice { get; set; }

    public decimal? ExitPrice { get; set; }

    public decimal? Difference { get; set; }

    public string Result { get; set; } = "PENDING"; // PENDING, WIN, LOSS, DRAW

    public string SignalMessage { get; set; } = string.Empty;

    public string ExpiryReason { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime DueAtUtc { get; set; }

    public DateTime? CheckedAtUtc { get; set; }
}
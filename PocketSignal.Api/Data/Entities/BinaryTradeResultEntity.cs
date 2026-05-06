namespace PocketSignal.Api.Data.Entities;

public class BinaryTradeResultEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Symbol { get; set; } = string.Empty;

    public string Direction { get; set; } = string.Empty;

    public int ExpiryMinutes { get; set; }

    public int Confidence { get; set; }

    public string Grade { get; set; } = string.Empty;

    public decimal EntryPrice { get; set; }

    public decimal? ExitPrice { get; set; }

    public decimal? Difference { get; set; }

    public string Result { get; set; } = "PENDING";

    public string SignalMessage { get; set; } = string.Empty;

    public string ExpiryReason { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime DueAtUtc { get; set; }

    public DateTime? CheckedAtUtc { get; set; }

    public DateTime? ResultNotifiedAtUtc { get; set; }

    public string ResultNotificationMessage { get; set; } = string.Empty;
}
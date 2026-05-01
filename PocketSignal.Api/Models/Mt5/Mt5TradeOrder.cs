namespace PocketSignal.Api.Models.Mt5;

public class Mt5TradeOrder
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Symbol { get; set; } = string.Empty;
    public string Direction { get; set; } = string.Empty;

    public decimal EntryPrice { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit1 { get; set; }
    public decimal TakeProfit2 { get; set; }

    public int Confidence { get; set; }
    public string Grade { get; set; } = string.Empty;

    public string Status { get; set; } = "PENDING";

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? SentToMt5AtUtc { get; set; }
    public DateTime? ExecutedAtUtc { get; set; }

    public string? Mt5Ticket { get; set; }
    public string? Error { get; set; }
}
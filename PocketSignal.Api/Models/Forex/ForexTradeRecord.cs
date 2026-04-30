namespace PocketSignal.Api.Models;

public class ForexTradeRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Symbol { get; set; } = string.Empty;

    public string Direction { get; set; } = string.Empty; // LONG, SHORT

    public decimal EntryPrice { get; set; }

    public decimal StopLoss { get; set; }

    public decimal TakeProfit1 { get; set; }

    public decimal TakeProfit2 { get; set; }

    public decimal? ExitPrice { get; set; }

    public decimal? Difference { get; set; }

    public int Confidence { get; set; }

    public string Grade { get; set; } = string.Empty;

    public string Result { get; set; } = "PENDING";
    // PENDING, WIN, WIN_TP2, LOSS, AMBIGUOUS, EXPIRED

    public bool IsTp1Hit { get; set; }

    public bool IsTp2Hit { get; set; }

    public bool IsStopLossHit { get; set; }

    public string SignalMessage { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddHours(4);

    public DateTime? CheckedAtUtc { get; set; }

    public DateTime? Tp1HitAtUtc { get; set; }

    public DateTime? Tp2HitAtUtc { get; set; }

    public DateTime? StopLossHitAtUtc { get; set; }
}
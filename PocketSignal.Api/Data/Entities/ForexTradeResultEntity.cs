namespace PocketSignal.Api.Data.Entities;

public class ForexTradeResultEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ForexSignalId { get; set; }

    public string Symbol { get; set; } = string.Empty;

    public string Direction { get; set; } = string.Empty;

    public decimal EntryPrice { get; set; }

    public decimal StopLoss { get; set; }

    public decimal TakeProfit1 { get; set; }

    public decimal TakeProfit2 { get; set; }

    public decimal? ExitPrice { get; set; }

    public decimal? Difference { get; set; }

    // PENDING, WIN, WIN_TP2, LOSS, AMBIGUOUS, EXPIRED
    public string Result { get; set; } = "PENDING";

    public bool IsTp1Hit { get; set; }

    public bool IsTp2Hit { get; set; }

    public bool IsStopLossHit { get; set; }

    public string Notes { get; set; } = string.Empty;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAtUtc { get; set; } = DateTime.UtcNow.AddHours(4);

    public DateTime? CheckedAtUtc { get; set; }

    public DateTime? Tp1HitAtUtc { get; set; }

    public DateTime? Tp2HitAtUtc { get; set; }

    public DateTime? StopLossHitAtUtc { get; set; }

    public string LastNotifiedResult { get; set; } = string.Empty;

    public DateTime? LastNotifiedAtUtc { get; set; }

    public string LastNotificationError { get; set; } = string.Empty;

    public ForexSignalEntity? ForexSignal { get; set; }
}
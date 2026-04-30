namespace PocketSignal.Api.Models.Stats;

public class DailySignalStats
{
    public string Date { get; set; } = DateTime.Now.ToString("yyyy-MM-dd");

    public int TotalChecks { get; set; }

    public int WaitCount { get; set; }

    public int SignalCount { get; set; }

    public int LongSignalCount { get; set; }

    public int ShortSignalCount { get; set; }

    public int TelegramSentCount { get; set; }

    public int TelegramSkippedCount { get; set; }

    public string LastSymbol { get; set; } = string.Empty;

    public string LastDirection { get; set; } = string.Empty;

    public int LastConfidence { get; set; }

    public string LastGrade { get; set; } = string.Empty;

    public string LastSignalMessage { get; set; } = string.Empty;

    public string LastNotificationMessage { get; set; } = string.Empty;

    public DateTime? LastCheckedAtUtc { get; set; }

    public DateTime? LastTelegramSentAtUtc { get; set; }
}
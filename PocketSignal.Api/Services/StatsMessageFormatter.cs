using System.Text;
using PocketSignal.Api.Models;

namespace PocketSignal.Api.Services;

public static class StatsMessageFormatter
{
    public static string Format(DailySignalStats stats)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"Daily Stats - {stats.Date}");
        sb.AppendLine();

        sb.AppendLine($"Total checks: {stats.TotalChecks}");
        sb.AppendLine($"WAIT count: {stats.WaitCount}");
        sb.AppendLine($"Signal count: {stats.SignalCount}");
        sb.AppendLine($"LONG signals: {stats.LongSignalCount}");
        sb.AppendLine($"SHORT signals: {stats.ShortSignalCount}");
        sb.AppendLine();

        sb.AppendLine($"Telegram sent: {stats.TelegramSentCount}");
        sb.AppendLine($"Telegram skipped: {stats.TelegramSkippedCount}");
        sb.AppendLine();

        if (stats.LastCheckedAtUtc != null)
        {
            sb.AppendLine($"Last check UTC: {stats.LastCheckedAtUtc:yyyy-MM-dd HH:mm:ss}");
        }

        if (!string.IsNullOrWhiteSpace(stats.LastSymbol))
        {
            sb.AppendLine($"Last symbol: {stats.LastSymbol}");
            sb.AppendLine($"Last direction: {stats.LastDirection}");
            sb.AppendLine($"Last confidence: {stats.LastConfidence}%");
            sb.AppendLine($"Last grade: {stats.LastGrade}");
            sb.AppendLine($"Last signal message: {stats.LastSignalMessage}");
            sb.AppendLine($"Last notification: {stats.LastNotificationMessage}");
        }

        if (stats.LastTelegramSentAtUtc != null)
        {
            sb.AppendLine($"Last Telegram sent UTC: {stats.LastTelegramSentAtUtc:yyyy-MM-dd HH:mm:ss}");
        }

        return sb.ToString();
    }
}
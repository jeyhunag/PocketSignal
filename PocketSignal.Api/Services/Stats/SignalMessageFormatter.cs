using System.Text;
using PocketSignal.Api.Models.Binary;

namespace PocketSignal.Api.Services.Stats;

public static class SignalMessageFormatter
{
    public static string Format(SmartTradeSignal signal)
    {
        var sb = new StringBuilder();

        if (signal.Direction == "WAIT")
        {
            sb.AppendLine($"{signal.Symbol} WAIT");
            sb.AppendLine();
            sb.AppendLine($"Confidence: {signal.Confidence}%");
            sb.AppendLine($"Grade: {signal.Grade}");
            sb.AppendLine($"Last Close: {signal.LastClose}");

            if (!string.IsNullOrWhiteSpace(signal.ExpiryReason))
            {
                sb.AppendLine($"Expiry Reason: {signal.ExpiryReason}");
            }

            sb.AppendLine();
            sb.AppendLine("Reasons:");

            foreach (var reason in signal.Reasons)
            {
                sb.AppendLine($"- {reason}");
            }

            AppendSideAnalyses(sb, signal);

            sb.AppendLine();
            sb.AppendLine($"Time UTC: {signal.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}");

            return sb.ToString();
        }

        sb.AppendLine($"🚨 {signal.Symbol} {signal.Direction} {signal.ExpiryMinutes} dəqiqəlik aç");
        sb.AppendLine();
        sb.AppendLine($"Confidence: {signal.Confidence}%");
        sb.AppendLine($"Grade: {signal.Grade}");
        sb.AppendLine($"Entry: {signal.EntryType}");
        sb.AppendLine($"Valid: {signal.ValidForSeconds} saniyə");
        sb.AppendLine($"Last Close: {signal.LastClose}");
        if (!string.IsNullOrWhiteSpace(signal.ExpiryReason))
        {
            sb.AppendLine($"Expiry Reason: {signal.ExpiryReason}");
        }
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(signal.InvalidIf))
        {
            sb.AppendLine($"Invalid if: {signal.InvalidIf}");
            sb.AppendLine();
        }

        sb.AppendLine("Reasons:");

        foreach (var reason in signal.Reasons)
        {
            sb.AppendLine($"- {reason}");
        }

        AppendSideAnalyses(sb, signal);

        sb.AppendLine();
        sb.AppendLine($"Time UTC: {signal.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}");

        return sb.ToString();
    }

    private static void AppendSideAnalyses(StringBuilder sb, SmartTradeSignal signal)
    {
        if (signal.SideAnalyses.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("Side Scores:");

        foreach (var side in signal.SideAnalyses)
        {
            sb.AppendLine($"- {side.Direction}: {side.Score}");
        }
    }
}
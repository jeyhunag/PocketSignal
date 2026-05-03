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
            sb.AppendLine($"{signal.Symbol} WAIT {signal.Confidence}%");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(signal.ExpiryReason))
            {
                sb.AppendLine($"Reason: {signal.ExpiryReason}");
                sb.AppendLine();
            }

            if (signal.Reasons.Count > 0)
            {
                sb.AppendLine("Details:");

                foreach (var reason in signal.Reasons.Take(12))
                {
                    sb.AppendLine($"- {reason}");
                }

                sb.AppendLine();
            }

            if (signal.SideAnalyses.Count > 0)
            {
                sb.AppendLine("Side Scores:");

                foreach (var side in signal.SideAnalyses)
                {
                    sb.AppendLine($"- {side.Direction}: {side.Score}");
                }

                sb.AppendLine();
            }

            sb.AppendLine($"Time UTC: {signal.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}");

            return sb.ToString();
        }

        var direction = signal.Direction == "LONG"
            ? "Long"
            : signal.Direction == "SHORT"
                ? "Short"
                : signal.Direction;

        sb.AppendLine($"🚨 {signal.Symbol} {direction} {signal.Confidence}% | {signal.ExpiryMinutes} dəqiqə");
        sb.AppendLine();

        if (signal.ValidForSeconds > 0)
        {
            sb.AppendLine($"Valid: {signal.ValidForSeconds} saniyə");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(signal.InvalidIf))
        {
            sb.AppendLine("Invalid if:");
            sb.AppendLine(signal.InvalidIf);
        }

        return sb.ToString();
    }
}
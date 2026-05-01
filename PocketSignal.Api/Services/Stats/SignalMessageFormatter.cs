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
            return sb.ToString();
        }

        var directionText = ToTitle(signal.Direction);

        sb.AppendLine($"🚨 {signal.Symbol} {directionText} {signal.Confidence}% | {signal.ExpiryMinutes} dəqiqə");
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

    private static string ToTitle(string direction)
    {
        direction = direction.Trim().ToUpperInvariant();

        return direction switch
        {
            "LONG" => "Long",
            "SHORT" => "Short",
            _ => direction
        };
    }
}
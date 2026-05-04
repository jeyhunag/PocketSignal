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
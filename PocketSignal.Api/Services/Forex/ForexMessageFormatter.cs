using PocketSignal.Api.Models.Forex;
using System.Text;

namespace PocketSignal.Api.Services.Forex;

public static class ForexMessageFormatter
{
    public static string Format(ForexTradeSignal signal)
    {
        var directionEmoji = signal.Direction == "LONG" ? "🟢" : "🔴";
        var directionText = signal.Direction == "LONG" ? "LONG" : "SHORT";

        var sb = new StringBuilder();

        sb.AppendLine("🚨 FOREX SIGNAL");
        sb.AppendLine();

        sb.AppendLine($"{directionEmoji} {signal.Confidence}% | {signal.Symbol} {directionText}");
        sb.AppendLine();

        sb.AppendLine("Trade Plan:");
        sb.AppendLine($"Entry: {FormatPrice(signal.EntryPrice)}");
        sb.AppendLine($"Stop Loss: {FormatPrice(signal.StopLoss)}");
        sb.AppendLine($"Take Profit 1: {FormatPrice(signal.TakeProfit1)}");
        sb.AppendLine($"Take Profit 2: {FormatPrice(signal.TakeProfit2)}");

        if (signal.ValidForMinutes > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Valid: {signal.ValidForMinutes} dəqiqə");
        }

        return sb.ToString();
    }

    private static string FormatPrice(decimal value)
    {
        if (value == 0)
            return "0";

        return value.ToString("0.#####");
    }
}
using System.Globalization;
using System.Text;
using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services;

public static class ForexMessageFormatter
{
    public static string Format(ForexTradeSignal signal)
    {
        if (signal.Direction == "WAIT" ||
            signal.Direction != "LONG" && signal.Direction != "SHORT" ||
            signal.EntryPrice <= 0 ||
            signal.StopLoss <= 0 ||
            signal.TakeProfit1 <= 0 ||
            signal.TakeProfit2 <= 0)
        {
            return FormatWait(signal);
        }

        var icon = signal.Direction == "LONG"
            ? "🟢"
            : "🔴";

        var sb = new StringBuilder();

        sb.AppendLine("🚨 FOREX SIGNAL");
        sb.AppendLine();

        sb.AppendLine($"{icon} {signal.Confidence}% | {signal.Symbol} {signal.Direction}");
        sb.AppendLine();

        sb.AppendLine("Trade Plan:");
        sb.AppendLine($"Entry: {FormatPrice(signal.EntryPrice)}");
        sb.AppendLine($"Stop Loss: {FormatPrice(signal.StopLoss)}");
        sb.AppendLine($"Take Profit 1: {FormatPrice(signal.TakeProfit1)}");
        sb.AppendLine($"Take Profit 2: {FormatPrice(signal.TakeProfit2)}");
        sb.AppendLine();

        if (signal.RiskPips > 0)
            sb.AppendLine($"Risk: {FormatNumber(signal.RiskPips)} pips");

        if (signal.RiskReward1 > 0)
            sb.AppendLine($"RR1: 1:{FormatNumber(signal.RiskReward1)}");

        if (signal.RiskReward2 > 0)
            sb.AppendLine($"RR2: 1:{FormatNumber(signal.RiskReward2)}");

        if (signal.ValidForMinutes > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"Valid: {signal.ValidForMinutes} dəqiqə");
        }

        if (!string.IsNullOrWhiteSpace(signal.InvalidIf))
        {
            sb.AppendLine();
            sb.AppendLine("Invalid if:");
            sb.AppendLine(signal.InvalidIf);
        }

        return sb.ToString();
    }

    private static string FormatWait(ForexTradeSignal signal)
    {
        var sb = new StringBuilder();

        sb.AppendLine("⏳ FOREX WAIT");
        sb.AppendLine();

        sb.AppendLine($"Symbol: {signal.Symbol}");
        sb.AppendLine($"Confidence: {signal.Confidence}%");
        sb.AppendLine($"Grade: {signal.Grade}");
        sb.AppendLine();

        if (signal.Reasons.Count > 0)
        {
            sb.AppendLine("Reasons:");

            foreach (var reason in signal.Reasons.Take(8))
            {
                sb.AppendLine($"- {reason}");
            }
        }
        else
        {
            sb.AppendLine("Setup hələ tam hazır deyil.");
        }

        if (signal.StrategyResults.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Strategy scores:");

            foreach (var strategy in signal.StrategyResults.Take(5))
            {
                sb.AppendLine(
                    $"- {strategy.StrategyName}: {strategy.Direction} {strategy.Score}/{strategy.MaxScore}");
            }
        }

        return sb.ToString();
    }

    private static string FormatPrice(decimal value)
    {
        return value.ToString("0.#####", CultureInfo.InvariantCulture);
    }

    private static string FormatNumber(decimal value)
    {
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
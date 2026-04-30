using System.Globalization;
using System.Text;
using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Forex;

public static class ForexMessageFormatter
{
    public static string Format(ForexTradeSignal signal)
    {
        return signal.Direction == "WAIT"
            ? FormatWait(signal)
            : FormatTradeSignal(signal);
    }

    private static string FormatWait(ForexTradeSignal signal)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"📊 {signal.Symbol} FOREX WAIT");
        sb.AppendLine();

        sb.AppendLine($"Confidence: {signal.Confidence}%");
        sb.AppendLine($"Grade: {signal.Grade}");
        sb.AppendLine();

        sb.AppendLine("Reasons:");
        foreach (var reason in signal.Reasons)
        {
            sb.AppendLine($"- {reason}");
        }

        AppendStrategyScores(sb, signal);
        AppendSideScores(sb, signal);

        sb.AppendLine();
        sb.AppendLine($"Time UTC: {signal.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}");

        return sb.ToString();
    }

    private static string FormatTradeSignal(ForexTradeSignal signal)
    {
        var icon = signal.Direction == "LONG" ? "🟢" : "🔴";

        var sb = new StringBuilder();

        sb.AppendLine($"🚨 FOREX SIGNAL");
        sb.AppendLine();
        sb.AppendLine($"{icon} {signal.Symbol} {signal.Direction}");
        sb.AppendLine();

        sb.AppendLine("Trade Plan:");
        sb.AppendLine($"Entry: {FormatPrice(signal.EntryPrice)}");
        sb.AppendLine($"Stop Loss: {FormatPrice(signal.StopLoss)}");
        sb.AppendLine($"Take Profit 1: {FormatPrice(signal.TakeProfit1)}");
        sb.AppendLine($"Take Profit 2: {FormatPrice(signal.TakeProfit2)}");
        sb.AppendLine();

        sb.AppendLine("Risk / Reward:");
        sb.AppendLine($"Risk: {FormatNumber(signal.RiskPips)} pips");
        sb.AppendLine($"Reward TP1: {FormatNumber(signal.RewardPips1)} pips");
        sb.AppendLine($"Reward TP2: {FormatNumber(signal.RewardPips2)} pips");
        sb.AppendLine($"R/R TP1: 1:{FormatNumber(signal.RiskReward1)}");
        sb.AppendLine($"R/R TP2: 1:{FormatNumber(signal.RiskReward2)}");
        sb.AppendLine();

        sb.AppendLine("Signal Quality:");
        sb.AppendLine($"Confidence: {signal.Confidence}%");
        sb.AppendLine($"Grade: {signal.Grade}");
        sb.AppendLine($"Valid: {signal.ValidForMinutes} dəqiqə");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(signal.InvalidIf))
        {
            sb.AppendLine("Invalid if:");
            sb.AppendLine($"- {signal.InvalidIf}");
            sb.AppendLine();
        }

        sb.AppendLine("Reasons:");
        foreach (var reason in signal.Reasons)
        {
            sb.AppendLine($"- {reason}");
        }

        AppendStrategyScores(sb, signal);
        AppendSideScores(sb, signal);

        sb.AppendLine();
        sb.AppendLine($"Time UTC: {signal.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}");

        return sb.ToString();
    }

    private static void AppendStrategyScores(
        StringBuilder sb,
        ForexTradeSignal signal)
    {
        if (signal.StrategyResults == null || signal.StrategyResults.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("Strategy Scores:");

        foreach (var strategy in signal.StrategyResults)
        {
            var status = strategy.IsConfirmed ? "✅" : "⚪";
            var name = CleanStrategyName(strategy.StrategyName);

            sb.AppendLine(
                $"- {status} {name} [{strategy.Direction}]: {strategy.Score}/{strategy.MaxScore}");
        }
    }

    private static void AppendSideScores(
        StringBuilder sb,
        ForexTradeSignal signal)
    {
        if (signal.SideAnalyses == null || signal.SideAnalyses.Count == 0)
            return;

        sb.AppendLine();
        sb.AppendLine("Side Scores:");

        foreach (var side in signal.SideAnalyses)
        {
            var icon = side.Direction == signal.Direction ? "🔥" : "▫️";

            sb.AppendLine($"- {icon} {side.Direction}: {side.Score}");
        }
    }

    private static string CleanStrategyName(string strategyName)
    {
        return strategyName
            .Replace("Strategy", "")
            .Replace("MultiTimeframeConfirmation", "MTF Confirmation")
            .Replace("TrendContinuation", "Trend Continuation")
            .Replace("ReversalSweep", "Reversal Sweep")
            .Replace("SupportResistanceBounce", "S/R Bounce")
            .Replace("BreakoutRetest", "Breakout Retest")
            .Replace("PatternBreakoutConfirmation", "Pattern Breakout")
            .Replace("FalseBreakoutTrap", "False Breakout Trap")
            .Replace("NarrowRangeInsideBar", "NR / Inside Bar")
            .Replace("ForexSessionFilter", "Session Filter")
            .Replace("VolatilityFilter", "Volatility Filter")
            .Replace("RiskRewardValidation", "Risk/Reward Validation");
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
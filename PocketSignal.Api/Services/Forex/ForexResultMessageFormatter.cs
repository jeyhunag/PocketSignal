using System.Globalization;
using System.Text;
using PocketSignal.Api.Data.Entities;

namespace PocketSignal.Api.Services.Forex;

public static class ForexResultMessageFormatter
{
    public static string Format(ForexTradeResultEntity trade)
    {
        var sb = new StringBuilder();

        var icon = trade.Result switch
        {
            "WIN" => "✅",
            "WIN_TP2" => "🏆",
            "LOSS" => "❌",
            "AMBIGUOUS" => "⚠️",
            "EXPIRED" => "⌛",
            _ => "ℹ️"
        };

        var title = trade.Result switch
        {
            "WIN" => "WIN / TP1",
            "WIN_TP2" => "WIN / TP2",
            "LOSS" => "LOSS / STOP LOSS",
            "AMBIGUOUS" => "AMBIGUOUS",
            "EXPIRED" => "EXPIRED",
            _ => trade.Result
        };

        var isTest = trade.ForexSignal?.Grade == "TEST";

        if (isTest)
        {
            sb.AppendLine("🧪 TEST RESULT");
            sb.AppendLine();
        }

        sb.AppendLine($"{icon} {trade.Symbol} {trade.Direction} {title}");
        sb.AppendLine();

        sb.AppendLine($"Entry: {FormatPrice(trade.EntryPrice)}");
        sb.AppendLine($"Stop Loss: {FormatPrice(trade.StopLoss)}");
        sb.AppendLine($"Take Profit 1: {FormatPrice(trade.TakeProfit1)}");
        sb.AppendLine($"Take Profit 2: {FormatPrice(trade.TakeProfit2)}");

        if (trade.ExitPrice != null)
            sb.AppendLine($"Exit: {FormatPrice(trade.ExitPrice.Value)}");

        if (trade.Difference != null)
            sb.AppendLine($"Difference: {FormatPrice(trade.Difference.Value)}");

        sb.AppendLine();

        if (trade.Result == "WIN")
            sb.AppendLine("Nəticə: TP1 vuruldu.");

        if (trade.Result == "WIN_TP2")
            sb.AppendLine("Nəticə: TP2 vuruldu. Trade tam hədəfə çatdı.");

        if (trade.Result == "LOSS")
            sb.AppendLine("Nəticə: Stop Loss vuruldu.");

        if (trade.Result == "AMBIGUOUS")
            sb.AppendLine("Nəticə: Eyni 1m candle daxilində həm TP, həm SL göründü. Sıra bilinmədiyi üçün AMBIGUOUS qeyd edildi.");

        if (trade.Result == "EXPIRED")
            sb.AppendLine("Nəticə: Trade vaxt limitində TP/SL vermədi.");

        sb.AppendLine();

        sb.AppendLine($"Opened UTC: {trade.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}");

        if (trade.CheckedAtUtc != null)
            sb.AppendLine($"Checked UTC: {trade.CheckedAtUtc:yyyy-MM-dd HH:mm:ss}");

        if (trade.Tp1HitAtUtc != null)
            sb.AppendLine($"TP1 Hit UTC: {trade.Tp1HitAtUtc:yyyy-MM-dd HH:mm:ss}");

        if (trade.Tp2HitAtUtc != null)
            sb.AppendLine($"TP2 Hit UTC: {trade.Tp2HitAtUtc:yyyy-MM-dd HH:mm:ss}");

        if (trade.StopLossHitAtUtc != null)
            sb.AppendLine($"SL Hit UTC: {trade.StopLossHitAtUtc:yyyy-MM-dd HH:mm:ss}");

        if (!string.IsNullOrWhiteSpace(trade.Notes))
        {
            sb.AppendLine();
            sb.AppendLine($"Note: {trade.Notes}");
        }

        return sb.ToString();
    }

    private static string FormatPrice(decimal value)
    {
        return value.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
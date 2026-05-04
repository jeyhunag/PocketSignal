using System.Globalization;
using System.Text;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Services.MarketData;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Binary;

public class SignalResultTracker : ISignalResultTracker
{
    private readonly object _lock = new();
    private readonly List<SignalTradeRecord> _trades = new();

    private readonly IMarketDataService _marketDataService;
    private readonly ITelegramService _telegramService;
    private readonly ILogger<SignalResultTracker> _logger;

    private DateTime _summaryDateUtc = DateTime.UtcNow.Date;
    private int _lastSummarySentCompletedCount;

    public SignalResultTracker(
        IMarketDataService marketDataService,
        ITelegramService telegramService,
        ILogger<SignalResultTracker> logger)
    {
        _marketDataService = marketDataService;
        _telegramService = telegramService;
        _logger = logger;
    }

    public SignalTradeRecord? RegisterSignal(SmartTradeSignal signal)
    {
        if (signal.Direction == "WAIT")
            return null;

        if (signal.Direction != "LONG" && signal.Direction != "SHORT")
            return null;

        if (signal.ExpiryMinutes <= 0)
            return null;

        if (signal.LastClose <= 0)
            return null;

        var nowUtc = DateTime.UtcNow;

        var record = new SignalTradeRecord
        {
            Symbol = signal.Symbol,
            Direction = signal.Direction,
            ExpiryMinutes = signal.ExpiryMinutes,
            Confidence = signal.Confidence,
            Grade = signal.Grade,
            EntryPrice = signal.LastClose,
            SignalMessage = signal.Message,
            ExpiryReason = signal.ExpiryReason,
            CreatedAtUtc = nowUtc,
            DueAtUtc = nowUtc.AddMinutes(signal.ExpiryMinutes),
            Result = "PENDING"
        };

        lock (_lock)
        {
            _trades.Add(record);
        }

        return record;
    }

    public async Task EvaluateDueSignalsAsync(CancellationToken cancellationToken = default)
    {
        List<SignalTradeRecord> dueTrades;

        lock (_lock)
        {
            dueTrades = _trades
                .Where(x =>
                    x.Result == "PENDING" &&
                    x.DueAtUtc <= DateTime.UtcNow)
                .OrderBy(x => x.DueAtUtc)
                .ToList();
        }

        foreach (var trade in dueTrades)
        {
            try
            {
                await EvaluateSingleTradeAsync(trade, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Binary result yoxlanmadi. TradeId: {TradeId} | {Symbol} {Direction}",
                    trade.Id,
                    trade.Symbol,
                    trade.Direction);
            }
        }
    }

    public List<SignalTradeRecord> GetTodayTrades()
    {
        var today = DateTime.UtcNow.Date;

        lock (_lock)
        {
            return _trades
                .Where(x => x.CreatedAtUtc.Date == today)
                .OrderByDescending(x => x.CreatedAtUtc)
                .ToList();
        }
    }

    public string GetTodayStatus()
    {
        var trades = GetTodayTrades();

        var total = trades.Count;
        var pending = trades.Count(x => x.Result == "PENDING");
        var wins = trades.Count(x => x.Result == "WIN");
        var losses = trades.Count(x => x.Result == "LOSS");
        var draws = trades.Count(x => x.Result == "DRAW");

        var completed = wins + losses;
        var winRate = completed > 0
            ? Math.Round((decimal)wins / completed * 100, 2)
            : 0;

        var sb = new StringBuilder();

        sb.AppendLine($"Binary Signal Results - {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();

        sb.AppendLine($"Total signals: {total}");
        sb.AppendLine($"Pending: {pending}");
        sb.AppendLine($"WIN: {wins}");
        sb.AppendLine($"LOSS: {losses}");
        sb.AppendLine($"DRAW: {draws}");
        sb.AppendLine($"Win rate: {winRate}%");
        sb.AppendLine();

        if (trades.Count == 0)
        {
            sb.AppendLine("Bugun hele binary LONG/SHORT signal qeyde alinmayib.");
            return sb.ToString();
        }

        sb.AppendLine("Last trades:");
        sb.AppendLine();

        foreach (var trade in trades.Take(10))
        {
            sb.AppendLine($"{trade.CreatedAtUtc:HH:mm:ss} UTC | {trade.Symbol} {trade.Direction} {trade.ExpiryMinutes}m | {trade.Result}");
            sb.AppendLine($"Entry: {trade.EntryPrice}");

            if (trade.ExitPrice != null)
                sb.AppendLine($"Exit: {trade.ExitPrice} | Difference: {trade.Difference}");

            sb.AppendLine($"Confidence: {trade.Confidence}% | Grade: {trade.Grade}");
            sb.AppendLine($"Due: {trade.DueAtUtc:HH:mm:ss} UTC");

            if (trade.CheckedAtUtc != null)
                sb.AppendLine($"Checked: {trade.CheckedAtUtc:HH:mm:ss} UTC");

            if (trade.ResultNotifiedAtUtc != null)
                sb.AppendLine($"Telegram result sent: {trade.ResultNotifiedAtUtc:HH:mm:ss} UTC");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private async Task EvaluateSingleTradeAsync(
        SignalTradeRecord trade,
        CancellationToken cancellationToken)
    {
        var exitPrice = await GetLatestCloseAsync(
            trade.Symbol,
            cancellationToken);

        if (exitPrice == null)
        {
            _logger.LogInformation(
                "Binary result ucun exit price tapilmadi. TradeId: {TradeId} | Symbol: {Symbol}",
                trade.Id,
                trade.Symbol);

            return;
        }

        string result;
        decimal difference;

        lock (_lock)
        {
            if (trade.Result != "PENDING")
                return;

            result = CalculateResult(
                trade.Direction,
                trade.EntryPrice,
                exitPrice.Value);

            difference = exitPrice.Value - trade.EntryPrice;

            trade.ExitPrice = exitPrice.Value;
            trade.Difference = difference;
            trade.Result = result;
            trade.CheckedAtUtc = DateTime.UtcNow;
        }

        await NotifyResultOnceAsync(
            trade,
            cancellationToken);
    }

    private async Task NotifyResultOnceAsync(
        SignalTradeRecord trade,
        CancellationToken cancellationToken)
    {
        string message;

        lock (_lock)
        {
            if (trade.ResultNotifiedAtUtc != null)
                return;

            if (trade.Result == "PENDING")
                return;

            message = FormatResultMessage(trade);

            trade.ResultNotificationMessage = message;
            trade.ResultNotifiedAtUtc = DateTime.UtcNow;
        }

        try
        {
            await _telegramService.SendMessageAsync(
                message,
                cancellationToken);

            _logger.LogInformation(
                "Binary result Telegram-a gonderildi. TradeId: {TradeId} | {Symbol} {Direction} | Result: {Result}",
                trade.Id,
                trade.Symbol,
                trade.Direction,
                trade.Result);
        }
        catch
        {
            lock (_lock)
            {
                trade.ResultNotifiedAtUtc = null;
                trade.ResultNotificationMessage = string.Empty;
            }

            throw;
        }

        await TrySendDailySummaryIfNeededAsync(cancellationToken);
    }

    private async Task TrySendDailySummaryIfNeededAsync(
        CancellationToken cancellationToken)
    {
        string? summaryMessage = null;
        int completedToMark = 0;

        lock (_lock)
        {
            ResetSummaryMarkerIfNewDay();

            var today = DateTime.UtcNow.Date;

            var todayTrades = _trades
                .Where(x => x.CreatedAtUtc.Date == today)
                .ToList();

            var pending = todayTrades.Count(x => x.Result == "PENDING");
            var wins = todayTrades.Count(x => x.Result == "WIN");
            var losses = todayTrades.Count(x => x.Result == "LOSS");
            var draws = todayTrades.Count(x => x.Result == "DRAW");

            var completed = wins + losses + draws;

            if (completed < 10)
                return;

            if (completed % 10 != 0)
                return;

            if (_lastSummarySentCompletedCount == completed)
                return;

            _lastSummarySentCompletedCount = completed;
            completedToMark = completed;

            summaryMessage = FormatDailySummaryMessage(
                completed,
                pending,
                wins,
                losses,
                draws);
        }

        if (string.IsNullOrWhiteSpace(summaryMessage))
            return;

        try
        {
            await _telegramService.SendMessageAsync(
                summaryMessage,
                cancellationToken);

            _logger.LogInformation(
                "Binary daily summary Telegram-a gonderildi. Completed: {Completed}",
                completedToMark);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (_lastSummarySentCompletedCount == completedToMark)
                    _lastSummarySentCompletedCount = Math.Max(0, completedToMark - 1);
            }

            _logger.LogWarning(
                ex,
                "Binary daily summary Telegram-a gonderilmedi. Completed: {Completed}",
                completedToMark);
        }
    }

    private void ResetSummaryMarkerIfNewDay()
    {
        var today = DateTime.UtcNow.Date;

        if (_summaryDateUtc == today)
            return;

        _summaryDateUtc = today;
        _lastSummarySentCompletedCount = 0;
    }

    private static string FormatDailySummaryMessage(
        int completed,
        int pending,
        int wins,
        int losses,
        int draws)
    {
        var winLossTotal = wins + losses;

        var winRate = winLossTotal > 0
            ? Math.Round((decimal)wins / winLossTotal * 100m, 1)
            : 0;

        return
$"""
📊 Binary Daily Summary

Completed: {completed}
✅ Win: {wins}
❌ Lose: {losses}
➖ Draw: {draws}
⏳ Pending: {pending}

Win rate: {winRate}%
Date UTC: {DateTime.UtcNow:yyyy-MM-dd}
""";
    }

    private async Task<decimal?> GetLatestCloseAsync(
        string symbol,
        CancellationToken cancellationToken)
    {
        var response = await _marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            5,
            cancellationToken);

        if (response?.Values == null || response.Values.Count == 0)
            return null;

        var candles = response.Values
            .Select(x => new
            {
                x.Close,
                Time = TryParseTime(x.DateTime)
            })
            .Where(x => x.Time != null)
            .OrderBy(x => x.Time)
            .ToList();

        return candles.LastOrDefault()?.Close;
    }

    private static DateTime? TryParseTime(string value)
    {
        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd"
        };

        if (DateTime.TryParseExact(
                value,
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var time))
        {
            return time;
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string CalculateResult(
        string direction,
        decimal entryPrice,
        decimal exitPrice)
    {
        if (exitPrice == entryPrice)
            return "DRAW";

        if (direction == "LONG")
            return exitPrice > entryPrice ? "WIN" : "LOSS";

        if (direction == "SHORT")
            return exitPrice < entryPrice ? "WIN" : "LOSS";

        return "DRAW";
    }

    private static string FormatResultMessage(SignalTradeRecord trade)
    {
        var icon = trade.Result switch
        {
            "WIN" => "✅",
            "LOSS" => "❌",
            "DRAW" => "➖",
            _ => "ℹ️"
        };

        var titleResult = trade.Result switch
        {
            "WIN" => "WIN",
            "LOSS" => "LOSS",
            "DRAW" => "DRAW",
            _ => trade.Result
        };

        return
$"""
{icon} {trade.Symbol} {trade.Direction} {titleResult}

Entry: {FormatPrice(trade.EntryPrice)}
Exit: {FormatPrice(trade.ExitPrice)}
Difference: {FormatPrice(trade.Difference)}

Expiry: {trade.ExpiryMinutes} dəqiqə
Confidence: {trade.Confidence}%
Grade: {trade.Grade}
""";
    }

    private static string FormatPrice(decimal? value)
    {
        if (value == null)
            return "-";

        return value.Value.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
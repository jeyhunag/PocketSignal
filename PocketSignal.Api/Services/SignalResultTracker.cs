using System.Globalization;
using System.Text;
using PocketSignal.Api.Models;

namespace PocketSignal.Api.Services;

public class SignalResultTracker : ISignalResultTracker
{
    private readonly object _lock = new();
    private readonly List<SignalTradeRecord> _trades = new();
    private readonly IMarketDataService _marketDataService;

    public SignalResultTracker(IMarketDataService marketDataService)
    {
        _marketDataService = marketDataService;
    }

    public SignalTradeRecord? RegisterSignal(SmartTradeSignal signal)
    {
        if (signal.Direction == "WAIT")
            return null;

        if (signal.ExpiryMinutes <= 0)
            return null;

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
            CreatedAtUtc = DateTime.UtcNow,
            DueAtUtc = DateTime.UtcNow.AddMinutes(signal.ExpiryMinutes),
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
                .Where(x => x.Result == "PENDING" && x.DueAtUtc <= DateTime.UtcNow)
                .ToList();
        }

        foreach (var trade in dueTrades)
        {
            var exitPrice = await GetLatestCloseAsync(trade.Symbol, cancellationToken);

            if (exitPrice == null)
                continue;

            var result = CalculateResult(
                trade.Direction,
                trade.EntryPrice,
                exitPrice.Value);

            lock (_lock)
            {
                trade.ExitPrice = exitPrice.Value;
                trade.Difference = exitPrice.Value - trade.EntryPrice;
                trade.Result = result;
                trade.CheckedAtUtc = DateTime.UtcNow;
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

        sb.AppendLine($"Signal Results - {DateTime.UtcNow:yyyy-MM-dd}");
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
            sb.AppendLine("Bugun hele LONG/SHORT signal qeyde alinmayib.");
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

            sb.AppendLine();
        }

        return sb.ToString();
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
}
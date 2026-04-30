using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data;
using PocketSignal.Api.Data.Entities;
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.MarketData;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;

namespace PocketSignal.Api.Services.Forex;

public class ForexTradeResultTracker : IForexTradeResultTracker
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ForexTradeResultTracker> _logger;

    public ForexTradeResultTracker(
        IServiceScopeFactory scopeFactory,
        ILogger<ForexTradeResultTracker> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task EvaluateOpenTradesAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<PocketSignalDbContext>();
        var marketDataService = scope.ServiceProvider.GetRequiredService<IMarketDataService>();

        var openTrades = await dbContext.ForexTradeResults
            .Include(x => x.ForexSignal)
            .Where(x =>
                x.Result == "PENDING" ||
                x.Result == "WIN" ||
                (
                    (x.Result == "WIN_TP2" ||
                     x.Result == "LOSS" ||
                     x.Result == "AMBIGUOUS" ||
                     x.Result == "EXPIRED") &&
                    x.LastNotifiedResult != x.Result
                ))
            .OrderBy(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        if (openTrades.Count == 0)
            return;

        var notificationCandidates = new List<ForexTradeResultEntity>();

        var groupedTrades = openTrades
            .GroupBy(x => x.Symbol)
            .ToList();

        foreach (var group in groupedTrades)
        {
            var symbol = group.Key;

            var candles = await GetRecentCandlesAsync(
                marketDataService,
                symbol,
                cancellationToken);

            if (candles.Count == 0)
            {
                _logger.LogWarning(
                    "FOREX result tracker: candle tapilmadi. Symbol: {Symbol}",
                    symbol);

                continue;
            }

            foreach (var trade in group)
            {
                var oldResult = trade.Result;

                EvaluateSingleTrade(trade, candles);

                if (ShouldSendResultNotification(trade))
                {
                    notificationCandidates.Add(trade);
                }

                if (oldResult != trade.Result)
                {
                    _logger.LogInformation(
                        "FOREX trade result changed. {Symbol} {Direction}: {OldResult} -> {NewResult}",
                        trade.Symbol,
                        trade.Direction,
                        oldResult,
                        trade.Result);
                }
            }
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        if (notificationCandidates.Count > 0)
        {
            await SendResultNotificationsAsync(
                scope.ServiceProvider,
                notificationCandidates,
                cancellationToken);

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<List<ForexTradeResultEntity>> GetTodayTradesAsync(
        CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();

        var dbContext = scope.ServiceProvider.GetRequiredService<PocketSignalDbContext>();

        var today = DateTime.UtcNow.Date;

        return await dbContext.ForexTradeResults
            .Include(x => x.ForexSignal)
            .Where(x => x.CreatedAtUtc.Date == today)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);
    }

    public async Task<string> GetTodayStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var trades = await GetTodayTradesAsync(cancellationToken);

        var total = trades.Count;
        var pending = trades.Count(x => x.Result == "PENDING");
        var win = trades.Count(x => x.Result == "WIN");
        var winTp2 = trades.Count(x => x.Result == "WIN_TP2");
        var loss = trades.Count(x => x.Result == "LOSS");
        var ambiguous = trades.Count(x => x.Result == "AMBIGUOUS");
        var expired = trades.Count(x => x.Result == "EXPIRED");

        var telegramSent = trades.Count(x => !string.IsNullOrWhiteSpace(x.LastNotifiedResult));
        var telegramPending = trades.Count(x =>
            IsNotifiableResult(x.Result) &&
            x.LastNotifiedResult != x.Result);

        var completed = win + winTp2 + loss;

        var winRate = completed > 0
            ? Math.Round((decimal)(win + winTp2) / completed * 100m, 2)
            : 0;

        var sb = new StringBuilder();

        sb.AppendLine($"Forex Trade Results - {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();

        sb.AppendLine($"Total trades: {total}");
        sb.AppendLine($"Pending: {pending}");
        sb.AppendLine($"WIN / TP1: {win}");
        sb.AppendLine($"WIN_TP2: {winTp2}");
        sb.AppendLine($"LOSS: {loss}");
        sb.AppendLine($"AMBIGUOUS: {ambiguous}");
        sb.AppendLine($"EXPIRED: {expired}");
        sb.AppendLine($"Win rate: {winRate}%");
        sb.AppendLine();

        sb.AppendLine($"Telegram result sent: {telegramSent}");
        sb.AppendLine($"Telegram result pending: {telegramPending}");
        sb.AppendLine();

        if (trades.Count == 0)
        {
            sb.AppendLine("Bugun hele Forex LONG/SHORT trade qeyde alinmayib.");
            return sb.ToString();
        }

        sb.AppendLine("Last forex trades:");
        sb.AppendLine();

        foreach (var trade in trades.Take(10))
        {
            sb.AppendLine($"{trade.CreatedAtUtc:HH:mm:ss} UTC | {trade.Symbol} {trade.Direction} | {trade.Result}");
            sb.AppendLine($"Entry: {trade.EntryPrice}");
            sb.AppendLine($"SL: {trade.StopLoss}");
            sb.AppendLine($"TP1: {trade.TakeProfit1}");
            sb.AppendLine($"TP2: {trade.TakeProfit2}");

            if (trade.ExitPrice != null)
                sb.AppendLine($"Exit: {trade.ExitPrice}");

            if (trade.Difference != null)
                sb.AppendLine($"Difference: {trade.Difference}");

            sb.AppendLine($"Expires: {trade.ExpiresAtUtc:HH:mm:ss} UTC");

            if (trade.CheckedAtUtc != null)
                sb.AppendLine($"Checked: {trade.CheckedAtUtc:HH:mm:ss} UTC");

            if (trade.Tp1HitAtUtc != null)
                sb.AppendLine($"TP1 hit: {trade.Tp1HitAtUtc:HH:mm:ss} UTC");

            if (trade.Tp2HitAtUtc != null)
                sb.AppendLine($"TP2 hit: {trade.Tp2HitAtUtc:HH:mm:ss} UTC");

            if (trade.StopLossHitAtUtc != null)
                sb.AppendLine($"SL hit: {trade.StopLossHitAtUtc:HH:mm:ss} UTC");

            if (!string.IsNullOrWhiteSpace(trade.LastNotifiedResult))
                sb.AppendLine($"Telegram notified: {trade.LastNotifiedResult} at {trade.LastNotifiedAtUtc:HH:mm:ss} UTC");

            if (!string.IsNullOrWhiteSpace(trade.LastNotificationError))
                sb.AppendLine($"Telegram error: {trade.LastNotificationError}");

            if (!string.IsNullOrWhiteSpace(trade.Notes))
                sb.AppendLine($"Notes: {trade.Notes}");

            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static async Task<List<Candle>> GetRecentCandlesAsync(
        IMarketDataService marketDataService,
        string symbol,
        CancellationToken cancellationToken)
    {
        var response = await marketDataService.GetCandlesAsync(
            symbol,
            "1min",
            300,
            cancellationToken);

        if (response?.Values == null)
            return new List<Candle>();

        var candles = new List<Candle>();

        foreach (var item in response.Values)
        {
            var time = TryParseTime(item.DateTime);

            if (time == null)
                continue;

            candles.Add(new Candle
            {
                Symbol = symbol,
                Time = time.Value,
                Open = item.Open,
                High = item.High,
                Low = item.Low,
                Close = item.Close
            });
        }

        candles = candles
            .OrderBy(x => x.Time)
            .ToList();

        NormalizeProviderTimesToUtc(candles);

        var nowUtc = DateTime.UtcNow;

        return candles
            .Where(x => x.Time <= nowUtc.AddMinutes(2))
            .OrderBy(x => x.Time)
            .ToList();
    }

    private static void EvaluateSingleTrade(
        ForexTradeResultEntity trade,
        List<Candle> candles)
    {
        if (trade.Result != "PENDING" &&
            trade.Result != "WIN")
        {
            return;
        }

        var nowUtc = DateTime.UtcNow;

        var evaluationStartUtc = GetFirstEvaluatableCandleTime(trade.CreatedAtUtc);
        var latestAllowedCandleUtc = GetLastClosedOneMinuteCandleTime(nowUtc);

        if (latestAllowedCandleUtc < evaluationStartUtc)
            return;

        var tradeCandles = candles
            .Where(x =>
                x.Time >= evaluationStartUtc &&
                x.Time <= latestAllowedCandleUtc &&
                x.Time <= trade.ExpiresAtUtc)
            .OrderBy(x => x.Time)
            .ToList();

        foreach (var candle in tradeCandles)
        {
            if (trade.Result == "LOSS" ||
                trade.Result == "WIN_TP2" ||
                trade.Result == "AMBIGUOUS" ||
                trade.Result == "EXPIRED")
            {
                return;
            }

            if (trade.Direction == "LONG")
            {
                EvaluateLongTrade(trade, candle);
            }
            else if (trade.Direction == "SHORT")
            {
                EvaluateShortTrade(trade, candle);
            }
        }

        if (trade.Result == "PENDING" && nowUtc > trade.ExpiresAtUtc)
        {
            MarkExpired(trade);
            return;
        }

        if (tradeCandles.Count == 0)
            return;

        var last = tradeCandles.Last();

        trade.ExitPrice = last.Close;
        trade.Difference = CalculateDifference(
            trade.Direction,
            trade.EntryPrice,
            last.Close);

        trade.CheckedAtUtc = nowUtc;
    }

    private static void EvaluateLongTrade(
        ForexTradeResultEntity trade,
        Candle candle)
    {
        var slHit = candle.Low <= trade.StopLoss;
        var tp1Hit = candle.High >= trade.TakeProfit1;
        var tp2Hit = candle.High >= trade.TakeProfit2;

        if (trade.Result == "PENDING")
        {
            if (slHit && (tp1Hit || tp2Hit))
            {
                MarkAmbiguous(trade, candle);
                return;
            }

            if (slHit)
            {
                MarkLoss(trade, candle, trade.StopLoss);
                return;
            }

            if (tp2Hit)
            {
                MarkWinTp2(trade, candle);
                return;
            }

            if (tp1Hit)
            {
                MarkWin(trade, candle);
                return;
            }
        }

        if (trade.Result == "WIN" && tp2Hit)
        {
            MarkWinTp2(trade, candle);
        }
    }

    private static void EvaluateShortTrade(
        ForexTradeResultEntity trade,
        Candle candle)
    {
        var slHit = candle.High >= trade.StopLoss;
        var tp1Hit = candle.Low <= trade.TakeProfit1;
        var tp2Hit = candle.Low <= trade.TakeProfit2;

        if (trade.Result == "PENDING")
        {
            if (slHit && (tp1Hit || tp2Hit))
            {
                MarkAmbiguous(trade, candle);
                return;
            }

            if (slHit)
            {
                MarkLoss(trade, candle, trade.StopLoss);
                return;
            }

            if (tp2Hit)
            {
                MarkWinTp2(trade, candle);
                return;
            }

            if (tp1Hit)
            {
                MarkWin(trade, candle);
                return;
            }
        }

        if (trade.Result == "WIN" && tp2Hit)
        {
            MarkWinTp2(trade, candle);
        }
    }

    private static void MarkWin(
        ForexTradeResultEntity trade,
        Candle candle)
    {
        trade.Result = "WIN";
        trade.IsTp1Hit = true;
        trade.Tp1HitAtUtc = candle.Time;
        trade.ExitPrice = trade.TakeProfit1;
        trade.Difference = CalculateDifference(
            trade.Direction,
            trade.EntryPrice,
            trade.TakeProfit1);
        trade.CheckedAtUtc = DateTime.UtcNow;
        trade.Notes = "TP1 vuruldu. Forex signal WIN kimi qeyd edildi.";

        UpdateSignalStatus(trade, "WIN");
    }

    private static void MarkWinTp2(
        ForexTradeResultEntity trade,
        Candle candle)
    {
        trade.Result = "WIN_TP2";
        trade.IsTp1Hit = true;
        trade.IsTp2Hit = true;

        trade.Tp1HitAtUtc ??= candle.Time;
        trade.Tp2HitAtUtc = candle.Time;

        trade.ExitPrice = trade.TakeProfit2;
        trade.Difference = CalculateDifference(
            trade.Direction,
            trade.EntryPrice,
            trade.TakeProfit2);
        trade.CheckedAtUtc = DateTime.UtcNow;
        trade.Notes = "TP2 vuruldu. Forex signal tam WIN_TP2 kimi qeyd edildi.";

        UpdateSignalStatus(trade, "WIN_TP2");
    }

    private static void MarkLoss(
        ForexTradeResultEntity trade,
        Candle candle,
        decimal exitPrice)
    {
        trade.Result = "LOSS";
        trade.IsStopLossHit = true;
        trade.StopLossHitAtUtc = candle.Time;
        trade.ExitPrice = exitPrice;
        trade.Difference = CalculateDifference(
            trade.Direction,
            trade.EntryPrice,
            exitPrice);
        trade.CheckedAtUtc = DateTime.UtcNow;
        trade.Notes = "Stop Loss vuruldu. Forex signal LOSS kimi qeyd edildi.";

        UpdateSignalStatus(trade, "LOSS");
    }

    private static void MarkAmbiguous(
        ForexTradeResultEntity trade,
        Candle candle)
    {
        trade.Result = "AMBIGUOUS";
        trade.CheckedAtUtc = DateTime.UtcNow;
        trade.Notes = "Eyni 1m candle icinde hem TP, hem SL seviyyesi gorundu. Sira bilinmediyi ucun AMBIGUOUS qeyd edildi.";

        trade.ExitPrice = candle.Close;
        trade.Difference = CalculateDifference(
            trade.Direction,
            trade.EntryPrice,
            candle.Close);

        UpdateSignalStatus(trade, "AMBIGUOUS");
    }

    private static void MarkExpired(ForexTradeResultEntity trade)
    {
        trade.Result = "EXPIRED";
        trade.CheckedAtUtc = DateTime.UtcNow;
        trade.Notes = "Trade 4 saat erzinde TP/SL neticesi vermedi.";

        UpdateSignalStatus(trade, "EXPIRED");
    }

    private static void UpdateSignalStatus(
        ForexTradeResultEntity trade,
        string status)
    {
        if (trade.ForexSignal == null)
            return;

        trade.ForexSignal.Status = status;
        trade.ForexSignal.UpdatedAtUtc = DateTime.UtcNow;
    }

    private static bool ShouldSendResultNotification(ForexTradeResultEntity trade)
    {
        if (!IsNotifiableResult(trade.Result))
            return false;

        return trade.LastNotifiedResult != trade.Result;
    }

    private static bool IsNotifiableResult(string result)
    {
        return result == "WIN" ||
               result == "WIN_TP2" ||
               result == "LOSS" ||
               result == "AMBIGUOUS" ||
               result == "EXPIRED";
    }

    private async Task SendResultNotificationsAsync(
        IServiceProvider serviceProvider,
        List<ForexTradeResultEntity> trades,
        CancellationToken cancellationToken)
    {
        foreach (var trade in trades)
        {
            var message = ForexResultMessageFormatter.Format(trade);

            var telegramResult = await SendTelegramMessageAsync(
                serviceProvider,
                message,
                cancellationToken);

            if (telegramResult.Sent)
            {
                trade.LastNotifiedResult = trade.Result;
                trade.LastNotifiedAtUtc = DateTime.UtcNow;
                trade.LastNotificationError = string.Empty;

                _logger.LogInformation(
                    "FOREX result Telegram notification sent. {Symbol} {Direction} {Result}",
                    trade.Symbol,
                    trade.Direction,
                    trade.Result);
            }
            else
            {
                trade.LastNotificationError = telegramResult.Error;

                _logger.LogWarning(
                    "FOREX result Telegram notification failed. {Symbol} {Direction} {Result}. Error: {Error}",
                    trade.Symbol,
                    trade.Direction,
                    trade.Result,
                    telegramResult.Error);
            }
        }
    }

    private static async Task<(bool Sent, string Error)> SendTelegramMessageAsync(
        IServiceProvider serviceProvider,
        string message,
        CancellationToken cancellationToken)
    {
        var configuration = serviceProvider.GetRequiredService<IConfiguration>();
        var httpClientFactory = serviceProvider.GetRequiredService<IHttpClientFactory>();

        var botToken = configuration["Telegram:Token"];
        var chatId = configuration["Telegram:Id"];

        if (string.IsNullOrWhiteSpace(botToken))
            return (false, "Telegram token tapilmadi. appsettings.json daxilinde Telegram:Token yoxla.");

        if (string.IsNullOrWhiteSpace(chatId))
            return (false, "Telegram chat id tapilmadi. appsettings.json daxilinde Telegram:Id yoxla.");

        var httpClient = httpClientFactory.CreateClient();

        var url = $"https://api.telegram.org/bot{botToken}/sendMessage";

        var payload = new
        {
            chat_id = chatId,
            text = message
        };

        try
        {
            var response = await httpClient.PostAsJsonAsync(
                url,
                payload,
                cancellationToken);

            if (response.IsSuccessStatusCode)
                return (true, string.Empty);

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

            return (false, $"Telegram HTTP {(int)response.StatusCode}: {responseText}");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static decimal CalculateDifference(
        string direction,
        decimal entryPrice,
        decimal exitPrice)
    {
        if (direction == "LONG")
            return exitPrice - entryPrice;

        if (direction == "SHORT")
            return entryPrice - exitPrice;

        return exitPrice - entryPrice;
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
            return DateTime.SpecifyKind(time, DateTimeKind.Utc);
        }

        return null;
    }

    private static void NormalizeProviderTimesToUtc(List<Candle> candles)
    {
        if (candles.Count == 0)
            return;

        var nowUtc = DateTime.UtcNow;
        var latestCandleTime = candles.Max(x => x.Time);

        if (latestCandleTime > nowUtc.AddMinutes(2))
        {
            var difference = latestCandleTime - nowUtc;
            var offsetHours = (int)Math.Round(
                difference.TotalHours,
                MidpointRounding.AwayFromZero);

            if (offsetHours >= 1 && offsetHours <= 14)
            {
                foreach (var candle in candles)
                {
                    candle.Time = DateTime.SpecifyKind(
                        candle.Time.AddHours(-offsetHours),
                        DateTimeKind.Utc);
                }
            }
        }
        else
        {
            foreach (var candle in candles)
            {
                candle.Time = DateTime.SpecifyKind(
                    candle.Time,
                    DateTimeKind.Utc);
            }
        }
    }

    private static DateTime GetFirstEvaluatableCandleTime(DateTime createdAtUtc)
    {
        createdAtUtc = DateTime.SpecifyKind(createdAtUtc, DateTimeKind.Utc);

        var candleMinute = new DateTime(
            createdAtUtc.Year,
            createdAtUtc.Month,
            createdAtUtc.Day,
            createdAtUtc.Hour,
            createdAtUtc.Minute,
            0,
            DateTimeKind.Utc);

        if (createdAtUtc.Second == 0 && createdAtUtc.Millisecond == 0)
            return candleMinute;

        return candleMinute.AddMinutes(1);
    }

    private static DateTime GetLastClosedOneMinuteCandleTime(DateTime nowUtc)
    {
        nowUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);

        var currentMinute = new DateTime(
            nowUtc.Year,
            nowUtc.Month,
            nowUtc.Day,
            nowUtc.Hour,
            nowUtc.Minute,
            0,
            DateTimeKind.Utc);

        return currentMinute.AddMinutes(-1);
    }
}
using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data;
using PocketSignal.Api.Data.Entities;
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Forex;
using System.Text;
using System.Text.Json;

namespace PocketSignal.Api.Services;

public class ForexSignalDatabaseService : IForexSignalDatabaseService
{
    private readonly PocketSignalDbContext _dbContext;

    public ForexSignalDatabaseService(PocketSignalDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<Guid> SaveSignalAsync(
        ForexTradeSignal signal,
        bool telegramSent,
        string notificationMessage,
        CancellationToken cancellationToken = default)
    {
        var status = GetStatus(signal, telegramSent, notificationMessage);

        var entity = new ForexSignalEntity
        {
            Symbol = signal.Symbol,
            Direction = signal.Direction,
            IsTradable = signal.Direction != "WAIT" && signal.Confidence >= 82,

            EntryPrice = signal.EntryPrice,
            StopLoss = signal.StopLoss,
            TakeProfit1 = signal.TakeProfit1,
            TakeProfit2 = signal.TakeProfit2,

            RiskPips = signal.RiskPips,
            RewardPips1 = signal.RewardPips1,
            RewardPips2 = signal.RewardPips2,
            RiskReward1 = signal.RiskReward1,
            RiskReward2 = signal.RiskReward2,

            Confidence = signal.Confidence,
            Grade = signal.Grade,
            Message = signal.Message,
            InvalidIf = signal.InvalidIf,
            ValidForMinutes = signal.ValidForMinutes,

            ReasonsJson = JsonSerializer.Serialize(signal.Reasons),
            StrategyBreakdownJson = JsonSerializer.Serialize(signal.StrategyResults),

            Status = status,
            CreatedAtUtc = DateTime.UtcNow
        };

        foreach (var strategy in signal.StrategyResults)
        {
            entity.StrategyScores.Add(new ForexStrategyScoreEntity
            {
                ForexSignalId = entity.Id,
                StrategyName = strategy.StrategyName,
                Direction = strategy.Direction,
                Score = strategy.Score,
                MaxScore = strategy.MaxScore,
                IsConfirmed = strategy.IsConfirmed,
                ReasonsJson = JsonSerializer.Serialize(strategy.Reasons),
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        if (signal.Direction != "WAIT" && telegramSent)
        {
            entity.TradeResult = new ForexTradeResultEntity
            {
                ForexSignalId = entity.Id,
                Symbol = signal.Symbol,
                Direction = signal.Direction,

                EntryPrice = signal.EntryPrice,
                StopLoss = signal.StopLoss,
                TakeProfit1 = signal.TakeProfit1,
                TakeProfit2 = signal.TakeProfit2,

                Result = "PENDING",
                CreatedAtUtc = DateTime.UtcNow,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(4),
                Notes = "Forex signal Telegram-a gonderildi ve DB-de PENDING kimi qeyd edildi."
            };
        }

        _dbContext.ForexSignals.Add(entity);

        await _dbContext.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    public async Task<List<ForexSignalEntity>> GetLatestSignalsAsync(
        int count = 50,
        CancellationToken cancellationToken = default)
    {
        return await _dbContext.ForexSignals
            .Include(x => x.StrategyScores)
            .Include(x => x.TradeResult)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(count)
            .ToListAsync(cancellationToken);
    }

    public async Task<string> GetTodayStatusAsync(
        CancellationToken cancellationToken = default)
    {
        var today = DateTime.UtcNow.Date;

        var signals = await _dbContext.ForexSignals
            .Where(x => x.CreatedAtUtc.Date == today)
            .OrderByDescending(x => x.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        var total = signals.Count;
        var wait = signals.Count(x => x.Direction == "WAIT");
        var longCount = signals.Count(x => x.Direction == "LONG");
        var shortCount = signals.Count(x => x.Direction == "SHORT");
        var tradable = signals.Count(x => x.IsTradable);
        var pending = signals.Count(x => x.Status == "PENDING");
        var skipped = signals.Count(x => x.Status == "SKIPPED" || x.Status == "COOLDOWN");

        var avgConfidence = signals.Count > 0
            ? Math.Round(signals.Average(x => x.Confidence), 2)
            : 0;

        var sb = new StringBuilder();

        sb.AppendLine($"Forex DB Signals - {DateTime.UtcNow:yyyy-MM-dd}");
        sb.AppendLine();

        sb.AppendLine($"Total saved: {total}");
        sb.AppendLine($"WAIT: {wait}");
        sb.AppendLine($"LONG: {longCount}");
        sb.AppendLine($"SHORT: {shortCount}");
        sb.AppendLine($"Tradable: {tradable}");
        sb.AppendLine($"Pending trades: {pending}");
        sb.AppendLine($"Skipped/Cooldown: {skipped}");
        sb.AppendLine($"Average confidence: {avgConfidence}%");
        sb.AppendLine();

        if (signals.Count == 0)
        {
            sb.AppendLine("Bugun hele DB-ye forex analiz yazilmayib.");
            return sb.ToString();
        }

        sb.AppendLine("Last saved signals:");
        sb.AppendLine();

        foreach (var signal in signals.Take(10))
        {
            sb.AppendLine($"{signal.CreatedAtUtc:HH:mm:ss} UTC | {signal.Symbol} {signal.Direction} | {signal.Status}");
            sb.AppendLine($"Confidence: {signal.Confidence}% | Grade: {signal.Grade}");

            if (signal.Direction != "WAIT")
            {
                sb.AppendLine($"Entry: {signal.EntryPrice}");
                sb.AppendLine($"SL: {signal.StopLoss}");
                sb.AppendLine($"TP1: {signal.TakeProfit1}");
                sb.AppendLine($"TP2: {signal.TakeProfit2}");
            }

            sb.AppendLine($"Message: {signal.Message}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string GetStatus(
        ForexTradeSignal signal,
        bool telegramSent,
        string notificationMessage)
    {
        if (signal.Direction == "WAIT")
            return "WAIT";

        if (telegramSent)
            return "PENDING";

        if (notificationMessage.Contains("Cooldown", StringComparison.OrdinalIgnoreCase))
            return "COOLDOWN";

        return "SKIPPED";
    }
}
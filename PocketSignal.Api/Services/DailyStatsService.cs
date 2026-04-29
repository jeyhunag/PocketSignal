using PocketSignal.Api.Models;

namespace PocketSignal.Api.Services;

public class DailyStatsService : IDailyStatsService
{
    private readonly object _lock = new();

    private DailySignalStats _todayStats = CreateNewStats();

    public void RecordCheck(
        SmartTradeSignal signal,
        bool telegramSent,
        string notificationMessage)
    {
        lock (_lock)
        {
            EnsureToday();

            _todayStats.TotalChecks++;

            if (signal.Direction == "WAIT")
            {
                _todayStats.WaitCount++;
            }
            else
            {
                _todayStats.SignalCount++;

                if (signal.Direction == "LONG")
                    _todayStats.LongSignalCount++;

                if (signal.Direction == "SHORT")
                    _todayStats.ShortSignalCount++;
            }

            if (telegramSent)
            {
                _todayStats.TelegramSentCount++;
                _todayStats.LastTelegramSentAtUtc = DateTime.UtcNow;
            }
            else
            {
                _todayStats.TelegramSkippedCount++;
            }

            _todayStats.LastSymbol = signal.Symbol;
            _todayStats.LastDirection = signal.Direction;
            _todayStats.LastConfidence = signal.Confidence;
            _todayStats.LastGrade = signal.Grade;
            _todayStats.LastSignalMessage = signal.Message;
            _todayStats.LastNotificationMessage = notificationMessage;
            _todayStats.LastCheckedAtUtc = DateTime.UtcNow;
        }
    }

    public DailySignalStats GetToday()
    {
        lock (_lock)
        {
            EnsureToday();

            return new DailySignalStats
            {
                Date = _todayStats.Date,
                TotalChecks = _todayStats.TotalChecks,
                WaitCount = _todayStats.WaitCount,
                SignalCount = _todayStats.SignalCount,
                LongSignalCount = _todayStats.LongSignalCount,
                ShortSignalCount = _todayStats.ShortSignalCount,
                TelegramSentCount = _todayStats.TelegramSentCount,
                TelegramSkippedCount = _todayStats.TelegramSkippedCount,
                LastSymbol = _todayStats.LastSymbol,
                LastDirection = _todayStats.LastDirection,
                LastConfidence = _todayStats.LastConfidence,
                LastGrade = _todayStats.LastGrade,
                LastSignalMessage = _todayStats.LastSignalMessage,
                LastNotificationMessage = _todayStats.LastNotificationMessage,
                LastCheckedAtUtc = _todayStats.LastCheckedAtUtc,
                LastTelegramSentAtUtc = _todayStats.LastTelegramSentAtUtc
            };
        }
    }

    private void EnsureToday()
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");

        if (_todayStats.Date != today)
        {
            _todayStats = CreateNewStats();
        }
    }

    private static DailySignalStats CreateNewStats()
    {
        return new DailySignalStats
        {
            Date = DateTime.Now.ToString("yyyy-MM-dd")
        };
    }
}
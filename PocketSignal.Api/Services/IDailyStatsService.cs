using PocketSignal.Api.Models;

namespace PocketSignal.Api.Services;

public interface IDailyStatsService
{
    void RecordCheck(
        SmartTradeSignal signal,
        bool telegramSent,
        string notificationMessage);

    DailySignalStats GetToday();
}
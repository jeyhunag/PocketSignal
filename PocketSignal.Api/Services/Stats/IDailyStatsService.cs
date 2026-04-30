using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Models.Stats;

namespace PocketSignal.Api.Services.Stats;

public interface IDailyStatsService
{
    void RecordCheck(
        SmartTradeSignal signal,
        bool telegramSent,
        string notificationMessage);

    DailySignalStats GetToday();
}
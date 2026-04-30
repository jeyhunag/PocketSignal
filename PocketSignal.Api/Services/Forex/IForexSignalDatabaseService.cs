using PocketSignal.Api.Data.Entities;
using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services;

public interface IForexSignalDatabaseService
{
    Task<Guid> SaveSignalAsync(
        ForexTradeSignal signal,
        bool telegramSent,
        string notificationMessage,
        CancellationToken cancellationToken = default);

    Task<List<ForexSignalEntity>> GetLatestSignalsAsync(
        int count = 50,
        CancellationToken cancellationToken = default);

    Task<string> GetTodayStatusAsync(
        CancellationToken cancellationToken = default);
}
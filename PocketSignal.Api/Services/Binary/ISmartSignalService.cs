using PocketSignal.Api.Models.Binary;

namespace PocketSignal.Api.Services.Binary;

public interface ISmartSignalService
{
    Task<SmartTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}
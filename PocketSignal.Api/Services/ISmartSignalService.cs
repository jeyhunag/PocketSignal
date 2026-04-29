using PocketSignal.Api.Models;

namespace PocketSignal.Api.Services;

public interface ISmartSignalService
{
    Task<SmartTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default);
}
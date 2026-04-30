using PocketSignal.Api.Models.Forex;

namespace PocketSignal.Api.Services.Forex;

public interface IForexChartImageService
{
    Task<string?> GenerateSignalChartAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken = default);
}
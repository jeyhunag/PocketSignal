using PocketSignal.Api.Models.Binary;

namespace PocketSignal.Api.Services.Binary;

public interface IBinaryChartImageService
{
    Task<string?> GenerateSignalChartAsync(
        SmartTradeSignal signal,
        CancellationToken cancellationToken = default);
}
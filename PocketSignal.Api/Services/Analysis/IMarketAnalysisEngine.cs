using PocketSignal.Api.Models.Analysis;

namespace PocketSignal.Api.Services.Analysis;

public interface IMarketAnalysisEngine
{
    CoreMarketAnalysisResult Analyze(
        string symbol,
        IReadOnlyList<PriceCandle> m15Candles,
        IReadOnlyList<PriceCandle> m5Candles,
        IReadOnlyList<PriceCandle> m1Candles);
}
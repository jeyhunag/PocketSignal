namespace PocketSignal.Api.Models.Analysis;

public class TimeframeAnalysis
{
    public string Timeframe { get; set; } = "";

    public MarketTrend Trend { get; set; } = MarketTrend.Range;

    public int TrendStrength { get; set; }

    public double LastClose { get; set; }

    public double LastSwingHigh { get; set; }

    public double LastSwingLow { get; set; }

    public List<MarketZone> Zones { get; set; } = new();

    public List<string> Notes { get; set; } = new();
}
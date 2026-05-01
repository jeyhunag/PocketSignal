namespace PocketSignal.Api.Models.Analysis;

public class CoreMarketAnalysisResult
{
    public string Symbol { get; set; } = "";

    public TimeframeAnalysis M15 { get; set; } = new();

    public TimeframeAnalysis M5 { get; set; } = new();

    public TimeframeAnalysis M1 { get; set; } = new();

    public TradeDirection Direction { get; set; } = TradeDirection.Wait;

    public int Confidence { get; set; }

    public string Grade { get; set; } = "NO_TRADE";

    public int LongScore { get; set; }

    public int ShortScore { get; set; }

    public int ScoreGap { get; set; }

    public bool IsBlocked { get; set; }

    public string BlockReason { get; set; } = "";

    public int SuggestedExpiryMinutes { get; set; }

    public double EntryPrice { get; set; }

    public double InvalidPrice { get; set; }

    public List<string> Reasons { get; set; } = new();

    public List<string> BlockReasons { get; set; } = new();
}
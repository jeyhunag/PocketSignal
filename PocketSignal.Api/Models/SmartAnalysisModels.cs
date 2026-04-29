namespace PocketSignal.Api.Models;

public enum SwingKind
{
    High,
    Low
}

public class SwingPoint
{
    public int Index { get; set; }
    public DateTime Time { get; set; }
    public decimal Price { get; set; }
    public SwingKind Kind { get; set; }
}

public class PriceZone
{
    public string Type { get; set; } = string.Empty; // FVG, OrderBlock
    public string Direction { get; set; } = string.Empty; // LONG, SHORT
    public DateTime Time { get; set; }
    public decimal Low { get; set; }
    public decimal High { get; set; }

    public bool Contains(decimal price, decimal tolerance)
    {
        return price >= Low - tolerance && price <= High + tolerance;
    }
}

public class DirectionScore
{
    public string Direction { get; set; } = "WAIT";
    public int Score { get; set; }

    public bool IsM15Aligned { get; set; }
    public bool IsM5Aligned { get; set; }

    public bool HasM5Zone { get; set; }
    public bool HasLiquiditySweep { get; set; }
    public bool HasChoch { get; set; }
    public bool HasPriceAction { get; set; }
    public bool IsVolatilityNormal { get; set; }
    public bool IsEntryClean { get; set; }

    public string InvalidIf { get; set; } = string.Empty;
    public List<string> Reasons { get; set; } = new();
}

public class SideAnalysis
{
    public string Direction { get; set; } = "WAIT";
    public int Score { get; set; }
    public List<string> Reasons { get; set; } = new();
}
namespace PocketSignal.Api.Models.Analysis;

public class MarketZone
{
    public string Type { get; set; } = "UNKNOWN";
    // SUPPORT, RESISTANCE, BULLISH_FVG, BEARISH_FVG, DEMAND, SUPPLY

    public double Low { get; set; }

    public double High { get; set; }

    public string Timeframe { get; set; } = "";

    public int Strength { get; set; }

    public bool Contains(double price)
    {
        return price >= Low && price <= High;
    }

    public double DistanceTo(double price)
    {
        if (Contains(price))
            return 0;

        if (price < Low)
            return Low - price;

        return price - High;
    }
}
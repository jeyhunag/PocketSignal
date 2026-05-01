namespace PocketSignal.Api.Models.Analysis;

public class PriceCandle
{
    public DateTime TimeUtc { get; set; }

    public double Open { get; set; }

    public double High { get; set; }

    public double Low { get; set; }

    public double Close { get; set; }

    public double Volume { get; set; }

    public bool IsBullish => Close > Open;

    public bool IsBearish => Close < Open;

    public double Body => Math.Abs(Close - Open);

    public double Range => High - Low;

    public double UpperWick => High - Math.Max(Open, Close);

    public double LowerWick => Math.Min(Open, Close) - Low;
}
namespace PocketSignal.Api.Models.Admin;

public class AdminRuntimeSettings
{
    public bool BinaryEnabled { get; set; } = true;
    public string BinaryActiveSymbol { get; set; } = "EUR/USD";
    public List<string> BinarySymbols { get; set; } = new()
    {
        "EUR/USD",
        "GBP/USD",
        "USD/JPY",
        "EUR/GBP",
        "GBP/JPY",
        "AUD/USD",
        "USD/CAD",
        "EUR/JPY"
    };

    public bool ForexEnabled { get; set; } = true;
    public string ForexActiveSymbol { get; set; } = "GBP/JPY";
    public List<string> ForexSymbols { get; set; } = new()
    {
        "GBP/JPY",
        "EUR/USD",
        "USD/JPY",
        "EUR/GBP",
        "GBP/USD",
        "BTC/USD",
        "ETH/USD",
        "XAU/USD",
        "USOIL"
    };

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
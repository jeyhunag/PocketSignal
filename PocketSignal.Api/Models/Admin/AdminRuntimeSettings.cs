namespace PocketSignal.Api.Models.Admin;

public class AdminRuntimeSettings
{
    public bool BinaryEnabled { get; set; } = true;

    public string BinaryActiveSymbol { get; set; } = "EUR/USD";

    // Çoxlu seçim (forex kimi) — birdən çox binary cütü.
    public List<string> BinaryActiveSymbols { get; set; } = new();

    public List<string> BinarySymbols { get; set; } = new()
    {
        "EUR/USD",
        "GBP/USD",
        "USD/JPY",
        "EUR/GBP",
        "GBP/JPY",
        "AUD/USD",
        "USD/CAD",
        "EUR/JPY",
        "AUD/JPY",
        "EUR/CAD",
        "CAD/CHF",
        "USD/CHF",
        "CHF/JPY",
        "AUD/CHF",
        "GBP/AUD",
        "EUR/AUD",
        "CAD/JPY",
        "AUD/CAD"
    };

    public bool ForexEnabled { get; set; } = true;

    // Cassandra analiz timeframe-i: "1min", "5min", "15min".
    public string ForexTimeframe { get; set; } = "15min";

    // Binary Cassandra timeframe-i.
    public string BinaryTimeframe { get; set; } = "15min";

    // Köhnə sistemlə uyğunluq üçün qalır.
    public string ForexActiveSymbol { get; set; } = "GBP/JPY";

    // Yeni sistem: bir və ya çox Forex/Gold/Crypto symbol aktiv ola bilər.
    public List<string> ForexActiveSymbols { get; set; } = new()
    {
        "GBP/JPY"
    };

    public List<string> ForexSymbols { get; set; } = new()
    {
        "GBP/JPY",
        "EUR/USD",
        "USD/JPY",
        "EUR/GBP",
        "GBP/USD",
        "AUD/USD",
        "USD/CAD",
        "EUR/JPY",
        "AUD/JPY",
        "EUR/CAD",
        "CAD/CHF",
        "USD/CHF",
        "CHF/JPY",
        "AUD/CHF",
        "GBP/AUD",
        "EUR/AUD",
        "CAD/JPY",
        "AUD/CAD",
        "BTC/USD",
        "ETH/USD",
        "XAU/USD",
        "USOIL"
    };

    public bool Mt5AutoTradeEnabled { get; set; } = false;

    public double Mt5LotSize { get; set; } = 0.02;

    public string Mt5TakeProfitMode { get; set; } = "TP1";

    public int Mt5MinimumConfidence { get; set; } = 82;

    public string Mt5MinimumGrade { get; set; } = "B";

    public int Mt5CooldownMinutes { get; set; } = 60;

    public int Mt5MaxPendingMinutes { get; set; } = 10;

    public int Mt5MaxTradesPerDay { get; set; } = 5;

    public bool Mt5DemoOnly { get; set; } = true;

    public bool Mt5OnePositionPerSymbol { get; set; } = true;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
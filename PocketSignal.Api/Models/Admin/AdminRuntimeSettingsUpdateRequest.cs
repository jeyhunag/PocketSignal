namespace PocketSignal.Api.Models.Admin;

public class AdminRuntimeSettingsUpdateRequest
{
    public bool BinaryEnabled { get; set; }

    public string BinaryActiveSymbol { get; set; } = "EUR/USD";

    public bool ForexEnabled { get; set; }

    // Köhnə uyğunluq üçün qalır.
    public string ForexActiveSymbol { get; set; } = "GBP/JPY";

    // Yeni multi-select forex symbols.
    public List<string> ForexActiveSymbols { get; set; } = new();

    public bool Mt5AutoTradeEnabled { get; set; }

    public double Mt5LotSize { get; set; } = 0.02;

    public string Mt5TakeProfitMode { get; set; } = "TP1";

    public int Mt5MinimumConfidence { get; set; } = 82;

    public string Mt5MinimumGrade { get; set; } = "B";

    public int Mt5CooldownMinutes { get; set; } = 60;

    public int Mt5MaxPendingMinutes { get; set; } = 10;

    public int Mt5MaxTradesPerDay { get; set; } = 5;

    public bool Mt5DemoOnly { get; set; } = true;

    public bool Mt5OnePositionPerSymbol { get; set; } = true;
}
namespace PocketSignal.Api.Models.Admin;

public class AdminRuntimeSettingsUpdateRequest
{
    public bool BinaryEnabled { get; set; }
    public string BinaryActiveSymbol { get; set; } = "EUR/USD";

    public bool ForexEnabled { get; set; }
    public string ForexActiveSymbol { get; set; } = "GBP/JPY";
}
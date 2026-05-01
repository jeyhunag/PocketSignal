namespace PocketSignal.Api.Services.Mt5;

public class Mt5AutoTradeEnqueueResult
{
    public bool Added { get; set; }

    public string Message { get; set; } = string.Empty;

    public Mt5AutoTradeOrder? Order { get; set; }

    public static Mt5AutoTradeEnqueueResult Success(
        string message,
        Mt5AutoTradeOrder order)
    {
        return new Mt5AutoTradeEnqueueResult
        {
            Added = true,
            Message = message,
            Order = order
        };
    }

    public static Mt5AutoTradeEnqueueResult Skipped(string message)
    {
        return new Mt5AutoTradeEnqueueResult
        {
            Added = false,
            Message = message,
            Order = null
        };
    }
}
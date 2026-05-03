namespace PocketSignal.Api.Services.MarketData;

public static class MarketDataApiGroupContext
{
    private static readonly AsyncLocal<string?> CurrentGroup = new();

    public static string Group
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CurrentGroup.Value))
                return "Default";

            return CurrentGroup.Value;
        }
    }

    public static IDisposable Use(string group)
    {
        var previous = CurrentGroup.Value;

        CurrentGroup.Value = string.IsNullOrWhiteSpace(group)
            ? "Default"
            : group.Trim();

        return new RestoreScope(previous);
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public RestoreScope(string? previous)
        {
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            CurrentGroup.Value = _previous;
            _disposed = true;
        }
    }
}
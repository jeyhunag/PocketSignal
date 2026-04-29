using PocketSignal.Api.Models;

namespace PocketSignal.Api.Services;

public interface ISignalResultTracker
{
    SignalTradeRecord? RegisterSignal(SmartTradeSignal signal);

    Task EvaluateDueSignalsAsync(CancellationToken cancellationToken = default);

    List<SignalTradeRecord> GetTodayTrades();

    string GetTodayStatus();
}
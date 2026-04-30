using PocketSignal.Api.Models.Binary;

namespace PocketSignal.Api.Services.Binary;

public interface ISignalResultTracker
{
    SignalTradeRecord? RegisterSignal(SmartTradeSignal signal);

    Task EvaluateDueSignalsAsync(CancellationToken cancellationToken = default);

    List<SignalTradeRecord> GetTodayTrades();

    string GetTodayStatus();
}
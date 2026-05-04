using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Binary;

public class BinaryDailyResultSummaryService : IBinaryDailyResultSummaryService
{
    private readonly ITelegramService _telegramService;
    private readonly object _lock = new();

    private DateTime _date = DateTime.Now.Date;
    private int _total;
    private int _win;
    private int _lose;
    private int _lastSummarySentAt;

    public BinaryDailyResultSummaryService(
        ITelegramService telegramService)
    {
        _telegramService = telegramService;
    }

    public async Task RecordResultAsync(
        bool isWin,
        CancellationToken cancellationToken = default)
    {
        BinaryDailyResultSummary? summaryToSend = null;

        lock (_lock)
        {
            ResetIfNewDay();

            _total++;

            if (isWin)
                _win++;
            else
                _lose++;

            if (_total >= 10 &&
                _total % 10 == 0 &&
                _lastSummarySentAt != _total)
            {
                _lastSummarySentAt = _total;

                summaryToSend = new BinaryDailyResultSummary
                {
                    Date = _date,
                    Total = _total,
                    Win = _win,
                    Lose = _lose
                };
            }
        }

        if (summaryToSend != null)
        {
            var message =
                "📊 Binary Daily Summary\n\n" +
                $"Total: {summaryToSend.Total}\n" +
                $"✅ Win: {summaryToSend.Win}\n" +
                $"❌ Lose: {summaryToSend.Lose}\n" +
                $"Win rate: {summaryToSend.WinRate}%";

            await _telegramService.SendMessageAsync(
                message,
                cancellationToken);
        }
    }

    public BinaryDailyResultSummary GetToday()
    {
        lock (_lock)
        {
            ResetIfNewDay();

            return new BinaryDailyResultSummary
            {
                Date = _date,
                Total = _total,
                Win = _win,
                Lose = _lose
            };
        }
    }

    private void ResetIfNewDay()
    {
        var today = DateTime.Now.Date;

        if (_date == today)
            return;

        _date = today;
        _total = 0;
        _win = 0;
        _lose = 0;
        _lastSummarySentAt = 0;
    }
}
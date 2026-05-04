namespace PocketSignal.Api.Services.Binary;

public interface IBinaryDailyResultSummaryService
{
    Task RecordResultAsync(
        bool isWin,
        CancellationToken cancellationToken = default);

    BinaryDailyResultSummary GetToday();
}

public class BinaryDailyResultSummary
{
    public DateTime Date { get; set; }

    public int Total { get; set; }

    public int Win { get; set; }

    public int Lose { get; set; }

    public decimal WinRate =>
        Total <= 0
            ? 0
            : Math.Round((decimal)Win / Total * 100m, 1);
}
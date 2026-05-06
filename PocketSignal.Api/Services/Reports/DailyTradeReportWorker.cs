using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Services.Binary;
using PocketSignal.Api.Services.Forex;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Services.Reports;

public class DailyTradeReportWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<DailyTradeReportWorker> _logger;

    private readonly string _stateFilePath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public DailyTradeReportWorker(
        IServiceScopeFactory scopeFactory,
        IWebHostEnvironment environment,
        ILogger<DailyTradeReportWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _environment = environment;
        _logger = logger;

        _stateFilePath = Path.Combine(
            _environment.ContentRootPath,
            "daily-report-state.json");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DailyTradeReportWorker basladi.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var delay = GetDelayUntilNextAzerbaijanMidnight();

                _logger.LogInformation(
                    "Novbeti gunluk hesabat ucun gozleme vaxti: {Delay}",
                    delay);

                await Task.Delay(delay, stoppingToken);

                await SendDailyReportsAsync(stoppingToken);

                await Task.Delay(
                    TimeSpan.FromMinutes(1),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "DailyTradeReportWorker xetasi bas verdi.");

                await Task.Delay(
                    TimeSpan.FromMinutes(5),
                    stoppingToken);
            }
        }
    }

    private async Task SendDailyReportsAsync(CancellationToken cancellationToken)
    {
        var azTimeZone = GetAzerbaijanTimeZone();
        var nowAz = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, azTimeZone);

        // 00:00-da hesabat yeni bitmiş gün üçün gedir.
        var reportDateAz = nowAz.Date.AddDays(-1);
        var reportDateText = reportDateAz.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var state = await LoadStateAsync(cancellationToken);

        using var scope = _scopeFactory.CreateScope();

        var telegramService =
            scope.ServiceProvider.GetRequiredService<ITelegramService>();

        var binaryResultTracker =
            scope.ServiceProvider.GetRequiredService<ISignalResultTracker>();

        var forexTradeResultTracker =
            scope.ServiceProvider.GetRequiredService<IForexTradeResultTracker>();

        var dbContext =
            scope.ServiceProvider.GetRequiredService<PocketSignalDbContext>();

        await binaryResultTracker.EvaluateDueSignalsAsync(cancellationToken);
        await forexTradeResultTracker.EvaluateOpenTradesAsync(cancellationToken);

        if (state.LastBinaryReportDateAz != reportDateText)
        {
            var binaryMessage = BuildBinaryDailyReport(
                binaryResultTracker,
                reportDateAz);

            await telegramService.SendMessageAsync(
                binaryMessage,
                cancellationToken);

            state.LastBinaryReportDateAz = reportDateText;

            await SaveStateAsync(state, cancellationToken);

            _logger.LogInformation(
                "Binary gunluk hesabat Telegram-a gonderildi. Date AZ: {Date}",
                reportDateText);
        }

        if (state.LastForexReportDateAz != reportDateText)
        {
            var forexMessage = await BuildForexDailyReportAsync(
                dbContext,
                reportDateAz,
                azTimeZone,
                cancellationToken);

            await telegramService.SendMessageAsync(
                forexMessage,
                cancellationToken);

            state.LastForexReportDateAz = reportDateText;

            await SaveStateAsync(state, cancellationToken);

            _logger.LogInformation(
                "Forex gunluk hesabat Telegram-a gonderildi. Date AZ: {Date}",
                reportDateText);
        }
    }

    private static string BuildBinaryDailyReport(
        ISignalResultTracker binaryResultTracker,
        DateTime reportDateAz)
    {
        var trades = binaryResultTracker.GetTradesByAzerbaijanDate(reportDateAz);

        var total = trades.Count;
        var pending = trades.Count(x => x.Result == "PENDING");
        var wins = trades.Count(x => x.Result == "WIN");
        var losses = trades.Count(x => x.Result == "LOSS");
        var draws = trades.Count(x => x.Result == "DRAW");

        var completed = wins + losses;

        var winRate = completed > 0
            ? Math.Round((decimal)wins / completed * 100m, 1)
            : 0;

        var sb = new StringBuilder();

        sb.AppendLine("📊 Binary günlük hesabat");
        sb.AppendLine();
        sb.AppendLine($"Tarix: {reportDateAz:yyyy-MM-dd} AZT");
        sb.AppendLine();
        sb.AppendLine($"Cəmi əməliyyat: {total}");
        sb.AppendLine($"✅ Win: {wins}");
        sb.AppendLine($"❌ Lose: {losses}");
        sb.AppendLine($"➖ Draw: {draws}");
        sb.AppendLine($"⏳ Pending: {pending}");
        sb.AppendLine($"Win rate: {winRate}%");

        var bySymbol = trades
            .GroupBy(x => x.Symbol)
            .OrderByDescending(x => x.Count())
            .Take(10)
            .ToList();

        if (bySymbol.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Symbol üzrə:");

            foreach (var group in bySymbol)
            {
                var symbolWins = group.Count(x => x.Result == "WIN");
                var symbolLosses = group.Count(x => x.Result == "LOSS");
                var symbolDraws = group.Count(x => x.Result == "DRAW");
                var symbolPending = group.Count(x => x.Result == "PENDING");

                sb.AppendLine(
                    $"{group.Key}: Total {group.Count()} | W:{symbolWins} L:{symbolLosses} D:{symbolDraws} P:{symbolPending}");
            }
        }

        return sb.ToString();
    }

    private static async Task<string> BuildForexDailyReportAsync(
        PocketSignalDbContext dbContext,
        DateTime reportDateAz,
        TimeZoneInfo azTimeZone,
        CancellationToken cancellationToken)
    {
        var startAz = reportDateAz.Date;
        var endAz = startAz.AddDays(1);

        var startUtc = TimeZoneInfo.ConvertTimeToUtc(startAz, azTimeZone);
        var endUtc = TimeZoneInfo.ConvertTimeToUtc(endAz, azTimeZone);

        var trades = await dbContext.ForexTradeResults
            .AsNoTracking()
            .Where(x =>
                x.CreatedAtUtc >= startUtc &&
                x.CreatedAtUtc < endUtc)
            .OrderByDescending(x => x.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var total = trades.Count;

        var wins = trades.Count(x =>
            x.Result == "WIN" ||
            x.Result == "WIN_TP2" ||
            x.IsTp1Hit ||
            x.IsTp2Hit);

        var losses = trades.Count(x =>
            (x.Result == "LOSS" || x.IsStopLossHit) &&
            x.Result != "WIN" &&
            x.Result != "WIN_TP2" &&
            !x.IsTp1Hit &&
            !x.IsTp2Hit);

        var pending = trades.Count(x => x.Result == "PENDING");

        var expired = trades.Count(x => x.Result == "EXPIRED");
        var ambiguous = trades.Count(x => x.Result == "AMBIGUOUS");

        var other = total - wins - losses - pending - expired - ambiguous;

        if (other < 0)
            other = 0;

        var completed = wins + losses;

        var winRate = completed > 0
            ? Math.Round((decimal)wins / completed * 100m, 1)
            : 0;

        var sb = new StringBuilder();

        sb.AppendLine("📊 Forex günlük hesabat");
        sb.AppendLine();
        sb.AppendLine($"Tarix: {reportDateAz:yyyy-MM-dd} AZT");
        sb.AppendLine();
        sb.AppendLine($"Cəmi əməliyyat: {total}");
        sb.AppendLine($"✅ Win: {wins}");
        sb.AppendLine($"❌ Lose: {losses}");
        sb.AppendLine($"⏳ Pending: {pending}");
        sb.AppendLine($"⌛ Expired: {expired}");
        sb.AppendLine($"⚠️ Ambiguous: {ambiguous}");

        if (other > 0)
            sb.AppendLine($"ℹ️ Other: {other}");

        sb.AppendLine($"Win rate: {winRate}%");

        var bySymbol = trades
            .GroupBy(x => x.Symbol)
            .OrderByDescending(x => x.Count())
            .Take(10)
            .ToList();

        if (bySymbol.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Symbol üzrə:");

            foreach (var group in bySymbol)
            {
                var symbolWins = group.Count(x =>
                    x.Result == "WIN" ||
                    x.Result == "WIN_TP2" ||
                    x.IsTp1Hit ||
                    x.IsTp2Hit);

                var symbolLosses = group.Count(x =>
                    (x.Result == "LOSS" || x.IsStopLossHit) &&
                    x.Result != "WIN" &&
                    x.Result != "WIN_TP2" &&
                    !x.IsTp1Hit &&
                    !x.IsTp2Hit);

                var symbolPending = group.Count(x => x.Result == "PENDING");

                sb.AppendLine(
                    $"{group.Key}: Total {group.Count()} | W:{symbolWins} L:{symbolLosses} P:{symbolPending}");
            }
        }

        return sb.ToString();
    }

    private TimeSpan GetDelayUntilNextAzerbaijanMidnight()
    {
        var azTimeZone = GetAzerbaijanTimeZone();

        var nowUtc = DateTime.UtcNow;
        var nowAz = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, azTimeZone);

        // 00:00-dan 20 saniyə sonra göndəririk ki, tarix tam dəyişsin.
        var nextMidnightAz = nowAz.Date.AddDays(1).AddSeconds(20);

        var nextMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(
            nextMidnightAz,
            azTimeZone);

        var delay = nextMidnightUtc - nowUtc;

        if (delay < TimeSpan.FromSeconds(10))
            delay = TimeSpan.FromSeconds(10);

        return delay;
    }

    private async Task<DailyReportState> LoadStateAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_stateFilePath))
            return new DailyReportState();

        try
        {
            var json = await File.ReadAllTextAsync(
                _stateFilePath,
                cancellationToken);

            var state = JsonSerializer.Deserialize<DailyReportState>(
                json,
                JsonOptions);

            return state ?? new DailyReportState();
        }
        catch
        {
            return new DailyReportState();
        }
    }

    private async Task SaveStateAsync(
        DailyReportState state,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(
            state,
            JsonOptions);

        await File.WriteAllTextAsync(
            _stateFilePath,
            json,
            cancellationToken);
    }

    private static TimeZoneInfo GetAzerbaijanTimeZone()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Azerbaijan Standard Time");
        }
        catch
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById("Asia/Baku");
            }
            catch
            {
                return TimeZoneInfo.CreateCustomTimeZone(
                    "Azerbaijan Time",
                    TimeSpan.FromHours(4),
                    "Azerbaijan Time",
                    "Azerbaijan Time");
            }
        }
    }

    private sealed class DailyReportState
    {
        public string? LastBinaryReportDateAz { get; set; }

        public string? LastForexReportDateAz { get; set; }
    }
}
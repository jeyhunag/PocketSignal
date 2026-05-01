using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.Admin;

namespace PocketSignal.Api.Services.Mt5;

public class Mt5AutoTradeQueueService : IMt5AutoTradeQueueService
{
    private readonly object _lock = new();
    private readonly List<Mt5AutoTradeOrder> _orders = new();

    private readonly IConfiguration _configuration;
    private readonly IAdminRuntimeSettingsService _settingsService;
    private readonly ILogger<Mt5AutoTradeQueueService> _logger;

    public Mt5AutoTradeQueueService(
        IConfiguration configuration,
        IAdminRuntimeSettingsService settingsService,
        ILogger<Mt5AutoTradeQueueService> logger)
    {
        _configuration = configuration;
        _settingsService = settingsService;
        _logger = logger;
    }

    public async Task<Mt5AutoTradeEnqueueResult> EnqueueAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken = default)
    {
        var settings = await _settingsService.GetAsync(cancellationToken);

        var appsettingsEnabled = _configuration.GetValue<bool>(
            "Mt5AutoTrade:Enabled",
            true);

        if (!appsettingsEnabled)
        {
            return Mt5AutoTradeEnqueueResult.Skipped(
                "MT5 AutoTrade appsettings-de deaktivdir.");
        }

        if (!settings.Mt5AutoTradeEnabled)
        {
            return Mt5AutoTradeEnqueueResult.Skipped(
                "MT5 AutoTrade admin panelde deaktivdir.");
        }

        if (signal.Direction != "LONG" && signal.Direction != "SHORT")
        {
            return Mt5AutoTradeEnqueueResult.Skipped(
                "MT5 AutoTrade ucun direction LONG/SHORT deyil.");
        }

        if (signal.Confidence < settings.Mt5MinimumConfidence)
        {
            return Mt5AutoTradeEnqueueResult.Skipped(
                $"MT5 AutoTrade skip: confidence {signal.Confidence}% minimum {settings.Mt5MinimumConfidence}%-den asagidir.");
        }

        if (GradeRank(signal.Grade) < GradeRank(settings.Mt5MinimumGrade))
        {
            return Mt5AutoTradeEnqueueResult.Skipped(
                $"MT5 AutoTrade skip: grade {signal.Grade} minimum {settings.Mt5MinimumGrade} seviyyesinden asagidir.");
        }

        if (!IsTradePlanValid(signal))
        {
            return Mt5AutoTradeEnqueueResult.Skipped(
                "MT5 AutoTrade skip: trade plan duzgun deyil.");
        }

        lock (_lock)
        {
            CleanupOldOrders();
            ExpirePendingOrders(settings.Mt5MaxPendingMinutes); 

            var todayUtc = DateTime.UtcNow.Date;

            var todayTradeCount = _orders.Count(x =>
                x.CreatedAtUtc.Date == todayUtc &&
                IsCountedForDailyLimit(x.Status));

            if (todayTradeCount >= settings.Mt5MaxTradesPerDay)
            {
                return Mt5AutoTradeEnqueueResult.Skipped(
                    $"MT5 AutoTrade skip: gunluk max trade limiti dolub. Limit: {settings.Mt5MaxTradesPerDay}");
            }

            if (settings.Mt5CooldownMinutes > 0)
            {
                var sinceUtc = DateTime.UtcNow.AddMinutes(-settings.Mt5CooldownMinutes);

                var duplicate = _orders.Any(x =>
                    x.CreatedAtUtc >= sinceUtc &&
                    x.Symbol == signal.Symbol &&
                    x.Direction == signal.Direction &&
                    IsActiveForCooldown(x.Status));

                if (duplicate)
                {
                    return Mt5AutoTradeEnqueueResult.Skipped(
                        $"MT5 AutoTrade cooldown aktivdir. {settings.Mt5CooldownMinutes} deqiqe erzinde eyni symbol/direction order artiq var.");
                }
            }

            var order = new Mt5AutoTradeOrder
            {
                Id = Guid.NewGuid(),
                Symbol = signal.Symbol,
                Direction = signal.Direction,
                EntryPrice = signal.EntryPrice,
                StopLoss = signal.StopLoss,
                TakeProfit1 = signal.TakeProfit1,
                TakeProfit2 = signal.TakeProfit2,
                LotSize = Convert.ToDecimal(settings.Mt5LotSize),
                TakeProfitMode = settings.Mt5TakeProfitMode,
                Confidence = signal.Confidence,
                Grade = signal.Grade,
                Status = "PENDING",
                CreatedAtUtc = DateTime.UtcNow
            };

            _orders.Add(order);

            _logger.LogInformation(
                "MT5 AutoTrade order queue-ya elave edildi. Id: {Id} | {Symbol} {Direction} | Lot: {Lot} | TP Mode: {TpMode}",
                order.Id,
                order.Symbol,
                order.Direction,
                order.LotSize,
                order.TakeProfitMode);

            return Mt5AutoTradeEnqueueResult.Success(
                "MT5 order queue-ya elave edildi.",
                order);
        }
    }

    public async Task<Mt5AutoTradeOrder?> GetNextOrderAsync(
        string eaKey,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidEaKey(eaKey))
            return null;

        var settings = await _settingsService.GetAsync(cancellationToken);

        lock (_lock)
        {
            CleanupOldOrders();
            ExpirePendingOrders(settings.Mt5MaxPendingMinutes);

            var order = _orders
                .Where(x => x.Status == "PENDING")
                .OrderBy(x => x.CreatedAtUtc)
                .FirstOrDefault();

            if (order == null)
                return null;

            order.Status = "SENT_TO_MT5";
            order.SentToMt5AtUtc = DateTime.UtcNow;

            return order;
        }
    }

    public Task<bool> MarkExecutedAsync(
        string eaKey,
        Guid id,
        string ticket,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidEaKey(eaKey))
            return Task.FromResult(false);

        lock (_lock)
        {
            var order = _orders.FirstOrDefault(x => x.Id == id);

            if (order == null)
                return Task.FromResult(false);

            order.Status = "EXECUTED";
            order.ExecutedAtUtc = DateTime.UtcNow;
            order.Mt5Ticket = ticket;
            order.Error = null;

            _logger.LogInformation(
                "MT5 order EXECUTED. Id: {Id} | Ticket: {Ticket}",
                id,
                ticket);

            return Task.FromResult(true);
        }
    }

    public Task<bool> MarkErrorAsync(
        string eaKey,
        Guid id,
        string error,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidEaKey(eaKey))
            return Task.FromResult(false);

        lock (_lock)
        {
            var order = _orders.FirstOrDefault(x => x.Id == id);

            if (order == null)
                return Task.FromResult(false);

            order.Status = "ERROR";
            order.ExecutedAtUtc = DateTime.UtcNow;
            order.Error = error;

            _logger.LogWarning(
                "MT5 order ERROR. Id: {Id} | Error: {Error}",
                id,
                error);

            return Task.FromResult(true);
        }
    }

    public IReadOnlyList<Mt5AutoTradeOrder> GetRecentOrders()
    {
        lock (_lock)
        {
            CleanupOldOrders();

            return _orders
                .OrderByDescending(x => x.CreatedAtUtc)
                .Take(30)
                .ToList();
        }
    }

    private bool IsValidEaKey(string eaKey)
    {
        var configuredKey = _configuration.GetValue<string>("Mt5AutoTrade:EaKey");

        if (string.IsNullOrWhiteSpace(configuredKey))
            return false;

        return string.Equals(
            eaKey,
            configuredKey,
            StringComparison.Ordinal);
    }

    private void CleanupOldOrders()
    {
        var cutoff = DateTime.UtcNow.AddDays(-2);

        _orders.RemoveAll(x => x.CreatedAtUtc < cutoff);
    }

    private void ExpirePendingOrders(int maxPendingMinutes)
    {
        if (maxPendingMinutes <= 0)
            maxPendingMinutes = 10;

        var cutoff = DateTime.UtcNow.AddMinutes(-maxPendingMinutes);

        foreach (var order in _orders.Where(x =>
                     x.Status == "PENDING" &&
                     x.CreatedAtUtc < cutoff))
        {
            order.Status = "EXPIRED";
            order.ExecutedAtUtc = DateTime.UtcNow;
            order.Error = $"Pending vaxti bitdi. MaxPendingMinutes: {maxPendingMinutes}";

            _logger.LogWarning(
                "MT5 order EXPIRED. Id: {Id} | {Symbol} {Direction} | CreatedAtUtc: {CreatedAtUtc} | MaxPendingMinutes: {MaxPendingMinutes}",
                order.Id,
                order.Symbol,
                order.Direction,
                order.CreatedAtUtc,
                maxPendingMinutes);
        }
    }

    private static bool IsCountedForDailyLimit(string? status)
    {
        return status == "PENDING" ||
               status == "SENT_TO_MT5" ||
               status == "EXECUTED";
    }

    private static bool IsActiveForCooldown(string? status)
    {
        return status == "PENDING" ||
               status == "SENT_TO_MT5" ||
               status == "EXECUTED";
    }

    private static bool IsTradePlanValid(ForexTradeSignal signal)
    {
        if (signal.EntryPrice <= 0)
            return false;

        if (signal.StopLoss <= 0)
            return false;

        if (signal.TakeProfit1 <= 0)
            return false;

        if (signal.TakeProfit2 <= 0)
            return false;

        if (signal.Direction == "LONG")
        {
            return signal.StopLoss < signal.EntryPrice &&
                   signal.TakeProfit1 > signal.EntryPrice &&
                   signal.TakeProfit2 > signal.TakeProfit1;
        }

        if (signal.Direction == "SHORT")
        {
            return signal.StopLoss > signal.EntryPrice &&
                   signal.TakeProfit1 < signal.EntryPrice &&
                   signal.TakeProfit2 < signal.TakeProfit1;
        }

        return false;
    }

    private static int GradeRank(string? grade)
    {
        grade = grade?.Trim().ToUpperInvariant();

        return grade switch
        {
            "A+" => 4,
            "A" => 3,
            "B" => 2,
            "C" => 1,
            _ => 0
        };
    }
}
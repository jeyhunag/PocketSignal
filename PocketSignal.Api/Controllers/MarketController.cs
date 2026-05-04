using Microsoft.AspNetCore.Mvc;
using PocketSignal.Api.Services.Binary;
using PocketSignal.Api.Services.MarketData;
using PocketSignal.Api.Services.Stats;
using PocketSignal.Api.Services.Telegram;

namespace PocketSignal.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MarketController : ControllerBase
{
    private readonly IMarketDataService _marketDataService;
    private readonly ISmartSignalService _smartSignalService;
    private readonly ITelegramService _telegramService;
    private readonly ISignalNotificationService _signalNotificationService;
    private readonly IDailyStatsService _dailyStatsService;
    private readonly ISignalResultTracker _signalResultTracker;

    public MarketController(
        IMarketDataService marketDataService,
        ISmartSignalService smartSignalService,
        ITelegramService telegramService,
        ISignalNotificationService signalNotificationService,
        IDailyStatsService dailyStatsService,
        ISignalResultTracker signalResultTracker)
    {
        _marketDataService = marketDataService;
        _smartSignalService = smartSignalService;
        _telegramService = telegramService;
        _signalNotificationService = signalNotificationService;
        _dailyStatsService = dailyStatsService;
        _signalResultTracker = signalResultTracker;
    }

    [HttpGet("candles")]
    public async Task<IActionResult> GetCandles(
        [FromQuery] string symbol = "EUR/USD",
        [FromQuery] string interval = "1min",
        [FromQuery] int outputSize = 100,
        CancellationToken cancellationToken = default)
    {
        using (MarketDataApiGroupContext.Use("Binary"))
        {
            var result = await _marketDataService.GetCandlesAsync(
                symbol,
                interval,
                outputSize,
                cancellationToken);

            if (result is null)
                return StatusCode(500, "Data gelmedi.");

            if (result.Status == "error")
                return BadRequest(result);

            return Ok(result);
        }
    }

    [HttpGet("signal")]
    public async Task<IActionResult> GetSignal(
        [FromQuery] string symbol = "EUR/USD",
        CancellationToken cancellationToken = default)
    {
        using (MarketDataApiGroupContext.Use("Binary"))
        {
            var signal = await _smartSignalService.AnalyzeAsync(
                symbol,
                cancellationToken);

            return Ok(signal);
        }
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(
        [FromQuery] string symbol = "EUR/USD",
        CancellationToken cancellationToken = default)
    {
        using (MarketDataApiGroupContext.Use("Binary"))
        {
            var signal = await _smartSignalService.AnalyzeAsync(
                symbol,
                cancellationToken);

            var message = SignalMessageFormatter.Format(signal);

            return Content(message, "text/plain; charset=utf-8");
        }
    }

    [HttpGet("notify")]
    public async Task<IActionResult> NotifySignal(
        [FromQuery] string symbol = "EUR/USD",
        CancellationToken cancellationToken = default)
    {
        using (MarketDataApiGroupContext.Use("Binary"))
        {
            var signal = await _smartSignalService.AnalyzeAsync(
                symbol,
                cancellationToken);

            var result = await _signalNotificationService.NotifyIfValidSignalAsync(
                signal,
                cancellationToken);

            _dailyStatsService.RecordCheck(
                signal,
                result.Sent,
                result.Message);

            return Ok(new
            {
                sent = result.Sent,
                notificationMessage = result.Message,

                symbol = signal.Symbol,
                direction = signal.Direction,
                expiryMinutes = signal.ExpiryMinutes,
                expiryReason = signal.ExpiryReason,
                confidence = signal.Confidence,
                grade = signal.Grade,
                signalMessage = signal.Message,
                reasons = signal.Reasons
            });
        }
    }

    [HttpGet("test-telegram")]
    public async Task<IActionResult> TestTelegram(
        CancellationToken cancellationToken = default)
    {
        var message =
            "✅ PocketSignal Telegram test mesajı uğurla göndərildi.\n\n" +
            $"Time UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}";

        await _telegramService.SendMessageAsync(
            message,
            cancellationToken);

        return Ok(new
        {
            sent = true,
            message = "Telegram test mesaji gonderildi."
        });
    }

    [HttpGet("stats")]
    public IActionResult GetStats()
    {
        var stats = _dailyStatsService.GetToday();

        return Ok(stats);
    }

    [HttpGet("stats-status")]
    public IActionResult GetStatsStatus()
    {
        var stats = _dailyStatsService.GetToday();

        var message = StatsMessageFormatter.Format(stats);

        return Content(message, "text/plain; charset=utf-8");
    }

    [HttpGet("signal-results")]
    public IActionResult GetSignalResults()
    {
        var trades = _signalResultTracker.GetTodayTrades();

        return Ok(trades);
    }

    [HttpGet("signal-results-status")]
    public async Task<IActionResult> GetSignalResultsStatus(
        CancellationToken cancellationToken = default)
    {
        using (MarketDataApiGroupContext.Use("Binary"))
        {
            await _signalResultTracker.EvaluateDueSignalsAsync(
                cancellationToken);

            var message = _signalResultTracker.GetTodayStatus();

            return Content(message, "text/plain; charset=utf-8");
        }
    }
}
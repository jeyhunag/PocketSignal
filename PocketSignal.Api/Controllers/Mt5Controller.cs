using Microsoft.AspNetCore.Mvc;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.Mt5;

namespace PocketSignal.Api.Controllers;

[ApiController]
[Route("api/mt5")]
public class Mt5Controller : ControllerBase
{
    private readonly IMt5AutoTradeQueueService _queueService;

    public Mt5Controller(IMt5AutoTradeQueueService queueService)
    {
        _queueService = queueService;
    }

    [HttpGet("next-order")]
    public async Task<IActionResult> GetNextOrder(
        [FromQuery] string eaKey,
        CancellationToken cancellationToken)
    {
        var order = await _queueService.GetNextOrderAsync(
            eaKey,
            cancellationToken);

        if (order == null)
            return NoContent();

        return Ok(new
        {
            id = order.Id,
            symbol = order.Symbol,
            direction = order.Direction,
            entryPrice = order.EntryPrice,
            stopLoss = order.StopLoss,
            takeProfit1 = order.TakeProfit1,
            takeProfit2 = order.TakeProfit2,
            lotSize = order.LotSize,
            takeProfitMode = order.TakeProfitMode,
            confidence = order.Confidence,
            grade = order.Grade
        });
    }

    [HttpGet("mark-executed")]
    public async Task<IActionResult> MarkExecuted(
        [FromQuery] string eaKey,
        [FromQuery] Guid id,
        [FromQuery] string ticket,
        CancellationToken cancellationToken)
    {
        var ok = await _queueService.MarkExecutedAsync(
            eaKey,
            id,
            ticket,
            cancellationToken);

        if (!ok)
            return BadRequest(new { message = "MT5 order executed kimi qeyd edilmedi." });

        return Ok(new { message = "MT5 order executed kimi qeyd edildi." });
    }

    [HttpGet("mark-error")]
    public async Task<IActionResult> MarkError(
        [FromQuery] string eaKey,
        [FromQuery] Guid id,
        [FromQuery] string error,
        CancellationToken cancellationToken)
    {
        var ok = await _queueService.MarkErrorAsync(
            eaKey,
            id,
            error,
            cancellationToken);

        if (!ok)
            return BadRequest(new { message = "MT5 order error kimi qeyd edilmedi." });

        return Ok(new { message = "MT5 order error kimi qeyd edildi." });
    }

    [HttpGet("status")]
    public IActionResult Status()
    {
        var orders = _queueService.GetRecentOrders();

        return Ok(orders);
    }

    [HttpGet("test-create")]
    public async Task<IActionResult> TestCreate(
        [FromQuery] string symbol = "GBP/JPY",
        [FromQuery] string direction = "LONG",
        CancellationToken cancellationToken = default)
    {
        direction = direction.ToUpperInvariant();

        if (direction != "LONG" && direction != "SHORT")
        {
            return BadRequest(new
            {
                message = "Direction yalnız LONG və ya SHORT ola bilər."
            });
        }

        var entry = GetDemoEntry(symbol);
        var risk = GetDemoRisk(symbol, entry);

        var signal = new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = direction,
            EntryPrice = entry,
            StopLoss = direction == "LONG"
                ? entry - risk
                : entry + risk,
            TakeProfit1 = direction == "LONG"
                ? entry + risk * 2
                : entry - risk * 2,
            TakeProfit2 = direction == "LONG"
                ? entry + risk * 3
                : entry - risk * 3,
            Confidence = 99,
            Grade = "A+",
            Message = $"{symbol} {direction} TEST MT5",
            CreatedAtUtc = DateTime.UtcNow
        };

        signal.EntryPrice = RoundPrice(symbol, signal.EntryPrice);
        signal.StopLoss = RoundPrice(symbol, signal.StopLoss);
        signal.TakeProfit1 = RoundPrice(symbol, signal.TakeProfit1);
        signal.TakeProfit2 = RoundPrice(symbol, signal.TakeProfit2);

        var result = await _queueService.EnqueueAsync(
            signal,
            cancellationToken);

        return Ok(new
        {
            result.Added,
            result.Message,
            result.Order
        });
    }

    private static decimal GetDemoEntry(string symbol)
    {
        var normalized = symbol.ToUpperInvariant();

        if (normalized.Contains("JPY"))
            return 213.250m;

        if (normalized.Contains("XAU"))
            return 2300.00m;

        if (normalized.Contains("BTC"))
            return 65000.00m;

        if (normalized.Contains("ETH"))
            return 3200.00m;

        if (normalized.Contains("USOIL"))
            return 80.00m;

        return 1.10000m;
    }

    private static decimal GetDemoRisk(string symbol, decimal entry)
    {
        var normalized = symbol.ToUpperInvariant();

        if (normalized.Contains("JPY"))
            return 0.200m;

        if (normalized.Contains("XAU"))
            return 5.00m;

        if (normalized.Contains("BTC"))
            return 300.00m;

        if (normalized.Contains("ETH"))
            return 30.00m;

        if (normalized.Contains("USOIL"))
            return 0.50m;

        return 0.00100m;
    }

    private static decimal RoundPrice(string symbol, decimal price)
    {
        var normalized = symbol.ToUpperInvariant();

        if (normalized.Contains("JPY"))
            return Math.Round(price, 3);

        if (normalized.Contains("XAU") ||
            normalized.Contains("BTC") ||
            normalized.Contains("ETH") ||
            normalized.Contains("USOIL"))
            return Math.Round(price, 2);

        return Math.Round(price, 5);
    }
}
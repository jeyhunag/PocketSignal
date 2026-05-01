using System.Globalization;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Services.Analysis;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Binary;

public class CoreBinarySignalService : ISmartSignalService
{
    private const int MinimumConfidence = 82;

    private readonly IMarketDataService _marketDataService;
    private readonly IMarketAnalysisEngine _analysisEngine;

    public CoreBinarySignalService(
        IMarketDataService marketDataService,
        IMarketAnalysisEngine analysisEngine)
    {
        _marketDataService = marketDataService;
        _analysisEngine = analysisEngine;
    }

    public async Task<SmartTradeSignal> AnalyzeAsync(
        string symbol,
        CancellationToken cancellationToken = default)
    {
        var m15Response = await _marketDataService.GetCandlesAsync(symbol, "15min", 150, cancellationToken);
        var m5Response = await _marketDataService.GetCandlesAsync(symbol, "5min", 150, cancellationToken);
        var m1Response = await _marketDataService.GetCandlesAsync(symbol, "1min", 150, cancellationToken);

        var m15 = MapCandles(m15Response);
        var m5 = MapCandles(m5Response);
        var m1 = MapCandles(m1Response);

        var core = _analysisEngine.Analyze(
            symbol,
            m15,
            m5,
            m1);

        if (core.IsBlocked || core.Direction == TradeDirection.Wait)
        {
            return CreateWaitSignal(symbol, core);
        }

        if (core.Confidence < MinimumConfidence)
        {
            core.BlockReasons.Add($"Binary minimum confidence tamamlanmadi. Lazimdir: {MinimumConfidence}, indi: {core.Confidence}");
            return CreateWaitSignal(symbol, core);
        }

        var direction = ToDirectionText(core.Direction);
        var entry = RoundPrice(symbol, (decimal)core.EntryPrice);
        var invalidPrice = RoundPrice(symbol, (decimal)core.InvalidPrice);

        var invalidIf = direction == "LONG"
            ? $"M1 candle {invalidPrice} altında bağlansa signal ləğvdir."
            : $"M1 candle {invalidPrice} üstündə bağlansa signal ləğvdir.";

        return new SmartTradeSignal
        {
            Symbol = symbol,
            Direction = direction,
            ExpiryMinutes = core.SuggestedExpiryMinutes,
            ExpiryReason = "Yeni M1-M15 core analiz sistemi ilə expiry seçildi.",
            Confidence = core.Confidence,
            Grade = core.Grade,
            Message = $"{symbol} {direction} {core.Confidence}% | {core.SuggestedExpiryMinutes} dəqiqə",
            EntryType = "NEXT_M1_CANDLE_OPEN_OR_NOW_IF_VALID",
            ValidForSeconds = 25,
            LastClose = entry,
            InvalidIf = invalidIf,
            Reasons = core.Reasons.Take(8).ToList(),
            SideAnalyses = new List<SideAnalysis>
            {
                new SideAnalysis
                {
                    Direction = direction,
                    Score = core.Confidence,
                    Reasons = core.Reasons
                }
            },
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static SmartTradeSignal CreateWaitSignal(
        string symbol,
        CoreMarketAnalysisResult core)
    {
        var reasons = new List<string>();

        if (core.BlockReasons.Count > 0)
            reasons.AddRange(core.BlockReasons);

        if (!string.IsNullOrWhiteSpace(core.BlockReason))
            reasons.Add(core.BlockReason);

        reasons.AddRange(core.Reasons);

        return new SmartTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            ExpiryMinutes = 0,
            ExpiryReason = "Yeni core analiz WAIT verdi.",
            Confidence = core.Confidence,
            Grade = "NO_TRADE",
            Message = $"{symbol} WAIT",
            EntryType = "NO_ENTRY",
            ValidForSeconds = 0,
            LastClose = core.EntryPrice > 0 ? RoundPrice(symbol, (decimal)core.EntryPrice) : 0,
            InvalidIf = "",
            Reasons = reasons.Distinct().Take(12).ToList(),
            SideAnalyses = new List<SideAnalysis>(),
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static List<PriceCandle> MapCandles(TwelveDataResponse? response)
    {
        if (response?.Values == null)
            return new List<PriceCandle>();

        var formats = new[]
        {
            "yyyy-MM-dd HH:mm:ss",
            "yyyy-MM-dd HH:mm",
            "yyyy-MM-dd"
        };

        var candles = new List<PriceCandle>();

        foreach (var item in response.Values)
        {
            if (!DateTime.TryParseExact(
                    item.DateTime,
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var time))
            {
                continue;
            }

            candles.Add(new PriceCandle
            {
                TimeUtc = time,
                Open = (double)item.Open,
                High = (double)item.High,
                Low = (double)item.Low,
                Close = (double)item.Close,
                Volume = 0
            });
        }

        return candles
            .OrderBy(x => x.TimeUtc)
            .ToList();
    }

    private static string ToDirectionText(TradeDirection direction)
    {
        return direction switch
        {
            TradeDirection.Long => "LONG",
            TradeDirection.Short => "SHORT",
            _ => "WAIT"
        };
    }

    private static decimal RoundPrice(string symbol, decimal price)
    {
        var digits = GetDigits(symbol);
        return Math.Round(price, digits);
    }

    private static int GetDigits(string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("JPY"))
            return 3;

        if (symbol.Contains("XAU"))
            return 2;

        if (symbol.Contains("BTC") || symbol.Contains("ETH"))
            return 2;

        if (symbol.Contains("USOIL"))
            return 2;

        return 5;
    }
}
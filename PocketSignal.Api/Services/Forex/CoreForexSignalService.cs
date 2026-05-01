using System.Globalization;
using PocketSignal.Api.Models.Analysis;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.Analysis;
using PocketSignal.Api.Services.MarketData;

namespace PocketSignal.Api.Services.Forex;

public class CoreForexSignalService : IForexSignalService
{
    private const int MinimumConfidence = 82;

    private readonly IMarketDataService _marketDataService;
    private readonly IMarketAnalysisEngine _analysisEngine;

    public CoreForexSignalService(
        IMarketDataService marketDataService,
        IMarketAnalysisEngine analysisEngine)
    {
        _marketDataService = marketDataService;
        _analysisEngine = analysisEngine;
    }

    public async Task<ForexTradeSignal> AnalyzeAsync(
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
            core.BlockReasons.Add($"Forex minimum confidence tamamlanmadi. Lazimdir: {MinimumConfidence}, indi: {core.Confidence}");
            return CreateWaitSignal(symbol, core);
        }

        var direction = ToDirectionText(core.Direction);
        var entry = RoundPrice(symbol, (decimal)core.EntryPrice);

        var riskPlan = BuildRiskPlan(
            symbol,
            direction,
            entry,
            m5);

        if (!riskPlan.IsValid)
        {
            core.BlockReasons.Add(riskPlan.InvalidReason);
            return CreateWaitSignal(symbol, core);
        }

        var stopLoss = RoundPrice(symbol, riskPlan.StopLoss);
        var takeProfit1 = RoundPrice(symbol, riskPlan.TakeProfit1);
        var takeProfit2 = RoundPrice(symbol, riskPlan.TakeProfit2);

        var pipSize = GetPipSize(symbol);

        var riskPips = Math.Abs(entry - stopLoss) / pipSize;
        var rewardPips1 = Math.Abs(takeProfit1 - entry) / pipSize;
        var rewardPips2 = Math.Abs(takeProfit2 - entry) / pipSize;

        var reasons = new List<string>();
        reasons.Add("Yeni Core Forex Engine: M15 + M5 + M1 analizi ilə signal təsdiqləndi.");
        reasons.AddRange(core.Reasons.Take(10));
        reasons.Add(riskPlan.Reason);

        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = direction,

            EntryPrice = entry,
            StopLoss = stopLoss,
            TakeProfit1 = takeProfit1,
            TakeProfit2 = takeProfit2,

            RiskPips = Math.Round(riskPips, 1),
            RewardPips1 = Math.Round(rewardPips1, 1),
            RewardPips2 = Math.Round(rewardPips2, 1),
            RiskReward1 = 2,
            RiskReward2 = 3,

            Confidence = core.Confidence,
            Grade = core.Grade,

            Message = $"{symbol} {direction} Entry: {entry} SL: {stopLoss} TP1: {takeProfit1} TP2: {takeProfit2}",

            InvalidIf = direction == "LONG"
                ? $"M5 candle {stopLoss} altında bağlansa trade ləğvdir."
                : $"M5 candle {stopLoss} üstündə bağlansa trade ləğvdir.",

            ValidForMinutes = GetValidForMinutes(core.Confidence),
            Reasons = reasons.Distinct().ToList(),
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

    private static ForexTradeSignal CreateWaitSignal(
        string symbol,
        CoreMarketAnalysisResult core)
    {
        var reasons = new List<string>();

        if (core.BlockReasons.Count > 0)
            reasons.AddRange(core.BlockReasons);

        if (!string.IsNullOrWhiteSpace(core.BlockReason))
            reasons.Add(core.BlockReason);

        reasons.AddRange(core.Reasons);

        return new ForexTradeSignal
        {
            Symbol = symbol,
            Direction = "WAIT",
            Confidence = core.Confidence,
            Grade = "NO_TRADE",
            Message = $"{symbol} FOREX WAIT",
            Reasons = reasons.Distinct().Take(15).ToList(),
            SideAnalyses = new List<SideAnalysis>(),
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    private static ForexRiskPlan BuildRiskPlan(
        string symbol,
        string direction,
        decimal entry,
        IReadOnlyList<PriceCandle> m5Candles)
    {
        if (m5Candles.Count < 20)
        {
            return ForexRiskPlan.Invalid("Risk plan üçün kifayət qədər M5 candle yoxdur.");
        }

        var recent = m5Candles.TakeLast(20).ToList();
        var structure = m5Candles.TakeLast(12).ToList();

        var avgRange = recent
            .Select(x => (decimal)x.Range)
            .DefaultIfEmpty(0)
            .Average();

        if (avgRange <= 0)
        {
            return ForexRiskPlan.Invalid("M5 average range düzgün hesablanmadı.");
        }

        var buffer = avgRange * 0.25m;
        var minDistance = GetPipSize(symbol) * 8m;

        decimal stopLoss;
        decimal takeProfit1;
        decimal takeProfit2;
        decimal risk;

        if (direction == "LONG")
        {
            var structureLow = structure.Min(x => (decimal)x.Low);
            stopLoss = structureLow - buffer;

            if (stopLoss >= entry)
                stopLoss = entry - Math.Max(buffer, minDistance);

            risk = entry - stopLoss;

            if (risk <= 0)
                return ForexRiskPlan.Invalid("LONG risk plan səhvdir: SL entry-dən aşağıda deyil.");

            takeProfit1 = entry + risk * 2m;
            takeProfit2 = entry + risk * 3m;
        }
        else if (direction == "SHORT")
        {
            var structureHigh = structure.Max(x => (decimal)x.High);
            stopLoss = structureHigh + buffer;

            if (stopLoss <= entry)
                stopLoss = entry + Math.Max(buffer, minDistance);

            risk = stopLoss - entry;

            if (risk <= 0)
                return ForexRiskPlan.Invalid("SHORT risk plan səhvdir: SL entry-dən yuxarıda deyil.");

            takeProfit1 = entry - risk * 2m;
            takeProfit2 = entry - risk * 3m;
        }
        else
        {
            return ForexRiskPlan.Invalid("Direction LONG/SHORT deyil.");
        }

        return ForexRiskPlan.Valid(
            stopLoss,
            takeProfit1,
            takeProfit2,
            "Risk plan yeni core sistemlə quruldu: SL M5 struktur arxasında, TP1 1:2, TP2 1:3.");
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

    private static int GetValidForMinutes(int confidence)
    {
        if (confidence >= 92)
            return 15;

        if (confidence >= 85)
            return 10;

        return 7;
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

    private static decimal GetPipSize(string symbol)
    {
        symbol = symbol.ToUpperInvariant();

        if (symbol.Contains("JPY"))
            return 0.01m;

        if (symbol.Contains("XAU"))
            return 0.10m;

        if (symbol.Contains("BTC"))
            return 1m;

        if (symbol.Contains("ETH"))
            return 0.10m;

        if (symbol.Contains("USOIL"))
            return 0.01m;

        return 0.0001m;
    }

    private sealed class ForexRiskPlan
    {
        public bool IsValid { get; private set; }

        public decimal StopLoss { get; private set; }

        public decimal TakeProfit1 { get; private set; }

        public decimal TakeProfit2 { get; private set; }

        public string Reason { get; private set; } = "";

        public string InvalidReason { get; private set; } = "";

        public static ForexRiskPlan Valid(
            decimal stopLoss,
            decimal takeProfit1,
            decimal takeProfit2,
            string reason)
        {
            return new ForexRiskPlan
            {
                IsValid = true,
                StopLoss = stopLoss,
                TakeProfit1 = takeProfit1,
                TakeProfit2 = takeProfit2,
                Reason = reason
            };
        }

        public static ForexRiskPlan Invalid(string reason)
        {
            return new ForexRiskPlan
            {
                IsValid = false,
                InvalidReason = reason
            };
        }
    }
}
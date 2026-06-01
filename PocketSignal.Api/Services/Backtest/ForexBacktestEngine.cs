using System.Globalization;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.Forex;

namespace PocketSignal.Api.Services.Backtest;

/// <summary>
/// Forex strategiyası üçün walk-forward backtest mühərriki.
///
/// Necə işləyir:
/// 1. Tarixi datanı (M15/M5/M1) bir dəfə çəkir.
/// 2. M1 candle-ları boyunca addım-addım irəliləyir.
/// 3. Hər addımda kursoru o ana qoyur və strategiyanı çağırır.
/// 4. Strategiya signal verərsə, gələcək candle-larda TP1/TP2/SL-dən hansının
///    əvvəl vurulduğunu yoxlayır → WIN / LOSS.
/// 5. Sonda statistika çıxarır: win-rate, profit factor, drawdown və s.
/// </summary>
public class ForexBacktestEngine
{
    private readonly ReplayMarketDataService _replay;

    public ForexBacktestEngine(ReplayMarketDataService replay)
    {
        _replay = replay;
    }

    public async Task<BacktestReport> RunAsync(
        string symbol,
        List<CandleDto> m15,
        List<CandleDto> m5,
        List<CandleDto> m1,
        int stepEveryNCandles = 5,
        string strategyName = "breaker",
        CancellationToken cancellationToken = default)
    {
        _replay.LoadHistory("15min", m15);
        _replay.LoadHistory("5min", m5);
        _replay.LoadHistory("1min", m1);

        // Strategiya seçimi. Hər ikisi IForexSignalService implement edir,
        // ona görə engine eyni qalır.
        IForexSignalService strategy = strategyName.ToLowerInvariant() switch
        {
            "ema" => new EmaPullbackForexSignalService(_replay),
            _ => new CoreForexSignalService(_replay)
        };

        var m1Series = _replay.GetFullSeries("1min")
            .Select(ToCandle)
            .OrderBy(x => x.time)
            .ToList();

        var report = new BacktestReport
        {
            Symbol = symbol,
            FromUtc = m1Series.FirstOrDefault().time,
            ToUtc = m1Series.LastOrDefault().time
        };

        // İlk 260 candle strategiyanın tarixçə tələbi üçün lazımdır, ondan sonra başla.
        var startIndex = 260;

        // Hold müddəti candle SAYI ilə ölçülür (timeframe-dən asılı olmasın).
        // M1-də 80 candle = ~1.3 saat; M15-də 80 candle = 20 saat. Hər ikisi məntiqli.
        var maxHoldCandles = 80;

        // Eyni anda yalnız 1 açıq trade (real davranışa uyğun).
        var openTrade = false;
        var tradeOpenIndex = 0;

        for (var i = startIndex; i < m1Series.Count; i += stepEveryNCandles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var now = m1Series[i].time;

            // Açıq trade varsa, yeni signal axtarma (overlapping qarşısı).
            if (openTrade)
            {
                if (i - tradeOpenIndex >= maxHoldCandles)
                    openTrade = false;
                else
                    continue;
            }

            _replay.SetCursor(now);

            ForexTradeSignal signal;
            try
            {
                signal = await strategy.AnalyzeAsync(symbol, cancellationToken);
            }
            catch
            {
                continue;
            }

            if (signal.Direction != "LONG" && signal.Direction != "SHORT")
                continue;

            // Signal var — gələcək candle-larla nəticəni simulyasiya et.
            var outcome = SimulateOutcome(
                signal,
                m1Series,
                i,
                maxHoldCandles);

            if (outcome == TradeOutcome.NoFill)
                continue;

            var trade = new BacktestTrade
            {
                Direction = signal.Direction,
                EntryTimeUtc = now,
                Entry = signal.EntryPrice,
                StopLoss = signal.StopLoss,
                TakeProfit1 = signal.TakeProfit1,
                TakeProfit2 = signal.TakeProfit2,
                Confidence = signal.Confidence,
                Grade = signal.Grade,
                RiskReward1 = signal.RiskReward1,
                Outcome = outcome.ToString()
            };

            // R-multiple: WIN_TP2 = +RR2, WIN_TP1 = +RR1, LOSS = -1R
            trade.RMultiple = outcome switch
            {
                TradeOutcome.WinTp2 => (double)signal.RiskReward2,
                TradeOutcome.WinTp1 => (double)signal.RiskReward1,
                TradeOutcome.Loss => -1.0,
                _ => 0.0
            };

            report.Trades.Add(trade);

            openTrade = true;
            tradeOpenIndex = i;
        }

        report.Compute();
        return report;
    }

    /// <summary>
    /// Signal açıldıqdan sonra gələcək M1 candle-larında TP/SL ardıcıllığını yoxlayır.
    /// Eyni candle-da həm TP, həm SL toxunularsa, mühafizəkar davranıb LOSS sayır.
    /// </summary>
    private static TradeOutcome SimulateOutcome(
        ForexTradeSignal signal,
        List<(DateTime time, double o, double h, double l, double c)> series,
        int entryIndex,
        int maxHoldCandles)
    {
        var entry = (double)signal.EntryPrice;
        var sl = (double)signal.StopLoss;
        var tp1 = (double)signal.TakeProfit1;
        var tp2 = (double)signal.TakeProfit2;

        var isLong = signal.Direction == "LONG";
        var hitTp1 = false;

        var endIndex = Math.Min(series.Count - 1, entryIndex + maxHoldCandles);

        for (var j = entryIndex + 1; j <= endIndex; j++)
        {
            var candle = series[j];

            if (isLong)
            {
                var slHit = candle.l <= sl;
                var tp1Hit = candle.h >= tp1;
                var tp2Hit = candle.h >= tp2;

                // Eyni candle-da SL və TP → mühafizəkar: SL əvvəl sayılır.
                if (slHit && (tp1Hit || tp2Hit))
                    return hitTp1 ? TradeOutcome.WinTp1 : TradeOutcome.Loss;

                if (tp2Hit) return TradeOutcome.WinTp2;
                if (tp1Hit) hitTp1 = true;
                if (slHit) return hitTp1 ? TradeOutcome.WinTp1 : TradeOutcome.Loss;
            }
            else
            {
                var slHit = candle.h >= sl;
                var tp1Hit = candle.l <= tp1;
                var tp2Hit = candle.l <= tp2;

                if (slHit && (tp1Hit || tp2Hit))
                    return hitTp1 ? TradeOutcome.WinTp1 : TradeOutcome.Loss;

                if (tp2Hit) return TradeOutcome.WinTp2;
                if (tp1Hit) hitTp1 = true;
                if (slHit) return hitTp1 ? TradeOutcome.WinTp1 : TradeOutcome.Loss;
            }
        }

        // Vaxt bitdi: TP1 vurulmuşdusa kiçik qazanc, yoxsa nəticəsiz (filtir).
        return hitTp1 ? TradeOutcome.WinTp1 : TradeOutcome.NoFill;
    }

    private static (DateTime time, double o, double h, double l, double c) ToCandle(CandleDto c)
    {
        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };
        DateTime.TryParseExact(c.DateTime, formats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var t);

        return (t, (double)c.Open, (double)c.High, (double)c.Low, (double)c.Close);
    }

    private enum TradeOutcome
    {
        NoFill,
        Loss,
        WinTp1,
        WinTp2
    }
}
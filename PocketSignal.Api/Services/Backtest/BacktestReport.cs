namespace PocketSignal.Api.Services.Backtest;

public class BacktestTrade
{
    public string Direction { get; set; } = string.Empty;
    public DateTime EntryTimeUtc { get; set; }
    public decimal Entry { get; set; }
    public decimal StopLoss { get; set; }
    public decimal TakeProfit1 { get; set; }
    public decimal TakeProfit2 { get; set; }
    public int Confidence { get; set; }
    public string Grade { get; set; } = string.Empty;
    public decimal RiskReward1 { get; set; }
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Nəticənin R vahidində dəyəri: +RR (win), -1 (loss).</summary>
    public double RMultiple { get; set; }
}

public class BacktestReport
{
    public string Symbol { get; set; } = string.Empty;
    public DateTime FromUtc { get; set; }
    public DateTime ToUtc { get; set; }

    /// <summary>Bir trade-in maksimum saxlanma müddəti (dəqiqə).</summary>
    public int MaxHoldMinutes { get; set; } = 240;

    public List<BacktestTrade> Trades { get; set; } = new();

    // === Hesablanan metrikalar ===
    public int TotalTrades { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public double WinRatePercent { get; set; }

    /// <summary>Ümumi qazanc R vahidində (məsələn +14.5R).</summary>
    public double TotalR { get; set; }

    /// <summary>Profit factor = qazanılan R / itirilən R. 1-dən böyük = qazanclı.</summary>
    public double ProfitFactor { get; set; }

    /// <summary>Ən pis ardıcıl düşüş (R vahidində).</summary>
    public double MaxDrawdownR { get; set; }

    public int MaxConsecutiveLosses { get; set; }
    public double AverageConfidence { get; set; }

    public List<string> Summary { get; set; } = new();

    public void Compute()
    {
        TotalTrades = Trades.Count;

        if (TotalTrades == 0)
        {
            Summary.Add("Heç bir trade tapılmadı. Strategiya bu data aralığında signal vermədi.");
            return;
        }

        Wins = Trades.Count(t => t.RMultiple > 0);
        Losses = Trades.Count(t => t.RMultiple < 0);
        WinRatePercent = Math.Round(100.0 * Wins / TotalTrades, 1);

        TotalR = Math.Round(Trades.Sum(t => t.RMultiple), 2);

        var grossWin = Trades.Where(t => t.RMultiple > 0).Sum(t => t.RMultiple);
        var grossLoss = Math.Abs(Trades.Where(t => t.RMultiple < 0).Sum(t => t.RMultiple));
        ProfitFactor = grossLoss > 0 ? Math.Round(grossWin / grossLoss, 2) : grossWin;

        AverageConfidence = Math.Round(Trades.Average(t => t.Confidence), 1);

        // Equity curve üzərindən max drawdown (R vahidində).
        double peak = 0, equity = 0, maxDd = 0;
        var consecLoss = 0;
        var maxConsecLoss = 0;

        foreach (var t in Trades)
        {
            equity += t.RMultiple;
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDd) maxDd = dd;

            if (t.RMultiple < 0)
            {
                consecLoss++;
                if (consecLoss > maxConsecLoss) maxConsecLoss = consecLoss;
            }
            else
            {
                consecLoss = 0;
            }
        }

        MaxDrawdownR = Math.Round(maxDd, 2);
        MaxConsecutiveLosses = maxConsecLoss;

        Summary.Add($"Cəmi trade: {TotalTrades}");
        Summary.Add($"Win-rate: {WinRatePercent}% ({Wins}W / {Losses}L)");
        Summary.Add($"Ümumi nəticə: {TotalR}R");
        Summary.Add($"Profit factor: {ProfitFactor}");
        Summary.Add($"Maksimum drawdown: {MaxDrawdownR}R");
        Summary.Add($"Maksimum ardıcıl loss: {MaxConsecutiveLosses}");
        Summary.Add($"Orta confidence: {AverageConfidence}%");

        var verdict = ProfitFactor switch
        {
            >= 1.5 => "GÜCLÜ — bu strategiya bu data üzərində yaxşı nəticə verir.",
            >= 1.1 => "ORTA — qazanclıdır, amma optimallaşdırma lazımdır.",
            >= 0.9 => "ZƏİF — demək olar breakeven, riskə dəyməz.",
            _ => "PİS — bu strategiya bu data üzərində itirir."
        };
        Summary.Add($"Nəticə: {verdict}");
    }
}
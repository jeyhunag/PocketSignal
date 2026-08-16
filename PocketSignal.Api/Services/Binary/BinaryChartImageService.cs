using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Binary;
using PocketSignal.Api.Services.MarketData;
using SkiaSharp;

namespace PocketSignal.Api.Services.Binary;

/// <summary>
/// CASSANDRA üslubunda XAU/USD şəkli — ORİJİNAL görünüş.
/// Ağ fon, mərkəzdə başlıq, nazik zona xətləri, yaşıl/qırmızı giriş oxları,
/// sağda səliqəli qiymət etiketləri. M15 candlestick.
/// </summary>
public class BinaryChartImageService : IBinaryChartImageService
{
    private readonly IMarketDataService _marketDataService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<BinaryChartImageService> _logger;
    private string _currentSymbol = "XAU/USD";

    public BinaryChartImageService(
        IMarketDataService marketDataService,
        IWebHostEnvironment environment,
        ILogger<BinaryChartImageService> logger)
    {
        _marketDataService = marketDataService;
        _environment = environment;
        _logger = logger;
    }

    public async Task<string?> GenerateSignalChartAsync(
        SmartTradeSignal signal,
        CancellationToken cancellationToken = default)
    {
        if (signal.Bias != "SELL" && signal.Bias != "BUY")
            return null;

        try
        {
            var response = await _marketDataService.GetCandlesAsync(
                signal.Symbol,
                string.IsNullOrWhiteSpace(signal.Timeframe) ? "15min" : signal.Timeframe,
                150,
                cancellationToken);

            var candles = MapCandles(response, signal.Symbol);

            if (candles.Count < 20)
            {
                _logger.LogWarning("Cassandra chart üçün kifayət qədər M15 candle yoxdur.");
                return null;
            }

            return CreateChartImage(signal, candles.TakeLast(100).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cassandra chart yaradılan zaman xəta baş verdi.");
            return null;
        }
    }

    private string CreateChartImage(
        SmartTradeSignal signal,
        List<Candle> candles)
    {
        _currentSymbol = signal.Symbol;

        var outputDirectory = GetOutputDirectory();
        Directory.CreateDirectory(outputDirectory);

        var fileName =
            $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_XAUUSD_{signal.Bias}_{Guid.NewGuid():N}.png";
        var filePath = Path.Combine(outputDirectory, fileName);

        // Orijinal Cassandra ölçüsü — dik (portrait) format.
        const int width = 900;
        const int height = 1050;

        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);

        // ===== ORİJİNAL: ağ fon =====
        var background = SKColors.White;
        var chartBg = SKColors.White;
        var gridColor = SKColor.Parse("#E5E7EB");        // açıq boz grid
        var axisColor = SKColor.Parse("#374151");        // tünd boz oxlar/mətn
        var textColor = SKColor.Parse("#111827");        // qara mətn
        var mutedText = SKColor.Parse("#6B7280");        // boz mətn

        var bullColor = SKColor.Parse("#26A69A");        // yaşıl şam
        var bearColor = SKColor.Parse("#EF5350");        // qırmızı şam

        var sellColor = SKColor.Parse("#DC2626");        // qırmızı — SELL/resistance
        var buyColor = SKColor.Parse("#059669");         // yaşıl — BUY/support
        var decisionColor = SKColor.Parse("#6B7280");    // boz — qərar nöqtəsi

        canvas.Clear(background);

        // ===== BAŞLIQ (mərkəzdə) =====
        using var titlePaint = new SKPaint
        {
            Color = textColor,
            IsAntialias = true,
            TextSize = 22,
            TextAlign = SKTextAlign.Center,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        using var subtitlePaint = new SKPaint
        {
            Color = mutedText,
            IsAntialias = true,
            TextSize = 15,
            TextAlign = SKTextAlign.Center,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        var biasColor = signal.Bias == "SELL" ? sellColor : buyColor;

        canvas.DrawText(
            $"Cassandra Analysis - {InstrumentName(signal.Symbol)} | Bias: {signal.Bias}",
            width / 2f, 40, titlePaint);
        canvas.DrawText(
            "Giriş zonaları qrafikdə göstərilib",
            width / 2f, 64, subtitlePaint);

        // ===== CHART sahəsi =====
        var chartRect = new SKRect(70, 100, width - 110, height - 260);

        DrawGrid(canvas, chartRect, gridColor);

        // Y oxu (sol) çərçivə xətti
        using var axisPaint = new SKPaint
        {
            Color = axisColor,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };
        canvas.DrawLine(chartRect.Left, chartRect.Top, chartRect.Left, chartRect.Bottom, axisPaint);
        canvas.DrawLine(chartRect.Left, chartRect.Bottom, chartRect.Right, chartRect.Bottom, axisPaint);

        // Qiymət aralığı
        var allLevels = new List<decimal> { signal.DecisionPoint };
        allLevels.AddRange(signal.SellZones);
        allLevels.AddRange(signal.BuyZones);
        if (signal.CounterZone > 0) allLevels.Add(signal.CounterZone);

        var minPrice = Math.Min(candles.Min(x => x.Low), allLevels.Min());
        var maxPrice = Math.Max(candles.Max(x => x.High), allLevels.Max());
        var padding = (maxPrice - minPrice) * 0.08m;
        if (padding <= 0) padding = 1m;
        minPrice -= padding;
        maxPrice += padding;

        // Y oxu qiymət etiketləri (sol)
        DrawPriceAxis(canvas, chartRect, minPrice, maxPrice, mutedText);

        DrawCandles(canvas, candles, chartRect, minPrice, maxPrice, bullColor, bearColor);

        // ===== BIAS OXU (yuxarıda, nazik) =====
        DrawBiasArrow(canvas, chartRect, signal, minPrice, maxPrice, sellColor, buyColor);

        // ===== ZONALAR — PİYADA DÖYÜŞÜ məntiqi ilə =====
        // Bias istiqamətindəki zonalar (piyada). Amma qiymət bir zonanı QIRIBSA
        // (BUY-da zona qiymətdən yuxarı qalıbsa = qırılıb → artıq SELL zona = qırmızı).
        var activeZones = signal.Bias == "SELL" ? signal.SellZones : signal.BuyZones;

        foreach (var zone in activeZones)
        {
            bool broken;
            string label;
            SKColor color;

            if (signal.Bias == "BUY")
            {
                // BUY zona qiymətin ALTINDA olmalıdır. Yuxarıda qalıbsa qırılıb → SELL (qırmızı, nazik).
                broken = zone > signal.LastPrice;
                color = broken ? sellColor : buyColor;
                label = broken ? "Sell zone" : "Buy zone";
            }
            else
            {
                // SELL zona qiymətin ÜSTÜNDƏ olmalıdır. Aşağıda qalıbsa qırılıb → BUY (yaşıl, nazik).
                broken = zone < signal.LastPrice;
                color = broken ? buyColor : sellColor;
                label = broken ? "Buy zone" : "Sell zone";
            }

            DrawZoneLine(canvas, chartRect, minPrice, maxPrice, zone, label, color, signal.Bias);
        }

        // ===== BIASA TƏRS ZONA — qalın QIRMIZI xətt (varsa) =====
        // BUY bias-da yuxarıdakı güclü resistance, SELL bias-da aşağıdakı güclü support.
        if (signal.CounterZone > 0)
        {
            var counterColor = signal.Bias == "BUY" ? sellColor : buyColor;
            var counterLabel = signal.Bias == "BUY" ? "Sell zone (Biasa tərs)" : "Buy zone (Biasa tərs)";
            DrawCounterLine(canvas, chartRect, minPrice, maxPrice, signal.CounterZone, counterLabel, counterColor);
        }

        // Qərar nöqtəsi (şah) — GÜCLÜ qalın xətt
        DrawDecisionLine(canvas, chartRect, minPrice, maxPrice, signal.DecisionPoint, decisionColor);

        // Son qiymət etiketi (sağ yuxarı)
        DrawLastPriceLabel(canvas, chartRect, signal.LastPrice, signal.Bias, minPrice, maxPrice, biasColor);

        // ===== ALTDA MƏTN =====
        DrawNote(canvas, signal, 70, height - 230, textColor, mutedText, biasColor);

        using var footerPaint = new SKPaint
        {
            Color = SKColor.Parse("#9CA3AF"),
            IsAntialias = true,
            TextSize = 12,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };
        canvas.DrawText(
            $"{TfLabel(signal.Timeframe)} chart | {signal.Symbol} | Created UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
            70, height - 24, footerPaint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        using var stream = File.OpenWrite(filePath);
        data.SaveTo(stream);

        return filePath;
    }

    private void DrawNote(
        SKCanvas canvas,
        SmartTradeSignal signal,
        float x,
        float y,
        SKColor textColor,
        SKColor mutedColor,
        SKColor biasColor)
    {
        using var headerPaint = new SKPaint
        {
            Color = biasColor,
            IsAntialias = true,
            TextSize = 17,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };
        using var linePaint = new SKPaint
        {
            Color = textColor,
            IsAntialias = true,
            TextSize = 14,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        var lineHeight = 20f;
        var currentY = y;

        canvas.DrawText($"Bias: {signal.Bias}", x, currentY, headerPaint);
        currentY += lineHeight + 2;

        var zones = signal.Bias == "SELL" ? signal.SellZones : signal.BuyZones;
        var zoneLabel = signal.Bias == "SELL" ? "SELL zonaları" : "BUY zonaları";

        canvas.DrawText($"{zoneLabel}:", x, currentY, linePaint);
        currentY += lineHeight;

        foreach (var z in zones.Take(3))
        {
            canvas.DrawText($"   • {FormatPrice(z)}", x, currentY, linePaint);
            currentY += lineHeight;
        }

        canvas.DrawText($"Qərar Nöqtəsi (şah): {FormatPrice(signal.DecisionPoint)}", x, currentY, linePaint);
        currentY += lineHeight;

        // Biasa tərs zona — varsa, qırmızı/yaşıl rənglə.
        if (signal.CounterZone > 0)
        {
            var counterLabel = signal.Bias == "BUY" ? "Sell zone (Biasa tərs)" : "Buy zone (Biasa tərs)";
            using var counterPaint = new SKPaint
            {
                Color = signal.Bias == "BUY" ? SKColor.Parse("#DC2626") : SKColor.Parse("#059669"),
                IsAntialias = true,
                TextSize = 14,
                Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
            };
            canvas.DrawText($"{counterLabel}: {FormatPrice(signal.CounterZone)}", x, currentY, counterPaint);
            currentY += lineHeight;
        }

        canvas.DrawText($"Ən yaxın zona: {FormatPrice(signal.NearestZone)}", x, currentY, linePaint);
    }

    private string GetOutputDirectory()
    {
        var wwwroot = _environment.WebRootPath;
        if (string.IsNullOrWhiteSpace(wwwroot))
            wwwroot = Path.Combine(_environment.ContentRootPath, "wwwroot");
        return Path.Combine(wwwroot, "forex-charts");
    }

    private static void DrawGrid(SKCanvas canvas, SKRect chartRect, SKColor gridColor)
    {
        using var gridPaint = new SKPaint { Color = gridColor, StrokeWidth = 1, IsAntialias = true };
        for (var i = 1; i < 6; i++)
        {
            var yy = chartRect.Top + chartRect.Height / 6 * i;
            canvas.DrawLine(chartRect.Left, yy, chartRect.Right, yy, gridPaint);
        }
        for (var i = 1; i < 10; i++)
        {
            var xx = chartRect.Left + chartRect.Width / 10 * i;
            canvas.DrawLine(xx, chartRect.Top, xx, chartRect.Bottom, gridPaint);
        }
    }

    private void DrawPriceAxis(
        SKCanvas canvas, SKRect chartRect, decimal minPrice, decimal maxPrice, SKColor color)
    {
        using var txtPaint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            TextSize = 12,
            TextAlign = SKTextAlign.Right,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };
        for (var i = 0; i <= 6; i++)
        {
            var price = maxPrice - (maxPrice - minPrice) / 6 * i;
            var yy = chartRect.Top + chartRect.Height / 6 * i;
            canvas.DrawText(FormatPrice(price), chartRect.Left - 8, yy + 4, txtPaint);
        }
    }

    private static void DrawCandles(
        SKCanvas canvas, List<Candle> candles, SKRect chartRect,
        decimal minPrice, decimal maxPrice, SKColor bullColor, SKColor bearColor)
    {
        var candleCount = candles.Count;
        var slotWidth = chartRect.Width / candleCount;
        var candleBodyWidth = Math.Max(2, slotWidth * 0.6f);

        using var wickPaint = new SKPaint { StrokeWidth = 1, IsAntialias = true };
        using var bodyPaint = new SKPaint { IsAntialias = true };

        for (var i = 0; i < candleCount; i++)
        {
            var candle = candles[i];
            var x = chartRect.Left + slotWidth * i + slotWidth / 2;

            var openY = PriceToY(candle.Open, chartRect, minPrice, maxPrice);
            var closeY = PriceToY(candle.Close, chartRect, minPrice, maxPrice);
            var highY = PriceToY(candle.High, chartRect, minPrice, maxPrice);
            var lowY = PriceToY(candle.Low, chartRect, minPrice, maxPrice);

            var isBull = candle.Close >= candle.Open;
            var color = isBull ? bullColor : bearColor;
            wickPaint.Color = color;
            bodyPaint.Color = color;

            canvas.DrawLine(x, highY, x, lowY, wickPaint);

            var bodyTop = Math.Min(openY, closeY);
            var bodyBottom = Math.Max(openY, closeY);
            if (Math.Abs(bodyBottom - bodyTop) < 1) bodyBottom = bodyTop + 1;

            canvas.DrawRect(
                new SKRect(x - candleBodyWidth / 2, bodyTop, x + candleBodyWidth / 2, bodyBottom),
                bodyPaint);
        }
    }

    private void DrawZoneLine(
        SKCanvas canvas, SKRect chartRect, decimal minPrice, decimal maxPrice,
        decimal price, string label, SKColor color, string bias)
    {
        var y = PriceToY(price, chartRect, minPrice, maxPrice);
        if (y < chartRect.Top || y > chartRect.Bottom) return;

        // Nazik üfüqi xətt
        using var linePaint = new SKPaint
        {
            Color = color,
            StrokeWidth = 1.5f,
            IsAntialias = true
        };
        canvas.DrawLine(chartRect.Left, y, chartRect.Right, y, linePaint);

        // Sağda kiçik etiket
        using var bgPaint = new SKPaint { Color = color, IsAntialias = true };
        using var txtPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 12,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        var text = FormatPrice(price);
        var tw = txtPaint.MeasureText(text);
        var rect = new SKRect(chartRect.Right + 4, y - 10, chartRect.Right + 14 + tw, y + 10);
        canvas.DrawRect(rect, bgPaint);
        canvas.DrawText(text, rect.Left + 5, y + 4, txtPaint);

        // Zona adı (xəttin üstündə, sol)
        using var labelPaint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            TextSize = 11,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };
        canvas.DrawText(label, chartRect.Right - 120, y - 4, labelPaint);
    }

    private void DrawCounterLine(
        SKCanvas canvas, SKRect chartRect, decimal minPrice, decimal maxPrice,
        decimal price, string label, SKColor color)
    {
        var y = PriceToY(price, chartRect, minPrice, maxPrice);
        if (y < chartRect.Top || y > chartRect.Bottom) return;

        // Biasa tərs zona — qalın xətt (güclü tepki gözlənilir).
        using var linePaint = new SKPaint
        {
            Color = color,
            StrokeWidth = 3.5f,
            IsAntialias = true
        };
        canvas.DrawLine(chartRect.Left, y, chartRect.Right, y, linePaint);

        // Sağda qalın etiket.
        using var bgPaint = new SKPaint { Color = color, IsAntialias = true };
        using var txtPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 13,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };
        var text = FormatPrice(price);
        var tw = txtPaint.MeasureText(text);
        var rect = new SKRect(chartRect.Right + 4, y - 12, chartRect.Right + 14 + tw, y + 12);
        canvas.DrawRect(rect, bgPaint);
        canvas.DrawText(text, rect.Left + 5, y + 4, txtPaint);

        // Etiket adı xəttin üstündə (sol).
        using var labelPaint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            TextSize = 11,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };
        canvas.DrawText(label, chartRect.Right - 175, y - 5, labelPaint);
    }

    private void DrawDecisionLine(
        SKCanvas canvas, SKRect chartRect, decimal minPrice, decimal maxPrice,
        decimal price, SKColor color)
    {
        var y = PriceToY(price, chartRect, minPrice, maxPrice);
        if (y < chartRect.Top || y > chartRect.Bottom) return;

        // ŞAH — güclü qalın xətt.
        using var linePaint = new SKPaint
        {
            Color = color,
            StrokeWidth = 3.5f,
            IsAntialias = true
        };
        canvas.DrawLine(chartRect.Left, y, chartRect.Right, y, linePaint);

        // Sağda qalın etiket.
        using var bgPaint = new SKPaint { Color = color, IsAntialias = true };
        using var txtPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 13,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };
        var text = FormatPrice(price);
        var tw = txtPaint.MeasureText(text);
        var rect = new SKRect(chartRect.Right + 4, y - 12, chartRect.Right + 14 + tw, y + 12);
        canvas.DrawRect(rect, bgPaint);
        canvas.DrawText(text, rect.Left + 5, y + 4, txtPaint);

        // "ŞAH" adı xəttin üstündə.
        using var labelPaint = new SKPaint
        {
            Color = color,
            IsAntialias = true,
            TextSize = 12,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };
        canvas.DrawText("ŞAH (Qərar Nöqtəsi)", chartRect.Right - 165, y - 5, labelPaint);
    }

    private static void DrawBiasArrow(
        SKCanvas canvas, SKRect chartRect, SmartTradeSignal signal,
        decimal minPrice, decimal maxPrice, SKColor sellColor, SKColor buyColor)
    {
        // Bias oxu: BUY → aşağıdan yuxarı yaşıl ox (zonalardan qalxır);
        //           SELL → yuxarıdan aşağı qırmızı ox.
        var color = signal.Bias == "SELL" ? sellColor : buyColor;
        using var arrowPaint = new SKPaint
        {
            Color = color,
            StrokeWidth = 2.5f,
            IsAntialias = true,
            Style = SKPaintStyle.Stroke
        };

        var x = chartRect.Left + chartRect.Width * 0.72f;

        if (signal.Bias == "SELL")
        {
            var topY = chartRect.Top + 20;
            var botY = chartRect.Top + 90;
            canvas.DrawLine(x, topY, x, botY, arrowPaint);
            canvas.DrawLine(x, botY, x - 8, botY - 12, arrowPaint);
            canvas.DrawLine(x, botY, x + 8, botY - 12, arrowPaint);
        }
        else
        {
            // Yaşıl oxlar zonalardan yuxarı (giriş istiqaməti) — ən yaxın zonadan qalxan
            var nearY = PriceToY(signal.NearestZone, chartRect, minPrice, maxPrice);
            var topY = nearY - 60;
            canvas.DrawLine(x, nearY, x, topY, arrowPaint);
            canvas.DrawLine(x, topY, x - 8, topY + 12, arrowPaint);
            canvas.DrawLine(x, topY, x + 8, topY + 12, arrowPaint);
        }
    }

    private void DrawLastPriceLabel(
        SKCanvas canvas, SKRect chartRect, decimal price, string bias,
        decimal minPrice, decimal maxPrice, SKColor biasColor)
    {
        using var txtPaint = new SKPaint
        {
            Color = SKColor.Parse("#374151"),
            IsAntialias = true,
            TextSize = 12,
            TextAlign = SKTextAlign.Right,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };
        canvas.DrawText($"Son qiymət: {FormatPrice(price)}", chartRect.Right, chartRect.Top - 6, txtPaint);
        canvas.DrawText($"Bias: {bias}", chartRect.Right, chartRect.Top + 10, txtPaint);
    }

    private static float PriceToY(decimal price, SKRect chartRect, decimal minPrice, decimal maxPrice)
    {
        var range = maxPrice - minPrice;
        if (range <= 0) return chartRect.MidY;
        var percentage = (price - minPrice) / range;
        return chartRect.Bottom - (float)percentage * chartRect.Height;
    }

    private static List<Candle> MapCandles(TwelveDataResponse? response, string symbol)
    {
        if (response?.Values == null)
            return new List<Candle>();

        var formats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd" };
        var candles = new List<Candle>();

        foreach (var item in response.Values)
        {
            if (!DateTime.TryParseExact(
                    item.DateTime, formats,
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var time))
            {
                continue;
            }

            candles.Add(new Candle
            {
                Time = time,
                Symbol = symbol,
                Open = (decimal)item.Open,
                High = (decimal)item.High,
                Low = (decimal)item.Low,
                Close = (decimal)item.Close
            });
        }

        return candles.OrderBy(x => x.Time).ToList();
    }

    private static string TfLabel(string tf) => tf switch
    {
        "1min" => "M1",
        "5min" => "M5",
        _ => "M15"
    };

    private static string InstrumentName(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        if (s.Contains("XAU")) return "GOLD";
        return symbol;
    }

    private static int GetDigits(string symbol)
    {
        var s = symbol.ToUpperInvariant();
        if (s.Contains("JPY")) return 3;
        if (s.Contains("XAU")) return 2;
        if (s.Contains("BTC") || s.Contains("ETH")) return 2;
        if (s.Contains("USOIL")) return 2;
        return 5;
    }

    private string FormatPrice(decimal price)
    {
        var digits = GetDigits(_currentSymbol);
        var fmt = "0." + new string('0', digits);
        return price.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture);
    }
}
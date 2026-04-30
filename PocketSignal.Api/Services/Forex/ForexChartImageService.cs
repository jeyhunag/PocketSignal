using PocketSignal.Api.Models;
using PocketSignal.Api.Models.Common;
using PocketSignal.Api.Models.Forex;
using PocketSignal.Api.Services.MarketData;
using SkiaSharp;

namespace PocketSignal.Api.Services.Forex;

public class ForexChartImageService : IForexChartImageService
{
    private readonly IMarketDataService _marketDataService;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<ForexChartImageService> _logger;

    public ForexChartImageService(
        IMarketDataService marketDataService,
        IWebHostEnvironment environment,
        ILogger<ForexChartImageService> logger)
    {
        _marketDataService = marketDataService;
        _environment = environment;
        _logger = logger;
    }

    public async Task<string?> GenerateSignalChartAsync(
        ForexTradeSignal signal,
        CancellationToken cancellationToken = default)
    {
        if (signal.Direction != "LONG" && signal.Direction != "SHORT")
            return null;

        if (signal.EntryPrice <= 0 ||
            signal.StopLoss <= 0 ||
            signal.TakeProfit1 <= 0 ||
            signal.TakeProfit2 <= 0)
        {
            return null;
        }

        try
        {
            var response = await _marketDataService.GetCandlesAsync(
                signal.Symbol,
                "1min",
                120,
                cancellationToken);

            var candles = ForexAnalysis.MapCandles(
                response,
                signal.Symbol);

            if (candles.Count < 20)
            {
                _logger.LogWarning(
                    "Forex chart ucun kifayet qeder M1 candle yoxdur. Symbol: {Symbol}",
                    signal.Symbol);

                return null;
            }

            return CreateChartImage(
                signal,
                candles.TakeLast(90).ToList());
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Forex chart yaradilan zaman xeta bas verdi. Symbol: {Symbol}",
                signal.Symbol);

            return null;
        }
    }

    private string CreateChartImage(
        ForexTradeSignal signal,
        List<Candle> candles)
    {
        var outputDirectory = GetOutputDirectory();

        Directory.CreateDirectory(outputDirectory);

        var safeSymbol = signal.Symbol
            .Replace("/", "_")
            .Replace("-", "_")
            .Replace(" ", "_");

        var fileName =
            $"{DateTime.UtcNow:yyyyMMdd_HHmmss}_{safeSymbol}_{signal.Direction}_{Guid.NewGuid():N}.png";

        var filePath = Path.Combine(
            outputDirectory,
            fileName);

        const int width = 1280;
        const int height = 720;

        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);

        var background = SKColor.Parse("#0F141B");
        var panel = SKColor.Parse("#131A22");
        var gridColor = SKColor.Parse("#243040");
        var textColor = SKColor.Parse("#F4F4F5");
        var mutedTextColor = SKColor.Parse("#A1A1AA");

        var bullColor = SKColor.Parse("#26A69A");
        var bearColor = SKColor.Parse("#EF5350");

        var entryColor = SKColor.Parse("#60A5FA");
        var stopColor = SKColor.Parse("#EF4444");
        var tp1Color = SKColor.Parse("#22C55E");
        var tp2Color = SKColor.Parse("#16A34A");

        canvas.Clear(background);

        using var panelPaint = new SKPaint
        {
            Color = panel,
            IsAntialias = true
        };

        canvas.DrawRoundRect(
            new SKRoundRect(new SKRect(24, 24, width - 24, height - 24), 20, 20),
            panelPaint);

        using var titlePaint = new SKPaint
        {
            Color = textColor,
            IsAntialias = true,
            TextSize = 32,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        using var smallTextPaint = new SKPaint
        {
            Color = mutedTextColor,
            IsAntialias = true,
            TextSize = 20,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        var title = $"{signal.Symbol} {signal.Direction}";
        var subtitle =
            $"Confidence: {signal.Confidence}%   Entry: {FormatPrice(signal.EntryPrice)}   SL: {FormatPrice(signal.StopLoss)}   TP1: {FormatPrice(signal.TakeProfit1)}   TP2: {FormatPrice(signal.TakeProfit2)}";

        canvas.DrawText(title, 52, 70, titlePaint);
        canvas.DrawText(subtitle, 52, 105, smallTextPaint);

        var chartRect = new SKRect(52, 140, width - 190, height - 70);
        var priceScaleRect = new SKRect(chartRect.Right, chartRect.Top, width - 52, chartRect.Bottom);

        DrawGrid(
            canvas,
            chartRect,
            gridColor);

        var minPrice = candles.Min(x => x.Low);
        var maxPrice = candles.Max(x => x.High);

        minPrice = Math.Min(minPrice, Math.Min(signal.StopLoss, Math.Min(signal.TakeProfit1, signal.TakeProfit2)));
        maxPrice = Math.Max(maxPrice, Math.Max(signal.StopLoss, Math.Max(signal.TakeProfit1, signal.TakeProfit2)));

        var padding = (maxPrice - minPrice) * 0.12m;

        if (padding <= 0)
            padding = Math.Max(signal.EntryPrice * 0.001m, 0.0001m);

        minPrice -= padding;
        maxPrice += padding;

        DrawCandles(
            canvas,
            candles,
            chartRect,
            minPrice,
            maxPrice,
            bullColor,
            bearColor);

        DrawPriceLine(
            canvas,
            chartRect,
            priceScaleRect,
            minPrice,
            maxPrice,
            signal.EntryPrice,
            "ENTRY",
            entryColor);

        DrawPriceLine(
            canvas,
            chartRect,
            priceScaleRect,
            minPrice,
            maxPrice,
            signal.StopLoss,
            "SL",
            stopColor);

        DrawPriceLine(
            canvas,
            chartRect,
            priceScaleRect,
            minPrice,
            maxPrice,
            signal.TakeProfit1,
            "TP1",
            tp1Color);

        DrawPriceLine(
            canvas,
            chartRect,
            priceScaleRect,
            minPrice,
            maxPrice,
            signal.TakeProfit2,
            "TP2",
            tp2Color);

        DrawCurrentPriceLabel(
            canvas,
            chartRect,
            priceScaleRect,
            candles.Last().Close,
            minPrice,
            maxPrice);

        using var footerPaint = new SKPaint
        {
            Color = mutedTextColor,
            IsAntialias = true,
            TextSize = 18,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Normal)
        };

        canvas.DrawText(
            $"M1 chart | Created UTC: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}",
            52,
            height - 36,
            footerPaint);

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);

        using var stream = File.OpenWrite(filePath);
        data.SaveTo(stream);

        return filePath;
    }

    private string GetOutputDirectory()
    {
        var wwwroot = _environment.WebRootPath;

        if (string.IsNullOrWhiteSpace(wwwroot))
        {
            wwwroot = Path.Combine(
                _environment.ContentRootPath,
                "wwwroot");
        }

        return Path.Combine(
            wwwroot,
            "forex-charts");
    }

    private static void DrawGrid(
        SKCanvas canvas,
        SKRect chartRect,
        SKColor gridColor)
    {
        using var gridPaint = new SKPaint
        {
            Color = gridColor,
            StrokeWidth = 1,
            IsAntialias = true
        };

        for (var i = 0; i <= 6; i++)
        {
            var y = chartRect.Top + chartRect.Height / 6 * i;
            canvas.DrawLine(chartRect.Left, y, chartRect.Right, y, gridPaint);
        }

        for (var i = 0; i <= 10; i++)
        {
            var x = chartRect.Left + chartRect.Width / 10 * i;
            canvas.DrawLine(x, chartRect.Top, x, chartRect.Bottom, gridPaint);
        }
    }

    private static void DrawCandles(
        SKCanvas canvas,
        List<Candle> candles,
        SKRect chartRect,
        decimal minPrice,
        decimal maxPrice,
        SKColor bullColor,
        SKColor bearColor)
    {
        var candleCount = candles.Count;
        var slotWidth = chartRect.Width / candleCount;
        var candleBodyWidth = Math.Max(4, slotWidth * 0.58f);

        using var wickPaint = new SKPaint
        {
            StrokeWidth = 2,
            IsAntialias = true
        };

        using var bodyPaint = new SKPaint
        {
            IsAntialias = true
        };

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

            if (Math.Abs(bodyBottom - bodyTop) < 2)
            {
                bodyBottom = bodyTop + 2;
            }

            var bodyRect = new SKRect(
                x - candleBodyWidth / 2,
                bodyTop,
                x + candleBodyWidth / 2,
                bodyBottom);

            canvas.DrawRect(bodyRect, bodyPaint);
        }
    }

    private static void DrawPriceLine(
        SKCanvas canvas,
        SKRect chartRect,
        SKRect priceScaleRect,
        decimal minPrice,
        decimal maxPrice,
        decimal price,
        string label,
        SKColor color)
    {
        var y = PriceToY(price, chartRect, minPrice, maxPrice);

        using var linePaint = new SKPaint
        {
            Color = color,
            StrokeWidth = 2,
            IsAntialias = true,
            PathEffect = SKPathEffect.CreateDash(new[] { 8f, 6f }, 0)
        };

        canvas.DrawLine(chartRect.Left, y, chartRect.Right, y, linePaint);

        using var labelBackgroundPaint = new SKPaint
        {
            Color = color,
            IsAntialias = true
        };

        using var labelTextPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 17,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        var text = $"{label} {FormatPrice(price)}";
        var textWidth = labelTextPaint.MeasureText(text);

        var rect = new SKRect(
            priceScaleRect.Left + 8,
            y - 16,
            priceScaleRect.Left + 22 + textWidth,
            y + 16);

        canvas.DrawRoundRect(
            new SKRoundRect(rect, 7, 7),
            labelBackgroundPaint);

        canvas.DrawText(
            text,
            rect.Left + 7,
            y + 6,
            labelTextPaint);
    }

    private static void DrawCurrentPriceLabel(
        SKCanvas canvas,
        SKRect chartRect,
        SKRect priceScaleRect,
        decimal price,
        decimal minPrice,
        decimal maxPrice)
    {
        var y = PriceToY(price, chartRect, minPrice, maxPrice);

        using var paint = new SKPaint
        {
            Color = SKColor.Parse("#64748B"),
            IsAntialias = true
        };

        using var textPaint = new SKPaint
        {
            Color = SKColors.White,
            IsAntialias = true,
            TextSize = 16,
            Typeface = SKTypeface.FromFamilyName("Arial", SKFontStyle.Bold)
        };

        var text = $"LAST {FormatPrice(price)}";
        var textWidth = textPaint.MeasureText(text);

        var rect = new SKRect(
            priceScaleRect.Left + 8,
            y - 15,
            priceScaleRect.Left + 22 + textWidth,
            y + 15);

        canvas.DrawRoundRect(
            new SKRoundRect(rect, 7, 7),
            paint);

        canvas.DrawText(
            text,
            rect.Left + 7,
            y + 6,
            textPaint);
    }

    private static float PriceToY(
        decimal price,
        SKRect chartRect,
        decimal minPrice,
        decimal maxPrice)
    {
        var range = maxPrice - minPrice;

        if (range <= 0)
            return chartRect.MidY;

        var percentage = (price - minPrice) / range;

        return chartRect.Bottom - (float)percentage * chartRect.Height;
    }

    private static string FormatPrice(decimal price)
    {
        return price.ToString("0.#####");
    }
}
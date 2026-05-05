using System.Text.Json;
using PocketSignal.Api.Models.Admin;

namespace PocketSignal.Api.Services.Admin;

public class AdminRuntimeSettingsService : IAdminRuntimeSettingsService
{
    private readonly string _filePath;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AdminRuntimeSettingsService(IWebHostEnvironment environment)
    {
        _filePath = Path.Combine(
            environment.ContentRootPath,
            "admin-settings.json");
    }

    public async Task<AdminRuntimeSettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            if (!File.Exists(_filePath))
            {
                var fresh = CreateDefaultSettings();
                await SaveInternalAsync(fresh, cancellationToken);
                return fresh;
            }

            var json = await File.ReadAllTextAsync(
                _filePath,
                cancellationToken);

            var settings = JsonSerializer.Deserialize<AdminRuntimeSettings>(
                json,
                JsonOptions);

            settings ??= CreateDefaultSettings();

            Normalize(settings);

            return settings;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AdminRuntimeSettings> UpdateAsync(
        AdminRuntimeSettingsUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            AdminRuntimeSettings settings;

            if (File.Exists(_filePath))
            {
                var json = await File.ReadAllTextAsync(
                    _filePath,
                    cancellationToken);

                settings = JsonSerializer.Deserialize<AdminRuntimeSettings>(
                    json,
                    JsonOptions) ?? CreateDefaultSettings();
            }
            else
            {
                settings = CreateDefaultSettings();
            }

            Normalize(settings);

            settings.BinaryEnabled = request.BinaryEnabled;
            settings.ForexEnabled = request.ForexEnabled;

            if (settings.BinarySymbols.Contains(request.BinaryActiveSymbol))
                settings.BinaryActiveSymbol = request.BinaryActiveSymbol;

            if (settings.ForexSymbols.Contains(request.ForexActiveSymbol))
                settings.ForexActiveSymbol = request.ForexActiveSymbol;

            settings.Mt5AutoTradeEnabled = request.Mt5AutoTradeEnabled;
            settings.Mt5LotSize = ClampLotSize(request.Mt5LotSize);
            settings.Mt5TakeProfitMode = NormalizeTakeProfitMode(request.Mt5TakeProfitMode);
            settings.Mt5MinimumConfidence = Clamp(request.Mt5MinimumConfidence, 50, 99);
            settings.Mt5MinimumGrade = NormalizeGrade(request.Mt5MinimumGrade);
            settings.Mt5CooldownMinutes = Clamp(request.Mt5CooldownMinutes, 0, 1440);
            settings.Mt5MaxPendingMinutes = Clamp(
                request.Mt5MaxPendingMinutes <= 0 ? 10 : request.Mt5MaxPendingMinutes,
                1,
                1440);
            settings.Mt5MaxTradesPerDay = Clamp(request.Mt5MaxTradesPerDay, 1, 100);
            settings.Mt5DemoOnly = request.Mt5DemoOnly;
            settings.Mt5OnePositionPerSymbol = request.Mt5OnePositionPerSymbol;
            settings.UpdatedAtUtc = DateTime.UtcNow;

            await SaveInternalAsync(settings, cancellationToken);

            return settings;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task SaveInternalAsync(
        AdminRuntimeSettings settings,
        CancellationToken cancellationToken)
    {
        Normalize(settings);

        var json = JsonSerializer.Serialize(
            settings,
            JsonOptions);

        await File.WriteAllTextAsync(
            _filePath,
            json,
            cancellationToken);
    }

    private static AdminRuntimeSettings CreateDefaultSettings()
    {
        var settings = new AdminRuntimeSettings
        {
            UpdatedAtUtc = DateTime.UtcNow
        };

        Normalize(settings);

        return settings;
    }

    private static void Normalize(AdminRuntimeSettings settings)
    {
        var defaultBinarySymbols = new List<string>
    {
        "EUR/USD",
        "GBP/USD",
        "USD/JPY",
        "EUR/GBP",
        "GBP/JPY",
        "AUD/USD",
        "USD/CAD",
        "EUR/JPY",

        "AUD/JPY",
        "EUR/CAD",
        "CAD/CHF",
        "USD/CHF",
        "CHF/JPY",
        "AUD/CHF",
        "GBP/AUD",
        "EUR/AUD",
        "CAD/JPY",
        "AUD/CAD"
    };

        var defaultForexSymbols = new List<string>
    {
        "GBP/JPY",
        "EUR/USD",
        "USD/JPY",
        "EUR/GBP",
        "GBP/USD",
        "AUD/USD",
        "USD/CAD",
        "EUR/JPY",

        "AUD/JPY",
        "EUR/CAD",
        "CAD/CHF",
        "USD/CHF",
        "CHF/JPY",
        "AUD/CHF",
        "GBP/AUD",
        "EUR/AUD",
        "CAD/JPY",
        "AUD/CAD",

        "BTC/USD",
        "ETH/USD",
        "XAU/USD",
        "USOIL"
    };

        settings.BinarySymbols = MergeSymbols(
            settings.BinarySymbols,
            defaultBinarySymbols);

        settings.ForexSymbols = MergeSymbols(
            settings.ForexSymbols,
            defaultForexSymbols);

        if (!settings.BinarySymbols.Contains(settings.BinaryActiveSymbol))
            settings.BinaryActiveSymbol = settings.BinarySymbols.First();

        if (!settings.ForexSymbols.Contains(settings.ForexActiveSymbol))
            settings.ForexActiveSymbol = settings.ForexSymbols.First();

        settings.Mt5LotSize = ClampLotSize(settings.Mt5LotSize);
        settings.Mt5TakeProfitMode = NormalizeTakeProfitMode(settings.Mt5TakeProfitMode);
        settings.Mt5MinimumConfidence = Clamp(settings.Mt5MinimumConfidence, 50, 99);
        settings.Mt5MinimumGrade = NormalizeGrade(settings.Mt5MinimumGrade);
        settings.Mt5CooldownMinutes = Clamp(settings.Mt5CooldownMinutes, 0, 1440);

        if (settings.Mt5MaxPendingMinutes <= 0)
            settings.Mt5MaxPendingMinutes = 10;
        else
            settings.Mt5MaxPendingMinutes = Clamp(settings.Mt5MaxPendingMinutes, 1, 1440);

        settings.Mt5MaxTradesPerDay = Clamp(settings.Mt5MaxTradesPerDay, 1, 100);
    }

    private static List<string> MergeSymbols(
    List<string>? current,
    List<string> defaults)
    {
        var result = new List<string>();

        if (current != null)
        {
            foreach (var symbol in current)
            {
                var normalized = NormalizeSymbol(symbol);

                if (string.IsNullOrWhiteSpace(normalized))
                    continue;

                if (!result.Contains(normalized))
                    result.Add(normalized);
            }
        }

        foreach (var symbol in defaults)
        {
            var normalized = NormalizeSymbol(symbol);

            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            if (!result.Contains(normalized))
                result.Add(normalized);
        }

        return result;
    }

    private static string NormalizeSymbol(string symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
            return string.Empty;

        return symbol
            .Trim()
            .Replace("-", "/")
            .Replace(" ", "")
            .ToUpperInvariant();
    }

    private static double ClampLotSize(double value)
    {
        if (value < 0.01)
            return 0.01;

        if (value > 10)
            return 10;

        return Math.Round(value, 2);
    }

    private static string NormalizeTakeProfitMode(string value)
    {
        value = value.Trim().ToUpperInvariant();

        return value == "TP2"
            ? "TP2"
            : "TP1";
    }

    private static string NormalizeGrade(string value)
    {
        value = value.Trim().ToUpperInvariant();

        return value switch
        {
            "A+" => "A+",
            "A" => "A",
            "B" => "B",
            _ => "B"
        };
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min)
            return min;

        if (value > max)
            return max;

        return value;
    }
}
using System.Text.Json;
using PocketSignal.Api.Models.Admin;

namespace PocketSignal.Api.Services.Admin;

public class AdminRuntimeSettingsService : IAdminRuntimeSettingsService
{
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<AdminRuntimeSettingsService> _logger;
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AdminRuntimeSettingsService(
        IWebHostEnvironment environment,
        ILogger<AdminRuntimeSettingsService> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    public async Task<AdminRuntimeSettings> GetAsync(
        CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken);

        try
        {
            var path = GetSettingsPath();

            if (!File.Exists(path))
            {
                var defaultSettings = CreateDefaultSettings();
                await SaveInternalAsync(defaultSettings, cancellationToken);
                return defaultSettings;
            }

            var json = await File.ReadAllTextAsync(path, cancellationToken);

            if (string.IsNullOrWhiteSpace(json))
            {
                var defaultSettings = CreateDefaultSettings();
                await SaveInternalAsync(defaultSettings, cancellationToken);
                return defaultSettings;
            }

            var settings = JsonSerializer.Deserialize<AdminRuntimeSettings>(
                json,
                JsonOptions);

            if (settings == null)
            {
                var defaultSettings = CreateDefaultSettings();
                await SaveInternalAsync(defaultSettings, cancellationToken);
                return defaultSettings;
            }

            NormalizeSettings(settings);
            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin runtime settings oxunarken xeta bas verdi.");
            return CreateDefaultSettings();
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
            var settings = CreateDefaultSettings();

            var path = GetSettingsPath();

            if (File.Exists(path))
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken);

                if (!string.IsNullOrWhiteSpace(json))
                {
                    var existing = JsonSerializer.Deserialize<AdminRuntimeSettings>(
                        json,
                        JsonOptions);

                    if (existing != null)
                    {
                        settings = existing;
                    }
                }
            }

            NormalizeSettings(settings);

            settings.BinaryEnabled = request.BinaryEnabled;
            settings.ForexEnabled = request.ForexEnabled;

            if (settings.BinarySymbols.Contains(request.BinaryActiveSymbol))
            {
                settings.BinaryActiveSymbol = request.BinaryActiveSymbol;
            }

            if (settings.ForexSymbols.Contains(request.ForexActiveSymbol))
            {
                settings.ForexActiveSymbol = request.ForexActiveSymbol;
            }

            settings.UpdatedAtUtc = DateTime.UtcNow;

            await SaveInternalAsync(settings, cancellationToken);

            return settings;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Admin runtime settings update olunarken xeta bas verdi.");
            throw;
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
        NormalizeSettings(settings);

        var json = JsonSerializer.Serialize(
            settings,
            JsonOptions);

        await File.WriteAllTextAsync(
            GetSettingsPath(),
            json,
            cancellationToken);
    }

    private string GetSettingsPath()
    {
        return Path.Combine(
            _environment.ContentRootPath,
            "admin-settings.json");
    }

    private static AdminRuntimeSettings CreateDefaultSettings()
    {
        var settings = new AdminRuntimeSettings();
        NormalizeSettings(settings);
        return settings;
    }

    private static void NormalizeSettings(AdminRuntimeSettings settings)
    {
        if (settings.BinarySymbols == null || settings.BinarySymbols.Count == 0)
        {
            settings.BinarySymbols = new List<string>
            {
                "EUR/USD",
                "GBP/USD",
                "USD/JPY",
                "EUR/GBP",
                "GBP/JPY",
                "AUD/USD",
                "USD/CAD",
                "EUR/JPY"
            };
        }

        if (settings.ForexSymbols == null || settings.ForexSymbols.Count == 0)
        {
            settings.ForexSymbols = new List<string>
            {
                "GBP/JPY",
                "EUR/USD",
                "USD/JPY",
                "EUR/GBP",
                "GBP/USD",
                "BTC/USD",
                "ETH/USD",
                "XAU/USD",
                "USOIL"
            };
        }

        if (string.IsNullOrWhiteSpace(settings.BinaryActiveSymbol) ||
            !settings.BinarySymbols.Contains(settings.BinaryActiveSymbol))
        {
            settings.BinaryActiveSymbol = settings.BinarySymbols.First();
        }

        if (string.IsNullOrWhiteSpace(settings.ForexActiveSymbol) ||
            !settings.ForexSymbols.Contains(settings.ForexActiveSymbol))
        {
            settings.ForexActiveSymbol = settings.ForexSymbols.First();
        }

        if (settings.UpdatedAtUtc == default)
        {
            settings.UpdatedAtUtc = DateTime.UtcNow;
        }
    }
}
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data;
using PocketSignal.Api.Services;
using PocketSignal.Api.Services.Admin;
using PocketSignal.Api.Services.Binary;
using PocketSignal.Api.Services.Forex;
using PocketSignal.Api.Services.MarketData;
using PocketSignal.Api.Services.Stats;
using PocketSignal.Api.Services.Telegram;
using PocketSignal.Api.Services.Workers;

var builder = WebApplication.CreateBuilder(args);

var wwwrootPath = Path.Combine(
    builder.Environment.ContentRootPath,
    "wwwroot");

Directory.CreateDirectory(wwwrootPath);
Directory.CreateDirectory(Path.Combine(wwwrootPath, "forex-charts"));
Directory.CreateDirectory(Path.Combine(wwwrootPath, "binary-charts"));

builder.WebHost.UseWebRoot(wwwrootPath);

builder.Services.AddControllers();

builder.Services.AddHttpClient();

builder.Services.AddMemoryCache();

builder.Services.AddDbContext<PocketSignalDbContext>(options =>
{
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("PocketSignalDb"));
});

builder.Services.AddHttpClient<IMarketDataService, TwelveDataMarketDataService>(client =>
{
    client.BaseAddress = new Uri("https://api.twelvedata.com/");
});

// Admin runtime settings
builder.Services.AddSingleton<IAdminRuntimeSettingsService, AdminRuntimeSettingsService>();

// Binary services
builder.Services.AddScoped<ISmartSignalService, SmartMoneySignalService>();
builder.Services.AddScoped<ISignalNotificationService, SignalNotificationService>();
builder.Services.AddScoped<IBinaryChartImageService, BinaryChartImageService>();
builder.Services.AddSingleton<ISignalResultTracker, SignalResultTracker>();
builder.Services.AddSingleton<IDailyStatsService, DailyStatsService>();

// Forex services
builder.Services.AddScoped<IForexSignalService, ForexSignalService>();
builder.Services.AddScoped<IForexNotificationService, ForexNotificationService>();
builder.Services.AddScoped<IForexSignalDatabaseService, ForexSignalDatabaseService>();
builder.Services.AddScoped<IForexChartImageService, ForexChartImageService>();
builder.Services.AddSingleton<IForexTradeResultTracker, ForexTradeResultTracker>();

// Telegram
builder.Services.AddHttpClient<ITelegramService, TelegramService>();

// Workers
builder.Services.AddHostedService<SignalWorker>();
builder.Services.AddHostedService<ForexWorker>();
builder.Services.AddHostedService<StartupNotificationWorker>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

Directory.CreateDirectory(app.Environment.WebRootPath ?? wwwrootPath);
Directory.CreateDirectory(Path.Combine(app.Environment.WebRootPath ?? wwwrootPath, "forex-charts"));
Directory.CreateDirectory(Path.Combine(app.Environment.WebRootPath ?? wwwrootPath, "binary-charts"));

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseAuthorization();

app.MapControllers();

app.Run();
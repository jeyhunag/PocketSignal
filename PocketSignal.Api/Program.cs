using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data;
using PocketSignal.Api.Services;
using PocketSignal.Api.Services.Admin;
using PocketSignal.Api.Services.Analysis;
using PocketSignal.Api.Services.Binary;
using PocketSignal.Api.Services.Forex;
using PocketSignal.Api.Services.MarketData;
using PocketSignal.Api.Services.Mt5;
using PocketSignal.Api.Services.Stats;
using PocketSignal.Api.Services.Telegram;
using PocketSignal.Api.Services.Workers;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(
    "http://127.0.0.1:5080",
    "https://127.0.0.1:7079");

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

// Core analysis engine
builder.Services.AddSingleton<IMarketAnalysisEngine, MarketAnalysisEngine>();

// Binary services
builder.Services.AddScoped<ISmartSignalService, CoreBinarySignalService>();
builder.Services.AddScoped<ISignalNotificationService, SignalNotificationService>();
builder.Services.AddScoped<IBinaryChartImageService, BinaryChartImageService>();
builder.Services.AddSingleton<ISignalResultTracker, SignalResultTracker>();
builder.Services.AddSingleton<IDailyStatsService, DailyStatsService>();
builder.Services.AddSingleton<IBinaryDailyResultSummaryService, BinaryDailyResultSummaryService>();

// Forex services
builder.Services.AddScoped<CoreForexSignalService>();
builder.Services.AddScoped<XauUsdScalpingSignalService>();
builder.Services.AddScoped<IForexSignalService, ForexSignalRouterService>();

builder.Services.AddScoped<IForexNotificationService, ForexNotificationService>();
builder.Services.AddScoped<IForexSignalDatabaseService, ForexSignalDatabaseService>();
builder.Services.AddScoped<IForexChartImageService, ForexChartImageService>();
builder.Services.AddSingleton<IForexTradeResultTracker, ForexTradeResultTracker>();

// MT5 AutoTrade
builder.Services.AddSingleton<IMt5AutoTradeQueueService, Mt5AutoTradeQueueService>();

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

// app.UseHttpsRedirection();

app.UseStaticFiles();

app.UseAuthorization();

app.MapControllers();

app.Run();
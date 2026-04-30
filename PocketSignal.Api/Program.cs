using PocketSignal.Api.Services;
using PocketSignal.Api.Services.Binary;
using PocketSignal.Api.Services.Forex;
using PocketSignal.Api.Services.MarketData;
using PocketSignal.Api.Services.Stats;
using PocketSignal.Api.Services.Telegram;
using PocketSignal.Api.Services.Workers;
using Microsoft.EntityFrameworkCore;
using PocketSignal.Api.Data;

var builder = WebApplication.CreateBuilder(args);

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

builder.Services.AddScoped<ISmartSignalService, SmartMoneySignalService>();

builder.Services.AddHttpClient<ITelegramService, TelegramService>();

builder.Services.AddScoped<ISignalNotificationService, SignalNotificationService>();

builder.Services.AddScoped<IForexSignalService, ForexSignalService>();

builder.Services.AddScoped<IForexNotificationService, ForexNotificationService>();

builder.Services.AddScoped<IForexSignalDatabaseService, ForexSignalDatabaseService>();

builder.Services.AddSingleton<IForexTradeResultTracker, ForexTradeResultTracker>();

builder.Services.AddSingleton<IDailyStatsService, DailyStatsService>();

builder.Services.AddSingleton<ISignalResultTracker, SignalResultTracker>();

builder.Services.AddHostedService<SignalWorker>();
builder.Services.AddHostedService<ForexWorker>();
builder.Services.AddHostedService<StartupNotificationWorker>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
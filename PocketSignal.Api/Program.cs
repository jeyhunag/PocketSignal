using PocketSignal.Api.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddMemoryCache();

builder.Services.AddHttpClient<IMarketDataService, TwelveDataMarketDataService>(client =>
{
    client.BaseAddress = new Uri("https://api.twelvedata.com/");
});

builder.Services.AddScoped<ISmartSignalService, SmartMoneySignalService>();

builder.Services.AddHttpClient<ITelegramService, TelegramService>();

builder.Services.AddScoped<ISignalNotificationService, SignalNotificationService>();

builder.Services.AddSingleton<IDailyStatsService, DailyStatsService>();

builder.Services.AddSingleton<ISignalResultTracker, SignalResultTracker>();

builder.Services.AddHostedService<SignalWorker>();

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
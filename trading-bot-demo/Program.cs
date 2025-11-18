using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using TradingBotDemo.Config;
using TradingBotDemo.Exchange;
using TradingBotDemo.Services;
using TradingBotDemo.Trading;


var builder = Host.CreateApplicationBuilder(args);
var botConfig = builder.Configuration.GetSection("BotConfig").Get<BotConfig>() ?? new BotConfig();
builder.Services.AddSingleton(botConfig);
builder.Services.AddSingleton<IExchangeClient, BinanceExchangeClient>();
builder.Services.AddSingleton<ITelegramNotifier, TelegramNotifier>();
builder.Services.AddSingleton<BinanceTradingService>();
builder.Services.AddSingleton<TradingEngine>();

var host = builder.Build();

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cts.Cancel();
};

var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
logger.LogInformation("Starting aggressive trading simulation...");

var engine = host.Services.GetRequiredService<TradingEngine>();
await engine.InitializeAsync(cts.Token);

try
{
    await engine.RunAsync(cts.Token);
}
catch (OperationCanceledException)
{
    // graceful shutdown
}

logger.LogInformation("Trading simulation stopped.");

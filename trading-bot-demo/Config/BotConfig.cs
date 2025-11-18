using System.Collections.Generic;

namespace TradingBotDemo.Config;

public class BotConfig
{
    // Demo modunda 1.000 USDT sanal bakiye kullan
    public decimal InitialBalance { get; set; } = 1000m;

    // İzlenecek semboller (örnek)
    public string[] Symbols { get; set; } = { "BTCUSDT", "ETHUSDT" };

    public int CandlesLookback { get; set; } = 120;

    // Düşük kaldıraçlı, küçük notional ile daha kontrollü işlem
    public decimal BaseOrderSizeUsd { get; set; } = 1000m;
    public int Leverage { get; set; } = 5;

    // Pozisyon ve risk ayarları
    public int MaxOpenPositionsPerSymbol { get; set; } = 5;
    public decimal MaxDailyLossPercent { get; set; } = 5m;
    public decimal TakeProfitPercent { get; set; } = 1.4m;
    public decimal StopLossPercent { get; set; } = 0.6m;
    public decimal MinVolatilityThreshold { get; set; } = 0.05m;

    public int RiskOffCooldownMinutes { get; set; } = 0;
    public int MinSecondsBetweenTrades { get; set; } = 0;

    public string RestBaseUrl { get; set; } = "https://fapi.binance.com";
    public string WebSocketBaseUrl { get; set; } = "wss://fstream.binance.com/ws";
    public string KlinesEndpoint { get; set; } = "/fapi/v1/klines";
    public string PriceTickerEndpoint { get; set; } = "/fapi/v1/ticker/price";
    public string OrderEndpoint { get; set; } = "/fapi/v1/order";

    public bool LogPerSecond { get; set; } = true;

    public bool EnableTelegramNotifications { get; set; } = true;
    // Token/chat ID değerlerini appsettings.json veya ortam değişkeniyle geç (repo'da boş bırakıldı)
    public string TelegramBotToken { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    public int TelegramStatusIntervalMinutes { get; set; } = 1;

    // Github paylaşımı için varsayılan olarak canlı mod kapalı
    public bool EnableLiveTrading { get; set; } = false;

    // Canlı modda bakiyenin ne kadarı kullanılacak (0-1)
    public decimal LiveTradingBalanceFraction { get; set; } = 0.3m;

    // Binance API anahtarları -> appsettings/ENV üzerinden doldurun (repo'da boş)
    public string BinanceApiKey { get; set; } = "";
    public string BinanceApiSecret { get; set; } = "";

    // Binance lot-size precision / min notional örnekleri
    public Dictionary<string, SymbolPrecision> SymbolPrecisions { get; set; } = new()
    {
        ["BTCUSDT"] = new SymbolPrecision
        {
            QuantityPrecision = 3,
            StepSize = 0.001m,
            MinNotional = 5m
        },
        ["ETHUSDT"] = new SymbolPrecision
        {
            QuantityPrecision = 3,
            StepSize = 0.001m,
            MinNotional = 5m
        }
    };
}

public class SymbolPrecision
{
    public int QuantityPrecision { get; set; } = 3;
    public decimal StepSize { get; set; } = 0.001m;
    public decimal MinNotional { get; set; } = 20m;
}

using TradingBotDemo.Models;

namespace TradingBotDemo.Exchange;

public interface IExchangeClient
{
    Task<List<Candle>> GetRecentCandlesAsync(string symbol, string interval, int limit, CancellationToken ct = default);
    IAsyncEnumerable<TickerUpdate> StreamTickerAsync(string symbol, CancellationToken ct = default);
    Task<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct = default);
}

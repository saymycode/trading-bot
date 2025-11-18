using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TradingBotDemo.Config;
using TradingBotDemo.Models;

namespace TradingBotDemo.Exchange;

public class BinanceExchangeClient : IExchangeClient, IAsyncDisposable
{
    private const int WebSocketConnectTimeoutSeconds = 5;
    private const int WebSocketReceiveTimeoutSeconds = 10;
    private const int RestFallbackSeconds = 15;

    private readonly BotConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ILogger<BinanceExchangeClient> _logger;

    public BinanceExchangeClient(BotConfig config, ILogger<BinanceExchangeClient> logger)
    {
        _config = config;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.RestBaseUrl)
        };
    }

    public async Task<List<Candle>> GetRecentCandlesAsync(string symbol, string interval, int limit, CancellationToken ct = default)
    {
        var endpoint = string.IsNullOrWhiteSpace(_config.KlinesEndpoint) ? "/api/v3/klines" : _config.KlinesEndpoint;
        var requestUrl = $"{endpoint}?symbol={symbol.ToUpperInvariant()}&interval={interval}&limit={limit}";
        using var response = await _httpClient.GetAsync(requestUrl, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var candles = new List<Candle>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            candles.Add(new Candle
            {
                OpenTime = DateTimeOffset.FromUnixTimeMilliseconds(item[0].GetInt64()).UtcDateTime,
                Open = decimal.Parse(item[1].GetString()!, CultureInfo.InvariantCulture),
                High = decimal.Parse(item[2].GetString()!, CultureInfo.InvariantCulture),
                Low = decimal.Parse(item[3].GetString()!, CultureInfo.InvariantCulture),
                Close = decimal.Parse(item[4].GetString()!, CultureInfo.InvariantCulture),
                Volume = decimal.Parse(item[5].GetString()!, CultureInfo.InvariantCulture)
            });
        }

        return candles;
    }

    public async IAsyncEnumerable<TickerUpdate> StreamTickerAsync(
        string symbol,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var lower = symbol.ToLowerInvariant();
        var wsEndpoint = _config.WebSocketBaseUrl.TrimEnd('/') + $"/{lower}@miniTicker";

        while (!ct.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(WebSocketConnectTimeoutSeconds));

            var buffer = new byte[4096];
            var memory = new MemoryStream();

            var connected = false;
            var addReconnectDelay = false;
            try
            {
                await socket.ConnectAsync(new Uri(wsEndpoint), connectCts.Token);
                connected = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("WebSocket connect timeout for {Symbol}. Falling back to REST price polling.", symbol);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "WebSocket connect error for {Symbol}. Falling back to REST price polling.", symbol);
                addReconnectDelay = true;
            }

            if (!connected)
            {
                await foreach (var fallbackTick in PollPriceFallback(symbol, RestFallbackSeconds, ct))
                {
                    yield return fallbackTick;
                }

                if (addReconnectDelay && !ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }

                continue;
            }

            while (!ct.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                memory.SetLength(0);
                WebSocketReceiveResult? result = null;

                try
                {
                    using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    receiveCts.CancelAfter(TimeSpan.FromSeconds(WebSocketReceiveTimeoutSeconds));

                    do
                    {
                        result = await socket.ReceiveAsync(buffer, receiveCts.Token);

                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", ct);
                            break;
                        }

                        memory.Write(buffer, 0, result.Count);
                    }
                    while (!result!.EndOfMessage);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    yield break;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("WebSocket receive timeout for {Symbol}. Reconnecting...", symbol);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "WebSocket receive error for {Symbol}. Reconnecting...", symbol);
                    break;
                }

                if (result == null || result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                decimal lastPrice;
                DateTime time;

                try
                {
                    memory.Seek(0, SeekOrigin.Begin);
                    using var doc = await JsonDocument.ParseAsync(memory, cancellationToken: ct);
                    var root = doc.RootElement;

                    lastPrice = decimal.Parse(root.GetProperty("c").GetString()!, CultureInfo.InvariantCulture);
                    time = DateTimeOffset.FromUnixTimeMilliseconds(root.GetProperty("E").GetInt64()).UtcDateTime;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "JSON parse error for {Symbol}. Skipping message.", symbol);
                    continue;
                }

                yield return new TickerUpdate
                {
                    Symbol = symbol,
                    Time = time,
                    LastPrice = lastPrice,
                    BestBid = lastPrice,
                    BestAsk = lastPrice
                };
            }

            if (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    private async IAsyncEnumerable<TickerUpdate> PollPriceFallback(
        string symbol,
        int durationSeconds,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var end = DateTime.UtcNow.AddSeconds(Math.Max(1, durationSeconds));
        while (!ct.IsCancellationRequested && DateTime.UtcNow < end)
        {
            decimal price;
            try
            {
                price = await GetCurrentPriceAsync(symbol, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                yield break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "REST price poll failed for {Symbol}.", symbol);
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }

            var now = DateTime.UtcNow;
            yield return new TickerUpdate
            {
                Symbol = symbol,
                Time = now,
                LastPrice = price,
                BestBid = price,
                BestAsk = price
            };

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    public async Task<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct = default)
    {
        var endpoint = string.IsNullOrWhiteSpace(_config.PriceTickerEndpoint) ? "/api/v3/ticker/price" : _config.PriceTickerEndpoint;
        var requestUrl = $"{endpoint}?symbol={symbol.ToUpperInvariant()}";
        using var response = await _httpClient.GetAsync(requestUrl, ct);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        return decimal.Parse(doc.RootElement.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

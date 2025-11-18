using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.Logging;
using TradingBotDemo.Config;
using TradingBotDemo.Models;

namespace TradingBotDemo.Exchange;

public class BinanceTradingService : IAsyncDisposable
{
    private readonly BotConfig _config;
    private readonly ILogger<BinanceTradingService> _logger;
    private readonly HttpClient _httpClient;
    private readonly Dictionary<string, SymbolPrecision> _symbolPrecisions;
    private readonly HashSet<string> _initializedLeverage = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _leverageLock = new(1, 1);

    public BinanceTradingService(BotConfig config, ILogger<BinanceTradingService> logger)
    {
        _config = config;
        _logger = logger;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(config.RestBaseUrl)
        };
        _symbolPrecisions = config.SymbolPrecisions ?? new();

        if (!string.IsNullOrWhiteSpace(_config.BinanceApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-MBX-APIKEY", _config.BinanceApiKey);
        }
    }

    public async Task<OrderResult> PlaceOrderAsync(NewOrderRequest request, CancellationToken ct = default)
    {
        if (!_config.EnableLiveTrading)
        {
            return new OrderResult { Success = false, Message = "Live trading disabled" };
        }

        if (string.IsNullOrWhiteSpace(_config.BinanceApiKey) || string.IsNullOrWhiteSpace(_config.BinanceApiSecret))
        {
            return new OrderResult { Success = false, Message = "Binance API key/secret missing" };
        }

        await EnsureLeverageAsync(request.Symbol, ct);

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var normalizedQuantity = NormalizeQuantity(request.Symbol, request.Quantity);
        if (normalizedQuantity <= 0)
        {
            return new OrderResult { Success = false, Message = "Order quantity too small after precision adjustment" };
        }

        var query = new StringBuilder();
        query.Append($"symbol={request.Symbol.ToUpperInvariant()}");
        query.Append($"&side={request.Side.ToString().ToUpperInvariant()}");
        query.Append($"&type={request.Type.ToString().ToUpperInvariant()}");
        query.Append($"&quantity={normalizedQuantity.ToString(CultureInfo.InvariantCulture)}");
        query.Append($"&timestamp={timestamp}");

        var signature = Sign(query.ToString());
        var endpointPath = string.IsNullOrWhiteSpace(_config.OrderEndpoint) ? "/api/v3/order" : _config.OrderEndpoint;
        var endpoint = $"{endpointPath}?{query}&signature={signature}";

        try
        {
            using var response = await _httpClient.PostAsync(endpoint, null, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance order rejected ({Status}): {Payload}", response.StatusCode, payload);
                return new OrderResult { Success = false, Message = payload };
            }

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var orderId = root.TryGetProperty("orderId", out var orderIdElement)
                ? orderIdElement.ToString()
                : Guid.NewGuid().ToString();

            return new OrderResult
            {
                Success = true,
                OrderId = orderId,
                ExecutedPrice = ExtractAveragePrice(root)
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Binance order failed for {Symbol}", request.Symbol);
            return new OrderResult { Success = false, Message = ex.Message };
        }
    }

    private decimal NormalizeQuantity(string symbol, decimal quantity)
    {
        if (quantity <= 0)
        {
            return 0m;
        }

        var key = symbol.ToUpperInvariant();
        var precision = 3;
        var step = 0.001m;

        if (_symbolPrecisions.TryGetValue(key, out var cfg))
        {
            precision = Math.Max(0, cfg.QuantityPrecision);
            step = cfg.StepSize > 0 ? cfg.StepSize : step;
        }

        // Floor to the nearest allowed step so we never exceed Binance precision.
        var adjusted = Math.Floor(quantity / step) * step;
        if (adjusted <= 0)
        {
            adjusted = step; // enforce minimum tradable size
        }

        return decimal.Round(adjusted, precision, MidpointRounding.ToZero);
    }

    public async Task<LiveAccountSnapshot?> GetAccountSnapshotAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_config.BinanceApiKey) || string.IsNullOrWhiteSpace(_config.BinanceApiSecret))
        {
            return null;
        }

        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var query = $"timestamp={timestamp}";
        var signature = Sign(query);
        var endpoint = $"/fapi/v2/account?{query}&signature={signature}";

        try
        {
            using var response = await _httpClient.GetAsync(endpoint, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance account snapshot failed (status {Status}): {Payload}", response.StatusCode, payload);
                return null;
            }

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            decimal walletBalance = TryGetDecimal(root, "totalWalletBalance");
            decimal totalUnrealized = TryGetDecimal(root, "totalUnrealizedProfit");

            if (walletBalance == 0 && root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.TryGetProperty("asset", out var assetCode) &&
                        string.Equals(assetCode.GetString(), "USDT", StringComparison.OrdinalIgnoreCase))
                    {
                        walletBalance = TryGetDecimal(asset, "walletBalance", walletBalance);
                        break;
                    }
                }
            }

            if (totalUnrealized == 0 && root.TryGetProperty("positions", out var positions) && positions.ValueKind == JsonValueKind.Array)
            {
                foreach (var pos in positions.EnumerateArray())
                {
                    totalUnrealized += TryGetDecimal(pos, "unRealizedProfit");
                }
            }

            return new LiveAccountSnapshot
            {
                WalletBalance = walletBalance,
                UnrealizedPnl = totalUnrealized
            };
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error fetching Binance account snapshot");
            return null;
        }
    }

    private static decimal TryGetDecimal(JsonElement element, string propertyName, decimal fallback = 0m)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            var str = prop.GetString();
            if (!string.IsNullOrWhiteSpace(str) && decimal.TryParse(str, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return fallback;
    }

    private async Task EnsureLeverageAsync(string symbol, CancellationToken ct)
    {
        var key = symbol.ToUpperInvariant();
        if (_config.Leverage <= 0)
        {
            return;
        }

        if (_initializedLeverage.Contains(key))
        {
            return;
        }

        await _leverageLock.WaitAsync(ct);
        try
        {
            if (_initializedLeverage.Contains(key))
            {
                return;
            }

            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var query = $"symbol={key}&leverage={_config.Leverage}&timestamp={timestamp}";
            var signature = Sign(query);
            var endpoint = $"/fapi/v1/leverage?{query}&signature={signature}";

            using var response = await _httpClient.PostAsync(endpoint, null, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance leverage set failed for {Symbol} (status {Status}): {Payload}", key, response.StatusCode, payload);
                return;
            }

            _initializedLeverage.Add(key);
            _logger.LogInformation("Binance leverage set to {Lev} for {Symbol}", _config.Leverage, key);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error setting leverage for {Symbol}", key);
        }
        finally
        {
            _leverageLock.Release();
        }
    }

    private static decimal ExtractAveragePrice(JsonElement root)
    {
        if (root.TryGetProperty("fills", out var fills) && fills.ValueKind == JsonValueKind.Array && fills.GetArrayLength() > 0)
        {
            decimal totalQuote = 0m;
            decimal totalQty = 0m;
            foreach (var fill in fills.EnumerateArray())
            {
                var price = decimal.Parse(fill.GetProperty("price").GetString()!, CultureInfo.InvariantCulture);
                var qty = decimal.Parse(fill.GetProperty("qty").GetString()!, CultureInfo.InvariantCulture);
                totalQuote += price * qty;
                totalQty += qty;
            }

            if (totalQty > 0)
            {
                return totalQuote / totalQty;
            }
        }

        if (root.TryGetProperty("price", out var priceElement))
        {
            var priceStr = priceElement.GetString();
            if (!string.IsNullOrWhiteSpace(priceStr))
            {
                return decimal.Parse(priceStr, CultureInfo.InvariantCulture);
            }
        }

        return 0m;
    }

    private string Sign(string query)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_config.BinanceApiSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(query));
        var builder = new StringBuilder();
        foreach (var b in hash)
        {
            builder.Append(b.ToString("x2"));
        }

        return builder.ToString();
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        _leverageLock.Dispose();
        return ValueTask.CompletedTask;
    }
}

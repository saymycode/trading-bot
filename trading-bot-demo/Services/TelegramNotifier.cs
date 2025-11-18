using System.Collections.Generic;
using System.Net.Http;
using Microsoft.Extensions.Logging;
using TradingBotDemo.Config;

namespace TradingBotDemo.Services;

public interface ITelegramNotifier : IDisposable
{
    Task NotifyTradeAsync(string message, CancellationToken ct = default);
}

public sealed class TelegramNotifier : ITelegramNotifier
{
    private readonly BotConfig _config;
    private readonly ILogger<TelegramNotifier> _logger;
    private readonly HttpClient _httpClient;

    public TelegramNotifier(BotConfig config, ILogger<TelegramNotifier> logger)
    {
        _config = config;
        _logger = logger;
        _httpClient = new HttpClient();
    }

    public async Task NotifyTradeAsync(string message, CancellationToken ct = default)
    {
        if (!_config.EnableTelegramNotifications || string.IsNullOrWhiteSpace(_config.TelegramBotToken) || string.IsNullOrWhiteSpace(_config.TelegramChatId))
        {
            return;
        }

        var url = $"https://api.telegram.org/bot{_config.TelegramBotToken}/sendMessage";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["chat_id"] = _config.TelegramChatId,
            ["text"] = message,
            ["parse_mode"] = "Markdown"
        });

        try
        {
            _logger.LogInformation("Sending Telegram message: {Message}", message);
            using var response = await _httpClient.PostAsync(url, content, ct);
            if (!response.IsSuccessStatusCode)
            {
                var payload = await response.Content.ReadAsStringAsync(ct);
                _logger.LogWarning("Telegram notification failed ({Status}): {Payload}", response.StatusCode, payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unable to send Telegram notification");
        }
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }
}

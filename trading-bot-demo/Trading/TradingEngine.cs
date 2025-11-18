using System.Linq;
using System.Text;
using TradingBotDemo.Config;
using TradingBotDemo.Exchange;
using TradingBotDemo.Models;
using TradingBotDemo.Services;
using Microsoft.Extensions.Logging;

namespace TradingBotDemo.Trading;

public class TradingEngine
{
    private const int AtrPeriod = 14;
    private const int BreakoutPeriod = 20;
    private readonly BotConfig _config;
    private readonly IExchangeClient _exchangeClient;
    private readonly ILogger<TradingEngine> _logger;
    private readonly ITelegramNotifier _telegramNotifier;
    private readonly BinanceTradingService _binanceTradingService;
    private readonly Dictionary<string, SymbolState> _symbolStates = new();
    private readonly List<Position> _openPositions = new();
    private readonly List<TradeEvent> _closedTrades = new();
    private readonly object _sync = new();

    private decimal _balance;
    private decimal _realizedPnl;
    private decimal _peakEquity;
    private bool _riskOff;
    private DateTime? _riskOffUntil;
    private DateTime _lastTradeTime;
    private DateTime _startTime;
    private decimal _liveUnrealizedPnl;
    private DateTime _lastAccountSync;

    /// <summary>
    /// Creates the engine with required dependencies.
    /// </summary>
    public TradingEngine(
        BotConfig config,
        IExchangeClient exchangeClient,
        ITelegramNotifier telegramNotifier,
        BinanceTradingService binanceTradingService,
        ILogger<TradingEngine> logger)
    {
        _config = config;
        _exchangeClient = exchangeClient;
        _telegramNotifier = telegramNotifier;
        _binanceTradingService = binanceTradingService;
        _logger = logger;
    }

    /// <summary>
    /// Bootstraps state: loads warm-up candles, seeds indicators, resets balances and optionally syncs live account.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct)
    {
        _startTime = DateTime.UtcNow;
        _balance = _config.InitialBalance;
        _realizedPnl = 0m;
        _peakEquity = _balance;
        _riskOff = false;
        _riskOffUntil = null;
        _lastTradeTime = DateTime.MinValue;
        _liveUnrealizedPnl = 0m;
        _lastAccountSync = DateTime.MinValue;

        if (_config.EnableLiveTrading)
        {
            await RefreshLiveAccountAsync(ct);
        }

        foreach (var symbol in _config.Symbols)
        {
            var candles = await _exchangeClient.GetRecentCandlesAsync(symbol, "1m", _config.CandlesLookback, ct);
            if (candles.Count == 0)
            {
                throw new InvalidOperationException($"No warm-up candles for {symbol}");
            }

            var ordered = candles.OrderBy(c => c.OpenTime).ToList();
            var lastClose = ordered.Last().Close;
            var nowMinute = TruncateToMinute(DateTime.UtcNow);
            var state = new SymbolState(symbol)
            {
                Candles = ordered,
                CurrentCandle = new Candle
                {
                    OpenTime = nowMinute,
                    Open = lastClose,
                    High = lastClose,
                    Low = lastClose,
                    Close = lastClose,
                    Volume = 0m
                },
                LastPrice = lastClose,
                LastUpdateTime = DateTime.UtcNow,
                Ema3 = CalculateInitialEma(ordered, 3),
                Ema9 = CalculateInitialEma(ordered, 9),
                Ema21 = CalculateInitialEma(ordered, 21),
            };
            state.PrevEma3 = state.Ema3;
            state.PrevEma9 = state.Ema9;
            state.PrevEma21 = state.Ema21;
            state.Atr = CalculateAtr(ordered.TakeLast(Math.Max(AtrPeriod, 5)).ToList());
            UpdateBreakoutLevels(state);
            state.Volatility = state.LastPrice == 0 ? 0 : state.Atr / state.LastPrice * 100m;
            _symbolStates[symbol] = state;
        }

        _logger.LogInformation("Trading engine initialized. Balance={Balance:F4}", _balance);
    }

    /// <summary>
    /// Starts symbol workers plus optional status/telegram loops.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        var tasks = _config.Symbols.Select(symbol => Task.Run(() => ProcessSymbolAsync(symbol, ct), ct)).ToList();
        if (_config.LogPerSecond)
        {
            tasks.Add(Task.Run(() => StatusLoopAsync(ct), ct));
        }

        if (_config.EnableTelegramNotifications && _config.TelegramStatusIntervalMinutes > 0)
        {
            tasks.Add(Task.Run(() => TelegramStatusLoopAsync(ct), ct));
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Streams ticker updates for a single symbol.
    /// </summary>
    private async Task ProcessSymbolAsync(string symbol, CancellationToken ct)
    {
        await foreach (var tick in _exchangeClient.StreamTickerAsync(symbol, ct))
        {
            HandleTicker(tick);
        }
    }

    /// <summary>
    /// Periodic status logger and live account sync loop.
    /// </summary>
    private async Task StatusLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            await RefreshLiveAccountAsync(ct);
            LogStatus();
        }
    }

    /// <summary>
    /// Core handler for each incoming tick: update state, check exits/entries under lock.
    /// </summary>
    private void HandleTicker(TickerUpdate tick)
    {
        if (!_symbolStates.TryGetValue(tick.Symbol, out var state))
        {
            return;
        }

        lock (_sync)
        {
            UpdateSymbolState(state, tick);
            var price = state.LastPrice;
            var exits = EvaluateExitCandidates(state, price);
            foreach (var position in exits)
            {
                ClosePosition(position, price);
            }

            UpdateRiskMetrics();

            if (ShouldBlockNewEntries())
            {
                return;
            }

            var entry = EvaluateEntry(state);
            if (entry != null)
            {
                OpenPosition(state, entry.Value);
            }
        }
    }

    /// <summary>
    /// Maintains candles, EMAs, ATR and breakout levels using the new tick.
    /// </summary>
    private void UpdateSymbolState(SymbolState state, TickerUpdate tick)
    {
        var price = tick.LastPrice;
        var tickMinute = TruncateToMinute(tick.Time);
        if (state.CurrentCandle == null)
        {
            state.CurrentCandle = new Candle
            {
                OpenTime = tickMinute,
                Open = price,
                High = price,
                Low = price,
                Close = price,
                Volume = 0m
            };
        }

        if (tickMinute > state.CurrentCandle.OpenTime)
        {
            state.CurrentCandle.Close = state.LastPrice;
            state.Candles.Add(state.CurrentCandle);
            if (state.Candles.Count > _config.CandlesLookback)
            {
                state.Candles.RemoveRange(0, state.Candles.Count - _config.CandlesLookback);
            }

            state.Atr = CalculateAtr(state.Candles.TakeLast(Math.Max(AtrPeriod, 5)).ToList());
            UpdateBreakoutLevels(state);
            state.Volatility = state.LastPrice == 0 ? 0 : state.Atr / state.LastPrice * 100m;

            state.CurrentCandle = new Candle
            {
                OpenTime = tickMinute,
                Open = price,
                High = price,
                Low = price,
                Close = price,
                Volume = 0m
            };
        }
        else
        {
            state.CurrentCandle.High = Math.Max(state.CurrentCandle.High, price);
            state.CurrentCandle.Low = Math.Min(state.CurrentCandle.Low, price);
            state.CurrentCandle.Close = price;
        }

        state.LastPrice = price;
        state.LastUpdateTime = tick.Time;

        var ema3 = UpdateEma(state.Ema3, price, 3);
        var ema9 = UpdateEma(state.Ema9, price, 9);
        var ema21 = UpdateEma(state.Ema21, price, 21);
        state.PrevEma3 = state.Ema3;
        state.Ema3 = ema3;
        state.PrevEma9 = state.Ema9;
        state.PrevEma21 = state.Ema21;
        state.Ema9 = ema9;
        state.Ema21 = ema21;
    }

    /// <summary>
    /// Checks open positions for take-profit, stop-loss or EMA flip exits.
    /// </summary>
    private List<Position> EvaluateExitCandidates(SymbolState state, decimal price)
    {
        var exits = new List<Position>();
        foreach (var position in _openPositions.Where(p => p.IsOpen && p.Symbol == state.Symbol))
        {
            var pnlPercent = CalculatePnlPercent(position, price);
            var takeProfitHit = pnlPercent >= _config.TakeProfitPercent;
            var stopLossHit = pnlPercent <= -_config.StopLossPercent;
            var emaFlip = position.Side == PositionSide.Long
                ? state.Ema9 < state.Ema21 && state.PrevEma9 >= state.PrevEma21
                : state.Ema9 > state.Ema21 && state.PrevEma9 <= state.PrevEma21;

            if (takeProfitHit || stopLossHit || emaFlip)
            {
                exits.Add(position);
            }
        }

        return exits;
    }

    /// <summary>
    /// Builds a long/short signal if volatility and breakout/EMA conditions allow.
    /// </summary>
    private (PositionSide side, decimal quantity)? EvaluateEntry(SymbolState state)
    {
        if (state.Volatility < _config.MinVolatilityThreshold)
        {
            return null;
        }

        // Sembol başına maksimum pozisyon sayısını uygula
        var openForSymbol = _openPositions.Count(p => p.IsOpen && p.Symbol == state.Symbol);
        if (_config.MaxOpenPositionsPerSymbol > 0 && openForSymbol >= _config.MaxOpenPositionsPerSymbol)
        {
            return null;
        }

        var price = state.LastPrice;
        if (price <= 0)
        {
            return null;
        }

        var orderBudget = _config.BaseOrderSizeUsd;
        if (_config.EnableLiveTrading && _config.LiveTradingBalanceFraction > 0)
        {
            var fraction = Math.Min(Math.Max(_config.LiveTradingBalanceFraction, 0m), 1m);
            if (fraction > 0m)
            {
                orderBudget = Math.Min(orderBudget, _balance * fraction);
            }
        }

        var quantity = (orderBudget * _config.Leverage) / price;
        if (quantity <= 0)
        {
            return null;
        }

        var breakoutLong = price > state.HighestHigh;
        var breakoutShort = price < state.LowestLow;
        var emaBullCross = state.PrevEma9 <= state.PrevEma21 && state.Ema9 > state.Ema21 && state.Ema9 > state.PrevEma9;
        var emaBearCross = state.PrevEma9 >= state.PrevEma21 && state.Ema9 < state.Ema21 && state.Ema9 < state.PrevEma9;
        var ema3SlopeUp = state.Ema3 > state.PrevEma3;
        var ema3SlopeDown = state.Ema3 < state.PrevEma3;

        if (breakoutLong || emaBullCross || ema3SlopeUp)
        {
            return (PositionSide.Long, quantity);
        }

        if (breakoutShort || emaBearCross || ema3SlopeDown)
        {
            return (PositionSide.Short, quantity);
        }

        return null;
    }

    /// <summary>
    /// Opens a simulated position and mirrors to live exchange if enabled.
    /// </summary>
    private void OpenPosition(SymbolState state, (PositionSide side, decimal quantity) signal)
    {
        var now = DateTime.UtcNow;
        _lastTradeTime = now;
        var position = new Position
        {
            Symbol = state.Symbol,
            Side = signal.side,
            Quantity = signal.quantity,
            Leverage = _config.Leverage,
            EntryPrice = state.LastPrice,
            OpenTime = now
        };
        _openPositions.Add(position);
        _logger.LogInformation("OPEN {Side} {Symbol} qty={Qty:F6} @ {Price:F4} | Balance={Bal:F4} | Equity={Equity:F4}",
            position.Side, position.Symbol, position.Quantity, position.EntryPrice, _balance, CurrentEquityUnsafe());

        MirrorLiveTrade(position, true);
        // SendTelegramUpdate(position, true, position.EntryPrice, 0m, 0m);
    }

    /// <summary>
    /// Closes a position, books PnL, updates balance and notifications.
    /// </summary>
    private void ClosePosition(Position position, decimal price)
    {
        if (!position.IsOpen)
        {
            return;
        }

        var pnl = CalculatePnl(position, price);
        var pnlPercent = CalculatePnlPercent(position, price);
        position.IsOpen = false;
        position.ClosePrice = price;
        position.CloseTime = DateTime.UtcNow;
        position.RealizedPnl = pnl;
        _balance += pnl;
        _realizedPnl += pnl;

        _closedTrades.Add(new TradeEvent
        {
            Time = position.CloseTime.Value,
            Symbol = position.Symbol,
            Side = position.Side,
            EntryPrice = position.EntryPrice,
            ExitPrice = price,
            Quantity = position.Quantity,
            Pnl = pnl,
            PnlPercent = pnlPercent
        });
        if (_closedTrades.Count > 500)
        {
            _closedTrades.RemoveRange(0, _closedTrades.Count - 500);
        }

        _logger.LogInformation("CLOSE {Side} {Symbol} qty={Qty:F6} @ {Price:F4} (entry {Entry:F4}) | PnL={Pnl:+0.0000;-0.0000} ({PnlPct:+0.0000;-0.0000}%) | Balance={Bal:F4} | Equity={Equity:F4}",
            position.Side, position.Symbol, position.Quantity, price, position.EntryPrice, pnl, pnlPercent, _balance, CurrentEquityUnsafe());

        MirrorLiveTrade(position, false);
        // SendTelegramUpdate(position, false, price, pnl, pnlPercent);
    }

    /// <summary>
    /// Logs current prices, exposure and equity snapshot.
    /// </summary>
    private void LogStatus()
    {
        lock (_sync)
        {
            var lastPrices = string.Join(" | ", _symbolStates.Values.Select(s => $"{s.Symbol}={s.LastPrice:F4}"));
            var longs = _openPositions.Count(p => p.IsOpen && p.Side == PositionSide.Long);
            var shorts = _openPositions.Count(p => p.IsOpen && p.Side == PositionSide.Short);
            var unrealized = CalculateUnrealizedPnl();
            var equity = _balance + unrealized;
            var drawdown = _peakEquity == 0 ? 0 : (_peakEquity - equity) / _peakEquity * 100m;
            var elapsed = DateTime.UtcNow - _startTime;
            _logger.LogInformation("STATUS | {Prices} | OpenPos={Longs}L/{Shorts}S | Bal={Bal:F4} | Realized={Realized:+0.0000;-0.0000} | Unrealized={Unrealized:+0.0000;-0.0000} | Equity={Equity:F4} | DD={DD:F4}% | Elapsed={Hours:D2}:{Minutes:D2}:{Seconds:D2}",
                lastPrices, longs, shorts, _balance, _realizedPnl, unrealized, equity, drawdown, (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
        }
    }

    /// <summary>
    /// Mirrors simulated trades to Binance when live trading is enabled.
    /// </summary>
    private void MirrorLiveTrade(Position position, bool opening)
    {
        if (!_config.EnableLiveTrading)
        {
            return;
        }

        var notional = Math.Abs(position.Quantity) * position.EntryPrice;
        if (_config.SymbolPrecisions != null &&
            _config.SymbolPrecisions.TryGetValue(position.Symbol, out var precision) &&
            precision.MinNotional > 0 &&
            notional < precision.MinNotional)
        {
            _logger.LogWarning("Binance {Action} order skipped for {Symbol}: notional {Notional:F4} below minimum {MinNotional:F4}",
                opening ? "open" : "close", position.Symbol, notional, precision.MinNotional);
            return;
        }

        var side = DetermineOrderSide(position.Side, opening);
        var request = new NewOrderRequest
        {
            Symbol = position.Symbol,
            Side = side,
            Quantity = Math.Abs(position.Quantity)
        };

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await _binanceTradingService.PlaceOrderAsync(request);
                if (!result.Success)
                {
                    _logger.LogWarning("Binance {Action} order failed for {Symbol}: {Message}", opening ? "open" : "close", position.Symbol, result.Message);
                }
                else
                {
                    _logger.LogInformation("Binance {Action} order executed ({OrderId}) at ~{Price:F4}", opening ? "open" : "close", result.OrderId, result.ExecutedPrice);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unable to mirror {Action} trade on Binance for {Symbol}", opening ? "open" : "close", position.Symbol);
            }
        });
    }

    /// <summary>
    /// Maps position intent (long/short, open/close) to Binance order side.
    /// </summary>
    private static OrderSide DetermineOrderSide(PositionSide positionSide, bool opening)
    {
        return (positionSide, opening) switch
        {
            (PositionSide.Long, true) => OrderSide.Buy,
            (PositionSide.Long, false) => OrderSide.Sell,
            (PositionSide.Short, true) => OrderSide.Sell,
            (PositionSide.Short, false) => OrderSide.Buy,
            _ => OrderSide.Buy
        };
    }

    /// <summary>
    /// Formats trade open/close details for Telegram notification.
    /// </summary>
    private void SendTelegramUpdate(Position position, bool opening, decimal price, decimal pnl, decimal pnlPercent)
    {
        var builder = new StringBuilder();
        builder.AppendLine(opening ? "[OPEN]" : "[CLOSE]");
        builder.AppendLine($"Pair: `{position.Symbol}`");
        builder.AppendLine($"Side: *{position.Side}*");
        builder.AppendLine($"Qty: {position.Quantity:F6}");
        builder.AppendLine($"Price: {price:F4}");
        if (!opening)
        {
            builder.AppendLine($"PnL: {pnl:+0.00;-0.00} ({pnlPercent:+0.00;-0.00}%)");
            builder.AppendLine($"Balance: {_balance:F4} | Equity: {CurrentEquityUnsafe():F4}");
        }

        SendTelegramMessage(builder.ToString());
    }

    /// <summary>
    /// Sends scheduled status updates to Telegram.
    /// </summary>
    private async Task TelegramStatusLoopAsync(CancellationToken ct)
    {
        var interval = Math.Max(1, _config.TelegramStatusIntervalMinutes);
        // Görevi başlatır başlatmaz bir durum mesajı gönder
        SendTelegramStatusSnapshot();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(interval), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            SendTelegramStatusSnapshot();
        }
    }

    /// <summary>
    /// Builds and pushes a one-off status snapshot to Telegram.
    /// </summary>
    private void SendTelegramStatusSnapshot()
    {
        StringBuilder builder;
        lock (_sync)
        {
            var unrealized = CalculateUnrealizedPnl();
            var equity = _balance + unrealized;
            var drawdown = _peakEquity == 0 ? 0 : (_peakEquity - equity) / _peakEquity * 100m;
            var openPositions = _openPositions.Where(p => p.IsOpen).ToList();
            var longs = openPositions.Count(p => p.Side == PositionSide.Long);
            var shorts = openPositions.Count(p => p.Side == PositionSide.Short);
            var lastPrices = string.Join(" | ", _symbolStates.Values.Select(s => $"{s.Symbol}={s.LastPrice:F4}"));

            builder = new StringBuilder();
            builder.AppendLine("[STATUS]");
            builder.AppendLine(lastPrices);
            builder.AppendLine($"Open positions: {longs} long / {shorts} short");
            builder.AppendLine($"Balance: {_balance:F4} | Unrealized: {unrealized:+0.0000;-0.0000} | Equity: {equity:F4}");
            builder.AppendLine($"Drawdown: {drawdown:F4}% | Closed trades: {_closedTrades.Count}");
        }

        SendTelegramMessage(builder.ToString());
    }

    /// <summary>
    /// Enqueues a Telegram message if notifications are enabled.
    /// </summary>
    private void SendTelegramMessage(string message)
    {
        if (!_config.EnableTelegramNotifications)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _telegramNotifier.NotifyTradeAsync(message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Telegram notification failed");
            }
        });
    }

    /// <summary>
    /// Updates breakout window highs/lows for entry logic.
    /// </summary>
    private void UpdateBreakoutLevels(SymbolState state)
    {
        var window = state.Candles.TakeLast(BreakoutPeriod).ToList();
        if (window.Count == 0)
        {
            state.HighestHigh = state.LastPrice;
            state.LowestLow = state.LastPrice;
        }
        else
        {
            state.HighestHigh = window.Max(c => c.High);
            state.LowestLow = window.Min(c => c.Low);
        }
    }

    /// <summary>
    /// Tracks drawdown and toggles risk-off state/cooldown.
    /// </summary>
    private void UpdateRiskMetrics()
    {
        var equity = _balance + CalculateUnrealizedPnl();
        if (equity > _peakEquity)
        {
            _peakEquity = equity;
        }

        if (_peakEquity > 0)
        {
            var dd = (_peakEquity - equity) / _peakEquity * 100m;
            if (dd >= _config.MaxDailyLossPercent)
            {
                if (!_riskOff)
                {
                    _logger.LogWarning("Risk-off mode activated. Drawdown {Drawdown:F4}% exceeds limit {Limit:F4}%.", dd, _config.MaxDailyLossPercent);
                }

                _riskOff = true;
                if (_config.RiskOffCooldownMinutes > 0)
                {
                    var until = DateTime.UtcNow.AddMinutes(_config.RiskOffCooldownMinutes);
                    if (!_riskOffUntil.HasValue || until > _riskOffUntil.Value)
                    {
                        _riskOffUntil = until;
                    }
                }
            }
            else if (dd < _config.MaxDailyLossPercent * 0.5m && _riskOff)
            {
                _riskOff = false;
                _riskOffUntil = null;
                _logger.LogInformation("Risk-off mode cleared. Drawdown back to {Drawdown:F4}%.", dd);
            }
        }
    }

    /// <summary>
    /// Applies guardrails to block new entries (risk-off, cooldown, throttle).
    /// </summary>
    private bool ShouldBlockNewEntries()
    {
        if (IsRiskOffActive())
        {
            return true;
        }

        if (_config.MinSecondsBetweenTrades > 0 && _lastTradeTime != DateTime.MinValue)
        {
            var nextAllowed = _lastTradeTime.AddSeconds(_config.MinSecondsBetweenTrades);
            if (DateTime.UtcNow < nextAllowed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns whether risk-off cooldown is still active.
    /// </summary>
    private bool IsRiskOffActive()
    {
        if (!_riskOff)
        {
            return false;
        }

        if (_riskOffUntil.HasValue && DateTime.UtcNow >= _riskOffUntil.Value)
        {
            _riskOff = false;
            _riskOffUntil = null;
            _peakEquity = CurrentEquityUnsafe();
            _logger.LogInformation("Risk-off cooldown elapsed. Resuming trading.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Aggregates unrealized PnL (live or simulated).
    /// </summary>
    private decimal CalculateUnrealizedPnl()
    {
        if (_config.EnableLiveTrading)
        {
            return _liveUnrealizedPnl;
        }

        decimal total = 0m;
        foreach (var position in _openPositions.Where(p => p.IsOpen))
        {
            if (_symbolStates.TryGetValue(position.Symbol, out var state))
            {
                total += CalculatePnl(position, state.LastPrice);
            }
        }

        return total;
    }

    /// <summary>
    /// Computes raw PnL for a position at a given price.
    /// </summary>
    private decimal CalculatePnl(Position position, decimal price)
    {
        var direction = position.Side == PositionSide.Long ? 1m : -1m;
        return (price - position.EntryPrice) * position.Quantity * direction;
    }

    /// <summary>
    /// Computes PnL percent for a position at a given price.
    /// </summary>
    private decimal CalculatePnlPercent(Position position, decimal price)
    {
        if (position.EntryPrice == 0)
        {
            return 0m;
        }

        var direction = position.Side == PositionSide.Long ? 1m : -1m;
        return (price - position.EntryPrice) / position.EntryPrice * 100m * direction;
    }

    /// <summary>
    /// EMA seed calculation using historical candles.
    /// </summary>
    private static decimal CalculateInitialEma(IReadOnlyList<Candle> candles, int period)
    {
        if (candles.Count == 0)
        {
            return 0m;
        }

        var k = 2m / (period + 1);
        var ema = candles[0].Close;
        foreach (var price in candles.Select(c => c.Close))
        {
            ema = price * k + ema * (1 - k);
        }

        return ema;
    }

    /// <summary>
    /// Updates EMA with the latest price.
    /// </summary>
    private static decimal UpdateEma(decimal previous, decimal price, int period)
    {
        if (previous == 0)
        {
            return price;
        }

        var k = 2m / (period + 1);
        return price * k + previous * (1 - k);
    }

    /// <summary>
    /// Calculates ATR over recent candles.
    /// </summary>
    private static decimal CalculateAtr(IReadOnlyList<Candle> candles)
    {
        if (candles.Count == 0)
        {
            return 0m;
        }

        decimal sum = 0m;
        decimal prevClose = candles[0].Close;
        foreach (var candle in candles)
        {
            var highLow = candle.High - candle.Low;
            var highClose = Math.Abs(candle.High - prevClose);
            var lowClose = Math.Abs(candle.Low - prevClose);
            var tr = new[] { highLow, highClose, lowClose }.Max();
            sum += tr;
            prevClose = candle.Close;
        }

        return sum / candles.Count;
    }

    /// <summary>
    /// Returns balance + unrealized PnL (not thread-safe).
    /// </summary>
    private decimal CurrentEquityUnsafe() => _balance + CalculateUnrealizedPnl();

    /// <summary>
    /// Periodically pulls live wallet/unrealized PnL when live trading is enabled.
    /// </summary>
    private async Task RefreshLiveAccountAsync(CancellationToken ct)
    {
        if (!_config.EnableLiveTrading)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (_lastAccountSync != DateTime.MinValue && now - _lastAccountSync < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _lastAccountSync = now;
        var snapshot = await _binanceTradingService.GetAccountSnapshotAsync(ct);
        if (snapshot == null)
        {
            return;
        }

        lock (_sync)
        {
            _balance = snapshot.WalletBalance;
            _liveUnrealizedPnl = snapshot.UnrealizedPnl;
            _realizedPnl = snapshot.WalletBalance - _config.InitialBalance;
            _peakEquity = Math.Max(_peakEquity, CurrentEquityUnsafe());
        }
    }

    /// <summary>
    /// Trims DateTime to minute precision (UTC).
    /// </summary>
    private static DateTime TruncateToMinute(DateTime time)
    {
        return new DateTime(time.Year, time.Month, time.Day, time.Hour, time.Minute, 0, DateTimeKind.Utc);
    }

    private sealed class SymbolState
    {
        public SymbolState(string symbol)
        {
            Symbol = symbol;
        }

        public string Symbol { get; }
        public List<Candle> Candles { get; set; } = new();
        public Candle? CurrentCandle { get; set; }
        public decimal LastPrice { get; set; }
        public DateTime LastUpdateTime { get; set; }
        public decimal Ema3 { get; set; }
        public decimal Ema9 { get; set; }
        public decimal Ema21 { get; set; }
        public decimal PrevEma3 { get; set; }
        public decimal PrevEma9 { get; set; }
        public decimal PrevEma21 { get; set; }
        public decimal Atr { get; set; }
        public decimal Volatility { get; set; }
        public decimal HighestHigh { get; set; }
        public decimal LowestLow { get; set; }
    }
}


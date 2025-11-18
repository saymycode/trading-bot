namespace TradingBotDemo.Models;

public enum PositionSide { Long, Short }
public enum OrderSide { Buy, Sell }
public enum OrderType { Market }

public class Candle
{
    public DateTime OpenTime { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public decimal Volume { get; set; }
}

public class TickerUpdate
{
    public DateTime Time { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public decimal BestBid { get; set; }
    public decimal BestAsk { get; set; }
    public decimal LastPrice { get; set; }
}

public class Position
{
    public Guid PositionId { get; set; } = Guid.NewGuid();
    public string Symbol { get; set; } = string.Empty;
    public PositionSide Side { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal Quantity { get; set; }
    public DateTime OpenTime { get; set; }
    public DateTime? CloseTime { get; set; }
    public decimal? ClosePrice { get; set; }
    public bool IsOpen { get; set; } = true;
    public decimal RealizedPnl { get; set; }
    public decimal Leverage { get; set; }
}

public class NewOrderRequest
{
    public string Symbol { get; set; } = string.Empty;
    public OrderSide Side { get; set; }
    public OrderType Type { get; set; } = OrderType.Market;
    public decimal Quantity { get; set; }
}

public class OrderResult
{
    public bool Success { get; set; }
    public string OrderId { get; set; } = string.Empty;
    public decimal ExecutedPrice { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class TradeEvent
{
    public DateTime Time { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public PositionSide Side { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal ExitPrice { get; set; }
    public decimal Quantity { get; set; }
    public decimal Pnl { get; set; }
    public decimal PnlPercent { get; set; }
}

public sealed class LiveAccountSnapshot
{
    public decimal WalletBalance { get; set; }
    public decimal UnrealizedPnl { get; set; }
}

# Trading Bot Demo (AggressiveBot) - TR / EN

## Proje Özeti / Project Summary
- TR: Binance USDT-M futures fiyat akışını dinleyen C# tabanlı agresif bir al-sat simülatörü. EMA(3/9/21), ATR ve 20 mumluk breakout sinyalleriyle otomatik long/short açıp kapatır; varsayılan mod simülasyondur.
- EN: A C# aggressive trading simulator for Binance USDT-M futures. Uses EMA(3/9/21), ATR, and 20-candle breakout signals to open/close long and short positions automatically; runs in simulation by default.

## Mimari ve Akış / Architecture & Flow
```
Program.cs -> TradingEngine
  - Per-symbol WebSocket miniTicker (REST price polling fallback on timeouts)
  - StatusLoop (every second): logs + optional live account sync
  - TelegramStatusLoop (configurable minutes) -> TelegramNotifier
  - BinanceTradingService: live orders, leverage init, account snapshot
```
- TR: Başlangıçta 120 adet 1 dakikalık mum alınır, EMA/ATR/breakout penceresi ısıtılır. Her tick sonrası mum ve göstergeler güncellenir, çıkışlar kontrol edilir, risk hesaplanır, geçerliyse giriş yapılır ve canlı işlem açıksa Binance’e yansıtılır.
- EN: On startup it fetches 120 one-minute candles to seed EMA/ATR/breakout windows. Each tick updates candles/indicators, evaluates exits, recalculates risk, opens entries if allowed, and mirrors to Binance when live trading is enabled.

## Başlatma ve Döngüler / Init & Loops
1) InitializeAsync  
   - TR: Bakiye/PnL/peak equity sıfırlanır; canlı mod açıksa hesap anlık görüntüsü çekilir. Her sembol için EMA3/9/21, ATR(14), breakout(20) ve volatilite hesaplanır.  
   - EN: Resets balance/PnL/peak equity; pulls live wallet snapshot when live mode is on. For each symbol calculates EMA3/9/21, ATR(14), breakout(20), and volatility.
2) RunAsync  
   - TR: Sembol bazlı WebSocket dinleyicileri, saniyelik status loop ve opsiyonel Telegram durum döngüsü paralel çalışır.  
   - EN: Spawns per-symbol WebSocket workers, a 1-second status loop, and an optional Telegram status loop.
3) Tick İşleme / Tick Handling  
   - TR: Mum/EMA/ATR güncellenir -> açık pozisyonlar TP/SL/EMA-flip için kontrol edilir -> drawdown ve cooldown korumaları uygulanır -> sinyal varsa pozisyon açılır ve gerekirse Binance’e gönderilir.  
   - EN: Updates candle/EMA/ATR -> checks open positions for TP/SL/EMA-flip -> applies drawdown and cooldown guards -> opens a position on signal and mirrors it to Binance if enabled.

## Ticaret Kuralları / Trading Rules
Giriş (Entry)  
- TR: Volatilite eşiği (ATR/Price >= MinVolatilityThreshold) geçilmiş olmalı ve risk-off/soğuma engeli bulunmamalı. Giriş için herhangi bir sinyal yeterlidir:  
  - Long: fiyat > HighestHigh(20) veya EMA9>EMA21 bullish çapraz (önce <=, şimdi > ve EMA9 yükseliyor) veya EMA3 pozitif eğim.  
  - Short: fiyat < LowestLow(20) veya EMA9<EMA21 bearish çapraz veya EMA3 negatif eğim.  
  - Sembol başına sınır: MaxOpenPositionsPerSymbol. Miktar = (OrderBudget x Leverage) / SonFiyat; canlı modda OrderBudget, cüzdanın `LiveTradingBalanceFraction` kadarıyla sınırlandırılır.
- EN: Volatility gate must pass (ATR/Price >= MinVolatilityThreshold) and risk-off/cooldown must be clear. Any of the following fires an entry:  
  - Long: price > HighestHigh(20) or EMA9 crosses above EMA21 (prev <=, now > and rising) or EMA3 slope up.  
  - Short: price < LowestLow(20) or EMA9 crosses below EMA21 or EMA3 slope down.  
  - Per-symbol cap: MaxOpenPositionsPerSymbol. Size = (OrderBudget x Leverage) / LastPrice; in live mode OrderBudget is capped by `LiveTradingBalanceFraction` of wallet.

Çıkış (Exit)  
- TR: Her tick kontrol edilir. TP: PnL% >= +TakeProfitPercent (vars 1.4%). SL: PnL% <= -StopLossPercent (vars 0.6%). EMA flip: Long için EMA9<EMA21’e iniş; Short için EMA9>EMA21’e çıkış.  
- EN: Checked every tick. TP when PnL% >= +TakeProfitPercent (1.4% default); SL when PnL% <= -StopLossPercent (0.6% default); EMA flip closes longs on EMA9<EMA21 crossover and shorts on EMA9>EMA21.

## Risk ve Koruma Katmanları / Risk & Guardrails
- TR: Peak equity’ye göre drawdown >= MaxDailyLossPercent (vars 5%) olursa risk-off açılır; drawdown limitin yarısının altına inince kapanır. `RiskOffCooldownMinutes` > 0 ise risk-off bitimine kadar yeni girişler durur. `MinSecondsBetweenTrades` ardışık işlemleri yavaşlatır.  
- EN: Risk-off starts when drawdown vs peak equity hits MaxDailyLossPercent (5% default) and clears once drawdown drops below half that level. Optional `RiskOffCooldownMinutes` keeps trading paused; `MinSecondsBetweenTrades` throttles back-to-back trades.

## Canlı İşlem ve Borsa Entegrasyonu / Live Trading & Exchange
- TR: `EnableLiveTrading=true` iken emirler Binance REST `/order` üzerinden gönderilir; kaldıraç `/fapi/v1/leverage` ile sembol başına bir kez ayarlanır; miktarlar `SymbolPrecisions` (quantity precision, step size, minNotional) ile normalize edilir. Cüzdan ve unrealized PnL ~5 saniyede bir okunur. WebSocket koparsa kısa süreli REST fiyat poll’una düşer.  
- EN: With `EnableLiveTrading=true`, orders go to Binance REST `/order`; leverage is set once per symbol via `/fapi/v1/leverage`; quantities are normalized using `SymbolPrecisions` (precision, step size, minNotional). Wallet and unrealized PnL refresh roughly every 5 seconds. Falls back to brief REST price polling if WebSocket drops.

## Loglama ve Bildirimler / Logging & Notifications
- TR: StatusLoop her saniye fiyatlar, açık long/short sayısı, gerçekleşmiş/gerçekleşmemiş PnL, equity ve drawdown’u loglar. Telegram durum mesajları `TelegramStatusIntervalMinutes` (vars 1 dk) aralıkla gönderilir. Açılış/kapanış mesajlarını aktifleştirmek isterseniz `TradingEngine` içindeki `SendTelegramUpdate` çağrılarını kullanabilirsiniz.  
- EN: StatusLoop logs per-second prices, long/short counts, realized/unrealized PnL, equity, and drawdown. Telegram status messages run every `TelegramStatusIntervalMinutes` (default 1 min). To enable trade open/close messages, wire up `SendTelegramUpdate` inside `TradingEngine`.

## Varsayılan Konfigürasyon (BotConfig.cs) / Configuration Defaults
| Parametre / Parameter | Varsayılan / Default | Açıklama (TR / EN) |
|-----------------------|----------------------|--------------------|
| InitialBalance | 1000 USD | Demo başlangıç bakiyesi / starting balance. |
| Symbols | BTCUSDT, ETHUSDT | İzlenen pariteler / tracked symbols. |
| CandlesLookback | 120 | Isıtma için 120x1m mum / warm-up window (1m). |
| BaseOrderSizeUsd | 1000 | Pozisyon bütçesi / per-trade USD budget. |
| Leverage | 5 | Simülasyon ve canlı emirler için kaldıraç / leverage multiplier. |
| MaxOpenPositionsPerSymbol | 5 | Sembol başına açık pozisyon sınırı / per-symbol open cap. |
| MaxDailyLossPercent | 5% | Drawdown limiti; aşılınca risk-off / DD trigger for risk-off. |
| TakeProfitPercent | 1.4% | TP eşiği / take-profit threshold. |
| StopLossPercent | 0.6% | SL eşiği / stop-loss threshold. |
| MinVolatilityThreshold | 0.05% | ATR/Price minimumu / minimum volatility to allow entries. |
| RiskOffCooldownMinutes | 0 | Risk-off sonrası bekleme / cooldown minutes (0 = off). |
| MinSecondsBetweenTrades | 0 | İşlem arası bekleme / throttle between trades. |
| LogPerSecond | true | Saniyelik status logları / per-second status logging. |
| EnableLiveTrading | false | Varsayılan demo modu / default simulation. |
| LiveTradingBalanceFraction | 0.3 | Canlı modda kullanılacak bakiye oranı / wallet fraction in live mode. |
| EnableTelegramNotifications | true | Telegram bildirimleri / enable Telegram. |
| TelegramStatusIntervalMinutes | 1 | Durum raporu sıklığı / status cadence. |
| Rest/WebSocket Base | fapi.binance.com / fstream.binance.com | Binance futures endpoint’leri; değiştirilebilir / endpoints are configurable. |
| SymbolPrecisions | BTC/ETH: qty 3, step 0.001, minNotional 5 | Canlı emirler için miktar normalizasyonu / quantity normalization for live orders. |

## Derleme ve Çalıştırma / Build & Run
1. TR: Proje klasörüne geçin `cd d:\Git\trading-bot-demo\trading-bot-demo`.  
   EN: Move into project folder `cd d:\Git\trading-bot-demo\trading-bot-demo`.
2. TR: Derle -> `dotnet build`  
   EN: Build -> `dotnet build`
3. TR: Çalıştır -> `dotnet run` (Ctrl+C ile durdur).  
   EN: Run -> `dotnet run` (stop with Ctrl+C).
4. TR: Canlı işlem için `EnableLiveTrading=true`, API anahtarları ve `LiveTradingBalanceFraction` ayarlarını yapın; eksik bilgide emirler otomatik reddedilir veya minNotional altında ise atlanır.  
   EN: For live trading set `EnableLiveTrading=true`, provide API keys, and tune `LiveTradingBalanceFraction`; orders are skipped when credentials are missing or below minNotional.

## Sınırlamalar / Limitations
- TR: Slippage ve komisyon hesaba katılmaz; timeframe 1 dakikadır; backtest yoktur; Telegram token/chatId repo’da yoktur; WebSocket kesilince kısa süreli REST poll’u devreye girer.  
- EN: No slippage/fee modelling; fixed 1m timeframe; no backtester; Telegram token/chatId not included; brief REST polling is used when WebSocket drops.

## Hızlı Özet / Quick Reference
| Konu / Topic | Değer / Value |
|--------------|---------------|
| Gösterge Periyotları / Indicator Periods | EMA(3/9/21), ATR(14), Breakout(20) |
| Zaman Dilimi / Timeframe | 1m |
| Giriş Filtreleri / Entry Gates | Volatility >= 0.05%, one of the signals |
| Çıkış Kuralları / Exits | TP 1.4%, SL 0.6%, EMA9/21 flip |
| Risk Limiti / Risk Limit | MaxDailyLossPercent 5% + optional cooldown |
| Boyutlandırma / Sizing | (BaseOrderSizeUsd x Leverage) / Price |
| Loglama / Logging | Status every second, Telegram status every 1 min (optional) |

---
Son Güncelleme / Last Update: 18 Kasım 2025 — Sürüm / Version: 2025.11

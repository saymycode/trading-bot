# Trading Bot Demo - Kapsamlı Dokümantasyon

## 🎯 Proje Özeti

**Trading Bot Demo** (AggressiveBot), gerçek zamanlı fiyat verilerini kullanarak **Binance** kripto borsasında otomatik olarak long ve short pozisyon açan ve kapatan bir **C# tabanlı algoritmik ticaret simülatörüdür**.

Bot, teknik analiz indikatörleri (EMA, ATR, Breakout) kullanarak ticaret kararlarını alır ve risk yönetimi (stop-loss, take-profit, drawdown limiti) mekanizmaları ile pozisyonları yönetir.

**Tipik Kullanım:** Demo/test amaçlı, gerçek para kullanılmaz.

---

## 📁 Proje Yapısı

```
trading-bot-demo/
├── Program.cs                      # Ana giriş noktası
├── Config/
│   └── BotConfig.cs               # Ticaret parametrelerinin merkezi yapı
├── Models/
│   └── TradingModels.cs           # Veri modelleri (Position, Candle, TickerUpdate vb.)
├── Exchange/
│   ├── IExchangeClient.cs         # Exchange API arayüzü (soyutlama)
│   └── BinanceExchangeClient.cs   # Binance REST & WebSocket uygulaması
├── Trading/
│   └── TradingEngine.cs           # Ana ticaret motoru (sinyal üretim, pozisyon yönetimi)
└── DOCUMENTATION.md               # Bu dosya
```

---

## 🔧 Mimarisi ve Veri Akışı

```
┌─────────────────────────────────────────────────────────────────┐
│                         Program.cs                              │
│  (Başlangıç, konfigürasyon yükleme, DI, Ctrl+C işlemleri)      │
└────────────────┬────────────────────────────────────────────────┘
                 │
        ┌────────▼─────────┐
        │  TradingEngine   │
        │  (Ana Motor)     │
        └────┬─────────────┘
            │
    ┌───────┴──────────┬─────────────────┐
    │                  │                 │
┌───▼────────────┐  ┌─▼──────────────┐  │
│ BinanceClient  │  │ TradingEngine  │  │
│ (REST API)     │  │ StatusLoop     │  │
└────────────────┘  │ (Her 60 saniye)│  │
                    └────────────────┘  │
    ┌───────────────────────────────────▼──────────┐
    │  BinanceClient (WebSocket Stream)           │
    │  (Her symbol için miniTicker feed)          │
    └───────────────┬──────────────────────────────┘
                    │
        ┌───────────▼────────────┐
        │  HandleTicker()        │
        │ (Her tick'te çağrılır) │
        └───────────┬────────────┘
                    │
    ┌───────────────┴───────────────┐
    │                               │
┌───▼──────────────┐       ┌────────▼────────────┐
│ UpdateSymbolState│       │ EvaluateEntry()     │
│ (Mum, EMA, ATR) │       │ (Sinyal üretimi)    │
└──────────────────┘       └────────┬────────────┘
                                    │
                    ┌───────────────┴──────────┐
                    │                          │
              ┌─────▼──────┐         ┌────────▼─────┐
              │ OpenPosition│        │ ClosePosition│
              │ (İşlem Aç) │        │ (İşlem Kapat)│
              └────────────┘         └──────────────┘
```

---

## ⚙️ Konfigürasyon Parametreleri (`BotConfig.cs`)

Tüm ticaret parametreleri `BotConfig` sınıfında merkezi olarak yönetilir. Varsayılan değerler aşağıdaki gibidir ve BTC/ETH çiftlerinde demo modunda işlem yapan 1.000 USDT'lik sanal bakiye için optimize edilmiştir:

| Parametre | Varsayılan | Birim | Açıklama |
|-----------|-----------|-------|----------|
| **InitialBalance** | `1000` | USD | Demo modu için başlangıç bakiyesi |
| **Symbols** | `["BTCUSDT", "ETHUSDT"]` | - | İzlenen ve işlem yapılan semboller |
| **CandlesLookback** | `60` | mum | Göstergeleri ısıtmak için kullanılan geçmiş mum sayısı |
| **BaseOrderSizeUsd** | `5` | USD | Her yeni pozisyon için temel dolar tutarı |
| **Leverage** | `20` | katlı | Pozisyon büyüklüğü çarpanı (quantity = BaseOrderSizeUsd * Leverage / price) |
| **MaxOpenPositionsPerSymbol** | `5` | adet | Bir sembolde aynı anda açık olabilecek max pozisyon sayısı |
| **MaxDailyLossPercent** | `20` | % | Çizdi (drawdown) bu değere ulaşınca bot "risk-off" moda geçer |
| **TakeProfitPercent** | `1.0` | % | Kar al hedefi (PnL% ≥ bu değer ise pozisyon otomatik kapatılır) |
| **StopLossPercent** | `0.8` | % | Zarar kes seviyesi (PnL% ≤ -bu değer ise pozisyon otomatik kapatılır) |
| **MinVolatilityThreshold** | `0.01` | % | Minimum volatilite eşiği (bunu altında yeni girişler engellenir) |
| **RestBaseUrl** | `https://fapi.binance.com` | - | Binance USDT-M futures REST API taban URL'si |
| **KlinesEndpoint** | `/fapi/v1/klines` | - | Mum verisinin çekildiği endpoint |
| **PriceTickerEndpoint** | `/fapi/v1/ticker/price` | - | Anlık fiyat için kullanılan endpoint |
| **OrderEndpoint** | `/fapi/v1/order` | - | Canlı emirlerin gönderildiği endpoint |
| **WebSocketBaseUrl** | `wss://fstream.binance.com/ws` | - | Binance futures WebSocket taban URL'si |
| **LogPerSecond** | `true` | - | Her saniye durumu logla |
| **EnableLiveTrading** | `false` | - | Varsayılan olarak demo modunda çalış |
| **LiveTradingBalanceFraction** | `1` | - | Bakiyenin tamamı kullanılabilir |
| **EnableTelegramNotifications** | `true` | - | İşlem logları ve durum raporlarını Telegram'a gönder |
| **TelegramStatusIntervalMinutes** | `1` | dakika | Periyodik Telegram durum raporu aralığı |

> 📌 **Neden bu değerler?** Varsayılan profil, kullanıcıyı doğrudan demo modunda başlatır, BTC ve ETH için eşzamanlı 5 pozisyona kadar izin verir ve tüm mesajları hem loglara hem de Telegram'a düşürür. Gerçek emir göndermek için `EnableLiveTrading` bayrağını `true` yapıp Binance API anahtarlarını paylaşmanız yeterlidir.

---

## 🚀 Çalışma Politikası (İş Akışı)

### 1. **Başlangıç Aşaması** (`InitializeAsync`)

```csharp
1. Başlangıç zamanı kaydedilir (_startTime = DateTime.UtcNow)
2. Bakiye, realized PnL, peak equity sıfırlanır
3. Her sembol için:
   - Son 60 dakikalık 1-dakika mum'lar REST API'dan getirilir
   - EMA3, EMA9, EMA21 göstergeleri hesaplanır
   - ATR (Average True Range) hesaplanır
   - Breakout seviyeleri (HighestHigh, LowestLow) belirlenir
   - Volatilite hesaplanır
4. Engine hazırlanır, WebSocket bağlantıları sırasına konur
```

### 2. **Çalışma Aşaması** (`RunAsync`)

Bot, iki paralel görev çalıştırır:

#### **A. WebSocket Veri Akışı** (`ProcessSymbolAsync`)
- Her sembol için gerçek zamanlı miniTicker feed'i dinlenir
- Her yeni fiyat güncellemesinde `HandleTicker()` çağrılır

#### **B. Durum Logu** (`StatusLoopAsync`)
- Her **60 saniye**de toplam durum (bakiye, açık pozisyon sayısı, PnL, drawdown, geçen zaman) loglanır
- Örnek log satırı:
  ```
  STATUS | BTCUSDT=95758.01 | ETHUSDT=3212.08 | OpenPos=2L/1S | Bal=950.25 | 
  Realized=+5.50 | Unrealized=-2.10 | Equity=953.65 | DD=4.64% | Elapsed=01:23:45
  ```

### 3. **Tick İşleme** (`HandleTicker`)

Her yeni fiyat güncellemesinde:

```csharp
1. Sembol durumunu güncelle
   ├─ Mum'ı güncelle (High, Low, Close)
   ├─ EMA'ları güncelle (EMA3, EMA9, EMA21)
   ├─ ATR, Volatilite hesapla
   └─ Breakout seviyeleri güncelr

2. Mevcut açık pozisyonları kontrol et
   ├─ Take-profit hedefine ulaşanları kapat
   ├─ Stop-loss seviyesini kıranları kapat
   └─ EMA crossover (9/21) ile çıkanları kapat

3. Risk metriklerini güncelle
   ├─ Pik equity'yi takip et
   ├─ Drawdown hesapla
   └─ Risk-off modu kontrol et (DD ≥ %10 ise)

4. Risk-off değilse, yeni pozisyon açmayı değerlendir
   ├─ Giriş sinyallerini kontrol et
   └─ Pozisyon aç
```

---

## 📊 Teknik İndikatörler ve Hesaplama

### **1. EMA (Exponential Moving Average)**

EMA'lar fiyat trendini takip eder. Sistem 3 EMA kullanır:

- **EMA3** (kısa dönem): En hassas, gürültüye duyarlı, ani yön değişikliklerini algılar
- **EMA9** (kısa-orta dönem): Denge noktası
- **EMA21** (orta dönem): Trend filtresi, ana yön göstergesi

**Hesaplama:**
```
k = 2 / (period + 1)
EMA_new = Price * k + EMA_prev * (1 - k)
```

Örnek (price=100, EMA_prev=98, period=3):
- k = 2 / 4 = 0.5
- EMA_new = 100 * 0.5 + 98 * 0.5 = 99

### **2. ATR (Average True Range)**

Piyasanın oynaklığını (volatility) ölçer. Her mum için True Range (TR) hesaplanır:

```
TR = max(High - Low, |High - PrevClose|, |Low - PrevClose|)
ATR = ortalama(TR, son 14 mum)
```

### **3. Volatilite (Volatility)**

```
Volatility (%) = (ATR / LastPrice) * 100
```

- Eğer Volatility < MinVolatilityThreshold (%0.01), yeni giriş engellenir
- Çok düşük volatilite = durgun, gürültülü piyasa = yanlış sinyaller

### **4. Breakout Seviyeleri**

```
HighestHigh = max(High, son 20 mum)
LowestLow = min(Low, son 20 mum)
```

---

## 🎬 Pozisyon Açma Kuralları

Bot, **EN AZ BİR** aşağıdaki koşul sağlanınca **VE** Volatility eşiğini geçince pozisyon açar:

### **Long (Alış) Sinyalleri:**

1. **Breakout Uzun**: `Price > HighestHigh` (son 20 mum'ın en yükseğini kır)
   - Güçlü yukarı kırılma sinyali

2. **EMA Bullish Cross**: `EMA9 > EMA21` (bullish crossover)
   - EMA9 EMA21'in üstüne çıkıyor
   - Koşullar:
     - Önceki: `EMA9 ≤ EMA21`
     - Şu an: `EMA9 > EMA21` VE `EMA9 > PrevEMA9`

3. **EMA3 Slope Up**: `EMA3 > PrevEMA3`
   - Kısa vadeli momentumun yukarıya gittiği sinyali (çok cüzi bir yükseklik de trigger yapabilir)

**Kontroller:**
- Volatility ≥ MinVolatilityThreshold (%0.01) ✓
- Şu an açık Long sayısı < MaxOpenPositionsPerSymbol (5) ✓
- Risk-off modu KAPAL ✓

### **Short (Satış) Sinyalleri:**

Aynı mantık, ters yön:

1. **Breakout Kısa**: `Price < LowestLow`
2. **EMA Bearish Cross**: `EMA9 < EMA21` (bearish crossover)
3. **EMA3 Slope Down**: `EMA3 < PrevEMA3`

---

## 🚪 Pozisyon Kapama Kuralları

Açık bir pozisyon **HER BİR** tick'te otomatik kapatılır eğer:

### **1. Take-Profit (Kar Al)**
```
PnL% = (CurrentPrice - EntryPrice) / EntryPrice * 100 * direction
Eğer PnL% ≥ TakeProfitPercent (1.0%):
  → Pozisyon kapatılır
```

**Örnek:** Long %1.0 karda ise kapatılır, Short %1.0 karda ise kapatılır

### **2. Stop-Loss (Zarar Kes)**
```
Eğer PnL% ≤ -StopLossPercent (-0.8%):
  → Pozisyon kapatılır
```

**Örnek:** Long %0.8 zararında veya Short %0.8 zararında ise kapatılır

### **3. EMA Flip (Trend Çevirme)**

- **Long pozisyonunda:** Eğer `EMA9` `EMA21`'in altına geçerse (`EMA9 < EMA21` VE önceki `EMA9 ≥ EMA21`)
  → Pozisyon kapatılır (trend sona erdi)

- **Short pozisyonunda:** Eğer `EMA9` `EMA21`'in üstüne geçerse
  → Pozisyon kapatılır

---

## 💰 Pozisyon Boyutu Hesabı

Her yeni pozisyon için adet (quantity) şu formülla hesaplanır:

```csharp
Quantity = (BaseOrderSizeUsd * Leverage) / CurrentPrice
```

**Örnek:**
- BaseOrderSizeUsd = $100
- Leverage = 2 (kaldıraçlı simülasyon)
- BTCUSDT fiyatı = $95,758
- Quantity = (100 * 2) / 95758 ≈ 0.00209 BTC

**Simülasyon Notu:** Bu kaldıraç sadece pozisyon boyutunda simüle edilir. Gerçek exchange tarafında marjin veya futures mekanizması yoktur; sadece adet büyüğü.

---

## ⚠️ Risk Yönetimi

### **1. Per-Position Risk:**

Her pozisyon için **iki** otomatik çıkış:
- Stop-Loss: -%0.8
- Take-Profit: +%1.0
- Trend çevirme (EMA flip)

### **2. Per-Symbol Risk:**

Max 5 açık pozisyon / sembol (aşırı yüklenme önü)

### **3. Portföy Risk (Drawdown Limiti):**

```
Drawdown (%) = (PeakEquity - CurrentEquity) / PeakEquity * 100

Eğer Drawdown ≥ MaxDailyLossPercent (10%):
  → Risk-off modu AÇILIR
     - Yeni pozisyon AÇILAMAZ
     - Mevcut pozisyonlar hala kapatılabilir (TP/SL çalışmaya devam eder)

Eğer Drawdown < MaxDailyLossPercent * 0.5 (5%):
  → Risk-off modu KAPANIR
     - Yeni pozisyon AÇILIR
```

**Amaç:** Büyük bir kayıptan sonra, bot kendini kurtarmasını sağlar; kayıplar azalınca ticaret devam eder.

---

## 📈 PnL (Kar/Zarar) Hesaplaması

Kapalı her pozisyon için:

```csharp
// Long için:
PnL = (ClosePrice - EntryPrice) * Quantity

// Short için:
PnL = (EntryPrice - ClosePrice) * Quantity

// Yüzdesel:
PnLPercent = (ClosePrice - EntryPrice) / EntryPrice * 100 * direction
```

**Bakiye Güncelleme:**
```csharp
_balance += RealizedPnL  // Kapanan pozisyonlar eklenir
_realizedPnl += RealizedPnL  // Toplam gerçekleşmiş kâr kaydedilir

// Açık pozisyonlardan gelen unrealized PnL görüntülenebilir:
UnrealizedPnL = Σ (CurrentPrice - EntryPrice) * Quantity (açık pozisyonlar)

// Total Equity:
Equity = Balance + UnrealizedPnL
```

---

## 📊 Durum Logu (Status Log)

Bot her 60 saniyede bir toplam durumu loglar:

```
STATUS | BTCUSDT=95758.01 | ETHUSDT=3212.08 | OpenPos=2L/1S | Bal=950.25 | 
Realized=+5.50 | Unrealized=-2.10 | Equity=953.65 | DD=4.64% | Elapsed=01:23:45
```

**Alanlar:**
- **BTCUSDT=95758.01**: Sembol fiyatı
- **OpenPos=2L/1S**: Açık Long=2, Short=1 pozisyon
- **Bal=950.25**: Mevcut bakiye (kapanan pozisyonlardan gelen net kar/zarar)
- **Realized=+5.50**: Kapanan pozisyonlardan toplam gerçekleşmiş kar
- **Unrealized=-2.10**: Açık pozisyonlardan toplam henüz kapatılmamış PnL
- **Equity=953.65**: Toplam sermaye (Bakiye + Unrealized)
- **DD=4.64%**: Zirve sermayeden ne kadar aşağıda (%)
- **Elapsed=01:23:45**: Botun çalışma süresi (HH:MM:SS)

---

## 🔄 İşlem Akışı Örneği

```
T=0: Bot başlar, Equity = 1000.00, Volatility = 0.05%

T=10s: BTCUSDT fiyatı 95700 → 95800 (breakout!)
  → EMA9 > HighestHigh ✓
  → Volatility 0.05% ≥ 0.01% ✓
  → Risk-off kapalı ✓
  → LONG pozisyon aç
     Qty = (100 * 2) / 95800 = 0.00209 BTC
     Entry = 95800
     Log: "OPEN LONG BTCUSDT qty=0.002090 @ 95800.00 | Balance=900.00 | Equity=900.00"

T=35s: BTCUSDT fiyatı 95800 → 96750 (%1.0 kar!)
  → PnL = (96750 - 95800) * 0.00209 ≈ 1.98 USD
  → PnLPercent ≈ +1.0% (TakeProfitPercent'e ulaştı)
  → Pozisyon kapatılır
  → Balance = 900.00 + 1.98 = 901.98
  → Log: "CLOSE LONG BTCUSDT qty=0.002090 @ 96750.00 (entry 95800.00) | 
           PnL=+1.98 (+1.00%) | Balance=901.98 | Equity=901.98"

T=60s: Status Log:
  "STATUS | BTCUSDT=96750.00 | ETHUSDT=3212.08 | OpenPos=0L/0S | Bal=901.98 | 
   Realized=+1.98 | Unrealized=+0.00 | Equity=901.98 | DD=0.00% | Elapsed=00:01:00"
```

---

## 🛠️ Derleme ve Çalıştırma

### **Ön Koşullar:**
- .NET 8.0 veya üzeri SDK
- PowerShell veya Terminal

### **Adımlar:**

1. **Proje klasörüne gidin:**
   ```powershell
   cd d:\Git\trading-bot-demo\trading-bot-demo
   ```

2. **Derle:**
   ```powershell
   dotnet build
   ```

3. **Çalıştır:**
   ```powershell
   dotnet run
   ```

4. **Durdur:**
   ```
   Ctrl + C
   ```

### **Beklenen Çıkti:**
```
info: TradingBotDemo.Program[0]
      Starting aggressive trading simulation...
info: TradingBotDemo.Trading.TradingEngine[0]
      Trading engine initialized. Balance=1000.00
info: TradingBotDemo.Trading.TradingEngine[0]
      STATUS | BTCUSDT=95758.01 | ETHUSDT=3212.08 | OpenPos=0L/0S | Bal=1000.00 | 
      Realized=+0.00 | Unrealized=+0.00 | Equity=1000.00 | DD=0.00% | Elapsed=00:01:00
[Her 60 saniye durum güncellenir...]
```

---

## 🎨 Kod Mimarisi (Önemli Sınıflar)

### **`TradingEngine`** (Ana Motor)

**Ana Metodlar:**
- `InitializeAsync()`: Bot başlat, göstergeleri ısıt
- `RunAsync()`: WebSocket flow ve status loop başlat
- `ProcessSymbolAsync()`: WebSocket feed'i dinle
- `HandleTicker()`: Her tick'te sinyalleri değerlendir
- `EvaluateEntry()`: Yeni pozisyon açmalı mı? Kontrol et
- `EvaluateExitCandidates()`: Hangi pozisyonlar kapatılmalı? Kontrol et
- `OpenPosition()`: Yeni pozisyon aç, loglama yap
- `ClosePosition()`: Pozisyon kapat, PnL hesapla
- `UpdateRiskMetrics()`: Drawdown ve risk-off durumunu güncelle
- `LogStatus()`: Her 60 saniye durum logla

### **`BinanceExchangeClient`** (Exchange Entegrasyonu)

**Ana Metodlar:**
- `GetRecentCandlesAsync()`: Son N mum'u REST API'dan çek
- `StreamTickerAsync()`: WebSocket ile gerçek zamanlı fiyat feed'i aç
- `GetCurrentPriceAsync()`: Cari fiyat sorgula

### **`BotConfig`** (Parametre Konfigürasyonu)

- Tüm ticaret kurallarının parametreleri
- `InitializeAsync` ile yüklenir
- Tüm sınıflara enjekte edilir (dependency injection)

### **`Models`** (Veri Yapıları)

- `Position`: Açık/kapanan pozisyon
- `Candle`: 1-dakika mum veri
- `TickerUpdate`: Gerçek zamanlı fiyat güncellemesi
- `TradeEvent`: Kapanan işlem kaydı

---

## 🧪 Parametreleri Optimize Etme

### **Agresif (Daha Çok İşlem):**
```csharp
MinVolatilityThreshold = 0.01m         // Çok düşük, az sinyali engelle
BaseOrderSizeUsd = 200m                 // Daha büyük pozisyon
TakeProfitPercent = 0.5m                // Hızlı kar al
StopLossPercent = 1.0m                  // Geniş stop-loss
Leverage = 3                            // Daha büyük kaldıraç (simüle)
MaxOpenPositionsPerSymbol = 10          // Daha çok açık pozisyon
```

### **Temkinli (Daha Az İşlem, Daha Seçici):**
```csharp
MinVolatilityThreshold = 0.1m           // Yüksek volatilite gerekli
BaseOrderSizeUsd = 50m                  // Küçük pozisyon
TakeProfitPercent = 2.0m                // Yavaş kar al
StopLossPercent = 0.5m                  // Dar stop-loss
Leverage = 1                            // Kaldıraç yok
MaxOpenPositionsPerSymbol = 2           // Az açık pozisyon
```

---

## 🚨 Uyarılar ve Limitasyonlar

1. **Simülasyon Modu**: Bu bot gerçek para ile işlem yapmaz. Bakiye ve PnL sadece simülas yonda hesaplanır.

2. **WebSocket Bağlantı**: Internette sorun olursa bot otomatik olarak yeniden bağlanır (2 saniye gecikme).

3. **Slippage ve Komiser Yok**: Bot, sipariş fiyatı = geri dönüş fiyatı varsayar; gerçek ortamda slippage ve komiser hesaplanmalı.

4. **Backtest Yok**: Bot sadece gerçek zamanlı veri ile çalışır. Geçmiş veri üzerinde test etmek için ayrı bir backtester yazılmalıdır.

5. **Binance API Rate Limit**: Çok hızlı çağrılar rate limit'e çarpabilir.

6. **1 Dakikalık Mumluk**: Bot sadece 1-dakika timeframe'inde çalışır. Daha kısa dönemler için kod uyarlanmalıdır.

---

## 📚 Kaynaklar

- **Binance API Docs**: https://binance-docs.github.io/
- **EMA Hesaplaması**: Exponential Moving Average formülü
- **ATR Hesaplaması**: Average True Range (J. Welles Wilder)
- **C# Async/Await**: Microsoft Docs
- **.NET Hosting**: Microsoft.Extensions.Hosting

---

## 📝 Özetleme Tablosu

| Konu | Açıklama | Varsayılan |
|------|----------|-----------|
| **Giriş Koşulu** | Breakout, EMA Cross, EMA3 Slope (HER BİRİ yeterli) + Volatility | Min %0.01 |
| **Çıkış Koşulu 1** | Take-Profit (kar al) | +%1.0 |
| **Çıkış Koşulu 2** | Stop-Loss (zarar kes) | -%0.8 |
| **Çıkış Koşulu 3** | EMA Flip (trend sona erdi) | EMA9 kesişim EMA21 |
| **Per-Position Max** | Açık pozisyon sayısı / sembol | 5 |
| **Portföy Limiti** | Max Drawdown | %10 (risk-off trigger) |
| **Pozisyon Boyutu** | Base USD * Leverage / Price | 100 * 2 / Price |
| **Gösterge Periyotları** | EMA(3,9,21), ATR(14), Breakout(20) | - |
| **Zaman Dilimi** | 1 dakika | - |
| **Status Log** | Her | 60 saniye |

---

**Son Güncelleme:** 17 Kasım 2025  
**Versiyon:** 1.0

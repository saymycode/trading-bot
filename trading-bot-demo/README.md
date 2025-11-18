# trading-bot-demo

## Genel Bakış
Bu depo, Binance spot verisini kullanan agresif bir algoritmik al-sat motoru içerir. Bot, dakikalık mum verilerini ve mini ticker akışını izleyerek momentum/breakout sinyalleri üretir. Uygulama varsayılan olarak simülasyon (demo) modunda çalışır ve tüm işlemler sadece konsoldaki raporlarda tutulur.

## Gereksinimler
- .NET 8 SDK
- İnternete çıkabilen bir ortam (Binance API çağrıları için)

## Çalıştırma
1. Gerekli paketleri indirmek için `dotnet restore` komutunu çalıştırın.
2. Ardından uygulamayı başlatmak için `dotnet run` komutunu kullanın.
3. Konsoldaki loglardan botun durumunu ve gönderilen bildirimleri takip edin.

## Yapılandırma
Tüm bot ayarları `Config/BotConfig.cs` içindeki `BotConfig` sınıfıyla yönetilir. Önemli alanlar:

- `Symbols`: Varsayılan olarak `BTCUSDT` ve `ETHUSDT` çiftleri izlenir.
- `InitialBalance`, `BaseOrderSizeUsd`, `MaxOpenPositionsPerSymbol`, `TakeProfitPercent`, `StopLossPercent` vb. risk ve pozisyon boyutu parametreleridir. Varsayılan profil 1.000 USDT sanal bakiye ile her sembolde 5 pozisyona kadar izin verir.
- `RiskOffCooldownMinutes` ve `MinSecondsBetweenTrades`: Günlük kayıp limiti tetiklendiğinde ve ardışık işlemler arasında ne kadar süre beklenmesi gerektiğini ayarlamanızı sağlar.
- `EnableLiveTrading`: Varsayılan olarak **kapalıdır**. Bot demo modunda çalışır; gerçek emir göndermek için bu alanı `true` yapıp Binance API anahtarlarınızı girmeniz gerekir.
- `BinanceApiKey` / `BinanceApiSecret`: Canlı al-sat için zorunludur.
- `LiveTradingBalanceFraction`: Canlı modda bakiyenin ne kadarlık kısmının bot tarafından kullanılacağını belirleyen 0-1 arası katsayıdır.
- `EnableTelegramNotifications`, `TelegramBotToken`, `TelegramChatId`, `TelegramStatusIntervalMinutes`: İşlem bildirimleriyle birlikte her 1 dakikada bir durum raporunu Telegram üzerinden göndermek için bu alanları kullanın.
- `RestBaseUrl`, `WebSocketBaseUrl`, `KlinesEndpoint`, `PriceTickerEndpoint`, `OrderEndpoint`: Varsayılan değerler Binance USDT-M futures (https://fapi.binance.com ve https://fstream.binance.com) uç noktalarına göre ayarlanmıştır. Spot veya farklı bir çevreye bağlanmak isterseniz bu alanları değiştirin.

Demo profilini olduğu gibi kullanabilir veya `appsettings.json` / ortam değişkenleri üzerinden `BotConfig` bölümünü özelleştirerek kendi parametrelerinizi geçebilirsiniz.

Ayarları `appsettings.json`, kullanıcı gizli dosyaları veya ortam değişkenleri üzerinden `BotConfig` bölümüne yansıtarak güncelleyebilirsiniz. Değer belirtilmediğinde sınıftaki varsayılanlar kullanılır.

## Gerçek Veri ve Bildirimler
- Uygulama her zaman `BinanceExchangeClient` sınıfını kullanır; mock veya rastgele fiyat üreten hiçbir bileşen yoktur.
- Dakikalık mumlar `/fapi/v1/klines`, anlık fiyatlar ise Binance mini ticker WebSocket akışı üzerinden alınır.
- `EnableLiveTrading = true` olduğunda tüm işlemler REST API (`/fapi/v1/order`) aracılığıyla Binance hesabınıza yansıtılır.
- Telegram entegrasyonu aktif edildiğinde her al-sat işlemi botunuzun belirttiğiniz kanala otomatik olarak bildirilir.

Bu sayede hem demo modunda stratejinizi test edebilir hem de istediğiniz anda gerçek hesapta emir iletilmesini sağlayabilirsiniz.

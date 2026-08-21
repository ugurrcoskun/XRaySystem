# XRaySystem.DeviceHost

`XRaySystem.DeviceHost`, geliştirme aşamasında `MockXRaySensorService` örneğinin tek sahibi olan ASP.NET Core Minimal API host'udur. WPF ve MAUI, ortak `RemoteSensorService` adapter'ıyla bu host'a bağlanabilir; mevcut Local çalışma biçimleri de korunur.

## Mevcut endpointler

- `GET /healthz`
- `GET /api/devices`
- `GET /api/devices/{deviceId}`
- `POST /api/devices/{deviceId}/connect`
- `POST /api/devices/{deviceId}/disconnect`
- `POST /api/devices/{deviceId}/parameters`
- `POST /api/devices/{deviceId}/live/start`
- `POST /api/devices/{deviceId}/live/stop`
- `GET /api/devices/{deviceId}/live/latest?afterSequence={sequence}`
- `POST /api/devices/{deviceId}/snap`
- `GET /api/devices/{deviceId}/images`
- `GET /api/images/{imageId}?mode=low`
- `GET /api/images/{imageId}?mode=raw`
- SignalR hub: `/hubs/device-events`

Komut gövdelerinde boş olmayan bir `commandId`, komut isteklerinde ayrıca `X-XRay-Client-Id` başlığı gönderilir. Parametre sözleşmesi hedef `voltageKv`, `powerW`, `exposureTimeMs` ve sensör `profile` değerini taşır. SNAP metadata'sında istenen parametreler ile ölçülen telemetri ayrı alanlardır; yerel dosya yolu veya piksel dizisi JSON'a konmaz.

`low`, uzun kenarı en fazla 768 piksel olan 8-bit BMP döndürür. `raw`, SHA-256 bütünlük bilgisi taşıyan sürümlü `.xrayraw` binary dosyası döndürür.

SignalR hub `device.status.changed`, `device.telemetry.received`, `device.snap.progress` ve `device.error.occurred` olaylarını yayınlar. SNAP ilerlemesi REST komutuyla aynı `commandId` üzerinden ilişkilendirilir. Hata olayı güvenli hata kodu ve kullanıcı mesajı taşır; stack trace yayınlamaz. Telemetri yalnız frame metadata'sı ve ölçüm değerlerini taşır; piksel verisi binary endpointte kalır. Canlı ölçüm relay kuyruğu kapasite 1 ve `DropOldest` kullanır, böylece yavaş istemci sınırsız frame biriktirmez.

Live telemetri yeni sequence bildirdiğinde istemci `/live/latest` endpoint'inden en güncel low BMP'yi alır. İstenen sequence'den daha yeni frame yoksa veya live durdurulmuşsa endpoint `204 No Content` döndürür. Sunucuda yalnız tek latest live BMP tutulur.

## Remote buffer

`appsettings.json` içindeki `RemoteImageBuffer` bölümü count, yaklaşık frame byte limiti ve TTL değerlerini belirler. Varsayılan değerler 10 görüntü, 128 MiB ve 30 dakikadır. Limit dolduğunda en eski remote kayıt çıkarılır. Bu buffer WPF TIFF dosyalarına veya SQLite veritabanına erişmez ve onları silemez.

## Güvenlik sınırı

- Development profili localhost HTTP testine izin verir; diğer ortamlarda `Security:RequireHttps=true` varsayılanıyla HTTP istekleri `426` ile reddedilir.
- Windows Android Emulator geliştirme akışı için özel host geçidi `10.0.2.2` izinli host listesindedir; bu yalnız emulatorün Windows hostundaki DeviceHost'a erişmesini sağlar.
- Development profilinde yerel mock kullanımını bozmamak için authentication kapalıdır ve istekler geliştirme amaçlı `Admin` kimliğiyle çalışır. Production'da authentication açıktır; geçerli anahtar tanımlanmamışsa host fail-closed biçimde başlamaz.
- `Viewer`, cihaz durumu, görüntü ve SignalR olaylarını okuyabilir. `Operator`, Viewer yetkilerine ek olarak connect/disconnect, parametre, live ve SNAP komutlarını çalıştırabilir. `Admin` tüm bu yetkilere sahiptir.
- RemoteClient her oturumda benzersiz bir client ID gönderir. Komut lease'i varsayılan 30 saniyedir; başka istemcinin eşzamanlı kontrol komutları `423 Locked` alır. Disconnect lease'i bırakır.
- Son 256 başarılı komut sonucu bellekte tutulur. Aynı `commandId` retry'ı sensör işlemini tekrar çalıştırmaz; kimliğin farklı işlemde kullanımı reddedilir.
- Doğrulanmış subject/rol, komut kimliği, istemci kimliği, işlem, cihaz ve sonuç yapılandırılmış `XRaySystem.DeviceAudit` log kategorisine yazılır.

Production anahtarı kaynak koda veya `appsettings.json` dosyasına yazılmamalıdır. Ortam değişkeni/secret store örneği:

```text
Security__AuthenticationEnabled=true
Security__ApiKeys__0__Key=<en-az-32-karakterlik-gizli-deger>
Security__ApiKeys__0__Subject=operator-01
Security__ApiKeys__0__Role=Operator
```

WPF, MAUI veya RemoteSmoke aynı anahtarı `XRAYSYSTEM_DEVICEHOST_API_KEY` ortam değişkeninden alır ve REST ile SignalR bağlantılarına otomatik ekler. `Tools/RemoteSmoke` için isteğe bağlı üçüncü argüman da anahtar olarak kullanılabilir; terminal geçmişine secret yazmamak için ortam değişkeni tercih edilmelidir.

Bu API-key katmanı geliştirme/prototip güvenlik sınırıdır. Gerçek cihaz/LAN kullanımı öncesinde TLS sertifikası, secret yönetimi, anahtar rotasyonu, oran sınırlama ve fiziksel güvenlik/interlock incelemesi ayrıca tamamlanmalıdır.

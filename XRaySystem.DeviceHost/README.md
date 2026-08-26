# XRaySystem.DeviceHost

`XRaySystem.DeviceHost`, cihaz bilgisayarında çalışan ve detector/generator adapter'larının tek sahibi olan ASP.NET Core servisidir. WPF aynı bilgisayardan, MAUI ise ağ üzerinden ortak `RemoteSensorService` adapter'ıyla bu servise bağlanır. Mobil uygulama donanım SDK'sına doğrudan erişmez ve X-ray güvenlik zamanlamasının sahibi değildir. Gerçek SDK gelene kadar host ayrı `MockXRayGenerator` ve `MockXRayDetector` adapter'larını `CoordinatedHardwareSensorService` state-machine'i üzerinden kullanır.

## Mevcut endpointler

- `GET /healthz`
- `GET /readyz`
- `GET /api/protocol`
- `GET /api/devices`
- `GET /api/devices/{deviceId}`
- `POST /api/devices/{deviceId}/connect`
- `POST /api/devices/{deviceId}/disconnect`
- `POST /api/devices/{deviceId}/parameters`
- `POST /api/devices/{deviceId}/grab/start`
- `POST /api/devices/{deviceId}/grab/freeze`
- `GET /api/devices/{deviceId}/grab/latest?afterSequence={sequence}`
- `POST /api/devices/{deviceId}/abort`
- `POST /api/devices/{deviceId}/snap`
- `GET /api/devices/{deviceId}/images`
- `GET /api/devices/{deviceId}/images/page?take={1..200}&cursor={opaque}`
- `GET /api/devices/{deviceId}/images/{imageId}`
- `GET /api/images/{imageId}?mode=low`
- `GET /api/images/{imageId}?mode=raw`
- SignalR hub: `/hubs/device-events`

Komut gövdelerinde boş olmayan bir `commandId`, komut isteklerinde ayrıca `X-XRay-Client-Id` başlığı gönderilir. SNAP isteği `frameMode: "Low"|"Raw"` taşır. Parametre sözleşmesi hedef `voltageKv`, `powerW`, `exposureTimeMs` ve sensör `profile` değerini taşır. Mock başlangıç hedefi arayüzlerle tutarlı biçimde `70 kV / 210 W / 500 ms` değeridir; bu sayı gerçek cihaz tekniği değildir. SNAP metadata'sında acquisition işlemi, LOW/RAW modu, istenen parametreler, ölçülen telemetri, doğrulanmış initiator subject/client ve `persistedOnDevice` doğrulaması ayrıdır; yerel dosya yolu veya piksel dizisi JSON'a konmaz. Pozlama tamamlandıktan sonra istemci bağlantısı kesilse bile PC tarafındaki kalıcı kayıt iptal edilmez. RAW + BMP çifti doğrulanmadan SNAP başarılı dönmez; kayıt hatası HTTP 507 üretir ve aynı `commandId` retry'ı ikinci kez pozlama yapmaz.

DeviceHost kataloğu ortak geçmişin otoritatif kaynağıdır. Yeni istemciler opaque cursor endpoint'ini kullanır; böylece sayfalar okunurken yeni SNAP eklenmesi eski bir kaydın atlanmasına neden olmaz. Offset endpoint'i yalnız geriye uyumluluk için korunur. WPF tüm kataloğu gösterir; mobil LOW önizlemeleri yerel cache'e alır, RAW kaynağı isteğe bağlı indirir ve etkin cache'i kesin olarak son 30 kayıtla sınırlar. Mobil cleanup DeviceHost arşivine çağrı yapamaz.

Cihaz durumu; client-facing `status` yanında `coordinatorState`, `generatorConnected`, `detectorConnected`, `xRayActive`, `detectorAcquiring` ve `activeAcquisitionId` alanlarını taşır. Bu alanlar koordineli donanım adapter'ından doğrulanmadığında `null` kalır; UI countdown veya renk üzerinden X-ray aktifliği tahmin etmemelidir. Remote istemci SignalR reconnect aşamasına girer girmez son hardware state'i geçersiz sayar ve X-ray durumunu `DOĞRULANMADI` olarak sunar; yeniden bağlanmada DeviceHost restart nedeniyle sıfırlanmış sequence güvenli bir yeni bağlantı nesli olarak yeniden okunur.

Eski `/live/start|stop|latest` yolları yalnız bir migration sürümü için uyumluluk alias'ı olarak tutulur. Yeni WPF/MAUI istemci katmanı `GRAB/FREEZE` yollarını kullanır.

`low`, uzun kenarı en fazla 768 piksel olan 8-bit BMP döndürür. `raw`, SHA-256 bütünlük bilgisi taşıyan sürümlü `.xrayraw` binary dosyası döndürür.

SignalR hub `device.status.changed`, `device.hardware.state.changed`, `device.telemetry.received`, `device.frame.ready`, `device.grab.frame.ready`, `device.grab.frozen`, `device.exposure.ended`, `device.snap.progress` ve `device.error.occurred` olaylarını yayınlar. `ExposureEnded` ile `FrameReady` ayrı olaylardır. SNAP ilerlemesi REST komutuyla aynı `commandId` üzerinden ilişkilendirilir. Hata olayı güvenli hata kodu ve kullanıcı mesajı taşır; stack trace yayınlamaz. Telemetri yalnız frame metadata'sı ve ölçüm değerlerini taşır; piksel verisi binary endpointte kalır. Canlı ölçüm relay kuyruğu kapasite 1 ve `DropOldest` kullanır, böylece yavaş istemci sınırsız frame biriktirmez.

GRAB telemetrisi yeni sequence bildirdiğinde istemci `/grab/latest` endpoint'inden en güncel low BMP'yi alır. İstenen sequence'den daha yeni frame yoksa veya GRAB dondurulmuşsa endpoint `204 No Content` döndürür. Sunucuda yalnız tek latest GRAB BMP tutulur.

## Bilgisayar–mobil ağ kurulumu

1. DeviceHost, detector ve generator'ın bağlı olduğu Windows bilgisayarda çalıştırılır. WPF bu servise `https://localhost:7088`, telefon ise bilgisayarın LAN adresi üzerinden `https://<bilgisayar-ip>:7088` ile bağlanır.
2. Bilgisayar adı/IP adresini içeren, telefonun güvendiği bir TLS sertifikası hazırlanır. Sertifika yolu ve parolası `Kestrel__Certificates__Default__Path` ile `Kestrel__Certificates__Default__Password` ortam değişkenlerine verilir.
3. En az 32 karakterlik Operator anahtarı `XRAYSYSTEM_DEVICEHOST_API_KEY` ortam değişkenine yazılır. Aynı anahtar mobilde DeviceHost bağlantı alanına bir kez girilir ve işletim sisteminin `SecureStorage` alanında saklanır.
4. Birbirinden ve API anahtarından farklı, en az 32 karakterlik `XRAYSYSTEM_ACQUISITION_INTEGRITY_KEY` ile `XRAYSYSTEM_AUDIT_INTEGRITY_KEY` değerleri servis secret store'unda tanımlanır. İlki acquisition sidecar'larını, ikincisi komut audit zincirini imzalar.
5. `XRaySystem.DeviceHost - LAN HTTPS` profili başlatılır. Windows Firewall'da yalnız güvenilen/private ağ profili için TCP 7088 inbound izni verilir.
6. Telefonda çalışma modu `DeviceHost (remote)`, adres `https://<bilgisayar-ip>:7088`, cihaz kimliği `mock-01` seçilir. Ayarlar kaydedilip uygulama yeniden başlatılır.

Host; TLS sertifikası, geçerli Operator anahtarı veya güvenilen ağ sınırı olmadan LAN'a açılmamalıdır. Geliştirme sertifikasını doğrulamayı kapatan mobil kod eklenmemiştir; sertifika doğrulaması başarısızsa bağlantı da başarısız olur.

## Remote buffer

`appsettings.json` içindeki `RemoteImageBuffer` bölümü count, yaklaşık frame byte limiti ve TTL değerlerini belirler. Varsayılan değerler 10 görüntü, 128 MiB ve 30 dakikadır. Limit dolduğunda en eski remote kayıt çıkarılır. Bu buffer kalıcı arşivi silmez.

## Cihaz bilgisayarındaki kalıcı SNAP arşivi

`AcquisitionStorage:Enabled` varsayılan olarak açıktır. `BaseFolder` boş bırakılırsa RAW + BMP + sürümlü `.xraymeta.json` üçlüleri kullanıcının LocalApplicationData alanındaki `XRaySystem/DeviceHost/Acquisitions` klasörüne yazılır. Üretim kurulumunda klasör Windows servis hesabının yazabildiği, erişim kontrollü ve yedeklenen bir veri alanına açıkça yönlendirilmelidir. Dosya adları ortak kV/mA/ms/UTC standardını kullanır; geçici RAW tekrar okunup hash ve acquisition metadata doğrulanmadan final isimlere taşınmaz. Metadata dosyası en son taşınan commit marker'dır ve exact target/measured provenance ile preview SHA-256 değerini taşır.

Servis yeniden başladığında en yeni arşiv kayıtları RAW iç hash'i, acquisition metadata, preview SHA-256 ve sidecar HMAC kontrolünden geçirilerek sınırlı remote buffer'a yeniden yüklenir; tüm doğrulanmış manifestler ayrıca lazy katalog indeksini yeniden kurar. Production'da `RequireSignedMetadata=true` varsayılandır ve `XRAYSYSTEM_ACQUISITION_INTEGRITY_KEY` eksik/zayıfsa host fail-closed başlar. Bu integrity key kurulum ömrü boyunca sabit ve yedekli tutulmalıdır; key kaybı eski kayıtların doğrulanamaması demektir. Eski veya yarım `.tmp`/RAW/BMP setleri 10 dakikalık grace süresinden sonra silinmeden tarihli `Quarantine` klasörüne taşınır. İmza/hash doğrulaması bozulan tam setler de API geçmişine alınmaz ve kurtarılabilir biçimde karantinaya taşınır. OS dosya ACL'i, yedekleme ve at-rest encryption ayrıca deployment sorumluluğudur.

## Acquisition safety ayarları

`appsettings.json` içindeki `AcquisitionSafety` bölümü generator-ready, detector-arm, exposure-start, exposure-stop-confirmation, detector-stop, frame-ready ve buffer-copy timeout'larını; ayrıca maksimum SNAP exposure ve maksimum GRAB oturum süresini milisaniye cinsinden belirler. Geçersiz/sıfır değerlerle host başlamaz. GRAB süre watchdog'u dolarsa normal FREEZE değil generator-first ABORT uygulanır ve durum `Faulted/Error` olur. Host kapanışında da hosted service önce bounded ABORT yolunu çağırır.

Bu değerler yalnız mock/prototip varsayılanlarıdır. Gerçek cihazda vendor dokümanı, donanım timer'ı, duty-cycle, interlock ve risk kontrolüyle doğrulanmadan production değeri sayılamaz.

## Güvenlik sınırı

- Development profili yalnız localhost HTTP testine izin verir. `LanDevelopment` profili tüm ağ arayüzlerinde yalnız HTTPS dinler ve authentication zorunludur.
- Windows Android Emulator geliştirme akışı için özel host geçidi `10.0.2.2` izinli host listesindedir; bu yalnız emulatorün Windows hostundaki DeviceHost'a erişmesini sağlar.
- Development profilinde yerel mock kullanımını bozmamak için authentication kapalıdır ve istekler geliştirme amaçlı `Admin` kimliğiyle çalışır. Production'da authentication açıktır; geçerli anahtar tanımlanmamışsa host fail-closed biçimde başlamaz.
- `Viewer`, cihaz durumu, görüntü ve SignalR olaylarını okuyabilir. `Operator`, Viewer yetkilerine ek olarak connect/disconnect, parametre, GRAB/FREEZE/ABORT ve SNAP komutlarını çalıştırabilir. `Admin` tüm bu yetkilere sahiptir.
- RemoteClient her oturumda benzersiz bir client ID gönderir. Komut lease'i varsayılan 30 saniyedir; başka istemcinin eşzamanlı kontrol komutları `423 Locked` alır. Disconnect lease'i bırakır.
- Son 256 başarılı komut sonucu aynı host ömründe bellekte tutulur. Aynı `commandId` retry'ı sensör işlemini tekrar çalıştırmaz; kimliğin farklı işlemde kullanımı reddedilir. Host restart'ında durable audit zincirinde daha önce görülmüş tüm command ID'ler yeniden pozlama riskini önlemek için `409 command_replay_after_restart` ile fail-safe reddedilir; istemci yeni bir komut üretmeden önce cihaz durumunu sorgulamalıdır.
- API istek gövdesi varsayılan olarak 64 KiB ile, subject/IP bazlı global istek hızı 60 saniyede 120 istek ile sınırlıdır. Aşım sırasıyla `413` ve `429` üretir; kuyruk oluşturmaz.
- Doğrulanmış subject/rol, komut kimliği, istemci kimliği, işlem, cihaz ve sonuç yapılandırılmış loga ve ayrı durable JSONL audit dosyasına yazılır. Audit girdileri monoton sequence, önceki kayıt SHA-256 zinciri ve `XRAYSYSTEM_AUDIT_INTEGRITY_KEY` HMAC'i taşır; mevcut zincir bozuksa host başlamaz. Dosya varsayılan 64 MiB sınırına ulaştığında yeni safety-relevant komut audit yazılamadan fail-closed reddedilir; deployment kontrollü archive/rotation yapmalıdır. API anahtarı, görüntü/piksel verisi ve serbest hata ayrıntısı audit dosyasına yazılmaz.

Production anahtarı kaynak koda veya `appsettings.json` dosyasına yazılmamalıdır. Ortam değişkeni/secret store örneği:

```text
Security__AuthenticationEnabled=true
Security__ApiKeys__0__Key=<en-az-32-karakterlik-gizli-deger>
Security__ApiKeys__0__Subject=operator-01
Security__ApiKeys__0__Role=Operator
XRAYSYSTEM_ACQUISITION_INTEGRITY_KEY=<ayri-en-az-32-karakterlik-gizli-deger>
XRAYSYSTEM_AUDIT_INTEGRITY_KEY=<ayri-en-az-32-karakterlik-gizli-deger>
```

WPF ve RemoteSmoke anahtarı `XRAYSYSTEM_DEVICEHOST_API_KEY` ortam değişkeninden alır. MAUI anahtarı işletim sisteminin güvenli deposundan yükler. REST tarafında API-key/Bearer, SignalR tarafında Bearer kimliği otomatik eklenir. `Tools/RemoteSmoke` için isteğe bağlı üçüncü argüman da anahtar olabilir; terminal geçmişine secret yazmamak için ortam değişkeni tercih edilmelidir.

Bu API-key katmanı geliştirme/prototip güvenlik sınırıdır. Gerçek cihaz/LAN kullanımı öncesinde TLS sertifika provisioning/rotation, kurumsal kimlik veya cihaz sertifikası, OS ACL/secret yönetimi, audit retention/rotation, zaman senkronizasyonu ve fiziksel güvenlik/interlock incelemesi ayrıca tamamlanmalıdır.

`/healthz` yalnız proses liveness bilgisidir. `/readyz`, acquisition ve audit klasörlerine write-through probe uygular ve audit kapasite limitini kontrol eder; kalıcı storage yazılamıyorsa `503` döner. Orkestrasyon/load balancer komut trafiğini yalnız readiness başarılı hosta yönlendirmelidir.

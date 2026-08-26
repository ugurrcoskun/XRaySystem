# XRaySystem

XRaySystem, CMOS X-Ray dedektör görüntüsü ve telemetri akışını gerçek cihaz gelmeden geliştirmek için hazırlanmış bir simülasyon/prototip çözümüdür. Mevcut kod klinik tanı veya hasta üzerinde kullanım için onaylı değildir.

## Projeler

- `XRaySystem.Domain`: sensörden bağımsız modeller ve arayüzler.
- `XRaySystem.Application`: use case ve DTO katmanı.
- `XRaySystem.Infrastructure`: mock/disk sensörleri, SQLite ve sample data.
- `XRaySystem.Contracts`: local dosya yolundan bağımsız REST/binary ağ sözleşmeleri.
- `XRaySystem.DeviceHost`: mock sensörün tek sahibi olan geliştirme amaçlı ASP.NET Core host.
- `XRaySystem.RemoteClient`: WPF ve MAUI için ortak REST + SignalR DeviceHost istemcisi.
- `XRaySystem.WPF`: Windows masaüstü istemcisi.
- `XRaySystem.Mobile`: Local mock veya Remote DeviceHost modunda çalışan .NET MAUI istemcisi.

Mimari ayrıntılar için `project.md`, uygulama sırası için `plan.md` kullanılmalıdır.

Mobil snap kayıtları iki temsilden oluşur: hızlı listeleme için BMP önizleme ve asıl görüntü kaynağı olarak metadata ile SHA-256 bütünlük doğrulaması taşıyan sürümlü `.xrayraw` Gray16 dosyası. Mobil SQLite veritabanı ve görüntüler `FileSystem.AppDataDirectory` altında tutulur; WPF'nin kalıcı TIFF/SQLite geçmişiyle karışmaz.

Kaynağı, lisansı ve anonimleştirmesi doğrulanmamış `Infrastructure/SampleData` içeriği karantinadadır ve varsayılan WPF build/publish çıktısına alınmaz. Kontrollü geliştirme opt-in'i yalnız RAW + manifest içindir; DICOM kaynakları uygulama paketine hiçbir durumda kopyalanmaz.

GitHub Actions CI, Linux üzerinde çekirdek testleri ve Windows üzerinde WPF derleme/Gray16 TIFF testini ayrı işler olarak çalıştırır. MAUI işi ilk temiz Android runtime doğrulamasından sonra eklenecektir; böylece henüz doğrulanmamış workload kurulumu CI sonucunu gizlemez.

DeviceHost; REST cihaz komutları, SignalR status/telemetry olayları, HMAC/hash doğrulamalı kalıcı ortak acquisition kataloğu, cursor sayfalama, 768 px LOW BMP ve on-demand `.xrayraw` indirme sağlar. WPF tüm katalog geçmişini birleştirir; mobil LOW-first çalışır ve yerel cache'i son 30 kayıtla sınırlar. Production HTTPS zorlaması, API-key authentication, Viewer/Operator/Admin rol politikaları, doğrulanmış kimliğe bağlı command audit, idempotency ve tek aktif istemci lease'i eklenmiştir. Ayrıntılar `XRaySystem.DeviceHost/README.md` içindedir.

`XRaySystem.RemoteClient`, status/telemetry olaylarını dinler, sequence tabanlı en güncel live BMP'yi indirir ve SNAP low/raw içeriklerini content-type doğrulamasıyla alır. `RemoteSensorService` bu hattı ortak `ISensorService` sözleşmesine uyarlar; böylece WPF ve MAUI aynı ViewModel/use case yapısıyla Local veya Remote çalışabilir. Gerçek uçtan uca kontrol `Tools/RemoteSmoke` ile yapılır.

## Local / Remote çalışma modu

- Varsayılan mod `Local`dır ve uygulama doğrudan `MockXRaySensorService` kullanır.
- Mobil uygulamada **Device Link > Çalışma Modu** alanından `Yerel mock` veya `DeviceHost (remote)` seçilir. Ayar kaydedildikten sonra uygulama kapatılıp yeniden açılır.
- Android Emulator için varsayılan DeviceHost adresi `http://10.0.2.2:5088`, Windows Machine/iOS/MacCatalyst için `http://127.0.0.1:5088` değeridir.
- WPF için Visual Studio çalıştırma profilinde `XRaySystem.WPF - Local Mock` veya `XRaySystem.WPF - Remote DeviceHost` seçilir; terminal komutu gerekmez.
- Remote moddan önce `XRaySystem.DeviceHost - HTTP` profiliyle DeviceHost çalıştırılmalıdır. HTTP cleartext erişimi Android'de yalnız Debug derlemesinde açıktır; Release/üretim için TLS zorunludur.
- Production remote bağlantısında WPF/MAUI istemcisine anahtar `XRAYSYSTEM_DEVICEHOST_API_KEY` ortam değişkeniyle verilir. Yerel Development mock profilinde anahtar gerekmez.

## Geliştirme Ortamları

### macOS — MacBook Air M1

macOS ana kod geliştirme ortamıdır. Domain, Application, Infrastructure, Python araçları ve MAUI geliştirmesi burada yapılabilir.

Kökteki `global.json`, .NET 10'un `10.0.200` veya daha yeni feature band'ini kabul eder. Böylece macOS'taki `10.0.202` ve Windows VM'deki `10.0.400` aynı çözümde kullanılabilir.

Çekirdek ve WPF cross-target compile kontrolü:

```bash
dotnet build XRaySystem.WPF/XRaySystem.WPF.csproj --no-restore --disable-build-servers -m:1
```

Bu komut WPF kodunun derlendiğini gösterir; WPF uygulamasını macOS üzerinde çalıştırmaz ve Windows runtime/UI doğrulamasının yerine geçmez.

Platformdan bağımsız unit ve SQLite migration testleri:

```bash
dotnet test XRaySystem.Tests/XRaySystem.Tests.csproj
```

Mac'te yalnızca .NET 10 runtime kuruluysa test projesindeki `RollForward=Major`, `net8.0` testhost'un aynı makinede çalışmasını sağlar.

Mobil proje için kurulu workload'ları kontrol et:

```bash
dotnet workload list
```

Eksik MAUI workload'larını proje tanımına göre geri yüklemek için, internet erişimi olan geliştirme ortamında:

```bash
dotnet workload restore XRaySystem.Mobile/XRaySystem.Mobile.csproj
```

Yalnızca MacCatalyst hedefini geliştirmek ve çalıştırmak için:

```bash
dotnet workload install maui-maccatalyst
dotnet build XRaySystem.Mobile/XRaySystem.Mobile.csproj -f net10.0-maccatalyst
dotnet run --project XRaySystem.Mobile/XRaySystem.Mobile.csproj -f net10.0-maccatalyst
```

Mobil dashboard telefon genişliğinde dikey, 900 piksel ve üzerindeki pencere genişliğinde iki kolonlu çalışır. Runtime smoke testinde connect/live/stop/snap, Window/Level, yerel geçmiş ve ekran döndürme/yeniden boyutlandırma birlikte doğrulanmalıdır.

Terminal komutu gerektirmeyen Windows Machine/Android Emulator hedef seçimi ve adım adım doğrulama için `Docs/Mobile_Smoke_Test.md` kullanılmalıdır.

MAUI XAML veya üretilmiş `InitializeComponent` hatasından sonra eski SourceGen çıktısını temizlemek için:

```bash
dotnet clean XRaySystem.Mobile/XRaySystem.Mobile.csproj
dotnet restore XRaySystem.Mobile/XRaySystem.Mobile.csproj
```

Projede deneysel `MauiXamlInflator=SourceGen` zorlaması kullanılmaz. Debug standart runtime XAML, Release ise XamlC davranışını kullanır. Native SQLite paketi CVE-2025-6965 nedeniyle `2.1.12` sürümüne sabitlenmiştir.

### Windows sanal makinesi

Windows VM, WPF runtime ve Windows'a özgü entegrasyon test ortamıdır.

```powershell
dotnet --info
dotnet --list-runtimes
dotnet restore XRaySystem.WPF\XRaySystem.WPF.csproj
dotnet build XRaySystem.WPF\XRaySystem.WPF.csproj
dotnet test XRaySystem.Tests\XRaySystem.Tests.csproj
dotnet test XRaySystem.WPF.Tests\XRaySystem.WPF.Tests.csproj
dotnet run --project XRaySystem.WPF\XRaySystem.WPF.csproj
```

Projeler `net8.0` hedeflediği için listede `Microsoft.NETCore.App 8.x` ve WPF için
`Microsoft.WindowsDesktop.App 8.x` bulunmalıdır. Yalnızca .NET 10 SDK/runtime varsa
Windows VM'ye .NET 8 Desktop Runtime kurulmalıdır; `global.json` SDK seçimini çözer,
ancak eksik çalışma zamanı paketinin yerine geçmez.

Her önemli WPF değişikliğinde şu smoke test uygulanmalıdır:

1. Uygulama `DISCONNECTED` durumunda açılır.
2. Connect ile live preview başlar.
3. Stop ve resume live çalışır.
4. Arka arkaya iki snap alınabilir.
5. `XRaySystem.WPF.Tests` içindeki Gray16 TIFF round-trip testi geçer.
6. TIFF dosyası ve SQLite metadata kaydı oluşur.
7. Uygulama yeniden açıldığında geçmiş korunur.
8. History görüntüsü, Window/Level, zoom ve pan çalışır.
9. Dört görüntü karşılaştırması açılır ve senkron zoom/pan çalışır.
10. Pencere 1050 piksel altına indirildiğinde ana görüntü görünür kalır.
11. Explorer'da gösterme ve TIFF dışa aktarma çalışır.
12. `XRaySystem.WPF - Remote DeviceHost` profiliyle aynı connect/live/stop/SNAP akışı DeviceHost üzerinden çalışır.

## Yerel veriler

WPF çalışma zamanı verileri Windows'ta şu dizinde tutulur:

```text
%LOCALAPPDATA%\XRaySystem\
├── XRaySystem.db
└── Images\
```

Bu veriler, `bin/obj`, IDE dosyaları ve kullanıcıya özel ayarlar Git'e eklenmemelidir. `Docs/`, `plan.md` ve `project.md` kullanıcı tercihiyle yerel tutulur ve mevcut `.gitignore` kararı değiştirilmez.

## Sample data uyarısı

`XRaySystem.Infrastructure/SampleData` altındaki DICOM/RAW verileri yalnızca simülasyon içindir. Mevcut örneklerin kaynak, lisans ve anonimleştirme durumu doğrulanmış değildir; ayrıntılı risk ve hash envanteri için `XRaySystem.Infrastructure/SampleData/README.md` okunmalıdır.

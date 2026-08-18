# XRaySystem

XRaySystem, CMOS X-Ray dedektör görüntüsü ve telemetri akışını gerçek cihaz gelmeden geliştirmek için hazırlanmış bir simülasyon/prototip çözümüdür. Mevcut kod klinik tanı veya hasta üzerinde kullanım için onaylı değildir.

## Projeler

- `XRaySystem.Domain`: sensörden bağımsız modeller ve arayüzler.
- `XRaySystem.Application`: use case ve DTO katmanı.
- `XRaySystem.Infrastructure`: mock/disk sensörleri, SQLite ve sample data.
- `XRaySystem.WPF`: Windows masaüstü istemcisi.
- `XRaySystem.Mobile`: .NET MAUI standalone mobil prototipi.

Mimari ayrıntılar için `project.md`, uygulama sırası için `plan.md` kullanılmalıdır.

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

## Yerel veriler

WPF çalışma zamanı verileri Windows'ta şu dizinde tutulur:

```text
%LOCALAPPDATA%\XRaySystem\
├── XRaySystem.db
└── Images\
```

Bu veriler, `bin/obj`, IDE dosyaları ve kullanıcıya özel ayarlar Git'e eklenmemelidir. Buna karşılık `README.md`, `project.md`, `plan.md` ve `Docs/` altındaki proje belgeleri sürüm kontrolünde tutulmalıdır.

## Sample data uyarısı

`XRaySystem.Infrastructure/SampleData` altındaki DICOM/RAW verileri yalnızca simülasyon içindir. Kaynak, lisans ve de-identification bilgileri doğrulanmadan yeni DICOM dosyaları depoya eklenmemelidir.

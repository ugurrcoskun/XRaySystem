# XRaySystem.RemoteClient

WPF ve MAUI için ortak, UI bağımsız DeviceHost istemcisidir.

- REST üzerinden cihaz durumu, connect/disconnect, parametre, live, SNAP ve geçmiş komutlarını çalıştırır.
- SignalR üzerinden status, telemetry, SNAP progress ve güvenli hata olaylarını alır ve otomatik yeniden bağlanmayı kullanır.
- Telemetri yeni bir live sequence bildirdiğinde yalnız en güncel low BMP'yi indirir.
- Live BMP'nin gerçek sequence, acquisition ID ve boyut başlıklarını doğrular; tetikleyen telemetriyle aynıymış gibi varsaymaz.
- SNAP geçmişinden low BMP ve `.xrayraw` binary içeriklerini alır.
- Beklenmeyen content-type, boş binary cevap ve başarısız HTTP durumlarını hata olarak bildirir.
- Yapılandırıldığında API anahtarını hem REST hem SignalR bağlantısında taşır; anahtarı loglamaz.
- Yerel WPF/MAUI dosya yollarını bilmez.

`RemoteSensorService`, binary raw SNAP'i doğrulayıp domain Gray16 ölçümüne; low live BMP'yi canonical top-left Gray16 sunum karesine dönüştürür. WPF ve MAUI bu adapter'ı Local/Remote DI seçimiyle kullanır. Mobil mod/adres seçimi uygulama içinden saklanır; WPF için Local ve Remote Visual Studio launch profilleri vardır.

Uçtan uca doğrulama için `Tools/RemoteSmoke` aracı DeviceHost'a bağlanır; SignalR live frame, SNAP low ve hash doğrulamalı raw indirme hattını birlikte denetler.

Production bağlantısında anahtar `XRAYSYSTEM_DEVICEHOST_API_KEY` ortam değişkeniyle WPF ve MAUI'ye verilir. Development localhost mock profilinde anahtar gerekmez.

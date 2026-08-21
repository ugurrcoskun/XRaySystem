# SampleData güvenlik ve kaynak envanteri

Bu klasördeki dosyalar yalnızca geliştirme simülasyonu içindir. Klinik tanı, hasta üzerinde kullanım veya yeniden dağıtım için onaylı değildir.

## Kaynak ve gizlilik durumu

- Kaynak URL/kurum ve lisans kaydı mevcut değildir.
- `CT1_BE.dcm`, `CT1_IMP.dcm` ve `CT1_LE.dcm` dosyalarının 19 Ağustos 2026 tarihli metadata denetiminde hasta adı, hasta kimliği, cinsiyet, kurum adı, çalışma tarihi ve SOP Instance UID alanlarının dolu olduğu görüldü. Değerler bu belgeye kopyalanmamıştır.
- Doldurulmuş alanların gerçek, takma veya test verisi olduğu doğrulanamadığından dosyalar **potansiyel olarak kimlik belirleyici** kabul edilmelidir.
- Piksel içine yazılmış kimlik bilgisinin bulunmadığı ayrıca doğrulanmamıştır.
- `CT_small` RAW çiftinin kaynak DICOM dosyası klasörde bulunmamaktadır.

Bu eksikler çözülene kadar DICOM dosyalarını veya bunlardan türetilen RAW dosyalarını public depoya, pakete ya da üçüncü tarafa dağıtmayın. Dosyalar daha önce uzak depoya gönderildiyse yalnız çalışma ağacından silmek yeterli değildir; Git geçmişi ayrıca denetlenmelidir.

WPF build/publish çıktısı bu verileri varsayılan olarak içermez. Yalnız kontrollü yerel geliştirme gereksiniminde `IncludeUnverifiedSampleData=true` açıkça verilirse kök RAW dosyaları ve manifest çıktıya kopyalanır; `DicomSource/*.dcm` hiçbir durumda proje çıktısına dahil edilmez. Bu opt-in dağıtım onayı anlamına gelmez.

## Bütünlük envanteri

| Dosya grubu | Snap SHA-256 | Live SHA-256 | Durum |
| --- | --- | --- | --- |
| `CT1_BE` | `3fea7d1f51900c2e3edc501040803328f995b87ce6a2f90d9aad121a0fd038bb` | `998149d604d04d18796e8cf007373bfe675a2ace81970c1dd383af8502ea2936` | Kaynak DICOM var; lisans ve anonimleştirme doğrulanmadı |
| `CT1_IMP` | `96d0fd0228fb933edb23989d2987390423cf0bccfa311fcd708c5c0349d0f1a4` | `80842ee500af1cd259cdc8dc4bf1397b51dd485babb67a0b8ca9ed70e63efb29` | Kaynak DICOM var; lisans ve anonimleştirme doğrulanmadı |
| `CT1_LE` | `96d0fd0228fb933edb23989d2987390423cf0bccfa311fcd708c5c0349d0f1a4` | `80842ee500af1cd259cdc8dc4bf1397b51dd485babb67a0b8ca9ed70e63efb29` | IMP ile türetilmiş RAW çıktıları byte-identical; dönüşüm doğrulanmalı |
| `CT_small` | `e87d385112b2e0f6665acbbaf53afcedf87deabfd289f8f5f61d5be4291438f4` | `6337e109a461b9ea952fa921f6254adfe37c771d61b8cdd79714055352bd676b` | Kaynak ve lisans bilinmiyor |

## Güvenli veri kabul kontrolü

1. Kaynak ve lisans yazılı olarak kaydedilir.
2. DICOM metadata anonimleştirilir ve yeni UID'ler üretilir.
3. Burned-in annotation/piksel kimliği görsel olarak denetlenir.
4. RAW dosyaları anonimleştirilmiş kaynaktan yeniden üretilir.
5. `manifest.json`, boyutlar ve bu envanterdeki hash değerleri güncellenir.
6. Otomatik SampleData manifest testi çalıştırılır.

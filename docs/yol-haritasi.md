# TriggerForge yol haritası — 1.1 → 2.0

Dayanak: `docs/rakip-analizi.md` (19 Kasım 2026). Faz numaraları PLAN.md'nin devamı (Faz 9
Pico köprüsü isteğe bağlı kalıyor). Her faz bir sürüm: 1.1.0, 1.2.0 …

## Çalışma düzeni (her fazda aynı döngü)

| Adım | İçerik | Çıktı |
|---|---|---|
| 1. Ön araştırma | Salt okunur | Protokol/belge özeti, kaynaklı; temiz oda kuralı: kod değil bilgi |
| 2. Sözleşme | En küçük kapsam | `docs/contract-phaseN.md`: işler, dosya sınırları, kabul ölçütü |
| 3. Plan denetimi | Onay olmadan kod yok | ONAYLANABİLİR kararı |
| 4. Bölme | Klasörleri ayrık işler | İş listesi |
| 5. Kod | En çok iki iş aynı anda | Diff + testler |
| 6. Kontrol | Diff okuma, `dotnet test`, `npm run build`, arayüz önizlemesi | En çok 2 düzelt-denetle turu |
| 7. Kod incelemesi | Bulgular hemen düzeltilir | İnceleme listesi |
| 8. Kapanış | PLAN.md "Faz N sonucu", commit + push, `build/release.ps1` | Yayınlanmış sürüm |
| 9. Donanım testi | KH | Kısa test listesi; sonuçlar sonraki fazda düzeltilir |

Kurallar: geliştirme sırasında controller'a veya PC'ye gerçek giriş/çıkış gönderen kod çalıştırılmaz;
profil şeması değişirse `version` + göç fonksiyonu + eski profil testi.

## Fazlar

### Faz 10 — Hızlı kazançlar → 1.1.0 (≈ 1 hafta)
Rakiplerin hepsinde olan, bizim altyapıya küçük eklenen işler.
- Kontrolcü/klavye kısayoluyla atamayı duraklat-sürdür (basılı tuşlar bırakılır, tepside durum); tam ekran oyunda masaüstü atamasını otomatik kapat.
- Gyro ratchet: "basılıyken durdur" kipi ve touchpad'e dokununca gyro.
- Gyro → sanal sağ stick (Xbox/DualSense sanal pad'e; anti-deadzone).
- Tuşta döngü (A→B→C) ve üçlü basış.
- Haptik onay darbesi (katman, makro, profil değişimi), Bluetooth RSSI, günlük otomatik profil yedeği.

Bölme: **A** `src/Tactile.Core/Mapping` + `Model` (motor) ‖ **B** `ui/` (arayüz, iki dil). Ardından **C** küçük: RSSI + yedek.
Risk: düşük. Test: birim testleri + KH masaüstünde 10 dk.

### Faz 11 — Tuş motoru v2 → 1.2.0 (≈ 2 hafta) ⚠ şema değişikliği
Rakiplerin bizi en çok geçtiği yer.
- Chord / aynı anda basma (çakışma önceliği, bileşen tuşları susturma).
- Çoklu shift katmanı: herhangi bir tuş, hold/toggle, Edge kolları ve Fn ile.
- Tetikte soft/full pull, stick dış/iç halka aksiyonları (eşik + histerezis).
- Flick stick + gerçek dünya kalibrasyonu ("bir tur = X derece" sihirbazı).

Ön araştırma: Steam Input activator ve JoyShockMapper belgeleri, yalnız davranış bilgisi.
Bölme: **A** motor (ButtonMachine/Mapper tek elde; burada paralel yazım yok) ‖ **B** arayüz düzenleyici. Yanında motor denetimi.
Risk: profil göçü bozulursa ayarlar kaybolur → göç testleri, eski profil örnekleri, yedek (Faz 10'dan).

### Faz 12 — Oyun uyumu → 1.3.0 (≈ 2 hafta)
- Oyuna göre otomatik sanal çıkış (exe listesi: DualSense destekli → DualSense, değilse Xbox; elle override).
- Sanal DS4 çıkışı (USB/IP üzerinde yeni descriptor; touchpad, gyro, ışık).
- DSU/Cemuhook hareket sunucusu (emülatörler).

Ön araştırma: DS4 USB descriptor ve DSU protokolü, kendi cümlelerimizle `docs/protocol-emulation.md`'ye.
Bölme: **A** `src/Tactile.Emulation` (DS4) ‖ **B** `Core/Integration` (DSU + otomatik seçim listesi). Arayüz sonra.
Risk: orta, donanım testi şart (KH: joy.cpl, bir DS4 oyunu, Cemu/Dolphin).

### Faz 13 — Yarış telemetrisi → 1.4.0 (≈ 1-2 hafta)
Kural motoru hazır; yalnız yeni kaynaklar.
- F1 serisi UDP, ACC shared memory, AC shared memory; sonra BeamNG/WRC.
- Her oyuna hazır şablon (fren direnci, ABS/patinaj titreşimi, devir ışığı).

Ön araştırma: EA F1 resmî UDP belgesi, ACC/AC shared memory düzenleri.
Bölme: **A** F1 UDP ‖ **B** ACC/AC shared memory (ayrı dosyalar). Arayüz kartları sonra.
Test: `udptest.py` benzeri sahte veri göndericileri; gerçek oyun KH'de yoksa oyunsuz test.

### Faz 14 — Paylaşım ve ekran → 1.5.0 (≈ 2 hafta)
- Topluluk profil kataloğu: `kaan237/triggerforge-profiles` reposu, imzalı `index.json`, uygulamada arama/filtre/önizleme, içe aktarmada "program çalıştır" silinir (mevcut kural).
- OSD: anti-cheat'e dokunmayan saydam üst pencere; profil/katman değişimi, pil uyarısı, duraklatma durumu.
- Ekran klavyesi (koltuktan kullanım).

Bölme: **A** katalog (C# + yayın aracı) ‖ **B** OSD penceresi (`Tactile.Host`). Klavye üçüncü iş.
Risk: katalog = dışarıdan gelen veri → güncelleyicideki imza/host allowlist düzeni aynen.

### Faz 15 — Büyük yatırımlar → 2.0 (her biri önce araştırma, sonra devam/dur kararı)
1. **Bluetooth mikrofon**: önce açık kaynak bilgi taraması (DS4Windows çatalı yapıyor). Olumluysa sanal DualSense'in mikrofon girişine bağlanır.
2. **Edge dahili profilleri**: oku → yedekle → yaz → tekrar oku. Protokol bilgisi yoksa yalnız USB capture ile KH'nin cihazında araştırma.
3. **Fiziksel DS4 desteği**: HID katmanında ikinci cihaz türü.
4. **Radial menu**: OSD penceresinin üstüne (Faz 14'e bağlı).

Her biri ayrı küçük faz olur; araştırma "olmaz" derse listeden düşer.

## Sıra ve paralellik

```
Faz 10 ──> Faz 11 ──> Faz 14 (OSD) ──> Faz 15.4 radial
   │
   ├──> Faz 12 (Emulation klasörü; 11 ile dosya çakışmaz, aynı anda yürüyebilir)
   └──> Faz 13 (Integration klasörü; bağımsız, boşlukları doldurur)
```

Faz 10 her şeyden önce: yedek ve duraklatma, sonraki fazlarda güvenlik ağı.
Aynı anda en çok iki iş; dosyaları ayrık değilse sırayla.

## KH'nin işleri (yalnız sonda)
- Her faz sonunda donanım test listesi (5-10 madde).
- Faz 12: bir DS4 destekli oyun, bir emülatör. Faz 13: varsa F1/ACC. Faz 15.2: Edge ile USB capture.

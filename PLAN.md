# DualSense Kontrol Merkezi — Uygulama Planı

Tarih: 2026-11-18 · Durum: taslak, kod yok · Sahip: KH

## 0. Hedef

Kullanıcı DualSense'i **Bluetooth ile** bağlar, **yalnızca bizim uygulamayı** açar. Sonuç:

- DualSense desteği olan oyunlarda **oyunun kendi** adaptif tetik + haptik + hoparlör desteği kablosuz çalışır.
- DualSense desteği olmayan oyunlarda sanal Xbox 360 ile oyun çalışır, tetik/LED/rumble efektlerini **bizim profillerimiz** verir.
- Her şey **oyun bazında** özelleştirilebilir.

Kapsam dışı (şimdilik): Linux, macOS, mobil, DualShock 4 desteği, bulut senkron.

## 1. Doğrulanmış teknik temel (kaynakla)

Bu bölümdeki her satır indirilen kaynak koddan okundu (2026-11-18).

| Konu | Değer | Kaynak |
|---|---|---|
| Kimlik | VID `054C`, PID `0CE6` (DualSense), `0DF2` (Edge) | hid-playstation.c, DualSenseClient |
| USB input | Report `0x01`, 64 B | hid-playstation.c:81-82 |
| BT input | Report `0x31`, 78 B, sonda CRC32 (seed `0xA1`), veri `data[2]`'den başlar | hid-playstation.c:83-84, 1456-1464 |
| USB output | Report `0x02`, 63 B = ID + 47 B ortak blok + 15 B boş | hid-playstation.c:85-86, 311-316 |
| BT output | Report `0x31`, 78 B = ID + seq_tag + tag `0x10` + 47 B ortak + 24 B + CRC32 (seed `0xA2`) | hid-playstation.c:87-88, 150, 301-309 |
| Ortak blok | flag0, flag1, motor R, motor L, ses seviyeleri, mic LED, power save, **R2 tetik [10..20]**, **L2 tetik [21..31]**, …, LED parlaklık, oyuncu LED, RGB | hid-playstation.c:270-299, SetStateData.cs:204-223 |
| Feature | `0x05` kalibrasyon (41 B), `0x09` eşleşme/MAC (20 B), `0x20` firmware (64 B) | hid-playstation.c:90-95 |
| Tetik modları | Off `0x05`, Feedback `0x21`, Weapon `0x25`, Vibration `0x26`, Bow `0x22`, Galloping `0x23`, Machine `0x27` (+ eski `0x01/02/06/11/12`) | TriggerEffectType.cs |
| Tetik parametreleri | 10 bölge × 3 bit kuvvet, aktif bölge maskesi, 11 B/tetik | TriggerEffectBuilder.cs |
| BT haptik | Report `0x32`, 142 B: `0x91` oturum bloğu + `0x92` haptik bloğu, **64 B = 32 kare s8 stereo @ 3 kHz**, her 10.667 ms | BluetoothAudioReportBuilder.cs |
| BT hoparlör | Report `0x35` (yalnız ses), `0x36` (durum + haptik + ses, 398 B), `0x39` (çift kare, 547 B); ses **Opus 48 kHz stereo, 200 B CBR** | BluetoothAudioReportBuilder.cs, DualSenseBtAudioPipeline.cs |
| Haptik kaynağı | Sanal USB DualSense **4 kanal 48 kHz** ses çıkışı; kanal 1-2 hoparlör, **3-4 haptik**; 512 kare → 16:1 ortalama → 32 kare s8 | UsbAudioFunction.cs:68-79, DualSenseBtAudioPipeline.cs:209-212 |
| Sanal cihaz | Uygulama içinde **USB/IP sunucusu** (127.0.0.1) + usbip-win2 VHCI sürücüsüne `PLUGIN_HARDWARE` IOCTL. Xbox 360 de aynı yolla, **ViGEmBus gerekmez** | EmulationServer.cs, UsbIp/*, VhciIoctlAttachStrategy.cs |
| Gizleme | HidHide; uygulamanın kendisi beyaz listede | HidHideService.cs |

Referans kodların lisansı:

| Proje | Lisans | Ne için okunacak |
|---|---|---|
| DualSenseClient | GPL-3.0 | Mimari, USB/IP, ses ve haptik hattı |
| DS5_Bridge | AGPL-3.0 | Pico yolu |
| SAxense | MPL-2.0 | En sade BT haptik örneği |
| DS5Dongle | MIT | Pico firmware |
| Linux hid-playstation | GPL-2.0 | Rapor yapıları |

**Lisans kararı (2026-11-18): kapalı kaynak** (`LICENSE`: All rights reserved).

**Kural: referans projelerden hiç kod alınmaz; her satır sıfırdan yazılır.** Referanslardan yalnız **protokol bilgisi** alınır: byte offsetleri, rapor ID'leri, CRC seed'leri, tetik bit düzeni, USB descriptor değerleri, paket zamanlaması. Bunlar donanımın nasıl çalıştığına dair gerçekler; telif konusu değil, atıf gerektirmez.

Yöntem: referanslar okunur, bilgi `docs/protocol.md`'ye kendi cümlelerimizle yazılır. Kod bu nottan, bizim mimarimize göre yazılır.

**Dış kütüphaneleri en aza indir:**

| İhtiyaç | Seçim |
|---|---|
| HID | Kendi P/Invoke'umuz: Windows `hid.dll` + `SetupAPI` (kütüphane yok) |
| WASAPI ses | Kendi COM interop'umuz (kütüphane yok) |
| USB/IP, CRC32, tetik, haptik | Kendi kodumuz |
| Opus kodlayıcı | **libopus** (BSD). Sıfırdan Opus yazmak aylar sürer, mantıklı değil |
| Arayüz | React (MIT) + Vite |

Kalan kütüphaneler (libopus, React) **lisans metninin dağıtılan pakette durmasını** şart koşuyor. Bu, kurulum klasöründe bir `licenses.txt` dosyası demek; arayüzde görünmez. Yasal zorunluluk olduğu için bu dosya kaldırılmaz.

## 2. Mimari kararlar

| Karar | Seçim | Neden | Alternatif |
|---|---|---|---|
| Dil | **C# / .NET 10** | Referansların hepsi C#; P/Invoke ile HID, WASAPI ve IOCTL kolay | Rust (daha zor), Electron (sürücü/ses katmanı zayıf) |
| HID | **Windows `hid.dll` + `SetupAPI`** (kendi P/Invoke'umuz) | Bağımlılık yok; `HidD_SetOutputReport`/`WriteFile` + overlapped okuma yeterli | HIDAPI |
| UI | **React + TypeScript + Vite**, C# kabuk içinde **WebView2** | Tasarım özgürlüğü (animasyon, canlı çizim, özel bileşenler), KH'nin bildiği alan; WebView2 Windows 11'de hazır | Avalonia (tek dil ama özel tasarım zahmetli), Electron (fazladan Chromium + Node) |
| UI ↔ çekirdek | WebView2 `PostWebMessageAsJson` / `window.chrome.webview` üzerinden tipli mesajlar; canlı input 60 Hz'e seyreltilir | Ağ portu açılmaz, ayrı süreç yok | Yerel WebSocket |
| Tray | C# tarafında (`NotifyIcon`); pencere kapanınca çekirdek çalışmaya devam eder | UI kapalıyken bellek düşük kalır | — |
| Opus | **libopus** (P/Invoke) | Standart ve hızlı; lisans metni `licenses.txt` dosyasında | — |
| Ses | **WASAPI**, kendi COM interop'umuz | Loopback yakalama, cihaz yönlendirme; bağımlılık yok | — |
| Sanal cihaz | Kendi USB/IP sunucumuz + usbip-win2 | Yazılımla, ek donanımsız, imzalı sürücü | Pico köprü (yedek yol, Faz 9) |
| Ayar saklama | JSON dosyaları `%APPDATA%\<app>\` | Elle düzenlenebilir, paylaşılabilir profil | SQLite |
| Süreç modeli | **Tek süreç**, çekirdek ve UI ayrı katman; UI kapanınca tray'de çalışır | Basit | İleride Windows servisi + UI |

Katmanlar (her biri ayrı .NET projesi):

```
App.Host          C# pencere + WebView2 + tray + mesaj köprüsü
ui/               React + TS + Vite arayüzü (ayrı klasör, derlenince Host'a gömülür)
App.Core          profil motoru, kural motoru, efekt motoru, olay yolu
Device.DualSense  rapor okuma/yazma, CRC, tetik builder, kalibrasyon
Device.Hid        hid.dll + SetupAPI sarmalayıcı, cihaz takip
Audio             WASAPI yakalama, haptik hattı, Opus
Emulation         USB/IP sunucusu, sanal DualSense/X360 cihazları, VHCI
Integration       UDP API, telemetri eklentileri, foreground izleme
Drivers           HidHide ve usbip-win2 kurulum/durum kontrolü
Tests             birim testleri + kayıtlı rapor dosyalarıyla tekrar testleri
```

Kural: `Device.*` katmanı UI'dan habersiz; her rapor builder'ı saf fonksiyon ve test edilebilir.

## 2b. Arayüz tasarım dili

Amaç: donanım aracı gibi hissettiren, kendine has bir arayüz. Hazır şablon görüntüsü olmayacak.

**Kimlik**
- **Vurgu rengi canlı lightbar rengidir.** Kullanıcı LED'i maviye çekince arayüzün vurgu rengi de mavi olur. Bu, uygulamanın imzası.
- Zemin: grafit, saf siyah değil. Yüzey katmanları ince çizgilerle ayrılır; kartlar gölgeyle değil, 1 px çizgiyle.
- Yazı: arayüz için tek bir grotesk font (ör. Geist veya IBM Plex Sans); sayılar ve değerler için eşit genişlikli rakamlar (`tabular-nums`), canlı değerler titremez.
- Köşeler küçük (4-6 px). Büyük yuvarlak "baloncuk" kartlar yok.

**Kaçınılacaklar (hazır yapay zekâ görüntüsü)**
- Mor-mavi gradyanlar, her yerde cam efekti (glassmorphism), neon parlamalar.
- Her başlığın yanında emoji veya rastgele ikon; "✨ AI" süslemeleri.
- Aynı boy kartlardan oluşan ızgara panolar; içi boş "istatistik kartları".
- Merkezde dev başlık + alt başlık + iki buton kalıbı.
- Anlamsız mikro animasyonlar; her şeyin hover'da büyümesi.

**Ekranı özel yapan parçalar**
- **Canlı controller çizimi** (SVG): basılan tuş yanar, analoglar hareket eder, tetik basıncı dolgu olarak görünür, touchpad'de parmak noktaları.
- **Tetik editörü:** 10 bölgeli direnç grafiği; bölgeye tıklayıp kuvveti sürükle; canlı tetik basıncı grafikte dikey çizgi olarak akar.
- **Analog ölü bölge editörü:** daire içinde gerçek analog izi (son 1 sn'nin noktaları), ölü bölge halkası sürüklenir.
- **Haptik/ses dalga görünümü:** gönderilen haptik sinyal canlı osiloskop gibi.
- **Pil ve bağlantı:** üst çubukta küçük, sürekli; ayrı pano yok.

**Hareket**
- Yalnız anlam taşıyan geçişler: sayfa değişimi 150 ms, değer değişimi anında. `prefers-reduced-motion` desteklenir.

**Teslim**
- Faz 2 başında 3 ana ekranın (Controller, Tetikler, Profiller) tıklanabilir taslağı yapılır, KH onaylamadan bileşen kütüphanesine geçilmez.

## 3. Fazlar

Her faz **çalışan bir ürünle** biter. Bir faz bitmeden sonrakine geçilmez.

---

### Faz 0 — Hazırlık (1-2 gün)

**Yapılacaklar**
1. `git init`, .NET 10 SDK kurulumunu doğrula, çözüm iskeleti (bölüm 2'deki projeler).
2. Lisans kararı (MIT / GPL-3.0). Karar `LICENSE` dosyasına yazılır.
3. Test donanımı listesi: DualSense (firmware sürümü not edilir), USB-C kablo, Bluetooth adaptörü modeli.
4. Sony firmware güncelleyici ile controller firmware'i güncellenir, sürüm `docs/hardware.md`'ye yazılır.
5. Kayıt aracı: ham input raporlarını `.bin` dosyasına döken küçük bir konsol komutu. Testler bu kayıtlarla çalışacak.
6. **Test oyunları listesi:** KH'nin kütüphanesindeki DualSense destekli oyunlar (adaptif tetik / haptik / hoparlör ayrı işaretli) ve Forza veya başka bir telemetri oyunu `docs/test-matrix.md`'ye yazılır. Faz 4-6 kabul ölçütleri bu listedeki oyunlarla yapılır.

**Kabul ölçütü:** `dotnet build` ve `dotnet test` temiz; boş UI penceresi açılıyor.

**Risk:** yok. **Geri alma:** klasörü silmek.

**Faz 0 sonucu (2026-11-18)**
- Kod adı `Tactile` (sonradan tek bir bul-değiştir ile değişir). Projeler: `Device.Hid`, `Device.DualSense`, `Core`, `Host`, `Tools.Capture`, `Tests`. `Audio`, `Emulation`, `Integration`, `Drivers` kendi fazlarında açılacak (boş proje tutulmadı).
- `dotnet build` uyarısız, `dotnet test` 5/5 geçti; `npm run build` temiz; `Tactile.exe` açılıyor (pencere başlığı doğrulandı), arayüz tarayıcı panelinde görüntülendi.
- `tactile-capture list/record` yazıldı; controller bağlı olmadığı için donanımda **denenmedi**.
- Firmware sürümü ve BT adaptörü `docs/hardware.md`'de boş; controller bağlanınca doldurulacak.
- Bu makinede Microsoft sunucularına IPv6 yolu takılıyor; NuGet için `DOTNET_SYSTEM_NET_DISABLEIPV6=1` kullanıldı (yalnız komut ortamı, sistem ayarı değişmedi).

---

### Faz 1 — Kontrolcüyle konuşmak (1-2 hafta)

**Yapılacaklar**
1. `Device.Hid`: cihaz listeleme, VID/PID filtresi, takma/çıkarma olayları, bağlantı türü tespiti (USB/BT).
2. Input ayrıştırma: USB `0x01` ve BT `0x31` (offset +1, CRC `0xA1` kontrolü). Çıktı: tek bir `InputState` nesnesi (tuşlar, analoglar, L2/R2 basıncı, gyro, ivme, 2 parmak touchpad, pil, şarj, kulaklık takılı mı, mic butonu).
3. BT'de tam rapor moduna geçiş: bağlanınca feature `0x05` okunur. Okumadan önce gelen kısa `0x01` raporları tespit edilip yoksayılır. (Bu davranış hid-playstation.c'nin kalibrasyon akışından çıkarım; Faz 1'de donanımda doğrulanacak.)
4. Kalibrasyon: `0x05`'ten gyro/ivme bias ve ölçek; `0x20`'den firmware/donanım sürümü; `0x09`'dan MAC (profil anahtarı olarak).
5. Output builder: 47 B ortak blok → USB `0x02` veya BT `0x31` + seq + CRC `0xA2`. Bir kez yazılır, ikisinde de kullanılır.
6. Tetik builder: Off, Feedback, Weapon, Vibration, Bow, Galloping, Machine, Multi-position Feedback/Vibration, Slope. Parametre sınırları kodda kontrol edilir.
7. LED: lightbar RGB + parlaklık, 5 oyuncu LED'i, mic LED (kapalı / açık / yanıp sönen).
8. Rumble: iki motor; `COMPATIBLE_VIBRATION` ve `HAPTICS_SELECT` bayrakları.
9. Yazma döngüsü: çıktılar değişince gönderilir, değişiklik yoksa gönderilmez. BT'de güvenli üst sınır **ölçülerek** bulunur (başlangıç varsayımı yok); sonuç `docs/hardware.md`'ye yazılır.
10. **BT haptik raporu `0x32`:** builder + 10.667 ms zamanlayıcı + test sinyali (sinüs, tık). Sanal cihaz gerektirmez; Faz 5a-5b'nin en riskli parçasını erkenden doğrular.
11. Basit test UI: canlı input görünümü, tetik/LED/rumble kaydırıcıları, "haptik test sesi çal" butonu.

**Kabul ölçütü**
- USB ve BT'de tüm tuşlar, analoglar, gyro ve touchpad canlı görünüyor.
- 7 tetik modunun hepsi iki tetikte, USB ve BT'de elle hissediliyor.
- Kablo takıp çıkarınca ve BT kopup gelince uygulama çökmeden yeniden bağlanıyor.
- CRC ve builder birim testleri geçiyor; kayıtlı raporlarla ayrıştırma testleri geçiyor.
- BT bağlıyken test sinyali haptik motorlarda hissediliyor (sağ/sol ayrı).
- BT tam rapor moduna geçiş yöntemi donanımda doğrulanmış ve `docs/hardware.md`'ye yazılmış.

**Riskler:** Windows BT yığınının büyük raporları yazamaması (DualSenseClient yazıyor, düşük risk); farklı firmware'de tetik farkları. **Geri alma:** bu fazda sürücü yok, sistem değişmiyor.

---

**Faz 1 sonucu (2026-11-18)**
- Kod: `InputParser`, `ImuCalibration`, `FirmwareInfo`, `OutputReport`, `TriggerEffect` (7 mod + çok bölgeli + eğim), `HapticReport`, `DualSenseController` (ayrı okuma/yazma handle'ı, CRC eleme, kopma), `HapticStreamer`, `ControllerService` (otomatik bağlanma), WebView2 köprüsü ve test ekranı, `tactile-capture probe`.
- Testler 27/27 geçiyor; giriş testleri gerçek BT kayıtlarıyla (`tests/Tactile.Tests/Fixtures/bt-sample.bin`).
- Donanımda elle doğrulandı (BT): ışık, oyuncu/mic LED, iki rumble motoru, silah, titreşim, artan direnç, dörtnala ve haptik sol/sağ. Haptik için oturum açma (prime) adımı gerekiyordu; `docs/protocol.md`'ye yazıldı.
- Denenmeyenler: USB bağlantısı, yay ve makine modu, BT yazma hız sınırı, uygulama penceresinden (GUI) gerçek controller ile kullanım, PS/mic tuşu.

---

### Faz 2 — Arayüz ve profiller (2-3 hafta)

**Özellikler**
- **Kontrolcü sayfası:** pil yüzdesi + şarj, bağlantı türü, firmware, MAC, sinyal gecikmesi (input seq aralığı).
- **Lightbar:** sabit renk, nefes, yanıp sönme, gökkuşağı, pil rengi (yeşil→kırmızı), sese tepkili (Faz 5a'dan sonra), oyuncu numarası; parlaklık; boştayken kapat.
- **Tetikler:** her tetik için mod + parametre editörü, canlı deneme, hazır şablonlar (tabanca, tüfek, yay, fren, gaz, kilitli tetik, titreşimli motor).
- **Tetik ölü bölgesi ve eğrisi:** L2/R2 basınç eğrisi (doğrusal, üstel, özel 5 noktalı).
- **Analoglar:** iç/dış ölü bölge, yuvarlak/kare ölü bölge, tepki eğrisi, eksen ters çevirme, anti-deadzone.
- **Gyro:** kalibrasyon (masada 3 sn durdur), hassasiyet, eksen seçimi, yumuşatma.
- **Profil sistemi:** profil = tüm ayarlar. Kontrolcü (MAC) başına varsayılan profil. İçe/dışa aktarma (JSON). Sürüm alanı ile ileriye uyumluluk.
- **Otomatik profil:** ön plandaki exe'ye göre profil değişimi (`GetForegroundWindow` + süreç adı), bekleme süresi, öncelik listesi.
- **Tray:** pil göstergesi, hızlı profil değişimi, düşük pil bildirimi (%20 / %10).
- **Uyku:** X dakika boşta kalınca controller'ı kapatma. Yöntem: `IOCTL_BTH_DISCONNECT_DEVICE` (`0x0041000C`) ile BT bağlantısını kesmek (DualSenseClient `WindowsBluetooth.cs:10-24`'te kullanılıyor).
- **Ayarlar:** Windows ile başlat, dil (TR/EN), tema, log klasörü.

**Kabul ölçütü:** iki farklı exe açıp kapatınca profil 1 saniye içinde değişiyor; uygulama yeniden açılınca ayarlar korunuyor; bozuk JSON uygulamayı çökertmiyor, varsayılana dönüp uyarıyor.

**Risk:** ayar şeması sonradan değişebilir → şemaya `version` alanı ve göç fonksiyonu. **Geri alma:** profil klasörünü silmek.

---

**Faz 2 sonucu (2026-11-18)**
- Üç alt ajanla yapıldı (çekirdek, arayüz, pencere/köprü). Ortak sözleşme `docs/contract.md`'de; çıktılar ayrıca kontrol edildi ve kod incelemesinden geçti.
- Çekirdek: profil ve ayar dosyaları (atomik yazım, bozuk dosyayı kenara alma, bilinmeyen enum değerini varsayılana çevirme), ışık efektleri, oyuncu LED'leri, analog/tetik eğrileri, gyro kalibrasyonu, öndeki oyuna göre profil seçimi, boşta uyku, pil uyarısı.
- Arayüz: Kontrolcü, Tetikler (10 bölgeli editör ve şablonlar), Işık, Analoglar, Profiller, Ayarlar; Faz 1 test araçları "Geliştirici araçları" altında.
- Pencere: tray simgesi ve menüsü, kapatınca tray'e küçültme, `--tray` ile gizli başlatma, Windows ile başlatma (HKCU Run, yalnız ayardan), tek kopya çalışma.
- Testler 95/95. Duman testinde tray modu, tek kopya ve ilk profil oluşturma çalıştı.
- Denenmeyenler: pencerenin gerçek controller ile kullanımı, BT bağlantısını kesme (boşta uyku), Windows ile başlatma kaydı, tray balonları, gyro "hareketsiz" eşiği.

---

### Faz 3 — Tuş atama ve klavye/fare (2 hafta)

Bu faz sanal kontrolcü **gerektirmez**; klavye ve fare `SendInput` ile gönderilir. **Controller tuşunu başka bir controller tuşuna çevirmek** (örn. ✕ ↔ ○) oyunun gördüğü cihazı değiştirmeyi gerektirir, bu yüzden **Faz 4'e** aittir.

**Özellikler**
- Her tuşa: klavye tuşu/kombinasyonu, fare tuşu, tekerlek, makro, profil değiştir, uygulama çalıştır, medya tuşları, ses aç/kapa, mikrofon sustur.
- **Katmanlar:** "PS tuşuna basılıyken" ikinci tuş haritası (shift katmanı).
- **Turbo** (ayarlanabilir hız), **toggle**, **uzun basma / çift basma / kısa basma** ayrı eylemler.
- **Makro editörü:** kaydet, gecikme düzenle, döngü.
- **Touchpad:** fare (hassasiyet, ivme), 2 parmak kaydırma, bölgelere ayırma (sol/sağ/4 bölge = 4 ayrı tuş), kaydırma hareketleri (swipe).
- **Gyro fare:** sadece bir tuşa basılıyken veya sürekli; yatay/dikey hassasiyet; "flick stick" ileride.
- **Analog → klavye:** WASD, ok tuşları, numpad.
- **Özel eylemler:** PS + kombinasyonla ekran görüntüsü, Xbox Game Bar, uygulamayı göster.

**Kabul ölçütü:** masaüstünde touchpad fareyle tarayıcı kullanılabiliyor; makro kaydı tekrarlanınca aynı sırada çalışıyor; tuş atamalı profil oyuna göre otomatik açılıyor.

**Risk:** `SendInput` yükseltilmiş (admin) pencerelere ulaşmaz → uygulamaya "yönetici olarak çalıştır" seçeneği. Anti-cheat'li oyunlar sentetik girdiyi engelleyebilir. **Geri alma:** profili kapatmak.

**Faz 3 sonucu (2026-11-18)**
- Üç alt ajanla yapıldı (çekirdek motoru, Windows katmanı + pencere, arayüz), aynı anda. Sözleşme `docs/contract-phase3.md`; ortak tuş listesi ve arayüzler `src/Tactile.Input`'ta, Windows kodu `src/Tactile.Input.Windows`'ta.
- Çekirdek: kısa/uzun/çift basma, turbo, aç/kapa, PS katmanı, referans sayımlı tuşlar, her geçişte "hepsini bırak" (profil değişimi, kapatma, duraklatma, kopma); touchpad fare/kaydırma/dokunarak tık/bölgeler/kaydırma hareketleri; gyro fare; analog → tuş/fare/tekerlek; makro çalıştırıcı (bir kez / basılıyken / aç-kapa); program çalıştır, profil değiştir, mikrofon sustur (LED'i izler), Win kısayolları.
- İçe aktarılan profillerdeki "program çalıştır" eylemleri silinir.
- Windows: `SendInput` (tarama kodu, medya tuşları sanal tuş), Core Audio ile mikrofon, yükseltilmiş pencere tespiti, "yönetici olarak yeniden başlat".
- Arayüz: "Tuşlar" bölümü (Tuşlar, Touchpad, Gyro fare, Analoglar, Makrolar), tuş yakalama alanı, makro kaydedici (sayfa içinde, genel klavye kancası yok), masaüstü şablonu.
- Testler 176/176. İnceleme düzeltmeleri: mikrofon LED'i susturma kalkınca profile dönüyor; yükseltilmiş oyun kontrolü profil geç açılınca da yapılıyor; makrolar çalışırken 1 ms zamanlayıcı.
- Denenmeyenler: gerçek klavye/fare çıkışı ve oyunlarda davranış, mikrofon susturma, gyro eksen yönleri, WebView2'de kombinasyon kaydı, yönetici olarak yeniden başlatma. Bilinen küçük risk: yeniden başlatmada eski kopya 5 sn'den geç kapanırsa yeni kopya da kapanır.

---

### Faz 4 — Sanal kontrolcü ve sürücüler (3-4 hafta) ⚠ kritik faz

**Yapılacaklar**
1. `Drivers`: HidHide ve usbip-win2 kurulu mu, sürümü ne, imzalı mı → durum sayfası. Kurulum, resmi imzalı installer'ları **sessiz modda** çalıştırarak yapılır (kendi sürücümüzü imzalamıyoruz).
2. **USB/IP sunucusu:** protokol 1.1.1; `OP_REQ_DEVLIST`, `OP_REQ_IMPORT`, `USBIP_CMD_SUBMIT/UNLINK`, `RET_SUBMIT`; bu fazda yalnız **kontrol ve interrupt** transferleri. İzokron (ses) transferleri Faz 5b'de eklenir. Yalnız `127.0.0.1` dinlenir.
3. **VHCI ekleme:** usbip-win2'nin cihaz arayüzünü bul, `PLUGIN_HARDWARE` ile ekle, `PLUGOUT_HARDWARE` ile çıkar. Uygulama çökerse açılışta artık cihazları temizle.
4. **Sanal Xbox 360:** XInput uyumlu descriptor'lar, input raporu, rumble geri bildirimi → gerçek kontrolcüye.
5. **Sanal DualSense (HID kısmı):** gerçek controller'ın descriptor'ları, `0x01` input, `0x02` output dinleme, `0x05/0x09/0x20` feature cevapları (gerçek cihazdan alınan değerlerle).
6. **Çeviri köprüsü:** sanal `0x02` → BT `0x31` + CRC; gerçek input → sanal `0x01`; gecikme ölçümü (sanal yazma → BT gönderme).
7. **HidHide:** gerçek controller'ı gizle, uygulamayı beyaz listeye al; uygulama kapanınca gizlemeyi geri al.
   - **Çökme güvenliği:** eklediğimiz her gizleme kaydı `%APPDATA%\<app>\hidden.json`'a önce yazılır, sonra HidHide'a uygulanır. Açılışta dosya varsa ve uygulama temiz kapanmadıysa kayıtlar geri alınır. Böylece çökme sonrası kullanıcı "controller'ım yok" durumunda kalmaz.
   - Kurtarma: uygulama açılmasa bile çalışan `app.exe --restore` komutu ve Başlat menüsünde "Kontrolcüyü geri getir" kısayolu.
8b. **Controller → controller tuş atama:** sanal cihaza giden input'ta tuş yer değiştirme, analog takası, tetik → tuş (Faz 3'ten taşındı).
8. **Steam Input uyarısı:** Steam açıksa ve oyunda Steam Input aktifse çift girdi uyarısı + "bu oyunda Steam Input'u kapat" yönergesi.
9. **Mod seçimi (profil başına):** Kapalı / Xbox 360 / DualSense.

**Kabul ölçütü**
- Sanal DualSense'te Windows "Oyun Denetleyicileri" tek DualSense gösteriyor (çift değil).
- Faz 0 test listesindeki DualSense destekli **en az 3 oyunda** kablosuz **adaptif tetik** çalışıyor.
- Xbox modunda XInput test aracında tüm tuşlar, rumble çalışıyor.
- Uygulama görev yöneticisinden zorla kapatılınca bir sonraki açılışta gerçek controller görünür ve sanal cihaz kalmamış.
- **Gecikme ölçümü:** sanal `0x02` raporunun geldiği an ile BT yazma çağrısının döndüğü an damgalanır; 10 dk oyunda p50/p95 loglanır. Hedef p95 < 8 ms.

**Riskler**
- **İzokron transfer ve zamanlama:** en zor teknik kısım. Faz 4'te HID kısmı bitirilir, ses Faz 5b'ye kalır.
- **Anti-cheat:** EAC/BattlEye için resmi bir HidHide/usbip açıklaması bulunamadı; sanal girdi bazı oyunlarda engellenebilir. Yönetim: profilde "bu oyunda emülasyonu kapat" seçeneği; ban riski olan online oyunlarda varsayılan kapalı; test listesi tutulur.
- **usbip-win2 kurulumu USB 3.0 hub'larını yeniden başlatır**; kurulum ekranında yazılı uyarı ve geri yükleme noktası önerisi.
- **Sürücü sürüm uyumu:** desteklenen sürüm aralığı sabitlenir; uyumsuzsa emülasyon kapalı kalır, uygulama çalışmaya devam eder.

**Geri alma:** sürücüleri kendi kaldırıcılarıyla kaldırmak; uygulama içinde "emülasyonu tamamen kapat" anahtarı.

**Faz 4 sonucu (2026-11-19, kod tamam, donanımda denenmedi)**
- Üç alt ajanla yapıldı (sanal cihaz, sürücüler, çekirdek + arayüz), aynı anda. Sözleşme `docs/contract-phase4.md`; protokol bilgisi `docs/protocol-emulation.md` (kendi cümlelerimizle, kaynaklı).
- `src/Tactile.Emulation`: 127.0.0.1'de USB/IP sunucusu (kontrol + interrupt), sanal Xbox 360 ve sanal DualSense (yalnız HID, ses yok). Her çalıştırmada yeni port.
- `src/Tactile.Drivers`: usbip-win2 (yalnız 0.9.8.1; yapı düzeni sürüme göre değişiyor) ve HidHide durumu, VHCI takma/çıkarma, HidHide gizleme. Her değişiklik önce `recovery.json`'a yazılıyor; çökme sonrası açılışta, `Tactile.exe --restore` ile ve Başlat menüsündeki "Kontrolcüyü geri getir" kısayoluyla geri alınıyor. Yalnız bizim eklediklerimiz geri alınıyor.
- Dağıtım: sürücüler pakete gömülmüyor. Uygulama resmi GitHub sürümünü indiriyor, boyut + SHA-256 + Authenticode imzacısını kontrol ediyor (usbip-win2: Cloudyne Systems, HidHide: Nefarius Software Solutions e.U.; 19 Kasım'da doğrulandı), kullanıcı UAC'ye onay veriyor. İkisi de yeniden başlatma istiyor. Lisanslar (BSD-2, MIT) faz 7'de `licenses.txt`'e girecek.
- Çekirdek: profil başına mod (Kapalı / Xbox 360 / DualSense; USB'de DualSense modu yok), controller → controller tuş yönlendirme, analog takası, klavyeye atanan tuşları sanal pad'de gizleme; Xbox rumble ve oyunun DualSense çıktısı (tetik, ışık, rumble) anında gerçek controller'a; gecikme p50/p95; Steam uyarısı.
- Arayüz: "Sanal kontrolcü" sayfası (sürücü kartları, kur düğmesi, USB hub ve yeniden başlatma uyarısı, durum, geri getir).
- Testler 314/314. İnceleme düzeltmeleri: HidHide meşgulken pad oyun ortasında sökülmüyor; meşgulken gizleme geri alınamazsa hata bildiriliyor ve tekrar deneniyor; çıkarma başarısız olsa da sürücünün yeniden bağlanma denemeleri durduruluyor; temiz kapanışta geri alınamayan kayıtlar silinmiyor.
- **Donanım testi (2026-11-19, KH):** sürücüler kuruldu (yeniden başlatma sonrası ikisi de "Hazır"). Xbox 360 kipi: Windows "Xbox 360 Denetleyicisi" görüyor, XInput slot 0, joy.cpl'de **tek** controller, tüm tuş ve eksenler doğru, XInput rumble (güçlü/hafif/durdur) gerçek controller'da. Zorla kapatma: sanal pad kendiliğinden düştü, gizleme kaldı; `--restore` gizlemeyi ve kaydı temizledi, joy.cpl'de DualSense geri geldi (restore ana anahtarı bilerek kapatıyor). İkinci zorla kapatmada açılış kurtarması eski kaydı silip pad'i yeniden taktı. DualSense kipi: Windows USB `VID_054C&PID_0CE6` olarak tanıyor, joy.cpl'de tek DualSense; oyun yerine sanal pad'e USB `0x02` yazan bir test programıyla ışık kırmızı, R2 silah direnci, L2 direnç gerçek controller'da hissedildi. **Gecikme p50 0,2 ms / p95 6,4 ms** (hedef < 8 ms).
- Kalan: gerçek oyunlarda tetik testi (Ghost of Tsushima + 2 oyun daha), 10 dk oyun içi gecikme ölçümü.
- Denenmeyenler (ilk yazım, sürücüler kurulmadan önce): gerçek takma/gizleme/geri alma, Windows'un descriptor istekleri (ilk çalıştırmada kurulum günlüğü okunacak), oyunlarda tetik, XInput testi, zorla kapatma testi, 10 dk gecikme ölçümü, `--restore` ikinci kopya ile, Bluetooth feature raporlarının USB biçimi. Kabul ölçütü için 2 DualSense destekli oyun daha gerekli.

---

### Faz 5a — Yerel ses → haptik (1-2 hafta, sanal cihaz gerektirmez)

Faz 1'deki `0x32` builder'ının üstüne kurulur; Faz 4'ü beklemez, Faz 2-3 ile paralel yapılabilir.

- **Sistem sesi → haptik:** WASAPI loopback ile sistem sesini veya **tek bir uygulamanın sesini** (process loopback) yakala; mod: Oyun / Film / Müzik (filtre ve eşik farkları).
- **Rumble → haptik:** eski oyunların rumble değerini yumuşak haptik dalgaya çevir.
- **Tuş/tetik → haptik:** tuşa basınca kısa tık hissi.
- **Controller hoparlörüne ses gönder:** seçili uygulamanın sesi → Opus → `0x35`.

**Kabul ölçütü:** müzik çalınca haptik ritimle titreşiyor; seçili uygulamanın sesi controller hoparlöründen duyuluyor; 30 dk kesintisiz.

**Faz 5a sonucu (2026-11-19, kod tamam, donanımda denenmedi)**
- Sözleşme `docs/contract-phase5a.md`; üç iş sırayla yazıldı, her biri kontrol edildikten sonra bir sonrakine geçildi. Hoparlör raporu `0x36` bilgisi `docs/protocol.md`'ye kendi cümlelerimizle eklendi (kaynaklar MIT ve AGPL; yalnız bilgi okundu, kod alınmadı).
- `src/Tactile.Audio` (yeni): kendi COM interop'umuzla WASAPI sistem sesi ve tek uygulama sesi yakalama (uygulama sesi Windows 20348 ve sonrası), çalan uygulama listesi, ses → haptik DSP (Oyun / Film / Müzik: biquad filtre, gürültü kapısı, sıkıştırma, Müzik'te vuruş vurgusu; gecikme sınırı 4 blok ≈ 43 ms), Opus kodlayıcı ve hoparlör beslemesi (512 → 480 yeniden örnekleme, mono, 200 B CBR).
- libopus 1.6.1: resmi Xiph kaynağından SHA-256 doğrulanarak derlendi (`third_party/opus/build-opus.sh`, statik CRT, yalnız KERNEL32 bağımlılığı). Lisans metni `third_party/opus/COPYING`, çıktıya `licenses/opus.txt` olarak kopyalanıyor; faz 7'de `licenses.txt`'e girecek.
- Çekirdek: `HapticEngine` tek akışta sesi, rumble'ı (sol ≈ 50 Hz, sağ ≈ 175 Hz yumuşak rampalı dalga) ve tık hissini karıştırıyor. Akış açıkken controller motor rumble'ını yok saydığı için rumble her zaman dalgaya çevriliyor. Hoparlör açıkken her tik `0x36`, değilse `0x32`. Aynı kaynak hem haptiği hem hoparlörü tek yakalamayla besliyor. Test tonu akışı geçici olarak devralıyor.
- Profil: `haptics` (ses kaynağı, uygulama, mod, şiddet, rumble → haptik, tık, hoparlör kaynağı ve sesi). Eski profiller varsayılanlarla açılıyor.
- Arayüz: "Haptik" sayfası (sesten haptik, rumble, tık hissi, kontrolcü hoparlörü, çalan uygulamalar listesi, USB uyarısı). Tarayıcı önizlemesinde kontrol edildi.
- Testler 369/369. İnceleme düzeltmeleri: ses yakalamanın başlatılması ana döngüyü bloklamıyor; uygulama sesi için 20 ms tampon süresi; akış yazma hatasıyla durursa en fazla saniyede bir yeniden kuruluyor; şiddet DSP'nin yumuşak kırpmasından önce uygulanıyor; uygulama listesi arayüz iş parçacığı dışında alınıyor.
- Donanım testi (2026-11-19, KH, Bluetooth): sistem sesi → haptik Müzik modunda ritim tutuyor, gecikme hissedilmiyor, şiddet kaydırıcısı çalışıyor. `0x36` hoparlör **durum bloksuz** düzenle çalışıyor (haptik aynı anda sürüyor); ses anlaşılır ama küçük mono hoparlör yüzünden kulaklık kadar net değil, bu yüzden Opus karmaşıklığı 10'a çıkarıldı ve müzik ipucu verildi (`bdc0576`). Tık hissi yönüyle birlikte, rumble → haptik dalga çalışıyor. Bulunan arayüz hatası: sayfanın altındaki anahtara basınca tüm pencere kayıyordu (gizli input konumlanmamış kapsayıcıya göre yerleşiyordu), düzeltildi (`6b2f660`).
- Tek uygulama yakalama: ilk denemede Spotify'da sessizdi; kod en küçük pid'li yardımcı işlemi seçiyordu. Artık aynı adlı işlem ağacının kökü seçiliyor (`6d2a730`), Spotify'da çalışıyor ve başka uygulamanın sesi titretmiyor.
- Bluetooth kopması (controller kapatıldı): haptik akışı sürerken uygulama çöktü (`OperationCanceledException` yazma iş parçacığında yakalanmıyordu). Düzeltildi (`022805b`); yeniden denemede uygulama açık kaldı, controller kendiliğinden bağlandı, titreşim kaldığı yerden sürdü.
- Kalan: 30 dk kesintisiz koşu, Oyun ve Film modlarının karşılaştırması.

### Faz 5b — Sanal DualSense ses arayüzü (3-5 hafta) ⚠ en riskli faz

**Yapılacaklar**
1. Sanal DualSense'e **USB Audio Class 1** fonksiyonu: 4 kanal 48 kHz hoparlör çıkışı, 2 kanal 48 kHz mikrofon girişi (izokron uç noktalar).
2. Windows ses cihazı olarak "DualSense Wireless Controller" görünür; oyun haptiği kanal 3-4'e yazar.
3. **Haptik hattı:** her 10.667 ms'de 512 kare al → kanal 3-4'ü ayır → 16'lık pencerelerde ortalama → 32 kare s8 stereo → `0x32` raporu (`0x91` + `0x92` blokları) → CRC → BT.
4. **Hoparlör hattı:** kanal 1-2 → 512→480 yeniden örnekleme → Opus 48 kHz stereo CBR 200 B → `0x35` / `0x36` / `0x39`.
5. Zamanlayıcı: yüksek çözünürlüklü zamanlayıcı (`timeBeginPeriod(1)` veya multimedia timer); BT kuyruğunun dolmasını izle; buffer uzunluğu 16-128 ayarı.
6. Mikrofon: controller mic → sanal cihazın mic girişi (BT yönü ayrıca araştırılır; olmazsa bu özellik ertelenir).
7. **Haptik ayarları:** güç (0-200%), düşük geçiren filtre, sadece haptik / sadece hoparlör / ikisi.

**Kabul ölçütü**
- Haptik destekli **en az 2 oyunda** (örn. Returnal, Ratchet & Clank) kablosuz haptik hissediliyor, kablolu ile yan yana karşılaştırma notu yazılıyor.
- 30 dakika oyunda kesinti veya kuyruk taşması yok.
- Gecikme ölçülüyor ve belgeleniyor.

**Riskler**
- BT haptik paket yapısı **resmi değil**, tersine mühendislik; firmware güncellemesi bozabilir. Yönetim: firmware sürümünü kaydet; desteklenmeyen sürümde haptik kapalı kalsın, uygulama çalışsın.
- BT bant genişliği; bazı adaptörlerde ses kesik gelebilir. Yönetim: adaptör uyumluluk listesi, `0x39` çift kare modu.
- Gecikme tahmini 35-60 ms (ölçülmemiş kaynak); kabul edilemezse buffer küçültülür, olmazsa Pico yolu (Faz 9).

**Geri alma:** ses fonksiyonunu kapatan anahtar; sanal cihaz HID-only moda döner.

**Faz 5b sonucu (2026-11-19, kod tamam, oyunsuz donanım testi geçti)**
- Sözleşme `docs/contract-phase5b.md` (4 iş, sırayla, her biri kontrol edilerek), protokol bilgisi `docs/protocol-emulation.md` §10 ve `docs/protocol.md` (`0x39`), kendi cümlelerimizle.
- `Tactile.Emulation`: USB/IP izokron aktarım (uç nokta başına ms'de bir paket, catch-up, UNLINK), sanal DualSense'in ses sürümü (full speed, gerçek pad'in 227 baytlık yapılandırması, mikrofon paketi 192, HID arayüz 3, UAC1 ses/sessiz denetimleri, sessiz mikrofon). Ayar kapalıysa Faz 4'ün HID-only pad'i.
- Çekirdek: oyun sesi kanal 3-4 → Bluetooth haptik (16'lık ortalama, /256 × güç), kanal 1-2 → hoparlör (5a Opus hattı). Oyun haptiği gelirken sesten haptik geri planda. Windows'un cihaz ses düzeyi/sessiz ayarı uygulanıyor. Akış yalnız son 3 sn'de gerçek ses geldiyse açık. Ayarlar: ses arayüzü, haptik tamponu, varsayılan ses cihazlarını koru; profil: oyun haptiği kipi, şiddet, yumuşatma; hoparlör paketi (tek kare `0x36` varsayılan, çift kare `0x39` deneysel).
- Donanım testi (KH, Bluetooth, test programıyla 4 kanallı ses): Windows "Hoparlör (DualSense Wireless Controller)" ve mikrofonu gösteriyor; 4 kanal 48 kHz açılıyor. Kanal 3-4 → titreşim iki tarafta, ses yok; kanal 1-2 → hoparlörden melodi, titreşim yok. Bulunanlar: Windows sanal pad'i varsayılan hoparlör ve mikrofon yapıyordu → takmadan önceki varsayılanlar 15 sn içinde geri alınıyor (doğrulandı, Realtek kaldı). Hoparlör cızırtısı → 2 bloklu titreşim tamponu (SpeakerFeed, AudioHaptics, GameAudio), çok azaldı; 12 sn'de yaklaşık 1 kopma kalıyor (Bluetooth takılması). `0x39` çift kare paketi bu controller'da hiç ses vermedi (neden açık: Windows'un BT rapor boyu ya da firmware), deneysel bırakıldı.
- Testler 421/421. Code-review high: 4 bulgu, 3'ü düzeltildi (Windows ses düzeyi, izokron kuyruk sınırı, akışın gereksiz açık kalması), 1'i (sessiz mikrofonun varsayılan olması) varsayılanları koruma özelliğiyle çözüldü.
- Kalan: haptik destekli gerçek oyunda deneme ve kablolu ile karşılaştırma notu, 30 dk koşu, oyun içi gecikme ölçümü, kalan seyrek hoparlör kopması (`0x39` neden çalışmıyor; BT kuyruk yönetimi), Bluetooth'tan mikrofon (ertelendi).

---

### Faz 6 — Oyun entegrasyonu (2-3 hafta)

**İlk sürüm (zorunlu):** UDP API + Forza telemetrisi + kural motoru. Diğer maddeler bu üçü çalıştıktan sonra, ihtiyaç oldukça eklenir.

**Özellikler**
- **UDP API (localhost):** JSON komutları: tetik, LED, oyuncu LED, rumble, mic LED, profil değişimi, haptik efekti; cevap olarak pil ve bağlantı durumu. Port ayarlanabilir, yalnız `127.0.0.1` dinlenir.
- **DSX uyumlu mod:** mevcut DSX modlarının (ForzaDualSense, DualSense4GTA5 vb.) komut biçimini kabul eden uyumluluk katmanı. Biçim, DSX'in açık GitHub örneklerinden okunur.
- **Telemetri eklentileri:** oyunun dışarı verdiği veriden tetik efekti:
  - Forza Horizon/Motorsport "Data Out" UDP → gaz/fren tetik direnci, ABS titreşimi, devir LED.
  - Assetto Corsa / ACC shared memory.
  - F1 serisi UDP.
  - (sonra) BeamNG, Dirt/WRC.
- **Kural motoru:** "telemetri değeri X → efekt Y" kurallarını UI'dan yazmak (eşik, eğri, hangi tetik, hangi LED).
- **Eklenti arayüzü:** yeni oyun = yeni `.dll` veya Lua/JS betiği (karar Faz 6 başında verilir).
- **Topluluk profilleri:** profil dosyasını paylaşma (dışa aktar → içe aktar); sunucu yok.

**Kabul ölçütü:** Forza Horizon 5'te frenle tetik sertleşiyor; mevcut bir DSX modu değişiklik yapmadan bizim uygulamayla çalışıyor; UDP'ye bozuk paket gönderince uygulama çökmüyor.

**Risk:** dış ağdan UDP komutu → yalnız loopback dinleme. **Geri alma:** entegrasyon kapalı anahtarı.

---

### Faz 7 — Dağıtım (1-2 hafta)

**Yapılacaklar**
- Tek installer (Inno Setup veya WiX): uygulama + isteğe bağlı sürücü kurulum adımı + kaldırıcı.
- İlk açılış sihirbazı: controller bağla → sürücü gerekirse kur → yeniden başlatma uyarısı → test.
- Kod imzalama: hangi sertifikayla imzalanacağı açık soru (bölüm 5, madde 6).
- Otomatik güncelleme: **imzalı manifest + SHA-256 + tam host allowlist** (NexoGameST updater denetiminden çıkan dersler).
- Çökme logları: yerel log klasörü, "logları dışa aktar" butonu; telemetri yok.
- Kurulumsuz (portable) sürüm: emülasyon olmadan çalışan hafif mod.

**Kabul ölçütü:** temiz bir Windows 11 makinede (VM değil, gerçek cihaz) installer → controller bağla → oyun → kaldır akışı sorunsuz; kaldırma sonrası HidHide gizleme listesi temiz.

---

### Faz 8 — DualSense Edge ve çoklu kontrolcü (1-2 hafta)

- Edge: arka kollar, Fn tuşu, controller içi profiller, tetik stop seviyeleri, analog modül.
- Aynı anda 2-4 controller: her biri ayrı profil, ayrı sanal cihaz, ayrı oyuncu LED'i.

### Faz 9 — Yedek yol: Pico 2 W köprü (isteğe bağlı)

Faz 5b gecikmesi kabul edilemezse veya anti-cheat yazılım yolunu engellerse: Pico 2 W firmware'i ile gerçek USB DualSense görünümü; uygulama aynı UI ve profil motoruyla Pico'yu yönetir. Referans: DS5Dongle (MIT).

## 4. Test stratejisi

- **Birim:** CRC, rapor builder'ları, tetik parametre paketleme, haptik decimation, Opus çerçeve boyutu, JSON şema göçü.
- **Tekrar testleri:** Faz 0'daki kayıtlı `.bin` input raporlarıyla ayrıştırma.
- **Donanım test listesi** (`docs/test-matrix.md`): oyun × mod (kapalı / X360 / DualSense) × bağlantı (USB / BT) × sonuç (tetik / haptik / hoparlör / sorun).
- **Uzun koşu:** her faz sonunda 30 dk oyun, bellek ve CPU kaydı.

## 5. Açık sorular (fazı gelince cevaplanacak)

1. ~~Lisans~~ → **kapalı kaynak** kararı verildi (2026-11-18), bölüm 1.
2. ~~UI~~ → **WebView2 + React** kararı verildi (2026-11-18), bölüm 2 ve 2b.
3. BT'de controller mikrofonunun sanal cihaza aktarılması mümkün mü? (Faz 5b)
4. Eklenti dili: C# DLL mi, betik mi? (Faz 6)
5. Uygulama adı. (Faz 7'den önce)
6. İmzalama: Nexo Corp sertifikası mı, ayrı bir kimlik mi? (Faz 7)

## 3b. Faz sırası ve paralellik

```
Faz 0 → Faz 1 ─┬→ Faz 2 → Faz 3 ─┐
               └→ Faz 5a ─────────┼→ Faz 4 → Faz 5b → Faz 6 → Faz 7 → Faz 8
                                  │                         (Faz 9 yalnız gerekirse)
```

- Faz 5a yalnız Faz 1'e bağlı; Faz 2-3 ile aynı dönemde yapılabilir.
- Faz 6'nın UDP API kısmı Faz 2'den sonra başlayabilir; DSX modlarının oyun içi testi Faz 4'ü bekler (Xbox modu gerekli).
- Tahmini toplam: tek kişiyle **4-6 ay** (Faz 9 hariç).

## 7. Plan denetimi (plan-audit, 2026-11-18)

İlk karar: **REVİZYON GEREKLİ**. Aşağıdaki düzeltmeler plana işlendi.

| # | Önem | Adım | Sorun | Yapılan düzeltme |
|---|---|---|---|---|
| 1 | Yüksek | Faz 3 | Controller tuşunu controller tuşuna çevirmek sanal cihaz ister; Faz 3'te yapılamaz | Faz 4 madde 8b'ye taşındı |
| 2 | Yüksek | Faz 4 | Çökme sonrası HidHide gizlemesi kalırsa kullanıcının controller'ı yok olur; DualSenseClient `HidHideService.cs`'te açılış temizliği yok | `hidden.json` önce-yaz kaydı, açılış temizliği, `--restore` komutu |
| 3 | Yüksek | Faz 5 | En riskli parça (BT haptik paketi) en sona kalmıştı | `0x32` testi Faz 1'e, yerel ses → haptik Faz 5a'ya alındı |
| 4 | Orta | Faz 4/5 | İzokron transfer hem Faz 4'te hem "ses Faz 5'e" diye çelişkiliydi | Faz 4: yalnız kontrol + interrupt; izokron Faz 5b |
| 5 | Orta | Faz 1 | "~250 rapor/sn" kanıtsız bir sayıydı | Sayı kaldırıldı, ölçülüp belgelenecek |
| 6 | Orta | Faz 1 | BT tam rapor modu geçişi çıkarımdı | Kabul ölçütüne donanım doğrulaması eklendi |
| 7 | Orta | Faz 4-5 | Kabul ölçütündeki oyunların KH'de olup olmadığı belli değildi | Faz 0'a test oyunları listesi eklendi |
| 8 | Orta | Faz 4 | "< 8 ms" hedefinin ölçüm yöntemi yoktu | Damga noktaları ve p50/p95 yöntemi yazıldı |
| 9 | Orta | Faz 6 | Fazla geniş kapsam (DSX uyumu, eklenti sistemi, 5 oyun) | Zorunlu ilk sürüm: UDP + Forza + kural motoru |
| 10 | Düşük | Faz 7 | Nexo Corp imzası bu kişisel projeye otomatik bağlanmıştı | Açık soruya çevrildi |

Bu düzeltmelerden sonra karar: **ONAYLANABİLİR**. Koşul: Faz 0'daki açık sorular 1-2 cevaplanmadan Faz 1'e geçilmez.

## 6. Kaynaklar

- Linux hid-playstation.c — https://github.com/torvalds/linux/blob/master/drivers/hid/hid-playstation.c
- DualSenseClient — https://github.com/DualSenseClient/DualSenseClient (commit bcdae12, 16 Kasım 2026)
- DS5_Bridge — https://github.com/SundayMoments/DS5_Bridge
- DS5Dongle — https://github.com/awalol/DS5Dongle
- SAxense — https://github.com/egormanga/SAxense
- usbip-win2 — https://github.com/vadimgrn/usbip-win2
- DSX (Paliverse) — https://github.com/Paliverse/DualSenseX
- DS4Windows Steam/HidHide belgeleri — https://schmaldeo.github.io/DS4WindowsDocs/

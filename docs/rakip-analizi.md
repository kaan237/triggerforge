# Rakip analizi — 19 Kasım 2026

Kaynaklar: web araştırması. Her iddianın
kaynağı aşağıdaki bağlantılarda; kapalı kaynak uygulamaların belgelenmemiş ayarları ve
donanımdaki davranışları doğrulanmadı. "Bizde var mı" sütunu kodla kontrol edildi.

## Piyasa özeti

| Uygulama | Fiyat | Bizden öne çıkan yanı |
|---|---|---|
| DSX (Paliverse, Steam) | ~8 $ + DSX+ 4 $ | DS4 desteği, Edge dahili profillerini okuma/yazma, Steam Workshop profil paylaşımı, Steam Cloud yedek, RSSI, sürücü onarımı, OBS giriş görseli, üçlü basış, 3.2 beta'da BT haptik/ses (bizdeki özellik, onlarda ücretli) |
| reWASD | 15 $'dan başlıyor, abonelik/ömür boyu | 10'a kadar shift katmanı, chord, combo editörü, radial menu, OSD, sanal Xbox One/DS4/DS3/Switch Pro, cihaz birleştirme, mobil uygulama, topluluk config'leri, CLI |
| DS4Windows (Pav Osmolski çatalı 5.0) | Ücretsiz | Sanal DS4/Edge/Xbox One, flick stick, dış halka, **BT mikrofon**, DSU/Cemuhook, OSC, DSX modları |
| Steam Input | Ücretsiz | Action set + layer, mode shift, activator'lar (soft/full pull, başlangıç/bırakma), flick stick, gyro ratchet, radial/touch menu, topluluk düzenleri |
| JoyShockMapper | Ücretsiz | Flick stick'in referansı, gerçek dünya gyro kalibrasyonu, gyro space (player turn/lean), otomatik gyro kalibrasyonu, chord/simul, ring bindings, 25'li touchpad ızgarası |
| PadForge | Ücretsiz | Katman, radial menu, BT DualSense ses/haptik, **F1/AC/ACC/WRC/BeamNG telemetri**, Steam topluluk düzenini içe aktarma, telefondan gamepad |
| DSZ (DualSense Zen) | Ücretsiz | **Oyuna göre otomatik sanal çıkış seçimi**, 16 yönde stick kalibrasyonu, kablo takınca BT oturumunu koruyarak şarj |
| DualSenseY v2 | Ücretsiz | DS4 desteği, sanal DS4, kontrolcüyü ağdan başka PC'ye aktarma |
| Sony PlayStation Accessories | Ücretsiz | Firmware güncelleme, Edge dahili profilleri, 6 stick eğrisi, tetik aralığı |
| Controller Companion / JoyToKey / Gopher360 / Xpadder | 0-10 $ | Ekran klavyesi, tam ekranda otomatik duraklama, tuşta A→B→C döngüsü, kombinasyonla duraklat |
| DualShock Calibration GUI | Ücretsiz | Stick merkez/aralık kalıcı kalibrasyonu, Edge modül kalibrasyonu |

Önemli gözlem: kablosuz oyun haptiği artık yalnız bizde değil. DSX (ücretli), PadForge, DSZ ve
DS4Windows çatalları da yapıyor. Farkımız ücretsiz + tek uygulamada her şey + Forza kural
motoru + iki dil. Rakiplerin en çok öne geçtiği alan **tuş atama derinliği** (katman, chord,
flick stick) ve **paylaşım** (topluluk profilleri).

## Bizde olmayanlar (değer / efor)

Efor: S = mevcut bileşende küçük iş, M = yeni alt akış, L = yeni protokol/sürücü ya da geniş doğrulama.

### Tuş atama ve gyro
| Özellik | Kimde | Değer | Efor | Bizdeki durum |
|---|---|---|---|---|
| Kombinasyon kısayoluyla atamayı duraklat/sürdür | Gopher360, reWASD, DSZ | Yüksek | S | Duraklatma var (`MappingPaused`) ama kontrolcü/klavye kısayolu yok |
| Gyro ratchet: tuş basılıyken gyro'yu durdur; touchpad'e dokununca aç | Steam Input, JSM, reWASD | Yüksek | S | "Basılıyken aç" ve "aç/kapa" var; ters yönü ve dokunmayla açma yok |
| Chord / aynı anda basma (L1+X gibi) | Steam Input, JSM, reWASD, DSZ | Yüksek | M | Yok |
| Birden çok shift katmanı (hold/toggle) | reWASD (10), Steam Input, AntiMicroX | Yüksek | M | Tek katman, tuşu seçilebiliyor |
| Flick stick + gerçek dünya kalibrasyonu | JSM, Steam Input, reWASD, DS4Windows | Yüksek | M | Yok |
| Gyro → sanal sağ stick (fare kabul etmeyen oyunlar için) | DS4Windows, reWASD, Steam Input | Yüksek | S-M | Gyro yalnız fareye gidiyor |
| Soft/full pull, stick dış/iç halka aksiyonları | Steam Input, JSM, DS4Windows | Yüksek | M | Yok |
| Tuşta döngü (A→B→C), üçlü basış | JoyToKey, AntiMicroX, DSX | Orta | S | Kısa/uzun/çift basma var |
| Otomatik gyro kalibrasyonu, gyro space (player turn/lean), tilt → stick | JSM, PadForge | Orta | M | Yalnız elle kalibrasyon |
| Touchpad ızgarası (9-25 bölge) | JSM | Düşük | S | 2 ya da 4 bölge |
| Haptik onay darbesi (katman değişince, makro başlayınca) | reWASD | Orta | S | Tık hissi var, olaylara bağlı değil |

### Sanal çıkış ve cihaz desteği
| Özellik | Kimde | Değer | Efor | Bizdeki durum |
|---|---|---|---|---|
| Oyuna göre otomatik çıkış seçimi (Xbox / DualSense) | DSZ | Yüksek | M | Profil başına elle seçiliyor |
| Sanal DS4 çıkışı | DSX, reWASD, DS4Windows, DualSenseY | Yüksek | M | Yok (Xbox 360 + DualSense) |
| Fiziksel DS4 desteği | DSX, DS4Windows, DualSenseY, JSM | Yüksek | L | Kapsam dışı bırakılmıştı |
| Bluetooth mikrofon | DS4Windows (Pav), DSX (planlı) | Yüksek | L | Ertelendi |
| Edge dahili profilleri oku/yedekle/yaz | Sony, DSX | Yüksek | L | Yok; protokol bilgisi açık değil (DSX yaptığına göre mümkün) |
| Sanal Xbox One (tetik motorları), Switch Pro, DS3 | reWASD, DS4Windows | Orta | L | Yok |
| DSU/Cemuhook hareket sunucusu (emülatörler: Cemu, Yuzu türevleri, Dolphin) | DS4Windows | Orta | M | Yok |
| Firmware sürümü + Bluetooth RSSI, sürücü onarımı | DSX, Sony | Orta | S | Firmware ve MAC var; RSSI ve onarım yok |
| Yön bazlı stick kalibrasyonu, stick testi | DSZ, Calibration GUI, Sony | Orta | M | Ölü bölge/eğri var, kalibrasyon yok |
| Kablo takınca BT oturumunu koruyarak şarj | DSZ | Orta | M | Yok |

### Oyun içi arayüz
| Özellik | Kimde | Değer | Efor | Bizdeki durum |
|---|---|---|---|---|
| OSD: profil/katman değişimi, pil uyarısı (anti-cheat'e dokunmayan üst pencere) | reWASD, Steam Input, Handheld Companion | Yüksek | M | Yalnız tepsi balonu |
| Radial menu (Edge Fn veya touchpad ile) | Steam Input, reWASD, PadForge | Orta | M-L | Yok |
| Ekran klavyesi (koltuktan PC kullanımı) | Controller Companion, Steam | Orta | M | Yok |
| Tam ekran oyunda masaüstü atamasını otomatik kapatma | Controller Companion | Orta | S | Oyuna göre profil var; ayrıca tam ekran kontrolü yok |
| OBS için giriş görselleştirmesi | DSX, Handheld Companion | Düşük | M | Yok |

### Paylaşım ve entegrasyon
| Özellik | Kimde | Değer | Efor | Bizdeki durum |
|---|---|---|---|---|
| Topluluk profil kataloğu (GitHub reposundan, sunucusuz) | DSX Workshop, reWASD, Steam Input | Yüksek | M | Yalnız JSON içe/dışa aktarma |
| F1 UDP, ACC/AC shared memory, BeamNG, WRC telemetrisi | PadForge, ACC DualSense Feedback | Yüksek | M | Yalnız Forza; kural motoru hazır |
| Otomatik profil yedeği | DSX (Steam Cloud) | Orta | S | Yok |
| Steam topluluk düzenini içe aktarma | PadForge | Orta | L | Yok |
| OSC çıkışı (VRChat, yayın araçları) | DS4Windows | Düşük | M | Yok |
| Eklenti/betik sistemi, koşullu aksiyonlar | Joystick Gremlin, PadForge | Orta | L | Planda var, yapılmadı |
| Komut satırı (profil değiştir, duraklat) | reWASD, AntiMicroX | Düşük | S | Yalnız `--restore`, `--tray` |
| Telefondan kontrol/izleme, ağdan PC'ye kontrolcü aktarımı | reWASD Mobile, DualSenseY, PadForge | Düşük | L | Yok |
| Birden çok cihazı tek sanal çıkışta birleştirme | reWASD, Joystick Gremlin | Düşük | L | Yok |

## Önerilen sıra

**Hızlı kazançlar (S, birkaç gün):**
1. Kombinasyonla atamayı duraklat/sürdür + tam ekranda otomatik kapatma
2. Gyro ratchet ve touchpad dokunuşuyla gyro
3. Gyro → sanal sağ stick
4. Tuşta döngü ve üçlü basış
5. Haptik onay darbesi, RSSI gösterimi, otomatik profil yedeği

**Asıl fark yaratacaklar (M):**
6. Chord + çoklu shift katmanı (aynı işte; tuş motoru tek yerde değişir)
7. Flick stick + soft/full pull + dış halka
8. Oyuna göre otomatik çıkış seçimi
9. F1 ve ACC telemetrisi (Forza kural motoruna yeni kaynak)
10. Topluluk profil kataloğu (`kaan237/triggerforge-profiles` gibi bir repo)
11. OSD
12. Sanal DS4

**Büyük yatırımlar (L):** BT mikrofon, Edge dahili profilleri, fiziksel DS4, radial menu, DSU.

## Kaynaklar

- DSX: https://store.steampowered.com/app/1812620/DSX · https://steamcommunity.com/app/1812620/announcements/ · https://steamcommunity.com/app/1812620/workshop/ · https://www.tweaktown.com/news/112219/dsx-v3-2-beta-adds-native-playstation-dualsense-haptic-feedback-over-bluetooth-on-pc/index.html
- reWASD: https://www.rewasd.com/releases · https://www.help.rewasd.com/basic-functions/shift-mode.html · https://help.rewasd.com/basic-functions/virtual-controller.html · https://www.help.rewasd.com/mapping-features/radial-menu.html · https://help.rewasd.com/preferences/desktop-overlay.html
- DS4Windows: https://pav-osmolski.github.io/DS4Windows/CHANGELOG/ · https://pav-osmolski.github.io/DS4Windows/docs/controller-audio/ · https://github.com/hbashton/DS4Windows
- Steam Input: https://partner.steamgames.com/doc/features/steam_controller/action_set_layers · https://partner.steamgames.com/doc/features/steam_controller/mode_shifting · https://partner.steamgames.com/doc/features/steam_controller/activators
- JoyShockMapper: https://github.com/JibbSmart/JoyShockMapper/blob/master/README.md
- PadForge: https://padforge.org/ · https://padforge.org/docs/features/game-telemetry/
- DSZ: https://github.com/MaximumFreak/dsz
- DualSenseY: https://github.com/WujekFoliarz/DualSenseY-v2
- Sony: https://www.playstation.com/en-us/support/hardware/set-up-edge-pc/ · https://controller.dl.playstation.net/controller/lang/en/2100004.html
- Controller Companion: https://store.steampowered.com/app/367670/Controller_Companion/ · JoyToKey: https://joytokey.net/en/advanced · Gopher360: https://github.com/Tylemagne/Gopher360
- Calibration GUI: https://dualshock-tools.github.io/ · ACC: https://github.com/Adudumax/ACC-DualSense-Feedback

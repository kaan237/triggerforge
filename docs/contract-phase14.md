# Faz 14 sözleşmesi: topluluk profil kataloğu, ekran üstü bildirim (OSD) ve ekran klavyesi

`docs/contract.md`, `docs/contract-phase3.md`, `docs/contract-phase7b.md` ve Faz 8–13 sözleşmelerine eklenir. Yol haritası dayanağı: `docs/yol-haritasi.md:69-75`. Sürüm **1.5.0** olur (`Directory.Build.props:5`).

Amaç: mevcut hiçbir profili, ayarı ve testi bozmadan üç paylaşım ve ekran özelliğini en küçük uygulanabilir kapsamla teslim etmek:
1. **Topluluk profil kataloğu** (`kaan237/triggerforge-profiles` public GitHub reposu, güncelleme anahtarından ayrı ECDSA P-256 anahtarıyla imzalı `index.json` + `index.json.sig`, yayın aracında `catalog keygen/pubkey/backup/restore/sign/verify` komutları, Profiller sayfasında "Topluluk" sekmesi, arama/filtre/önizleme, `Validation.StripRunActions` ile güvenli içe aktarma ve "Paylaşım için dışa aktar").
2. **Ekran üstü bildirim — OSD** (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE` bayraklı, tıklanamaz, odak çalmayan, oyuna enjeksiyon yapmayan anti-cheat güvenli WinForms katmanlı pencere; profil değişimi, katman değişimi, duraklatma/sürdürme, düşük pil ve sanal pad modu değişimi bildirimleri).
3. **Ekran klavyesi** (aynı katmanlı pencere altyapısıyla koltuktan kontrolcüyle kullanılan EN/TR QWERTY ızgarası; `SpecialAction.OnScreenKeyboard` veya DualSense Edge `Fn1 + Triangle` ile aç/kapa, `SendInput` Unicode çıktısı ve açıkken klavye tuşları için eşleme bastırma).

---

## 1. Kapsam (Scope)

### Güncelleyici güvenlik deseni ve mevcut kod dayanakları (`dosya:satır`)

Topluluk profil kataloğu, Faz 7b güncelleyicisinin (`docs/contract-phase7b.md:27-74`) kanıtlanmış güvenlik desenini bire bir yeniden kullanır:

- **Sabitlenmiş açık anahtar ve adresler**: `src/Tactile.Core/Updates/UpdateKey.cs:4-16` (`ManifestPublicKey` satır `7-8`, `InstallerSignerThumbprint` satır `11`, `ManifestUrl`, `ReleasesPage`, `AssetPrefix` satır `13-15`).
- **HTTP istemcisi, host allowlist, yönlendirme ve boyut sınırları**: `src/Tactile.Core/Updates/UpdateClient.cs:6-148`:
  - Boyut ve süre sınırları: `ManifestLimit = 64 * 1024`, `SignatureLimit = 1024`, `ManifestTimeout = TimeSpan.FromSeconds(15)`, `InstallerTimeout = TimeSpan.FromMinutes(10)` (`src/Tactile.Core/Updates/UpdateClient.cs:8-11`).
  - `HttpClientHandler` yapılandırması: `AllowAutoRedirect = false`, `UseCookies = false`, `UseDefaultCredentials = false`, `User-Agent: TriggerForge/<version>` (`src/Tactile.Core/Updates/UpdateClient.cs:15-28`).
  - Akış okuma sırasında boyut denetimi: hem `response.Content.Headers.ContentLength > limit` (`src/Tactile.Core/Updates/UpdateClient.cs:54-57`) hem de okunan toplam bayt `total > limit` (`src/Tactile.Core/Updates/UpdateClient.cs:71-75`) kontrolüyle sınır aşımında anında `UpdateError.TooLarge`.
  - Elle yönlendirme ve host allowlist (`GetAsync`, `src/Tactile.Core/Updates/UpdateClient.cs:99-145`): ilk URL ve her `301/302/303/307/308` `Location` hedefi için `url.IsAbsoluteUri && url.Scheme == Uri.UriSchemeHttps && url.Port == 443 && url.UserInfo.Length == 0` ve `url.IdnHost` kontrolü (`src/Tactile.Core/Updates/UpdateClient.cs:103-108`), en fazla `5` yönlendirme (`redirects > 5`, satır `110-113`), `.sig` dosyası `404/410` dönerse `UpdateError.BadSignature` (satır `137-140`).
- **ECDSA P-256 imza ve şema doğrulaması**: `src/Tactile.Core/Updates/UpdateManifest.cs:9-82`:
  - `ECDsa.ImportSubjectPublicKeyInfo`, `KeySize == 256`, 64 bayt base64 imza ve `VerifyData(body, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)` (`src/Tactile.Core/Updates/UpdateManifest.cs:16-31`). İmza doğrulanmadan JSON ayrıştırılmaz.
  - `JsonDocumentOptions { AllowDuplicateProperties = false }`, `schema == 1`, `size` ve 64 hex `sha256` denetimi (`src/Tactile.Core/Updates/UpdateManifest.cs:35-78`).
- **Sabit zamanlı SHA-256 ve dosya doğrulaması**: `src/Tactile.Core/Updates/UpdateService.cs:187-218` (`file.Length != asset.Size` veya `!CryptographicOperations.FixedTimeEquals(SHA256.HashDataAsync(...), asset.Sha256)` → `UpdateError.BadFile`), hata kodları `src/Tactile.Core/Updates/UpdateError.cs:3-18`, Authenticode doğrulaması `src/Tactile.Core/Updates/Authenticode.cs:11-95`.
- **Yayın aracı ve yayın betiği**:
  - `tools/Tactile.Tools.Release/Program.cs:15-153`: Windows CNG (`CngKey`, `CngAlgorithm.ECDsaP256`) üzerinde `KeyName = "TriggerForge update signing"` (`tools/Tactile.Tools.Release/Program.cs:19`) ile `keygen` (`satır 49-63`), `backup` (`satır 67-74`), `restore` (`satır 76-95`), `pubkey` (`satır 97-102`), `manifest` (`satır 104-141`), `verify` (`satır 143-152`).
  - `build/release.ps1:43-57`: `tools\Tactile.Tools.Release` derlemesi, `manifest` üretimi ve `src\Tactile.Core\Updates\UpdateKey.cs` içindeki `ManifestPublicKey` ile `verify` kontrolü.
- **Sahte HTTP birim testi deseni**: `tests/Tactile.Tests/Updates/UpdateTests.cs:336-417` (`FakeHttp : HttpMessageHandler` ve geçici тестовый P-256 anahtarı ile gerçek ağa çıkmadan tüm hata ve başarı dallarının testi).
- **Profil içe aktarma ve `run` eylemi temizleme kuralı**: `src/Tactile.Core/Storage/MappingValidation.cs:20-81` (`Validation.StripRunActions(Profile p, out int removed)`) ve `src/Tactile.Core/Storage/ProfileStore.cs:206-225` (`ProfileStore.Import(string json, out int removedRunActions)`).
- **Tepsi, pencere ve girdi enjeksiyonu altyapısı**: `src/Tactile.Host/TactileApp.cs:19-591`, `src/Tactile.Host/MainWindow.cs:10-121`, `src/Tactile.Host/Tray.cs:6-122`, `src/Tactile.Host/Bridge.cs:19-926`, `src/Tactile.Input/Platform.cs:5-75`, `src/Tactile.Input.Windows/WindowsInputInjector.cs:8-101`, `src/Tactile.Input.Windows/Native.cs:5-103`.

---

### Kapsam içi (3 ana özellik)

1. **Topluluk profil kataloğu (`kaan237/triggerforge-profiles`)**:
   - **Sunucusuz mimari**: Tüm katalog verisi `kaan237/triggerforge-profiles` public GitHub reposunda barındırılır. Repo kökünde `index.json` ve `index.json.sig`, profiller ise `profiles/<slug>.json` altında tutulur.
   - **Ayrı ECDSA P-256 anahtarı**:
     - Güncelleme anahtarından (`UpdateKey.ManifestPublicKey`, `src/Tactile.Core/Updates/UpdateKey.cs:7-8`, CNG `"TriggerForge update signing"`) **tamamen ayrı** yeni bir ECDSA P-256 anahtar çifti kullanılır.
     - Açık anahtar (SubjectPublicKeyInfo, base64) uygulamada `src/Tactile.Core/Catalog/CatalogKey.cs` içindeki `CatalogKey.CatalogPublicKey` sabitine gömülür.
     - Özel anahtar geliştirici makinesinde Windows CNG kullanıcı anahtar deposunda `CatalogKeyName = "TriggerForge catalog signing"` adıyla saklanır.
   - **Yayın aracına (`tools/Tactile.Tools.Release/Program.cs`) eklenecek katalog komutları**:
     - `catalog keygen`: CNG'de `"TriggerForge catalog signing"` adlı ECDSA P-256 anahtarını bir kez oluşturur (varsa üzerine yazmaz, hata kodu `1` döner) ve açık anahtarı (base64 SubjectPublicKeyInfo) ekrana yazar.
     - `catalog pubkey`: `"TriggerForge catalog signing"` açık anahtarını base64 olarak yazar.
     - `catalog backup <file> <password>`: katalog özel anahtarını AES-256-CBC / SHA-256 / `600_000` PBKDF2 iterasyonu (`tools/Tactile.Tools.Release/Program.cs:70` ile aynı parametreler) ile şifrelenmiş PKCS#8 dosyasına yedekler.
     - `catalog restore <file> <password>`: şifreli PKCS#8 yedeğini `"TriggerForge catalog signing"` CNG anahtarına geri yükler.
     - `catalog sign <repo-dir>`: `<repo-dir>/index.json` dosyasını okur; her girdinin `path` (`profiles/<slug>.json`) dosyasını `<repo-dir>` altında açar, gerçek bayt boyutunu (`size`), küçük harf hex SHA-256 özetini (`sha256`) ve özet özelliklerini (`summary`) günceller, profil JSON'unun `Validation.Normalize` ile geçerli olduğunu ve **hiçbir `ActionKind.Run` eylemi içermediğini** (`Validation.StripRunActions` `removed == 0`) doğrular (`run` varsa imzalamayı reddeder), `"updated"` tarihini UTC `yyyy-MM-dd` yapar, `index.json`'u biçimli UTF-8 olarak yazar ve `"TriggerForge catalog signing"` anahtarıyla (`HashAlgorithmName.SHA256`, `DSASignatureFormat.IeeeP1363FixedFieldConcatenation`) imzalayarak `<repo-dir>/index.json.sig` (base64) dosyasını üretir.
     - `catalog verify <repo-dir> <pubkey>`: `<repo-dir>/index.json` ve `<repo-dir>/index.json.sig` dosyalarını verilen base64 açık anahtarla ve tüm `profiles/*.json` dosyalarının `size` + `sha256` + şema kurallarıyla doğrular.
   - **İstemci ağ ve güvenlik kuralları (`src/Tactile.Core/Catalog/CatalogClient.cs`, `CatalogManifest.cs`, `CatalogService.cs`)**:
     - **Açılışta ağ yok**: Uygulama başladığında veya arka planda katalog için hiçbir ağ isteği yapılmaz. Ağ isteği yalnız kullanıcı Profiller sayfasında **"Topluluk" (`Community`)** sekmesini açtığında (`catalog.list`), yenile düğmesine bastığında (`catalog.list` `refresh: true`), bir profili önizlediğinde (`catalog.preview`) veya içe aktardığında (`catalog.import`) başlar.
     - **Host allowlist ve yönlendirme denetimi**: `AllowAutoRedirect = false`, `UseCookies = false`, `UseDefaultCredentials = false`. İlk URL ve her yönlendirme (`301/302/303/307/308`, en fazla `5` yönlendirme) `https` şemasında, port `443`, `UserInfo` boş ve `url.IdnHost` yalnız `"raw.githubusercontent.com"` veya `"github.com"` olmalıdır; aksi halde `CatalogError.UntrustedHost` döner ve bağlantı kesilir.
     - **Boyut ve süre sınırları**:
       - `index.json`: en fazla `512 KB` (`524_288` bayt).
       - `index.json.sig`: en fazla `1 KB` (`1024` bayt).
       - Tekil profil dosyası (`profiles/<slug>.json`): `entry.Size` kadar, üst sınır `256 KB` (`262_144` bayt).
       - `Content-Length` veya okunan akış sınırı aşarsa anında `CatalogError.TooLarge`.
       - Zaman aşımı: istek başına `15` saniye (`TimeSpan.FromSeconds(15)`).
     - **İmza ve içerik doğrulaması**:
       - `index.json` baytları ayrıştırılmadan önce `index.json.sig` imzası `CatalogKey.CatalogPublicKey` ile doğrulanır (`CatalogManifest.Verify`). İmza geçersiz veya eksikse `CatalogError.BadSignature` döner ve hiçbir katalog verisi gösterilmez.
       - Bir profil indirildiğinde (`catalog.preview` veya `catalog.import`), URL yalnızca `CatalogKey.ContentPrefix + entry.Path` olarak kurulur. İndirilen baytların uzunluğu `entry.Size` ile, SHA-256 özeti ise `CryptographicOperations.FixedTimeEquals` kullanılarak `entry.Sha256` ile karşılaştırılır; uyuşmazsa `CatalogError.BadFile` döner.
       - İçe aktarmada (`catalog.import`) doğrulanmış profil JSON'u `ProfileStore.Import(json, out int removed)` (`src/Tactile.Core/Storage/ProfileStore.cs:206-225`) üzerinden geçirilir: `Validation.Normalize` çalışır, yeni bir 32 karakterlik `Id` (`Validation.NewId()`) atanır, `Validation.StripRunActions` (`src/Tactile.Core/Storage/MappingValidation.cs:20-81`) tüm `run` eylemlerini siler ve profil diske kaydedilir.
     - **Ağ hatasında sessiz uyarı**: Ağ/DNS/zaman aşımı veya doğrulama hatasında tepsi baloncuğu (`_tray.Balloon`) ya da genel hata bildirimi (`PostError`) fırlatılmaz; hata yalnız `catalog.status` mesajı içinde (`state: "error"`, `error`, `message`) Topluluk sekmesinin kendi içinde sessiz uyarı satırı ve `"Tekrar dene"` (`"Try again"`) düğmesi olarak gösterilir.
   - **Profiller sayfası "Topluluk" sekmesi ve Paylaşım**:
     - Profiller sayfasında (`ui/src/pages/Profiles.tsx`) **"Profillerim" (`My profiles`)** ve **"Topluluk" (`Community`)** olmak üzere iki sekme bulunur.
     - **Topluluk sekmesi**: metin arama (ad, oyun `.exe`, yazar, etiket), oyun filtresi, kontrolcü filtresi (`Tümü` / `DualSense` / `DualSense Edge`), etiket filtresi, seçili girdi için **Önizleme** (yazar, sürüm, hedef oyunlar, kontrolcü, etiketler ve içerdiği özellik özeti: tetik efektleri, sanal kontrolcü kipi, tuş eşleme/katman/akor/makro/gyro/flick stick/telemetri özeti) ve **"İçe aktar" (`Import`)** düğmesi.
     - **Paylaşım için dışa aktar (`Export for sharing`)**: Profillerim sekmesinde seçili profil için **"Paylaşım için dışa aktar"** düğmesi (`profile.exportShare`). `Validation.StripRunActions` uygulanmış temiz profil JSON'unu indirmeye/kopyalamaya sunar ve `kaan237/triggerforge-profiles` reposuna Pull Request (PR) açma yönergesiyle birlikte hazır `index.json` girdi taslağını gösterir (PR'ı kullanıcı kendi tarayıcısında açar).

2. **Ekran üstü bildirim — OSD (`src/Tactile.Host/OverlayWindow.cs`, `src/Tactile.Host/OsdOverlay.cs`)**:
   - **Anti-cheat güvenli katmanlı pencere**:
     - Ayrı bir WinForms `Form` / `NativeWindow` (`OverlayWindow`), `CreateParams.ExStyle` üzerinde `WS_EX_LAYERED (0x00080000) | WS_EX_TRANSPARENT (0x00000020) | WS_EX_TOOLWINDOW (0x00000080) | WS_EX_NOACTIVATE (0x08000000) | WS_EX_TOPMOST (0x00000008)` bayraklarıyla oluşturulur.
     - `ShowWithoutActivation => true`, `FormBorderStyle = FormBorderStyle.None`, `ShowInTaskbar = false`, `TopMost = true`; `SetWindowPos` çağrılarında her zaman `SWP_NOACTIVATE (0x0010) | SWP_NOZORDER` / `HWND_TOPMOST` kullanılır ve `WM_MOUSEACTIVATE` mesajı `MA_NOACTIVATE (3)` döndürür.
     - Tıklanamaz (tüm fare tıklamaları altındaki oyun/uygulama penceresine geçer), klavye/fare odağını asla üzerine almaz.
     - **Oyuna enjeksiyon YOK**: Hiçbir oyun sürecine DLL enjekte edilmez, DirectX/OpenGL/Vulkan kancası (hook) kurulmaz ve oyun belleği okunmaz/yazılmaz.
     - **Tam ekran sınırı belgelemesi**: Standart Windows masaüstü birleştiricisi (DWM) üst penceresi olduğundan pencereli (*windowed*) ve penceresiz tam ekran (*borderless fullscreen*) oyunlarda görünür; gerçek özel tam ekran (*exclusive fullscreen*) kullanan oyunlarda işletim sistemi DWM katmanını devre dışı bırakabildiği için görünmeyebileceği hem bu sözleşmede hem de Ayarlar → OSD açıklamalarında (`hint`) açıkça belirtilir.
   - **OSD Olayları (5 olay)**:
     1. **Profil değişimi** (`AppCore.ActiveProfileChanged`): Başlık `"Profile"` / `"Profil"`, metin `<Profile.Name>` (oyun kuralıyla seçildiyse `ActiveReason` içindeki `<exe>` adı parantez içinde gösterilir).
     2. **Katman değişimi** (`AppCore.ActiveLayerChanged` — birincil kontrolcüde etkin katman değiştiğinde): Başlık `"Layer"` / `"Katman"`, metin etkin en üst katmanın adı (`<Layer.Name>`) veya tüm shift katmanları kapandığında `"Base"` / `"Temel"`.
     3. **Duraklatma / sürdürme** (`AppCore.MappingChanged` — `Mapping.Paused` değeri değiştiğinde): `"Mapping paused"` / `"Eşleme duraklatıldı"` (varsa nedeni: kısayol / tam ekran) veya `"Mapping resumed"` / `"Eşleme sürdürüldü"`.
     4. **Düşük pil** (`AppCore.Notice` — `NoticeKinds.LowBattery` geldiğinde): Başlık `"Low battery"` / `"Düşük pil"`, metin `Notice.Message` (örn. `"%20"` / `"%10"`).
     5. **Sanal pad modu değişimi** (`EmulationService.Changed` — birincil yuva `Status.Mode` değiştiğinde): Başlık `"Virtual controller"` / `"Sanal kontrolcü"`, metin `"Xbox 360"`, `"DualSense"`, `"DualShock 4"` veya `"Off"` / `"Kapalı"`.
   - **OSD Ayarları (`AppSettings.Osd`)**:
     - `enabled`: `bool`, varsayılan `false` (varsayılan olarak kapalı).
     - `corner`: `OsdCorner` (`TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`), varsayılan `TopRight` (`"topRight"`). Birincil ekranın (`Screen.PrimaryScreen.WorkingArea`) seçilen köşesinden `24 px` içeride konumlanır.
     - `durationSeconds`: `int`, varsayılan `2`, aralık `1..5` saniye. Yeni bir bildirim geldiğinde mevcut bildirimin metnini günceller ve süreyi sıfırlar.
     - `size`: `OsdSize` (`Small`, `Medium`, `Large`), varsayılan `Medium` (`"medium"`).
     - `opacity`: `int`, varsayılan `85`, aralık `30..100` (`%`).

3. **Ekran klavyesi — On-Screen Keyboard (`src/Tactile.Host/OnScreenKeyboard.cs`)**:
   - **Katmanlı pencere ve odak koruma**:
     - OSD ile aynı katmanlı pencere altyapısını (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST`, `ShowWithoutActivation => true`) kullanır; birincil ekranın alt-orta kısmında (`WorkingArea` alt kenarından `48 px` yukarıda, yatayda ortalanmış) açılır.
     - Hedef pencerenin (tarayıcı, oyun sohbet kutusu, başlatıcı, metin kutusu) odağını asla çalmaz; böylece gönderilen Unicode tuş vuruşları doğrudan ön plandaki pencereye yazılır.
   - **Açma / Kapama yolları**:
     1. **Eşleme eylemi**: `SpecialAction` enum'una (`src/Tactile.Core/Model/Mapping.cs:12`) `OnScreenKeyboard` (`"onScreenKeyboard"`) eklenir. Herhangi bir tuşa, katmana, akora, tetik kademesine veya dokunmatik yüzey bölgesine `{"kind": "special", "special": "onScreenKeyboard"}` atanabilir.
     2. **DualSense Edge kısayolu**: `settings.edgeShortcuts` etkin ve kontrolcü `ControllerModel.DualSenseEdge` iken `Fn1 + Triangle` (`EdgeShortcut.OnScreenKeyboard`, `src/Tactile.Core/Multi/EdgeShortcuts.cs:5-26`) ekran klavyesini açar/kapatır.
   - **EN ve TR QWERTY ızgara düzeni (`OnScreenKeyboardModel`)**:
     - Başlangıç dili `settings.language` (`en` veya `tr`) değerini izler.
     - **EN düzeni (4 satır)**:
       - Satır 0: `1`, `2`, `3`, `4`, `5`, `6`, `7`, `8`, `9`, `0`, `-`, `=` (Büyük harf / Shift açıkken: `!`, `@`, `#`, `$`, `%`, `^`, `&`, `*`, `(`, `)`, `_`, `+`)
       - Satır 1: `q`, `w`, `e`, `r`, `t`, `y`, `u`, `i`, `o`, `p`, `[`, `]` (Büyük: `Q..P`, `{`, `}`)
       - Satır 2: `a`, `s`, `d`, `f`, `g`, `h`, `j`, `k`, `l`, `;`, `'`, `/` (Büyük: `A..L`, `:`, `"`, `?`)
       - Satır 3: `z`, `x`, `c`, `v`, `b`, `n`, `m`, `,`, `.`, `@` (Büyük: `Z..M`, `<`, `>`, `_`)
     - **TR düzeni (4 satır — `ç, ğ, ı, ö, ş, ü` tam destekli)**:
       - Satır 0: `1`, `2`, `3`, `4`, `5`, `6`, `7`, `8`, `9`, `0`, `*`, `-` (Büyük: `!`, `'`, `^`, `+`, `%`, `&`, `/`, `(`, `)`, `=`, `?`, `_`)
       - Satır 1: `q`, `w`, `e`, `r`, `t`, `y`, `u`, `ı`, `o`, `p`, `ğ`, `ü` (Büyük: `Q`, `W`, `E`, `R`, `T`, `Y`, `U`, `I`, `O`, `P`, `Ğ`, `Ü`)
       - Satır 2: `a`, `s`, `d`, `f`, `g`, `h`, `j`, `k`, `l`, `ş`, `i`, `,` (Büyük: `A`, `S`, `D`, `F`, `G`, `H`, `J`, `K`, `L`, `Ş`, `İ`, `;`)
       - Satır 3: `z`, `x`, `c`, `v`, `b`, `n`, `m`, `ö`, `ç`, `.` (Büyük: `Z`, `X`, `C`, `V`, `B`, `N`, `M`, `Ö`, `Ç`, `:`)
     - Alt bilgi çubuğunda kontrolcü tuş ipuçları gösterilir: `✕ Yaz · ○ Sil · △ Boşluk · □ Büyük/Küçük · Options Enter · PS Kapat` (`✕ Type · ○ Backspace · △ Space · □ Shift · Options Enter · PS Close`).
   - **Kontrolcü denetimleri (klavye açıkken)**:
     - **D-pad (`Up`, `Down`, `Left`, `Right`) veya Sol Stick (`Left`)**: Seçili hücreyi 4 yönde hareket ettirir (satır ve sütun uçlarında sarar; farklı uzunluktaki satırlarda sütun indeksi `Math.Min(col, row.Length - 1)` ile korunur). Sol stick `|x| >= 0.5` veya `|y| >= 0.5` eşiğiyle ilk harekette anında, basılı tutulursa `180 ms` tekrar aralığıyla (`RepeatInterval`) ilerler.
     - **✕ (`Cross`)**: Seçili hücredeki karakteri Unicode `SendInput` (`IInputInjector.Text`) ile yazar.
     - **○ (`Circle`)**: Sil (`Key.Backspace` tap — `30 ms` veya anlık down+up).
     - **△ (`Triangle`)**: Boşluk (`Key.Space` tap).
     - **□ (`Square`)**: Büyük/küçük harf kipini (`Uppercase`) açar/kapatır.
     - **Options**: Enter (`Key.Enter` tap).
     - **PS**: Ekran klavyesini kapatır.
   - **Çıktı yolu (`SendInput` Unicode)**:
     - `IInputInjector` (`src/Tactile.Input/Platform.cs:5-16`) arayüzüne `void Text(ReadOnlySpan<char> text)` metodu eklenir.
     - `WindowsInputInjector.Text` (`src/Tactile.Input.Windows/WindowsInputInjector.cs`), her UTF-16 kod birimi (`char`) için `KeybdInput { Vk = 0, Scan = ch, Flags = KeyeventfUnicode, ExtraInfo = Signature }` ve `Flags = KeyeventfUnicode | KeyeventfKeyUp` kaydını `SendInput` (`src/Tactile.Input.Windows/Native.cs:78`) ile gönderir (`KeyeventfUnicode = 0x0004`). Böylece Türkçe karakterler (`ç, ğ, ı, İ, ö, ş, ü`) Windows etkin klavye dilinden bağımsız olarak doğru yazılır.
   - **Klavye açıkken eşleme bastırma**:
     - Ekran klavyesi açıldığında `Mapper.ReleaseAll()` çağrılır ve o anda basılı tüm sanal tuşlar/makrolar bırakılır.
     - Klavye açık olduğu sürece (`AppCore.OnScreenKeyboardOpen == true`), klavyenin kullandığı tuşlar (`Buttons.Up | Buttons.Down | Buttons.Left | Buttons.Right | Buttons.Cross | Buttons.Circle | Buttons.Triangle | Buttons.Square | Buttons.Options | Buttons.PS`) ve sol stick (`Left` merkeze `128, 128` çekilerek) `Mapper.OnInput` ve `EmulationService.OnInput` çağrılarından önce maskelenir/bastırılır.
     - Klavyeyi kapatan tuş (`PS` veya `OnScreenKeyboard` eylem tuşu) fiziksel olarak bırakılana kadar `Mapper` içinde `_suppressedButtons` maskesinde tutulur; böylece klavyeyi kapatmak için basılan `PS` tuşu profilin kendi `PS` katmanını veya eylemini yanlışlıkla tetiklemez.

### Kapsam dışı (Out of scope)

- **Hesaplı bulut eşitleme ve kullanıcı hesabı/oturum açma**: Sunucu veya hesap sistemi yoktur.
- **Uygulama içinden otomatik Pull Request açma / GitHub API token/OAuth akışı**: Kullanıcı profili "Paylaşım için dışa aktar" ile alır ve PR'ı kendi tarayıcısında açar.
- **Radial menu (tekerlek menü)**: Faz 15.4 kapsamında OSD penceresi üzerine eklenecektir (`docs/yol-haritasi.md:81`).
- **Oyun sürecine grafik kancası (DirectX / Vulkan / OpenGL hook veya DLL injection)**: Anti-cheat güvenliği nedeniyle kesinlikle kapsam dışıdır.
- **İkincil kontrolcüler (yuva 1–3)**: Faz 8 kuralı (`docs/contract-phase8.md:26-27`) korunur; eşleme, OSD katman bildirimi ve ekran klavyesi yalnızca birincil kontrolcüde (slot 0) çalışır.

---

## 2. Profil, Ayar ve Katalog Şeması (Profile, Settings, and Catalog Schema)

`Profile.CurrentVersion` (`src/Tactile.Core/Model/Profile.cs:26`) ve `AppSettings.CurrentVersion` (`src/Tactile.Core/Model/AppSettings.cs:8`) **`1`** olarak kalır.

### Göç ve geriye dönük uyumluluk kuralı

- Dosya sürüm numarası (`version: 1`) artırılmaz. Faz 2–13'ten kalan mevcut profil ve `settings.json` dosyaları, C# kayıt varsayılanları ve `LenientEnumConverterFactory` (`src/Tactile.Core/Storage/TactileJson.cs:48-78`) üzerinden yeni alanların varsayılan değerleriyle (`osd.enabled = false` vb.) sorunsuz okunur ve `Validation.Normalize` (`src/Tactile.Core/Storage/Validation.cs:14-256`) tarafından normalize edilir.
- Arayüzde `normalizeProfile`, `normalizeMapping` ve `normalizeSettings` (`ui/src/model.ts:198-521`) eksik alanları aynı varsayılanlarla doldurur.
- **Test zorunluluğu**: Faz 14 alanlarını içermeyen mevcut bir profil ve `settings.json` yüklendiğinde öncekiyle bire bir aynı normalize nesneyi ve çalışma zamanı davranışını üretmelidir.

### Profil eklemeleri (`Profile.Mapping` → `SpecialAction`)

`src/Tactile.Core/Model/Mapping.cs:12` içindeki `SpecialAction` enum'una `OnScreenKeyboard` eklenir:

```csharp
public enum SpecialAction { Screenshot, GameBar, TaskView, Desktop, ShowApp, OnScreenKeyboard }
```

```jsonc
{
  "mapping": {
    "buttons": {
      "mic": {
        "press": {
          "kind": "special",
          "special": "onScreenKeyboard" // "screenshot" | "gameBar" | "taskView" | "desktop" | "showApp" | "onScreenKeyboard"
        }
      }
    }
  }
}
```

### Ayar eklemeleri (`AppSettings.Osd`)

`src/Tactile.Core/Model/AppSettings.cs` içine `OsdSettings Osd { get; init; } = new();` eklenir:

```jsonc
{
  // ... mevcut AppSettings alanları ...
  "osd": {
    "enabled": false,            // bool, varsayılan false (kapalı)
    "corner": "topRight",        // "topLeft" | "topRight" | "bottomLeft" | "bottomRight", varsayılan "topRight"
    "durationSeconds": 2,        // int, varsayılan 2, 1..5 aralığına kıstırılır (clamped)
    "size": "medium",            // "small" | "medium" | "large", varsayılan "medium"
    "opacity": 85                // int, varsayılan 85, 30..100 aralığına kıstırılır (%)
  }
}
```

### Topluluk profil kataloğu `index.json` şeması (`kaan237/triggerforge-profiles`)

`index.json` (UTF-8, `<= 512 KB`) ve `index.json.sig` (`index.json` baytlarının ECDSA P-256 / SHA-256 IEEE P1363 `r||s` 64 baytlık imzasının base64 metni, `<= 1 KB`):

```jsonc
{
  "schema": 1,
  "updated": "2026-11-22",
  "profiles": [
    {
      "id": "ghost-of-tsushima-bow",
      "name": "Ghost of Tsushima — Bow & Gyro",
      "games": ["GhostOfTsushima.exe"],
      "controller": "dualsense",         // "dualsense" | "edge" | "any"
      "tags": ["action", "gyro", "bow"], // 1..8 küçük harf etiket, her biri 1..24 karakter
      "author": "kaan237",               // takma ad, 1..40 karakter
      "version": "1.0.0",                // System.Version
      "path": "profiles/ghost-of-tsushima-bow.json",
      "sha256": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      "size": 4120,                      // 1..262144 bayt
      "description": {
        "en": "Bow resistance on R2, feedback on L2, gyro aiming while L2 is held.",
        "tr": "R2'de yay direnci, L2'de direnç, L2 basılıyken gyro nişan."
      },
      "summary": {
        "leftTrigger": "feedback",       // TriggerMode
        "rightTrigger": "bow",           // TriggerMode
        "emulation": "dualSense",        // "off" | "xbox360" | "dualSense" | "dualShock4" | "auto"
        "mappingEnabled": true,
        "layerCount": 1,
        "chordCount": 0,
        "macroCount": 1,
        "gyroMouse": "whileHeld",        // GyroMouseMode
        "gyroStick": "off",              // GyroMouseMode
        "flickStick": false,
        "audioHaptics": false,
        "telemetryRules": 0
      }
    }
  ]
}
```

### Yeni alanlar için doğrulama ve güvenlik kuralları

- **`OsdSettings` normalizasyonu (`Validation.Normalize(AppSettings)`, `src/Tactile.Core/Storage/Validation.cs:198-256`)**:
  - `Osd`: `s.Osd ?? new()`.
  - `Corner`: `Defined(osd.Corner, OsdCorner.TopRight)`.
  - `DurationSeconds`: `Math.Clamp(osd.DurationSeconds == 0 ? 2 : osd.DurationSeconds, 1, 5)`.
  - `Size`: `Defined(osd.Size, OsdSize.Medium)`.
  - `Opacity`: `Math.Clamp(osd.Opacity == 0 ? 85 : osd.Opacity, 30, 100)`.
- **`SpecialAction.OnScreenKeyboard` doğrulaması (`src/Tactile.Core/Storage/MappingValidation.cs:544-546`)**:
  - `Defined(a.Special ?? SpecialAction.Screenshot, SpecialAction.Screenshot)` mevcut yapısı `SpecialAction.OnScreenKeyboard` değerini doğal olarak korur.
  - `ui/src/model.ts:394-396` içindeki `normalizeMappingAction` `special` kontrolüne `"onScreenKeyboard"` eklenir.
- **`CatalogManifest.Verify` doğrulama kuralları (`src/Tactile.Core/Catalog/CatalogManifest.cs`)**:
  - İmza: `CatalogKey.CatalogPublicKey` ile 64 bayt IEEE P1363 ECDSA P-256 / SHA-256 doğrulaması başarısızsa `CatalogException(CatalogError.BadSignature)` fırlatır.
  - JSON: `AllowDuplicateProperties = false`, `schema == 1`, `updated` geçerli `yyyy-MM-dd` (`DateOnly.TryParseExact`), `profiles` dizisi `0..500` öğe (`MaxCatalogProfiles = 500`).
  - Her `CatalogEntry` için:
    - `id`: benzersiz, `^[a-z0-9][a-z0-9-]{1,62}[a-z0-9]$` ile eşleşmeli.
    - `name`: boş olmayan, `1..64` karakter; `author`: boş olmayan, `1..40` karakter.
    - `version`: yalnızca ASCII rakam ve `.` içeren geçerli `System.Version`.
    - `controller`: `"dualsense"`, `"edge"` veya `"any"`.
    - `games`: `0..16` öğe, her biri boş olmayan `.exe` dosya adı (`Path.GetFileName(g) == g`, yol ayırıcı `/` veya `\` içermez).
    - `tags`: `0..8` öğe, her biri `1..24` karakter.
    - `path`: `^profiles/[a-z0-9][a-z0-9._-]{0,80}\.json$` ile eşleşmeli, `".."` veya `"\\"` içeremez.
    - `size`: `1..262_144` (`256 KB`).
    - `sha256`: 64 karakter ASCII hex (`sha256.Length == 64 && sha256.All(char.IsAsciiHexDigit)`).
    - Herhangi bir alan kurala uymazsa `CatalogException(CatalogError.BadManifest)` fırlatılır.

---

## 3. Köprü Sözleşmesi (`src/Tactile.Host/Bridge.cs` ↔ `ui/src/host.ts`)

İş A ve İş C aşağıdaki mesaj tiplerini ve alan adlarını bire bir aynı biçimde uygular:

### Host → UI mesajları

| Mesaj `type` | Alanlar / Şekil | Açıklama |
|---|---|---|
| `catalog.status` | `{ type: "catalog.status", state: "idle" \| "loading" \| "ready" \| "importing" \| "error", updated: string \| null, profiles: CatalogEntry[], importingId: string \| null, error: "network" \| "untrustedHost" \| "tooLarge" \| "badSignature" \| "badManifest" \| "badFile" \| null, message: string \| null }` | Katalog durumu değiştiğinde ve `catalog.list` / `catalog.import` sonrasında gönderilir. **Açılışta (`hello`) otomatik ağ isteği başlatmaz** (`hello` sırasında yalnızca mevcut bellek içi durum, başlangıçta `state: "idle", profiles: []` gönderilir). |
| `catalog.preview` | `{ type: "catalog.preview", id: string, summary: CatalogProfileSummary, description: string, games: string[], author: string, version: string, controller: "dualsense" \| "edge" \| "any", tags: string[] }` | `catalog.preview` isteğine yanıt olarak seçili katalog girdisinin özetini döndürür. |
| `exportShare` | `{ type: "exportShare", id: string, fileName: string, json: string, entryJson: string, removedRunActions: number }` | `profile.exportShare` isteğine yanıt olarak `Validation.StripRunActions` uygulanmış profil JSON'unu (`json`), önerilen `<slug>.json` dosya adını (`fileName`), `index.json` için hazır girdi taslağını (`entryJson`, gerçek `size` ve `sha256` hesaplanmış olarak) ve silinen `run` eylemi sayısını (`removedRunActions`) gönderir. |
| `settings` | `{ type: "settings", settings: AppSettings }` | Mevcut mesaj (`src/Tactile.Host/Bridge.cs:811`); `settings.osd` (`enabled`, `corner`, `durationSeconds`, `size`, `opacity`) alanını içerir. |

### UI → Host mesajları

| Mesaj `type` | Yük (Payload) alanları | Host davranışı |
|---|---|---|
| `catalog.list` | `refresh?: boolean` | `appSource && _ready` doğrulanır. Katalog daha önce yüklenmemişse (`idle` / `error`) veya `refresh === true` ise `CatalogService.FetchIndexAsync()` başlatır; zaten `ready` ise ve `refresh !== true` ise mevcut önbellekteki `catalog.status` mesajını yeniden gönderir. |
| `catalog.preview` | `id: string` | `appSource && _ready` doğrulanır. Yüklü katalogda `id` girdisini bulur ve `catalog.preview` mesajını gönderir (girdide `summary` mevcutsa ağa çıkmadan anında gönderir; yoksa profil dosyasını indirip SHA-256 + boyut doğrulamasından sonra özetini çıkarır). |
| `catalog.import` | `id: string` | `appSource && _ready` doğrulanır. Doğrulanmış katalogdaki `id` girdisinin `path` dosyasını indirir, `size` (`<= 256 KB`) ve sabit zamanlı `sha256` doğrulamasını yapar, `_core.Profiles.Import(json, out int removed)` çağırır (`Validation.Normalize` + `Validation.StripRunActions` + yeni `Id`), `PostProfiles()`, `PostCatalogStatus()` ve bilgi bildirimi (`"Imported “<Name>” from community catalog."` / `"Topluluk kataloğundan “<Ad>” içe aktarıldı."`) gönderir. |
| `profile.exportShare` | `id: string` | `appSource && _ready` doğrulanır. `_core.Profiles.Get(id)` profilini alır (yoksa `BadMessage`), `Validation.StripRunActions(profile, out int removed)` uygular, biçimli JSON baytlarının `size` ve `sha256` özetini hesaplar, `exportShare` mesajını gönderir. |

---

## 4. İş Bölümü (Work Split)

Ortak dosyalar (`AppSettings.cs`, `Validation.cs`, `MappingValidation.cs`, `Mapping.cs`, `AppCore.cs`, `Bridge.cs`, `TactileApp.cs`) **tek bir işe** atanmıştır; hiçbir dosya birden fazla işte düzenlenmez:
- `src/Tactile.Host/Bridge.cs` ve `src/Tactile.Host/MainWindow.cs` **yalnız İş A**'ya aittir.
- `src/Tactile.Core/Model/AppSettings.cs`, `src/Tactile.Core/Model/Mapping.cs`, `src/Tactile.Core/Storage/Validation.cs`, `src/Tactile.Core/Storage/MappingValidation.cs`, `src/Tactile.Core/Mapping/ActionDispatcher.cs`, `src/Tactile.Core/Mapping/Mapper.cs`, `src/Tactile.Core/Multi/EdgeShortcuts.cs`, `src/Tactile.Core/AppCore.cs`, `src/Tactile.Host/TactileApp.cs`, `src/Tactile.Input/**` ve `src/Tactile.Input.Windows/**` **yalnız İş B**'ye aittir.
- `ui/src/**` **yalnız İş C**'ye aittir.

Dosya kümeleri tamamen ayrık olduğu için **İş A** ve **İş B** paralel çalışabilir; birleştirme sırası: **İş A ‖ İş B → İş C** (veya üçü paralel, ardından kontrol adımında `dotnet test` + `npm run build`).

| İş | Kapsam | İzin verilen dosyalar | Yasaklı dosyalar | Zamanlama / Sıra |
|---|---|---|---|---|
| **A** | Topluluk profil kataloğu (`CatalogKey`, `CatalogClient`, `CatalogManifest`, `CatalogService`), `Bridge.cs` katalog ve paylaşım mesajları, `tools/Tactile.Tools.Release` (`catalog keygen/pubkey/backup/restore/sign/verify`), sahte HTTP ile katalog birim testleri | `src/Tactile.Core/Catalog/**`, `src/Tactile.Host/Bridge.cs`, `src/Tactile.Host/MainWindow.cs`, `tools/Tactile.Tools.Release/Program.cs`, `tests/Tactile.Tests/Catalog/**` | `src/Tactile.Core/Model/**`, `src/Tactile.Core/Storage/**`, `src/Tactile.Core/Mapping/**`, `src/Tactile.Core/AppCore.cs`, `src/Tactile.Host/TactileApp.cs`, `src/Tactile.Host/Overlay*`, `src/Tactile.Host/Osd*`, `src/Tactile.Host/OnScreenKeyboard*`, `ui/**`, `docs/**` | İş B ve İş C ile paralel (ortak dosya yok; mevcut `ProfileStore.Import` ve `Validation.StripRunActions` metodunu çağırır) |
| **B** | OSD ve Ekran klavyesi (`OverlayWindow`, `OsdOverlay`, `OnScreenKeyboard`), `OsdSettings` (`AppSettings`, `Validation`), `SpecialAction.OnScreenKeyboard` (`Mapping`, `ActionDispatcher`), Edge kısayolu, `IInputInjector.Text` (`KEYEVENTF_UNICODE`), klavye açıkken eşleme bastırma (`AppCore`), `TactileApp` bağlantısı ve birim testleri | `src/Tactile.Host/OverlayWindow.cs`, `src/Tactile.Host/OsdOverlay.cs`, `src/Tactile.Host/OnScreenKeyboard.cs`, `src/Tactile.Host/TactileApp.cs`, `src/Tactile.Core/Model/AppSettings.cs`, `src/Tactile.Core/Model/Mapping.cs`, `src/Tactile.Core/Storage/Validation.cs`, `src/Tactile.Core/Storage/MappingValidation.cs`, `src/Tactile.Core/Mapping/ActionDispatcher.cs`, `src/Tactile.Core/Mapping/Mapper.cs`, `src/Tactile.Core/Multi/EdgeShortcuts.cs`, `src/Tactile.Core/AppCore.cs`, `src/Tactile.Input/Platform.cs`, `src/Tactile.Input.Windows/Native.cs`, `src/Tactile.Input.Windows/WindowsInputInjector.cs`, `tests/Tactile.Tests/Phase14HostTests.cs`, `tests/Tactile.Tests/Support.cs`, `tests/Tactile.Tests/StoreTests.cs` | `src/Tactile.Core/Catalog/**`, `src/Tactile.Host/Bridge.cs`, `src/Tactile.Host/MainWindow.cs`, `tools/**`, `tests/Tactile.Tests/Catalog/**`, `ui/**`, `docs/**` | İş A ve İş C ile paralel (`ActionKind.Special` → `OnScreenKeyboard` eklemesi yalnız bu iştedir) |
| **C** | React/TypeScript arayüzü: Profiller sayfasında "Topluluk" sekmesi (arama, filtre, önizleme, içe aktarma) ve "Paylaşım için dışa aktar" modalı/kutusu, Ayarlar sayfasında OSD bölümü, Sistem eylemlerinde "Ekran klavyesi", EN/TR çeviriler ve `sim.ts` | `ui/src/**` | `src/**`, `tests/**`, `tools/**`, `docs/**` | İş A ve İş B ile paralel |

### İş A (`src/Tactile.Core/Catalog/**`, `src/Tactile.Host/Bridge.cs`, `src/Tactile.Host/MainWindow.cs`, `tools/Tactile.Tools.Release/Program.cs`, `tests/Tactile.Tests/Catalog/**`)

- **Oluşturulacak dosyalar**:
  - `src/Tactile.Core/Catalog/CatalogKey.cs`:
    - `CatalogPublicKey` (ECDSA P-256 SubjectPublicKeyInfo base64 sabiti — `UpdateKey.ManifestPublicKey` değerinden farklı, katalog anahtar çiftine ait sabit açık anahtar).
    - `IndexUrl = "https://raw.githubusercontent.com/kaan237/triggerforge-profiles/main/index.json"`.
    - `SignatureUrl = "https://raw.githubusercontent.com/kaan237/triggerforge-profiles/main/index.json.sig"`.
    - `ContentPrefix = "https://raw.githubusercontent.com/kaan237/triggerforge-profiles/main/"`.
    - `RepositoryUrl = "https://github.com/kaan237/triggerforge-profiles"`.
  - `src/Tactile.Core/Catalog/CatalogError.cs`:
    - `public enum CatalogError { Network, UntrustedHost, TooLarge, BadSignature, BadManifest, BadFile }`
    - `internal sealed class CatalogException(CatalogError code, string detail, Exception? inner = null) : Exception(detail, inner)`
  - `src/Tactile.Core/Catalog/CatalogManifest.cs`:
    - `public enum CatalogController { DualSense, Edge, Any }`
    - `public sealed record CatalogProfileSummary(TriggerMode LeftTrigger, TriggerMode RightTrigger, string Emulation, bool MappingEnabled, int LayerCount, int ChordCount, int MacroCount, GyroMouseMode GyroMouse, GyroMouseMode GyroStick, bool FlickStick, bool AudioHaptics, int TelemetryRules)` (`FromProfile(Profile p)` yardımcı metodu ile).
    - `public sealed record CatalogEntry(string Id, string Name, IReadOnlyList<string> Games, CatalogController Controller, IReadOnlyList<string> Tags, string Author, string Version, string Path, byte[] Sha256, string Sha256Hex, long Size, LocalizedMessage Description, CatalogProfileSummary Summary)`
    - `public sealed record CatalogManifest(string Updated, IReadOnlyList<CatalogEntry> Profiles)`
    - `CatalogManifest.Verify(byte[] body, string signature, byte[] publicKey)`: Bölüm 2'deki ECDSA P-256 imza ve şema kurallarını uygular.
    - `CatalogShareExporter.BuildSharePackage(Profile profile)`: `Validation.StripRunActions(profile, out int removed)` çağırır, temizlenmiş profil JSON'unu, `<slug>.json` dosya adını, SHA-256 özetini, bayt boyutunu ve `index.json` girdi taslağını üretir.
  - `src/Tactile.Core/Catalog/CatalogClient.cs`:
    - `UpdateClient` (`src/Tactile.Core/Updates/UpdateClient.cs:6-148`) desenini izler: `IndexLimit = 512 * 1024`, `SignatureLimit = 1024`, `ProfileLimit = 256 * 1024`, `RequestTimeout = TimeSpan.FromSeconds(15)`.
    - `AllowAutoRedirect = false`, `UseCookies = false`, `UseDefaultCredentials = false`, `User-Agent: TriggerForge/<version>`.
    - `GetAsync`: en fazla `5` yönlendirme, her URL'de `https`, port `443`, boş `UserInfo` ve `url.IdnHost is "raw.githubusercontent.com" or "github.com"` kontrolü (aksi halde `CatalogError.UntrustedHost`).
    - `FetchIndexAsync(CancellationToken)` ve `FetchProfileAsync(CatalogEntry entry, CancellationToken)` (URL'yi `new Uri(CatalogKey.ContentPrefix + entry.Path)` olarak kurar, `entry.Size` sınırı ve `CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), entry.Sha256)` kontrolü yapar; uyuşmazsa `CatalogError.BadFile`).
  - `src/Tactile.Core/Catalog/CatalogService.cs`:
    - `public enum CatalogState { Idle, Loading, Ready, Importing, Error }`
    - `public sealed record CatalogStatus(CatalogState State, string? Updated, IReadOnlyList<CatalogEntry> Profiles, string? ImportingId, CatalogError? Error, string? Message)`
    - Başlangıç durumu `CatalogState.Idle` (otomatik ağ isteği yok).
    - `FetchAsync(bool force = false)` ve `ImportAsync(string entryId, ProfileStore store)`: aynı anda tek işlem (`Busy` kilidi), `ImportAsync` içinde profili indirip doğrular ve `store.Import(json, out int removed)` ile kaydeder.
  - `tests/Tactile.Tests/Catalog/CatalogTests.cs`:
    - Sahte `HttpMessageHandler` ile katalog imza, yönlendirme, boyut sınırı, SHA-256, `StripRunActions`, yeni `Id` ataması ve `catalog sign/verify` mantığı birim testleri.
- **Düzenlenecek dosyalar**:
  - `src/Tactile.Host/Bridge.cs:27-41,74-83,114-133,209-219`:
    - Yapıcıya isteğe bağlı `CatalogService? catalog = null` parametresi eklenir (`catalog ?? new CatalogService(typeof(Bridge).Assembly.GetName().Version!)`; böylece `Phase10HostTests.cs:48` ve `Phase11HostTests.cs:22` içindeki mevcut `new Bridge(core, null!, ..., paths, updates)` çağrıları kırılmaz).
    - `hello` içinde `PostCatalogStatus()` çağrılır (ağ isteği başlatmadan mevcut `idle` durumunu gönderir).
    - `catalog.list`, `catalog.preview`, `catalog.import` ve `profile.exportShare` mesajlarını işler (`!appSource || !_ready` ise yok sayar).
  - `src/Tactile.Host/MainWindow.cs:20-32`: `Bridge` oluşturulurken veya `Dispose` sırasında `CatalogService` ömrünü yönetir.
  - `tools/Tactile.Tools.Release/Program.cs:19-47`:
    - `private const string CatalogKeyName = "TriggerForge catalog signing";` eklenir (`KeyName = "TriggerForge update signing"` korunur).
    - `["catalog", "keygen"]`, `["catalog", "pubkey"]`, `["catalog", "backup", var file, var password]`, `["catalog", "restore", var file, var password]`, `["catalog", "sign", var repoDir]`, `["catalog", "verify", var repoDir, var publicKey]` komut dalları eklenir.
- **Yasaklar**: `src/Tactile.Core/Model/**`, `src/Tactile.Core/Storage/**`, `src/Tactile.Core/Mapping/**`, `src/Tactile.Core/AppCore.cs`, `src/Tactile.Host/TactileApp.cs` veya `ui/**` altındaki hiçbir dosyaya dokunulmaz.

### İş B (`src/Tactile.Host/Overlay*`, `Osd*`, `OnScreenKeyboard*`, `TactileApp.cs`, `AppSettings.cs`, `Mapping.cs`, `Validation.cs`, `ActionDispatcher.cs`, `Mapper.cs`, `EdgeShortcuts.cs`, `AppCore.cs`, `Tactile.Input*`, `tests/Tactile.Tests/Phase14HostTests.cs`)

- **Oluşturulacak dosyalar**:
  - `src/Tactile.Host/OverlayWindow.cs`:
    - Katmanlı, tıklanamaz, odak çalmayan üst pencere temel sınıfı (`Form`):
      - `CreateParams`: `ExStyle |= 0x00080000 (WS_EX_LAYERED) | 0x00000020 (WS_EX_TRANSPARENT) | 0x00000080 (WS_EX_TOOLWINDOW) | 0x08000000 (WS_EX_NOACTIVATE) | 0x00000008 (WS_EX_TOPMOST)`.
      - `ShowWithoutActivation => true`, `FormBorderStyle = FormBorderStyle.None`, `ShowInTaskbar = false`, `TopMost = true`.
      - `WndProc`: `WM_MOUSEACTIVATE (0x0021)` → `MA_NOACTIVATE (3)`.
      - `ShowUnactivated()` / `PositionAt(Rectangle bounds, byte alpha)`: `SetWindowPos(Handle, HwndTopmost (-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpNoActivate (0x0010) | SwpShowWindow (0x0040))`.
  - `src/Tactile.Host/OsdOverlay.cs`:
    - `public enum OsdEventKind { Profile, Layer, Pause, LowBattery, VirtualController }`
    - `public sealed record OsdNotification(OsdEventKind Kind, string Title, string Detail)`
    - `internal interface IOsdView : IDisposable { void Show(OsdNotification notification, OsdSettings settings); void Hide(); }` (birim testlerinde gerçek WinForms penceresi açmadan `FakeOsdView` ile test edilebilmesi için).
    - `OsdCoordinator`: `AppSettings.Osd.Enabled` kapalıysa hiçbir şey göstermez; açıksa 5 olayı (`Profile`, `Layer`, `Pause`, `LowBattery`, `VirtualController`) biçimlendirir, `OsdCorner` (`TopLeft`, `TopRight`, `BottomLeft`, `BottomRight`), `OsdSize` (`Small`: `240×56`, `10pt`; `Medium`: `300×68`, `12pt`; `Large`: `380×84`, `14pt`), `Opacity` (`30..100` → alpha `77..255`) ve `DurationSeconds` (`1..5` sn) ayarlarıyla `IOsdView`'a iletir ve süre dolduğunda gizler.
    - `OsdOverlayWindow : OverlayWindow, IOsdView`: koyu yarı saydam yuvarlatılmış kart, sol vurgu çizgisi, başlık ve detay satırını çizen gerçek WinForms katmanlı penceresi.
  - `src/Tactile.Host/OnScreenKeyboard.cs`:
    - `internal interface IOnScreenKeyboardView : IDisposable { void Render(OnScreenKeyboardState state); void Hide(); }`
    - `public sealed record OnScreenKeyboardState(bool Visible, Language Language, bool Uppercase, int Row, int Column, IReadOnlyList<IReadOnlyList<string>> Grid, string CurrentKey)`
    - `OnScreenKeyboardController`:
      - Saf (pure), penceresiz test edilebilir durum makinesi + `IInputInjector` + `IOnScreenKeyboardView` sürücüsü.
      - EN ve TR (`ç, ğ, ı, ö, ş, ü` / `Ç, Ğ, İ, Ö, Ş, Ü`) 4 satırlık QWERTY ızgarası.
      - `Toggle(Language language)`: açar/kapatır (`Row = 1, Column = 0, Uppercase = false` ile başlar).
      - `HandleInput(InputState state, TimeSpan now)`:
        - D-pad (`Up`, `Down`, `Left`, `Right`) kenar algılaması ve Sol Stick (`Left` `±0.5` eşik + `180 ms` tekrar) ile `(Row, Column)` gezinmesi.
        - `Cross` basış kenarında `_injector.Text(CurrentKey)`.
        - `Circle` basış kenarında `_injector.Key(Key.Backspace, true)` + `_injector.Key(Key.Backspace, false)`.
        - `Triangle` basış kenarında `_injector.Key(Key.Space, true)` + `_injector.Key(Key.Space, false)`.
        - `Square` basış kenarında `Uppercase = !Uppercase` (TR düzeninde `i ↔ İ`, `ı ↔ I` dönüşümüyle).
        - `Options` basış kenarında `_injector.Key(Key.Enter, true)` + `_injector.Key(Key.Enter, false)`.
        - `PS` basış kenarında klavyeyi kapatır (`Close()`).
    - `OnScreenKeyboardWindow : OverlayWindow, IOnScreenKeyboardView`: alt-ortada QWERTY ızgarasını, seçili hücre vurgusunu ve kontrolcü tuş ipuçlarını (`✕`, `○`, `△`, `□`, `Options`, `PS`) çizen katmanlı pencere.
  - `tests/Tactile.Tests/Phase14HostTests.cs`:
    - OSD 5 olayı, ayarlar (`enabled`, `corner`, `durationSeconds`, `size`, `opacity`), `OverlayWindow` Win32 pencere stilleri (`WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`), ekran klavyesi EN/TR gezinme, `ç ğ ı ö ş ü` büyük/küçük harf yazımı, `Cross`/`Circle`/`Triangle`/`Square`/`Options`/`PS` davranışları, Edge `Fn1 + Triangle` kısayolu ve klavye açıkken eşleme bastırma birim testleri (gerçek pencere göstermeden ve gerçek `SendInput` çağırmadan `FakePlatform` + sahte görünüm ile).
- **Düzenlenecek dosyalar**:
  - `src/Tactile.Core/Model/AppSettings.cs:31-38,66`:
    - `public enum OsdCorner { TopLeft, TopRight, BottomLeft, BottomRight }`
    - `public enum OsdSize { Small, Medium, Large }`
    - `public sealed record OsdSettings { public bool Enabled { get; init; } public OsdCorner Corner { get; init; } = OsdCorner.TopRight; public int DurationSeconds { get; init; } = 2; public OsdSize Size { get; init; } = OsdSize.Medium; public int Opacity { get; init; } = 85; }`
    - `AppSettings` içine `public OsdSettings Osd { get; init; } = new();` eklenir.
  - `src/Tactile.Core/Model/Mapping.cs:12`:
    - `SpecialAction` enum'una `OnScreenKeyboard` eklenir (`Screenshot, GameBar, TaskView, Desktop, ShowApp, OnScreenKeyboard`).
  - `src/Tactile.Core/Storage/Validation.cs:200,235-256`:
    - `AppSettings.Osd` alanlarını normalize eder (`Corner`, `DurationSeconds` `1..5`, `Size`, `Opacity` `30..100`).
  - `src/Tactile.Core/Storage/MappingValidation.cs:544-546`:
    - `ActionKind.Special` doğrulaması `SpecialAction.OnScreenKeyboard` değerini kabul eder.
  - `src/Tactile.Core/Mapping/ActionDispatcher.cs:11-18,198-220`:
    - `IMappingHost` arayüzüne `void ToggleOnScreenKeyboard() { }` eklenir.
    - `Special(SpecialAction special, TimeSpan now)` içinde `SpecialAction.OnScreenKeyboard` geldiğinde `_worker.Post(_host.ToggleOnScreenKeyboard)` çağrılır (`ShowApp` yalnız `SpecialAction.ShowApp` için çağrılır).
  - `src/Tactile.Core/Mapping/Mapper.cs:35,150-206,339-370`:
    - `public string? ActiveLayerName` özelliği (tüm shift katmanları kapalıyken `null`, açıkken en yüksek indeksli etkin katmanın `Name` değeri).
    - `UpdateLayers` içinde `_activeLayers` değiştiğinde `_worker.Post(() => _host.LayerChanged(ActiveLayerName))` bildirimi yapılır.
    - `SuppressButtons(Buttons mask)` yardımcı metodu: ekran klavyesi kapandığında o anda basılı olan tuşları (`PS` vb.) `_suppressedButtons` maskesine ekler.
  - `src/Tactile.Core/Multi/EdgeShortcuts.cs:5-26`:
    - `EdgeShortcut` enum'una `OnScreenKeyboard` eklenir; `Read` içinde `if (PressedWith(Buttons.Fn1, Buttons.Triangle)) return EdgeShortcut.OnScreenKeyboard;` eklenir.
  - `src/Tactile.Input/Platform.cs:5-16,49-68`:
    - `IInputInjector` arayüzüne `void Text(ReadOnlySpan<char> text) { }` (veya `NullPlatform` içinde boş uygulama) eklenir.
  - `src/Tactile.Input.Windows/Native.cs:10-13`:
    - `public const uint KeyeventfUnicode = 0x0004;` eklenir.
  - `src/Tactile.Input.Windows/WindowsInputInjector.cs:44-46`:
    - `public void Text(ReadOnlySpan<char> text)`: her karakter için `KeybdInput { Vk = 0, Scan = ch, Flags = KeyeventfUnicode, ExtraInfo = Signature }` (down) ve `KeyeventfUnicode | KeyeventfKeyUp` (up) `SendInput` çağrısı yapar.
  - `src/Tactile.Core/AppCore.cs:77-79,386-389,748-852,1098-1111`:
    - `public event Action? OnScreenKeyboardToggleRequested;`
    - `public event Action<string?>? ActiveLayerChanged;`
    - `public event Action<InputState, TimeSpan>? OnScreenKeyboardInput;`
    - `private volatile bool _oskOpen;` ve `private Buttons _oskHoldMask;`
    - `public bool OnScreenKeyboardOpen` özelliği: `true` olduğunda `_mapper.ReleaseAll()` çağrılır; `false` olduğunda o an basılı olan `_latestInput?.Buttons` tuşları `_mapper.SuppressButtons(...)` ve `_oskHoldMask` ile bastırılır ki klavyeyi kapatan `PS` tuşu bırakılana kadar normal eşlemeyi veya sanal kontrolcüyü tetiklemesin.
    - `ProcessPrimaryInput`:
      - `shortcut == EdgeShortcut.OnScreenKeyboard` ise `OnScreenKeyboardToggleRequested?.Invoke()` tetiklenir ve o karedeki `Fn1 + Triangle` bastırılır.
      - `_oskOpen` `true` iken `OnScreenKeyboardInput?.Invoke(s, now)` çağrılır; klavyenin kullandığı tuşlar (`Up | Down | Left | Right | Cross | Circle | Triangle | Square | Options | PS`) `s.Buttons` içinden temizlenir ve `s.Left = new Stick(128, 128)` yapılarak `_mapper.OnInput` ve `_emulation?.OnInput`'a yalnızca kalan tuşlar/eksenler iletilir.
  - `src/Tactile.Host/TactileApp.cs:33-90,262-280,323-345,522-565`:
    - `OsdCoordinator` (`OsdOverlayWindow`) ve `OnScreenKeyboardController` (`OnScreenKeyboardWindow`) örneklerini oluşturur, `_core.ActiveProfileChanged`, `_core.ActiveLayerChanged`, `_core.MappingChanged`, `_core.Notice` (`LowBattery`), `_core.Emulation.Changed`, `_core.OnScreenKeyboardToggleRequested` ve `_core.OnScreenKeyboardInput` olaylarına bağlar ve `Exit()` / `Dispose()` içinde temizler.
  - `tests/Tactile.Tests/Support.cs` ve `tests/Tactile.Tests/StoreTests.cs`:
    - `FakeInjector` (`tests/Tactile.Tests/Support.cs`) içine `public List<string> TypedText { get; } = [];` ve `public void Text(ReadOnlySpan<char> text) => TypedText.Add(text.ToString());` eklenir.
    - `StoreTests.cs` içine `AppSettings.Osd` ve `SpecialAction.OnScreenKeyboard` geriye dönük uyumluluk ve sınır kıstırma testleri eklenir.
- **Yasaklar**: `src/Tactile.Core/Catalog/**`, `src/Tactile.Host/Bridge.cs`, `src/Tactile.Host/MainWindow.cs`, `tools/**` veya `ui/**` altındaki hiçbir dosyaya dokunulmaz. Testlerde gerçek `Form.Show()` veya gerçek `SendInput` kesinlikle çağrılmaz.

### İş C (`ui/src/**`)

- **Düzenlenecek dosyalar**:
  - `ui/src/host.ts:82,336-363,450-502`:
    - `Special` tipine `"onScreenKeyboard"` eklenir: `export type Special = "screenshot" | "gameBar" | "taskView" | "desktop" | "showApp" | "onScreenKeyboard";`.
    - `OsdCorner = "topLeft" | "topRight" | "bottomLeft" | "bottomRight"`, `OsdSize = "small" | "medium" | "large"`, `OsdSettings = { enabled: boolean; corner: OsdCorner; durationSeconds: number; size: OsdSize; opacity: number }` tipleri tanımlanır ve `AppSettings` içine `osd: OsdSettings` eklenir.
    - `CatalogController = "dualsense" | "edge" | "any"`, `CatalogError = "network" | "untrustedHost" | "tooLarge" | "badSignature" | "badManifest" | "badFile"`, `CatalogProfileSummary`, `CatalogEntry`, `CatalogStatus` (`type: "catalog.status"`), `CatalogPreview` (`type: "catalog.preview"`), `ExportShare` (`type: "exportShare"`) mesaj tipleri `HostMessage` birleşimine eklenir.
    - `UiMessage` birleşimine `{ type: "catalog.list"; refresh?: boolean }`, `{ type: "catalog.preview"; id: string }`, `{ type: "catalog.import"; id: string }`, `{ type: "profile.exportShare"; id: string }` eklenir.
  - `ui/src/model.ts:166-195,394-396,491-521`:
    - `defaultSettings()` içine `osd: { enabled: false, corner: "topRight", durationSeconds: 2, size: "medium", opacity: 85 }` eklenir.
    - `normalizeSettings()` içinde `s.osd` normalize edilir (`corner`, `durationSeconds` `1..5`, `size`, `opacity` `30..100`).
    - `normalizeMappingAction` içindeki `special` kontrolüne `special === "onScreenKeyboard"` eklenir.
  - `ui/src/mapping.ts:104-110`:
    - `specials` listesine `{ id: "onScreenKeyboard", get label() { return L("On-screen keyboard", "Ekran klavyesi"); }, get hint() { return L("Opens or closes the controller on-screen keyboard", "Kontrolcüyle kullanılan ekran klavyesini açar ya da kapatır"); } }` eklenir.
  - `ui/src/state.tsx:8-35,81-185`:
    - `catalog: CatalogStatus | null` ve `catalogPreview: CatalogPreview | null` durumları `AppState` içine eklenir (`catalog.status` ve `catalog.preview` mesajlarını dinler; dil değiştiğinde `catalog` yüklüyse yerel hata/açıklama metni için yeniler).
  - `ui/src/pages/Profiles.tsx:159-311`:
    - Sayfa başlığı altına **"Profillerim" (`L("My profiles", "Profillerim")`)** ve **"Topluluk" (`L("Community", "Topluluk")`)** sekme seçicisi (`Segmented`) eklenir.
    - **Topluluk sekmesi açıldığında** (`useEffect` yalnızca `tab === "community"` olduğunda çalışır; açılışta veya "Profillerim" sekmesindeyken ağ isteği gönderilmez) `send({ type: "catalog.list" })` çağrılır.
    - Topluluk sekmesinde:
      - Arama kutusu (`L("Search name, game, author or tag…", "Ad, oyun, yazar veya etiket ara…")`), Kontrolcü filtresi (`L("All controllers", "Tüm kontrolcüler")`, `"DualSense"`, `"DualSense Edge"`), Oyun filtresi ve Etiket çipleri, `"Yenile"` (`L("Refresh", "Yenile")`, `send({ type: "catalog.list", refresh: true })`) düğmesi.
      - Ağ/doğrulama hatasında (`catalog.state === "error"`) sekme içinde sessiz uyarı satırı (`catalog.message`) ve `"Tekrar dene"` (`L("Try again", "Tekrar dene")`) düğmesi.
      - Sol listede filtrelenen katalog profilleri (ad, yazar, sürüm, oyun `.exe` listesi, kontrolcü rozeti, etiketler); sağ panelde seçili profilin **Önizleme (`Preview`)** kartı (açıklama, L2/R2 tetik kipleri, sanal kontrolcü kipi, eşleme/katman/akor/makro/gyro/flick stick/telemetri özeti, boyut) ve **"İçe aktar" (`L("Import", "İçe aktar")`)** düğmesi (`send({ type: "catalog.import", id })`).
    - **Profillerim sekmesinde**:
      - Mevcut `"Dışa aktar"` düğmesinin yanına **"Paylaşım için dışa aktar" (`L("Export for sharing", "Paylaşım için dışa aktar")`)** düğmesi (`send({ type: "profile.exportShare", id: selected.id })`) eklenir.
      - `exportShare` mesajı geldiğinde açılan kutuda: (a) güvenlik için `run` eylemlerinin temizlendiği bilgisi, (b) temizlenmiş `<slug>.json` dosyasını indirme düğmesi, (c) `index.json` girdi taslağını panoya kopyalama düğmesi ve (d) `github.com/kaan237/triggerforge-profiles` reposuna Pull Request açma için 3 adımlı kısa yönerge gösterilir.
  - `ui/src/pages/Settings.tsx:130-292`:
    - **"Ekran bildirimi (OSD)" (`L("On-screen display (OSD)", "Ekran bildirimi (OSD)")`)** ayar grubu eklenir:
      - `Toggle`: `L("Show on-screen notifications", "Ekran üstü bildirimleri göster")` (`draft.osd.enabled`), ipucu: `L("Shows profile, layer, pause, low battery, and virtual controller changes on a click-through overlay. No game injection is used; notifications appear in windowed and borderless fullscreen games, and may not be visible in exclusive fullscreen games.", "Profil, katman, duraklatma, düşük pil ve sanal kontrolcü değişimlerini tıklanamaz bir üst katmanda gösterir. Oyuna enjeksiyon yapılmaz; pencereli ve penceresiz tam ekran oyunlarda görünür, gerçek özel tam ekran (exclusive fullscreen) oyunlarda görünmeyebilir.")`.
      - `Segmented` (`corner`): `L("Screen corner", "Ekran köşesi")` — `L("Top left", "Sol üst")`, `L("Top right", "Sağ üst")`, `L("Bottom left", "Sol alt")`, `L("Bottom right", "Sağ alt")`.
      - `NumberField` veya `Slider` (`durationSeconds`): `L("Display duration", "Görünme süresi")` (`1..5` sn).
      - `Segmented` (`size`): `L("Size", "Boyut")` — `L("Small", "Küçük")`, `L("Medium", "Orta")`, `L("Large", "Büyük")`.
      - `NumberField` veya `Slider` (`opacity`): `L("Opacity", "Saydamlık")` (`30..100` `%`).
    - Kontrolcüler bölümündeki Edge kısayolları listesine (`ui/src/pages/Settings.tsx:217-221`) `L("FnL + Triangle · On-screen keyboard", "FnL + Üçgen · Ekran klavyesi")` satırı eklenir.
  - `ui/src/sim.ts:535-795`:
    - 3 örnek topluluk profili (Ghost of Tsushima, EA SPORTS F1 / Forza, Masaüstü Koltuk/Medya) ile `catalog.list`, `catalog.preview`, `catalog.import` (`?catalog=error` URL parametresiyle ağ hatası simülasyonu dahil) ve `profile.exportShare` mesajlarını simüle eder.
- **Yasaklar**: `src/`, `tests/` veya `tools/` altındaki hiçbir dosyaya dokunulmaz. Kullanıcıya görünen her metin `L(en, tr)` (`ui/src/i18n.ts:16-18`) ile hem İngilizce hem Türkçe olmalıdır.

---

## 5. Kabul Ölçütleri ve Test Listesi (Acceptance Criteria and Test List)

### Ölçülebilir kabul ölçütleri

1. `dotnet build` ve `dotnet test` **0 uyarı ve 0 hata** ile geçer; mevcut tüm testler iddiaları değiştirilmeden geçmeye devam eder ve yeni Faz 14 birim testleri eklenir.
2. `ui/` dizininde `npm run build` **0 TypeScript veya Vite hatası** ile tamamlanır.
3. Faz 2–13'ten kalan eski profil ve `settings.json` dosyaları `version: 1`, `osd.enabled: false`, `osd.corner: "topRight"`, `osd.durationSeconds: 2`, `osd.size: "medium"`, `osd.opacity: 85` varsayılanlarıyla yüklenir.
4. Uygulama başlatıldığında veya `hello` mesajı işlendiğinde `CatalogService` **hiçbir HTTP isteği yapmaz** (`Requests.Count == 0`); ağ isteği yalnız `catalog.list` veya `catalog.import` çağrıldığında başlar.
5. `CatalogClient` ve `CatalogManifest.Verify`:
   - `CatalogKey.CatalogPublicKey` ile imzalanmamış, imzası eksik veya 1 biti değiştirilmiş `index.json` verisini `CatalogError.BadSignature` ile reddeder ve hiçbir profil listelemez.
   - `raw.githubusercontent.com` ve `github.com` dışındaki host'lara, `http://` adreslerine, `443` dışı portlara, `UserInfo` içeren URL'lere ve 5'i aşan yönlendirmelere izin vermez (`CatalogError.UntrustedHost`).
   - `512 KB`'ı aşan `index.json`, `1 KB`'ı aşan `index.json.sig` ve `256 KB`'ı veya `entry.Size` değerini aşan profil yanıtlarını `CatalogError.TooLarge` ile reddeder.
   - İndirilen profilin boyutu veya SHA-256 özeti `index.json` girdisiyle uyuşmazsa `CatalogError.BadFile` ile reddeder ve diske hiçbir profil yazmaz.
   - İçe aktarılan katalog profilinde bulunan tüm `ActionKind.Run` eylemleri `Validation.StripRunActions` ile temizlenir ve profile yeni bir `Id` (`Validation.NewId()`) verilir.
6. `tools/Tactile.Tools.Release` içindeki `catalog sign` ve `catalog verify` komutları `index.json` + `index.json.sig` üretir, doğrular ve içinde `ActionKind.Run` bulunan bir profil varsa imzalamayı reddeder.
7. `OverlayWindow` `CreateParams.ExStyle` değeri `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST` bayraklarını içerir ve `ShowWithoutActivation == true` döndürür.
8. `OsdCoordinator` `settings.osd.enabled == false` iken hiçbir bildirim göstermez; `true` iken profil değişimi, katman değişimi, duraklatma/sürdürme, düşük pil ve sanal pad modu değişimi olaylarını doğru metin ve süreyle gösterir.
9. `OnScreenKeyboardController` `SpecialAction.OnScreenKeyboard` ve DualSense Edge `Fn1 + Triangle` ile açılıp kapanır; D-pad ve sol stick ile gezinir; EN ve TR (`ç, ğ, ı, İ, ö, ş, ü`) karakterlerini `IInputInjector.Text` üzerinden yazar; `Circle` ile `Key.Backspace`, `Triangle` ile `Key.Space`, `Square` ile büyük/küçük harf, `Options` ile `Key.Enter` gönderir; `PS` ile kapanır; açıkken bu tuşların ve sol stick'in normal profil eşlemesini ve sanal kontrolcü iletimini bastırır ve açılış/kapanışta hiçbir tuş takılı kalmaz.

### Zorunlu birim testleri (`tests/Tactile.Tests/`)

- `tests/Tactile.Tests/Catalog/CatalogTests.cs` *(yeni, İş A)*:
  - `Does_not_touch_network_until_catalog_is_requested`: `CatalogService` oluşturulduğunda `Status.State == CatalogState.Idle` ve sahte `HttpMessageHandler` istek listesinin boş olduğunu doğrular.
  - `Accepts_valid_signed_index_and_imports_profile_with_new_id_and_stripped_run_actions`: geçerli P-256 imzalı `index.json` ve içinde `ActionKind.Run` bulunan bir profil JSON'u indirildiğinde `CatalogState.Ready` olduğunu, `ImportAsync` çağrıldığında SHA-256 doğrulamasının geçtiğini, tüm `run` eylemlerinin silindiğini (`removed > 0`), yeni bir `Id` atandığını ve `ProfileStore` içine kaydedildiğini doğrular.
  - `Rejects_tampered_index_or_signature_before_parsing`: `index.json` veya `index.json.sig` içinde 1 bayt değiştirildiğinde veya `.sig` `404` döndüğünde `CatalogError.BadSignature` verdiğini ve `Status.Profiles` listesinin boş kaldığını doğrular.
  - `Rejects_untrusted_hosts_non_https_and_redirect_loops`: `https://example.com/index.json`, `http://raw.githubusercontent.com/index.json`, `https://raw.githubusercontent.com:8443/index.json`, `https://user:pass@github.com/index.json` adreslerini ve güvenilmeyen yönlendirmeleri `CatalogError.UntrustedHost` ile, 5'ten fazla yönlendirmeyi `CatalogError.Network` ile reddettiğini doğrular.
  - `Rejects_oversized_index_signature_and_profile_payloads`: `512 KB + 1` bayt `index.json`, `1024 + 1` bayt `.sig` ve `256 KB + 1` bayt profil yanıtlarını (hem `Content-Length` başlığıyla hem de uzunluğu bilinmeyen akışla) `CatalogError.TooLarge` ile reddettiğini doğrular.
  - `Rejects_path_traversal_and_hash_or_size_mismatch_on_profile_download`: `path` alanı `../secret.json` veya `profiles\bad.json` olan imzalı `index.json`'u `CatalogError.BadManifest` ile; SHA-256 özeti veya boyutu `index.json` ile uyuşmayan profil dosyasını `CatalogError.BadFile` ile reddettiğini ve `ProfileStore`'a hiçbir şey eklemediğini doğrular.
  - `Share_export_strips_run_actions_and_computes_exact_sha256_and_size`: `CatalogShareExporter.BuildSharePackage` çıktısında `run` eylemlerinin temizlendiğini ve üretilen `entryJson` içindeki `sha256` ve `size` değerlerinin dışa aktarılan `json` baytlarıyla bire bir eşleştiğini doğrular.
- `tests/Tactile.Tests/Phase14HostTests.cs` *(yeni, İş B)*:
  - `Overlay_window_uses_layered_transparent_noactivate_toolwindow_styles`: pencereyi ekranda göstermeden (`Show()` çağırmadan) `OverlayWindow` `CreateParams.ExStyle` değerinin `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST` bitlerini içerdiğini ve `ShowWithoutActivation` özelliğinin `true` olduğunu doğrular.
  - `Osd_ stays_silent_when_disabled_and_formats_all_five_events_when_enabled`: `FakeOsdView` ve `ManualTime` ile `osd.enabled = false` iken hiçbir bildirimin tetiklenmediğini; `osd.enabled = true` iken profil değişimi, katman değişimi (Layer 1 açılış ve Base dönüş), eşleme duraklatma/sürdürme, düşük pil (`%20`) ve sanal kontrolcü kipi değişimi olaylarının doğru başlık/metinle gösterildiğini ve `durationSeconds` sonunda gizlendiğini doğrular.
  - `On_screen_keyboard_navigates_and_types_english_and_turkish_characters`: `FakePlatform` ve `ManualTime` ile D-pad ve sol stick gezinmesini, `Square` ile büyük/küçük harf geçişini, TR düzeninde `ç, ğ, ı, ö, ş, ü` ve büyük harfte `Ç, Ğ, İ, I, Ö, Ş, Ü` karakterlerinin `FakeInjector.TypedText` içine yazıldığını, `Circle` → `Key.Backspace`, `Triangle` → `Key.Space`, `Options` → `Key.Enter` ve `PS` → kapanma davranışlarını doğrular.
  - `On_screen_keyboard_opens_via_special_action_and_edge_fn1_triangle_and_suppresses_mapped_buttons`: bir tuşa `SpecialAction.OnScreenKeyboard` atandığında veya DualSense Edge üzerinde `Fn1 + Triangle` basıldığında klavyenin açıldığını; klavye açıkken basılı tutulan eşlenmiş bir tuşun hemen bırakıldığını (`ReleaseAll`), `Cross`/`Circle`/`Triangle`/`Square`/`D-pad`/`Options`/`PS` ve sol stick girdilerinin profildeki tuş eşlemelerini tetiklemediğini; `PS` ile klavye kapatıldığında `PS` tuşu bırakılana kadar `PS` katmanının veya eyleminin ateşlenmediğini doğrular.
- `tests/Tactile.Tests/StoreTests.cs` *(İş B)*:
  - `OsdSettings` (`enabled`, `corner`, `durationSeconds` `1..5`, `size`, `opacity` `30..100`) ve `SpecialAction.OnScreenKeyboard` normalizasyonu ve Faz 13 ayar/profil JSON dosyalarıyla geriye dönük uyumluluk testi.

---

## 6. Güvenlik Kuralları (Safety Rules — Every Job)

- **Testlerde gerçek ağ erişimi YOK**: Birim testleri hiçbir zaman gerçek GitHub veya internet adreslerine bağlanmaz. Tüm katalog testleri sahte `HttpMessageHandler` ve bellek içi geçici ECDSA P-256 anahtarları kullanır.
- **Testlerde gerçek pencere veya `SendInput` YOK**: Hiçbir zaman gerçek bir kontrolcüye çıktı gönderen, işletim sistemine klavye/fare girdisi (`SendInput`) enjekte eden, ekranda gerçek WinForms/OSD/Ekran klavyesi penceresi açan (`Form.Show`), genel kısayol tuşu (`RegisterHotKey`) kaydeden veya sürücü kuran kod çalıştırmaz. Uygulama yürütülebilir dosyası (`TriggerForge.exe`) geliştirme sırasında başlatılmaz. Tüm testler `FakePlatform`, `FakeForeground`, `FakeOsdView`, `FakeOnScreenKeyboardView` ve `ManualTime` kullanır.
- **Oyuna enjeksiyon YOK**: OSD ve ekran klavyesi yalnızca TriggerForge sürecinin kendi Win32 katmanlı pencereleridir; başka hiçbir sürece bellek/DLL/grafik müdahalesi yapılmaz.
- **İzin verilen komutlar**: Yalnızca `dotnet build`, `dotnet test` ve (`ui/` dizininde) `npm run build`.
- **Commit YOK**: `git commit`, `git push` veya git geçmişini değiştiren hiçbir komut çalıştırılmaz.
- **Takılı tuş önleme (Stuck-key prevention)**: Ekran klavyesinin açılması/kapanması, duraklatma geçişleri (`ui`, `shortcut`, `fullscreen`), katman değişimi, profil değişimi veya kontrolcü bağlantısının kopması durumunda basılı tüm sanal tuşlar, fare tuşları, makrolar, kilitli tuşlar (toggle) ve turbo çıktıları anında bırakılmalıdır.

---

## 7. KH İşleri ve Donanım Test Listesi (KH Tasks and Hardware Test Checklist)

### A. KH'nin katalog reposunu açma ve ilk imzalı `index.json`'u yayınlama adımları

Kod birleştirildikten sonra katalog anahtarını üretmek, açık anahtarı uygulamaya sabitlemek ve `kaan237/triggerforge-profiles` reposunda ilk imzalı kataloğu yayınlamak için aşağıdaki adımlar sırasıyla uygulanır:

1. **Katalog imzalama anahtarını üretme ve yedekleme (Windows CNG — mevcut kullanıcı)**:
   ```powershell
   dotnet run --project tools\Tactile.Tools.Release -- catalog keygen
   dotnet run --project tools\Tactile.Tools.Release -- catalog backup secrets\catalog-signing.p8 "<güçlü-parola>"
   ```
   - `catalog keygen` çıktısındaki base64 açık anahtar (`MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE...`) kopyalanır ve `src/Tactile.Core/Catalog/CatalogKey.cs` içindeki `CatalogPublicKey` sabitine yapıştırılır (`UpdateKey.ManifestPublicKey` değerine dokunulmaz).
2. **`kaan237/triggerforge-profiles` public reposunu oluşturma ve klonlama**:
   ```powershell
   gh repo create kaan237/triggerforge-profiles --public --description "Community profiles for TriggerForge" --clone
   mkdir triggerforge-profiles\profiles
   ```
3. **İlk topluluk profillerini ve `index.json` dosyasını hazırlama**:
   - TriggerForge içinde veya elde hazırlanan başlangıç profilleri ("Paylaşım için dışa aktar" ile `run` eylemlerinden arındırılmış olarak) `triggerforge-profiles\profiles\<slug>.json` altına konulur.
   - `triggerforge-profiles\index.json` içine `schema: 1`, `updated` ve `profiles` dizisi (`id`, `name`, `games`, `controller`, `tags`, `author`, `version`, `path`, `description`) yazılır.
4. **`index.json` dosyasını imzalama ve doğrulama**:
   ```powershell
   dotnet run --project tools\Tactile.Tools.Release -- catalog sign triggerforge-profiles
   dotnet run --project tools\Tactile.Tools.Release -- catalog verify triggerforge-profiles "<CatalogPublicKey-base64>"
   ```
   - `catalog sign` her `profiles/<slug>.json` dosyasının `size`, `sha256` ve `summary` alanlarını `index.json` içine yazar, hiçbir profilde `run` eylemi olmadığını denetler ve `triggerforge-profiles\index.json.sig` dosyasını oluşturur.
   - `catalog verify` çıktısının `"Catalog signature and all profile entries are valid."` (çıkış kodu `0`) olduğu görülür.
5. **Katalog reposunu yayınlama**:
   ```powershell
   git -C triggerforge-profiles add index.json index.json.sig profiles/
   git -C triggerforge-profiles commit -m "Initial signed community profile catalog"
   git -C triggerforge-profiles branch -M main
   git -C triggerforge-profiles push -u origin main
   ```

### B. Donanım test listesi (KH — Faz sonu, ~15 dk)

1. **Açılışta ağ isteği olmaması ve Topluluk sekmesi**: TriggerForge'u başlat → Profiller sayfasında "Profillerim" sekmesindeyken ağ isteği yapılmadığını, **"Topluluk"** sekmesine tıklayınca `kaan237/triggerforge-profiles` üzerinden imzalı `index.json`'un yüklendiğini doğrula.
2. **Arama, filtreleme, önizleme ve içe aktarma**: Topluluk sekmesinde oyun/kontrolcü (`DualSense` / `DualSense Edge`) ve etiket filtrelerini dene; bir profile tıklayıp sağdaki özellik özetini (tetikler, eşleme, sanal kontrolcü) incele; **"İçe aktar"** düğmesine bas → profilin yeni bir kimlikle "Profillerim" listesine eklendiğini ve bildirim çıktığını doğrula.
3. **Paylaşım için dışa aktarma (`run` temizleme)**: İçinde bir "Program çalıştır" (`run`) eylemi bulunan yerel bir profil seç → **"Paylaşım için dışa aktar"** düğmesine bas → indirilen JSON'da `run` eyleminin `"none"` olarak temizlendiğini ve hazır `index.json` girdi taslağının (`sha256` + `size`) gösterildiğini doğrula.
4. **OSD bildirimleri ve köşe/boyut/saydamlık**: Ayarlar → **Ekran bildirimi (OSD)** bölümünden OSD'yi aç (`Sağ üst`, `2 sn`, `Orta`, `%85`) → pencereli veya penceresiz tam ekran (*borderless*) bir uygulama/oyun açıkken:
   - Profil değiştir (`Fn1 + R1` veya tepsiden),
   - Shift katmanı tuşuna (`PS`) bas/bırak,
   - Eşlemeyi duraklat/sürdür,
   - Sanal kontrolcü modunu (`Xbox 360` ↔ `DualSense` ↔ `Kapalı`) değiştir
   → her olayda sağ üstte odak çalmayan ve fare tıklamalarını alt pencereye geçiren OSD kartının görünüp 2 saniye sonra kaybolduğunu doğrula.
5. **Ekran klavyesi (EN ve TR `ç ğ ı ö ş ü`)**: Bir tuşa `Sistem → Ekran klavyesi` ata (veya DualSense Edge'de `Fn1 + Triangle` bas) → Not Defteri veya tarayıcı arama kutusu odaktayken ekran klavyesini aç:
   - Odağın Not Defteri'nde kaldığını,
   - D-pad ve sol stick ile harfler arasında gezinildiğini,
   - `✕` ile karakter yazıldığını, `□` ile büyük/küçük harf değiştirildiğini, Türkçe dilinde `ç, ğ, ı, ö, ş, ü` ve `Ç, Ğ, İ, I, Ö, Ş, Ü` harflerinin doğru yazıldığını,
   - `△` ile boşluk, `○` ile silme (`Backspace`), `Options` ile satır başı (`Enter`) yapıldığını ve `PS` tuşuna basınca klavyenin kapandığını doğrula.
6. **Ekran klavyesi açıkken eşleme bastırma**: Profilde `✕`, `○` veya sol stick'e klavye/fare eşlemesi ata → ekran klavyesini açıp metin yaz → klavye açıkken profildeki normal eşlemelerin tetiklenmediğini, `PS` ile kapattıktan sonra normal eşlemenin takılı tuş bırakmadan kaldığı yerden çalıştığını doğrula.

---

## 8. Riskler ve Geri Alma (Risks and Rollback)

- **Risk — Dışarıdan gelen katalog verisiyle zararlı komut çalıştırma veya sahte yönlendirme**:
  - *Önlem*: `CatalogClient` yalnızca `https://raw.githubusercontent.com:443` ve `https://github.com:443` adreslerine izin verir (`AllowAutoRedirect = false`, en fazla 5 doğrulanan yönlendirme). `index.json` ayrıştırılmadan önce `CatalogKey.CatalogPublicKey` ile ECDSA P-256 imzası doğrulanır; her profil dosyası `index.json` içindeki `size` (`<= 256 KB`) ve sabit zamanlı `sha256` kontrolünden geçer, ardından `ProfileStore.Import` içinde `Validation.Normalize` ve `Validation.StripRunActions` çalıştırılarak tüm `run` eylemleri kayıtsız şartsız silinir. Ayrıca `tools/Tactile.Tools.Release catalog sign` komutu içinde `run` eylemi bulunan bir profili imzalamayı baştan reddeder.
- **Risk — OSD veya ekran klavyesinin oyun odağını çalması ya da oyunu masaüstüne küçültmesi**:
  - *Önlem*: `OverlayWindow` `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST` bayraklarıyla, `ShowWithoutActivation => true` ve `SetWindowPos(..., SWP_NOACTIVATE)` kullanılarak gösterilir ve `WM_MOUSEACTIVATE` mesajına `MA_NOACTIVATE` döndürür; ön plandaki pencerenin odağı hiç değişmez.
- **Risk — Anti-cheat yazılımlarının OSD'yi hile/enjeksiyon sanması**:
  - *Önlem*: Oyun süreçlerine hiçbir DLL enjekte edilmez, grafik API kancası (hook) kurulmaz ve oyun belleğine dokunulmaz; pencere tamamen TriggerForge sürecine ait standart bir masaüstü üst penceresidir. Gerçek *exclusive fullscreen* oyunlarda görünmeyebileceği kullanıcıya açıkça belgelenir.
- **Risk — Ekran klavyesini kapatan `PS` tuşunun profildeki `PS` katmanını veya eylemini tetiklemesi**:
  - *Önlem*: Ekran klavyesi kapanırken o anda fiziksel olarak basılı olan tüm tuşlar (`PS` dahil) `Mapper.SuppressButtons` ve `AppCore._oskHoldMask` içine alınır ve kullanıcı tuşu fiziksel olarak bırakana kadar `Mapper` ile `EmulationService` tarafından yok sayılır.
- **Geri alma anahtarları (Rollback switches)**:
  - `AppSettings.Osd.Enabled` varsayılan olarak `false` olduğundan kullanıcı açmadıkça OSD penceresi hiç gösterilmez.
  - Topluluk kataloğu açılışta hiçbir ağ isteği yapmaz; yalnız kullanıcı Profiller → "Topluluk" sekmesini açtığında çalışır.
  - Bir profilde `SpecialAction.OnScreenKeyboard` atanmadığında ve ekran klavyesi açılmadığında `Mapper` ve `AppCore` girdi yolu Faz 13 ile bire bir aynı şekilde çalışır.

---

## 9. Denetim kararları (bağlayıcı — yukarıdaki metinle çelişirse bu bölüm geçerlidir)

Yukarıdaki satır numaraları eski kod içindir; yerleri adla bulun.

**İş bölümü**
- K1. Sıra: **A ‖ B1 ‖ C**, sonra **B2**. B1 = çekirdek (Core/Input/Input.Windows/Mapping/Storage/AppCore ve testleri). B2 = `src/Tactile.Host/**` pencereleri, `TactileApp.cs` ve `Phase14HostTests.cs`. `OnScreenKeyboardModel` (ızgara, seçim, harf kipi, girdi durum makinesi) saf mantıktır: `src/Tactile.Core/Mapping/OnScreenKeyboard.cs`, B1'e aittir.
- K2. `tools/Tactile.Tools.Release/Tactile.Tools.Release.csproj` İş A'nındır ve `Tactile.Core` referansı alır. İmzalama ve doğrulama mantığı `src/Tactile.Core/Catalog/CatalogSigner.cs` içindedir (anahtar nesnesi parametre); CLI yalnız çağırır, testler doğrudan bu sınıfı dener.
- K3. `FakeInjector`/`FakePlatform` `tests/Tactile.Tests/MappingSupport.cs` içindedir (B1). `Directory.Build.props` sürümünü (1.5.0) B2 yazar.

**Katalog**
- K4. `catalog sign`/`verify`: önce her `path` §2 deseniyle, `Path.GetFullPath` kök sınırıyla ve reparse point reddiyle denetlenir. Ham profil JSON'unda herhangi bir yerde `kind` değeri `run` olan (büyük/küçük harf fark etmez) düğüm varsa reddedilir; bu denetim `Normalize`'dan önce ham JSON üzerinde yapılır. İstemci de içe aktarmadan önce aynı ham denetimi yapar ve `BadFile` döner; `StripRunActions` yine çalışır.
- K5. `index.json` köküne `revision` (pozitif tam sayı) eklenir. `catalog sign` her çalışmada 1 artırır. İstemci en son gördüğü imzalı revizyonu veri klasöründe saklar (`AppPaths` altında küçük bir dosya; bozuksa 0 sayılır); daha küçük revizyon `CatalogError.Outdated` ile reddedilir. Hata listesine `outdated` eklenir (UI dahil). Aynı revizyon kabul edilir.
- K6. Host allowlist yalnız `raw.githubusercontent.com`. Yönlendirme sınırı aşılırsa `Network`. İndirilen uzunluk `entry.Size`'a tam eşit olmalıdır.
- K7. `summary` zorunludur; `catalog.preview` hiç ağa çıkmaz (UI önizlemeyi doğrudan listedeki girdiden çizebilir, mesaj yine de vardır).
- K8. `CatalogPublicKey` boş veya geçersizse kurucular hata fırlatmaz; istekte ağa çıkılmadan `BadSignature` döner.
- K9. Tel biçimi (`catalog.status.profiles[]`): `{ id, name, games, controller, tags, author, version, size, description: { en, tr }, summary }`. `sha256`, `path` UI'ya gitmez. Dil seçimini UI yapar. Enum'lar camelCase (`dualsense` ve `edge`, `any` aynen).
- K10. `catalog.openRepo` (UI → Host, yüksüz): sabit `CatalogKey.RepositoryUrl` varsayılan tarayıcıda açılır. UI'da düz dış bağlantı kullanılmaz.
- K11. `profile.exportShare` sade kalır: temiz JSON, dosya adı, girdi taslağı, silinen `run` sayısı.
- K12. Katalog hatası `PostError`/balon üretmez. `CatalogService` ömrü `MainWindow`/`Bridge` kapanışında biter; süren istek iptal edilir. Aynı anda tek işlem.

**Ekran klavyesi**
- K13. `EdgeShortcuts.Resolve` `OnScreenKeyboard` için `null` döner; ikincil kontrolcüde bu kısayol hiçbir şey yapmaz. Enum değeri profil hesabını bozmaz (5+ profilli test).
- K14. Klavye açıkken `_mapper`'a ve `EmulationService`'e **hiçbir** kullanıcı girdisi gitmez: ikisine de nötr durum (tuşsuz, stickler ortada, tetikler 0, dokunma yok, gyro 0; pil ve benzeri alanlar korunur) verilir, `_pauseShortcut` atlanır. Gyro/hareket sunucusu (DSU) çıktısı da nötr olur. Açılışta `_mapper.ReleaseAll()`. `InputState` değişmezdir (`s with { ... }`).
- K15. Kapanışta o an basılı tuşlar `_oskHoldMask`'e alınır; her karede `_oskHoldMask &= s.Buttons`, maskeli tuşlar hem mapper'a hem emülasyona gitmez.
- K16. Kapatma yolları: `PS`, Edge `Fn1 + Triangle`, kontrolcünün kopması ya da birincilin değişmesi, eşlemenin ayarlardan kapatılması, uygulama çıkışı. Klavyeyi açan eylem tuşu açıkken çalışmaz.
- K17. `Backspace`/`Space`/`Enter` tek çağrıda bas + bırak (0 ms); takılı tuş olmaz. `Circle` basılı tutulursa 400 ms sonra 60 ms aralıkla tekrarlar.
- K18. Durum makinesi kilitlidir, HID okuma iş parçacığında çalışır; görünüm yalnız durum değişince UI iş parçacığına birleştirilmiş (`coalesced`) olarak gönderilir. Kapalıyken kare başına ayırma ve kilit yoktur.

**OSD ve pencere**
- K19. Bütün OSD olayları ve gizleme zamanlayıcısı UI iş parçacığında çalışır (`TactileApp.Ui`). `OsdCoordinator` ilk değerleri sessizce kaydeder; yalnız gerçek `Profile.Id`, katman, `Paused` ve birincil `EmulationMode` geçişinde bildirir. `ReleaseAllLocked` katmanları sıfırladığında önceki değer 0 değilse katman bildirimi gider; profil değişimiyle aynı anda gelen katman bildirimi bastırılır.
- K20. `SWP_NOZORDER` kullanılmaz (`HWND_TOPMOST` ile çelişir). Alfa `SetLayeredWindowAttributes(LWA_ALPHA)` ile her zaman açıkça verilir (%100'de de). Yuvarlak köşe `Region` ya da DWM ile.
- K21. Monitör: ön plandaki pencerenin monitörü (bulunamazsa birincil). Ölçüler o monitörün DPI'ı ile ölçeklenir; yazı boyutu da piksel olarak aynı oranla hesaplanır.
- K22. `Exit()`/`Dispose` içinde olay abonelikleri kaldırılır, pencere, zamanlayıcı ve GDI+ nesneleri bırakılır.

**Ek zorunlu testler**: `Bridge` katalog mesajları ve `!_ready` reddi; imzalama `run` reddi ve yol kaçışı reddi; revizyon düşürme reddi; klavye açıkken emülasyon çıkışı nötr; `KEYEVENTF_UNICODE` kayıt üretimi (saf fonksiyon olarak); OSD süre sıfırlama, köşe/DPI yerleşimi (saf fonksiyon); kopmada klavyenin kapanması; `WM_MOUSEACTIVATE` sonucu.

<p align="center"><img src="assets/icon/triggerforge-256.png" width="128" height="128" alt="TriggerForge logo"></p>

# TriggerForge

TriggerForge is a **DualSense PC control center** for Windows. The interface is
available in English and Turkish, with English selected for new installations.

### Features

- Configure adaptive triggers, the lightbar, player LEDs and vibration.
- Tune stick and trigger response, calibrate the gyro and create mappings and macros.
- Save profiles and switch between them automatically by game.
- Import signed community profiles and share your own.
- Open radial menus of up to 8 actions with a button and a stick.
- See profile, layer, battery and mode changes on screen, and type with an on-screen keyboard.
- Use Bluetooth audio haptics, send sound to the controller speaker or create a virtual controller.
- Control effects from compatible integrations: DSX commands over UDP and telemetry from Forza, EA SPORTS F1, Assetto Corsa, ACC, and BeamNG.drive.

### Requirements

- Windows 10 version 1809 or later, or Windows 11, on an x64 PC.
- A DualSense, DualSense Edge or DualShock 4 connected by Bluetooth or USB. Bluetooth is required for Bluetooth audio features. Adaptive triggers, haptics and player LEDs need a DualSense. DualShock 4 support has not been tested on hardware yet.
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).
- Virtual controller mode requires usbip-win2 and HidHide. Install them from the app when needed.

### Install, run portable or uninstall

1. Run `TriggerForge-<version>-setup.exe` to install for the PC. Setup needs administrator permission. The desktop shortcut is optional; "Start with Windows" is in the app's Settings.
2. For portable use, extract `TriggerForge-<version>-portable.zip` to a writable folder and run `TriggerForge.exe`. Keep `portable.txt` beside it. Settings, profiles and logs stay in the adjacent `data` folder.
3. To uninstall, close TriggerForge from its tray menu and remove it in **Windows Settings → Apps**, using the Windows account that ran the app. Settings, logs and drivers are kept by default, including in silent mode. To remove a portable copy, restore the controller and then delete the extracted folder.

### Restore the controller

Use **Restore controller** in the tray or Settings. If the window cannot open,
use **TriggerForge — Restore controller** from Start, or run
`TriggerForge.exe --restore` from its folder. Recovery removes the virtual
controller, makes the physical controller visible again and turns off emulation.

The uninstaller attempts recovery before removing the app. If recovery fails,
use the Start shortcut and retry uninstalling.

### Logs

In **Settings → Application**, open the log folder or export a ZIP for
troubleshooting. Installed copies keep logs in `%LOCALAPPDATA%\TriggerForge\logs`.
The export includes logs, settings and system information. Review it before sharing.

### Build from source

Use the .NET 10 SDK, Node.js 24 with npm, and Windows PowerShell or PowerShell
on Windows. Inno Setup 6.3 or newer in the 6.x series builds the optional installer.

```powershell
.\build\publish.ps1 -NoSign
```

The script builds the UI and publishes the self-contained x64 app to
`artifacts/app`. It writes the portable ZIP and, when Inno Setup is available,
the installer to `artifacts/`. It prints the SHA-256 hashes of the packages.
For a signed release, omit `-NoSign` and use `-Thumbprint` if you need to select a
different code-signing certificate in `Cert:\CurrentUser\My`.

TriggerForge is free software under the [GNU General Public License v3.0 or later](LICENSE): you may use, study, change and share it, and anything you distribute that is based on it must be released under the same license with its source code and keep the original copyright notice. Bundled components keep their own licenses, listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). The TriggerForge name and logo are not covered by the GPL and stay reserved to the author: unchanged copies of an official release may be shared under that name, but a modified version must use its own name and logo.

## Türkçe

TriggerForge, Windows için bir **DualSense PC kontrol merkezi**. Arayüz İngilizce
ve Türkçe kullanılabilir. Yeni kurulumlar İngilizce başlar.

### Özellikler

- Adaptif tetikleri, ışık çubuğunu, oyuncu LED'lerini ve titreşimi ayarlayın.
- Analog çubuk ve tetik tepkisini ayarlayın, jiroskobu kalibre edin, tuş eşlemeleri ve makrolar oluşturun.
- Profilleri kaydedin ve oyuna göre otomatik geçiş yapın.
- İmzalı topluluk profillerini içe aktarın ve kendi profilinizi paylaşın.
- Bir tuş ve stick ile 8 eyleme kadar daire menüler açın.
- Profil, katman, pil ve kip değişimlerini ekranda görün; ekran klavyesiyle yazın.
- Bluetooth üzerinden ses tabanlı dokunsal geri bildirim kullanın, kontrolcü hoparlörüne ses gönderin veya sanal kontrolcü oluşturun.
- DSX komutları ve Forza, EA SPORTS F1, Assetto Corsa, ACC ve BeamNG.drive telemetrisiyle efektleri yönetin.

### Gereksinimler

- x64 bilgisayarda Windows 10 sürüm 1809 veya üzeri ya da Windows 11.
- Bluetooth veya USB ile bağlı bir DualSense, DualSense Edge ya da DualShock 4. Bluetooth ses özellikleri için Bluetooth bağlantısı gerekir. Uyarlanabilir tetikler, haptik ve oyuncu LED'leri DualSense ister. DualShock 4 desteği henüz donanımda denenmedi.
- [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/).
- Sanal kontrolcü modu usbip-win2 ve HidHide gerektirir. Gerektiğinde sürücüleri uygulama içinden kurun.

### Kurulum, taşınabilir kullanım ve kaldırma

1. Bilgisayara kurmak için `TriggerForge-<sürüm>-setup.exe` dosyasını çalıştırın. Kurulum yönetici izni ister. Masaüstü kısayolu isteğe bağlıdır; "Windows ile başlat" uygulamanın Ayarlar sayfasındadır.
2. Taşınabilir kullanım için `TriggerForge-<sürüm>-portable.zip` arşivini yazılabilir bir klasöre çıkarın ve `TriggerForge.exe` dosyasını çalıştırın. `portable.txt` dosyasını yanında tutun. Ayarlar, profiller ve günlükler yanındaki `data` klasöründe saklanır.
3. Kaldırmak için TriggerForge'u bildirim alanından kapatın ve uygulamayı kullandığınız Windows hesabıyla **Windows Ayarları → Uygulamalar** bölümünden kaldırın. Sessiz kaldırma dahil, varsayılan olarak ayarlar, günlükler ve sürücüler saklanır. Taşınabilir kopyayı kaldırmak için kontrolcüyü geri getirin, ardından arşivi çıkardığınız klasörü silin.

### Kontrolcüyü geri getirme

Bildirim alanından veya Ayarlar'dan **Kontrolcüyü geri getir** seçeneğini
kullanın. Pencere açılmıyorsa Başlat menüsündeki
**TriggerForge — Kontrolcüyü geri getir** kısayolunu kullanın veya dosyanın
bulunduğu klasörde `TriggerForge.exe --restore` komutunu çalıştırın. Bu işlem
sanal kontrolcüyü kaldırır, fiziksel kontrolcüyü görünür yapar ve emülasyonu kapatır.

Kaldırıcı, uygulamayı silmeden önce kontrolcüyü geri getirmeyi dener. İşlem
başarısız olursa Başlat menüsündeki kısayolu kullanıp kaldırmayı tekrar deneyin.

### Günlükler

**Ayarlar → Uygulama** bölümünden günlük klasörünü açabilir veya sorun incelemek
için bir ZIP dışa aktarabilirsiniz. Kurulu uygulamanın günlükleri
`%LOCALAPPDATA%\TriggerForge\logs` klasöründedir. ZIP dosyası günlükleri, ayarları
ve sistem bilgilerini içerir. Paylaşmadan önce içeriğini inceleyin.

### Kaynak koddan paketleme

Windows üzerinde .NET 10 SDK, npm içeren Node.js 24 ve PowerShell gerekir.
Kurulum paketi için Inno Setup 6 serisinin 6.3 veya daha yeni bir sürümünü kullanın.

```powershell
.\build\publish.ps1 -NoSign
```

Betik arayüzü derler ve kendi .NET çalışma zamanını içeren x64 uygulamayı
`artifacts/app` klasörüne çıkarır. Taşınabilir ZIP'i ve Inno Setup varsa kurulum
paketini `artifacts/` klasörüne yazar. Paketlerin SHA-256 özetlerini gösterir.
İmzalı paket için `-NoSign` seçeneğini kaldırın. `Cert:\CurrentUser\My`
içinde başka bir imzalama sertifikası seçmek için `-Thumbprint` kullanın.

TriggerForge, [GNU Genel Kamu Lisansı v3.0 veya sonrası](LICENSE) ile özgür yazılımdır: kullanabilir, inceleyebilir, değiştirebilir ve paylaşabilirsiniz; buna dayanarak dağıttığınız her şey aynı lisansla, kaynak koduyla ve özgün telif bildirimi korunarak yayımlanmalıdır. Paketlenen bileşenler kendi lisanslarını korur; liste [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt) dosyasındadır. TriggerForge adı ve logosu GPL kapsamında değildir ve yazarına aittir: resmî bir sürümün değiştirilmemiş kopyaları bu adla paylaşılabilir, değiştirilmiş bir sürüm ise kendi adını ve logosunu kullanmalıdır.

# DiskDoktor

Windows, Linux ve macOS'ta çalışan disk temizleyici. Silinebilecek her şeyi bulur ve risk düzeyiyle sunar;
siz onaylamadan hiçbir şey silinmez.

## Neleri bulur

- **Sistem:** geçici dosyalar, Windows Update ve Teslim İyileştirme önbelleği, Windows.old ve yükseltme artıkları,
  bileşen deposu (DISM), hazırda bekletme dosyası, geri yükleme noktaları, günlükler, çökme dökümleri, Prefetch;
  Linux'ta journal, eski günlükler, /var/crash; macOS'ta sistem önbellekleri ve günlükleri.
- **Paket yöneticileri:** APT, DNF, Pacman önbelleği, artık paketler ve eski çekirdekler, eski Snap sürümleri,
  kullanılmayan Flatpak ortamları, Homebrew.
- **Oyun ve grafik:** DirectX/NVIDIA/AMD/Intel/Mesa gölgelendirici önbellekleri, Steam gölgelendirici önbelleği ve yarım
  indirmeler, Unity ve Unreal önbellekleri.
- **Tarayıcılar ve uygulamalar:** Chrome, Edge, Firefox, Brave, Opera, Vivaldi, Safari; Discord, Slack, Teams, Spotify,
  ~/.cache ve ~/Library/Caches.
- **Geliştirici:** npm, pnpm, Yarn, pip/uv, NuGet, Gradle, Maven, Cargo, Go, JetBrains, VS Code, Visual Studio, Xcode,
  Docker; ayrıca projelerdeki `node_modules`, `bin/obj`, `target`, `build`, Unity `Library`, Unreal `Intermediate`, `.godot`.
- **Kişisel dosyalar** (hepsi seçilmeden gelir, tek tek seçilir): 500 MB üstü büyük dosyalar, yinelenen dosyalar
  (en eski kopya korunur), İndirilenler'deki kurulum dosyaları ve 90 günden eski indirilenler, Geri Dönüşüm Kutusu/Çöp.

Yönetici gerektiren kalemlerde temizlik sırasında **bir kez** izin istenir (Windows'ta UAC, Linux'ta parola penceresi,
macOS'ta yönetici parolası). Windows'ta sistem klasörlerinin tam boyutunu görmek için uygulamadaki
"Yönetici olarak yeniden başlat" düğmesini kullanın.

- Arayüz: Avalonia 11 (.NET 8), açık/koyu/sistem teması
- Çekirdek: `DiskDoktor.Core` (arayüzden bağımsız, testli)

## İndirme ve kullanım (son kullanıcı)

1. GitHub'da bu deponun **Releases** sayfasından işletim sisteminize uygun dosyayı indirin
   (depo gizli olduğu için GitHub hesabınızla giriş yapmış olmanız gerekir):
   - **Windows:** `DiskDoktor-win-x64.zip` (ARM'lı Surface vb. için `win-arm64`) → zip'i bir klasöre çıkarın,
     `DiskDoktor.exe`'ye çift tıklayın. "Windows bilgisayarınızı korudu" uyarısında **Ek bilgi → Yine de çalıştır**.
     Kurulum gerekmez; masaüstü kısayolu için exe'ye sağ tık → Gönder → Masaüstü.
   - **Linux:** `DiskDoktor-linux-x64.tar.gz` (Raspberry Pi vb. için `linux-arm64`):
     ```bash
     tar xzf DiskDoktor-linux-x64.tar.gz && cd DiskDoktor && ./install.sh
     ```
     Uygulama menüsüne simgesiyle eklenir (sudo gerekmez). Kaldırmak için `./install.sh --kaldir`.
     Kurmadan denemek için `./DiskDoktor` yeterli.
   - **macOS:** Apple Silicon (M1–M4) için `DiskDoktor-osx-arm64.zip`, Intel Mac için `osx-x64` → zip'i açın,
     `DiskDoktor.app`'i Uygulamalar klasörüne sürükleyin. İmzasız olduğu için ilk açılışta **sağ tık → Aç → Aç**
     (gerekirse Sistem Ayarları → Gizlilik ve Güvenlik → "Yine de Aç").
2. **Taramayı başlat**'a basın. Bulgular Güvenli / Dikkat / Riskli olarak gruplanır; yalnız Güvenli olanlar seçili gelir.
3. İstediklerinizi işaretleyin. Bir kartta "▸ öğeyi göster" ile içindeki dosya/klasörleri tek tek seçebilirsiniz.
   **Temizle…** → listeyi kontrol edin → **Temizliği başlat**.
4. Varsayılan olarak dosyalar çöp kutusuna taşınır (geri alınabilir). Sonunda Markdown/CSV rapor kaydedebilirsiniz.

Release yoksa kaynak koddan derleyin: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) kurun,
depoyu `git clone` edin ve aşağıdaki "Dağıtım" komutunu çalıştırın. Simge `scripts/make-icon.py` ile üretilir.

## Çalıştırma (geliştirici)

```bash
dotnet run --project src/DiskDoktor.App
```

Salt okunur tarama (hiçbir şey silmez):

```bash
dotnet run --project src/DiskDoktor.App -- --tara
```

Windows'ta uygulama konsol penceresi açmaz; çıktıyı görmek için `DiskDoktor.exe --tara > tara.txt` kullanın.

## Dağıtım

Tek dosyalık, .NET kurulumu gerektirmeyen sürümler `dist/<platform>/` altına, paylaşılmaya hazır arşivler
(Windows `.zip`, Linux `.tar.gz` + `install.sh`, macOS `.app` içeren `.zip`) `dist/release/` altına çıkar:

```bash
scripts/publish.sh                 # hepsi
scripts/publish.sh win-x64 osx-arm64
```

Windows'ta: `.\scripts\publish.ps1` (yalnız `dist/<platform>/`, arşiv üretmez; arşiv için WSL'de `publish.sh`). Platformlar: `win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64`.

- Linux: `chmod +x DiskDoktor && ./DiskDoktor`. Grafik oturumu (X11 veya XWayland) gerekir.
- macOS: imzasız olduğu için ilk açılışta sağ tık → Aç. Çöp kutusu kuralı için Tam Disk Erişimi gerekebilir.
- Windows: SmartScreen uyarısında "Yine de çalıştır". Normal kullanıcı olarak açılır; yönetici gerektiren
  klasörlerde silinemeyenler raporda gösterilir.

## Güvenlik modeli

1. **Kurallar** (`src/DiskDoktor.Core/Rules/rules.json`): her kural kendi klasörünü, risk düzeyini ve yaş filtresini
   tanımlar. Kullanıcı kuralları aynı biçimde şu dosyaya eklenebilir; aynı `id` yerleşik kuralı değiştirir:
   - Windows: `%LOCALAPPDATA%\DiskDoktor\rules.json`
   - Linux: `~/.config/DiskDoktor/rules.json`
   - macOS: `~/Library/Application Support/DiskDoktor/rules.json`
2. **Komutlar** (apt-get clean, DISM, journalctl...) yalnız uygulamanın içindeki yerleşik kurallarda olabilir;
   kullanıcı kural dosyası komut tanımlayamaz. Yönetici yardımcısı her isteği yerleşik kurallara ve PathGuard'a göre
   yeniden denetler.
3. **Tarama** sembolik bağlantı ve junction'ları izlemez; iç içe kural klasörleri çift sayılmaz.
4. **PathGuard** silmeden hemen önce her yolu yeniden denetler: kural klasörünün tam içinde olmalı, arada bağlantı
   olmamalı, ev klasörü, sistem klasörleri, Belgeler/Masaüstü/Resimler ve uygulamanın kendi klasörü korunur.
5. **Silme** varsayılan olarak çöp kutusuna taşır (Windows Geri Dönüşüm Kutusu, freedesktop Trash, macOS ~/.Trash).
   Kullanımdaki dosyalar atlanır, kalanlar silinir ve rapora yazılır.
6. Her temizlik JSON günlüğü bırakır (`…/DiskDoktor/logs`, Linux'ta `~/.local/state/DiskDoktor/logs`);
   sonuç Markdown veya CSV olarak dışa aktarılabilir.

## Test

```bash
dotnet test
```

Testler çekirdeği gerçek geçici klasörlerde, arayüzü Avalonia Headless ile çalıştırır ve ekran görüntülerini
`tests/DiskDoktor.Tests/bin/Debug/net8.0/screenshots/` altına yazar.

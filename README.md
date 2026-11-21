# DiskDoktor

Windows, Linux ve macOS'ta çalışan güvenli disk temizleyici. Yalnızca yeniden üretilebilen önbellek, geçici ve günlük
dosyalarını bulur; önce gösterir, siz onaylamadan hiçbir şey silinmez.

- Arayüz: Avalonia 11 (.NET 8), açık/koyu/sistem teması
- Çekirdek: `DiskDoktor.Core` (arayüzden bağımsız, testli)

## İndirme ve kullanım (son kullanıcı)

1. GitHub'da bu deponun **Releases** sayfasından işletim sisteminize uygun dosyayı indirin
   (depo gizli olduğu için GitHub hesabınızla giriş yapmış olmanız gerekir):
   - Windows: `DiskDoktor-win-x64.zip` → açın, `DiskDoktor.exe`'ye çift tıklayın.
   - Linux: `DiskDoktor-linux-x64.tar.gz` → `tar xzf …`, sonra `chmod +x DiskDoktor && ./DiskDoktor`.
   - macOS (M1/M2/M3): `DiskDoktor-osx-arm64.zip` → açın, sağ tık → Aç.
2. **Taramayı başlat**'a basın. Bulgular Güvenli / Dikkat / Riskli olarak gruplanır; Güvenli olanlar seçili gelir.
3. İstediklerinizi işaretleyin → **Temizle…** → listeyi kontrol edin → **Temizliği başlat**.
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

Tek dosyalık, .NET kurulumu gerektirmeyen sürümler `dist/<platform>/` altına çıkar:

```bash
scripts/publish.sh                 # hepsi
scripts/publish.sh win-x64 osx-arm64
```

Windows'ta: `.\scripts\publish.ps1`. Platformlar: `win-x64 win-arm64 linux-x64 linux-arm64 osx-x64 osx-arm64`.

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
2. **Tarama** sembolik bağlantı ve junction'ları izlemez; iç içe kural klasörleri çift sayılmaz.
3. **PathGuard** silmeden hemen önce her yolu yeniden denetler: kural klasörünün tam içinde olmalı, arada bağlantı
   olmamalı, ev klasörü, sistem klasörleri, Belgeler/Masaüstü/Resimler ve uygulamanın kendi klasörü korunur.
4. **Silme** varsayılan olarak çöp kutusuna taşır (Windows Geri Dönüşüm Kutusu, freedesktop Trash, macOS ~/.Trash).
   Kullanımdaki dosyalar atlanır, kalanlar silinir ve rapora yazılır.
5. Her temizlik JSON günlüğü bırakır (`…/DiskDoktor/logs`, Linux'ta `~/.local/state/DiskDoktor/logs`);
   sonuç Markdown veya CSV olarak dışa aktarılabilir.

## Test

```bash
dotnet test
```

Testler çekirdeği gerçek geçici klasörlerde, arayüzü Avalonia Headless ile çalıştırır ve ekran görüntülerini
`tests/DiskDoktor.Tests/bin/Debug/net8.0/screenshots/` altına yazar.

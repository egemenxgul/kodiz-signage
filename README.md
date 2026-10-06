# Kodiz Signage

Kafe, mağaza ve benzeri mekânlar için Windows dijital tabela (digital signage) oynatıcısı.
Bir Windows PC'ye bağlı **bir veya birden çok ekranda** (doğrudan veya HDMI splitter üzerinden) fotoğraf,
video ve PDF sayfalarından oluşan oynatma listelerini tam ekran ve sonsuz döngüde gösterir. Her ekran
kendi medyasını oynatabilir. İnternet gerektirmez.

- .NET 8 / WPF, MVVM (CommunityToolkit.Mvvm), tek dosya self-contained `.exe`
- Türkçe / İngilizce arayüz (varsayılan: işletim sistemi dili, ayarlardan canlı değiştirilebilir)
- Windows 10 (1809+) / 11, x64 – **Windows 7, 8 ve 8.1 desteklenmez** (bkz. [Sistem gereksinimleri](#sistem-gereksinimleri))

---

## Özellikler

| Alan | Ayrıntı |
|---|---|
| Oynatıcı | Kenarlıksız, en üstte duran tam ekran pencere; imleç gizli; A/B katmanlı ön yükleme ve fade ile siyah karesiz geçiş; animasyonlu GIF |
| Formatlar | **Resim:** JPG, JPEG, PNG, BMP, GIF, WEBP, HEIC · **Video:** MP4, MOV, M4V (H.264 + AAC tam destekli; WMV, AVI, MKV, WEBM uyarıyla) · **PDF:** her sayfa bir görsel olur |
| Liste | Sürükle-bırak sıralama, çoklu seçim ve toplu düzenleme, görünen ad, aktif/pasif, resim süresi, tarih aralığı, **gün ve saat planı**, 10 sn içinde geri alınabilir silme |
| Planlama | Öğe başına tarih aralığı + gün + saat (gece yarısını geçebilir); genel **çalışma saatleri** (dışında siyah ekran) |
| İçe aktarma | İlerleme göstergesi, kopya dosya tespiti, disk alanı kontrolü, codec/çözünürlük/HEIC uyarıları, **izlenen klasör** (USB / OneDrive / ağ klasörü) |
| Çoklu ekran | Bağlı tüm ekranları algılar; her ekran açılıp kapatılabilir, isim alır ve **kendi medya listesini** oynatır. Ekrana özel ölçekleme, arka plan ve ses ayarı |
| Ekran | "Ekranları Tanımla", ekran kaybolursa **gizle ve bekle** (veya ana ekranda göster) + 5 sn'de bir yeniden deneme |
| Sistem | Kurulum ve güncelleme, Başlat menüsü kısayolu, Windows ile otomatik başlama, tek örnek, tray ikonu, değiştirilebilir global kısayollar, uyku engelleme, ayarlar için PIN, yedekle / geri yükle |
| Dayanıklılık | Atomik JSON yazımı, bozuk dosyada yedekten veya varsayılandan devam, global hata yakalama, çökmede kendini yeniden başlatma, art arda çökmede **güvenli mod** |

### Kısayollar

Kısayollar Windows genelinde çalışır: gösterim tam ekranken ve ayarlar penceresi kapalıyken de kullanılabilir. Varsayılanlar:

| Kısayol | İşlev |
|---|---|
| `Ctrl+Shift+S` | Ayarlar penceresini aç |
| `Ctrl+Shift+P` | Gösterimi durdur / başlat |
| *(atanmamış)* | Sonraki öğeye geç |
| `Ctrl+Shift+Q` | Uygulamadan çık (onay ister) |

**Ayarlar → Kısayollar** sekmesinde her kısayolun ne işe yaradığı yazar ve kısayollar değiştirilebilir:

- Kutuya tıklayıp yeni tuş kombinasyonuna basın. `Esc` iptal eder, `Backspace` kısayolu kaldırır.
- Kombinasyon Ctrl, Alt veya Win tuşlarından en az birini içermelidir (F1–F24 tek başına da olur).
  Alt+F4, Win+L gibi Windows kısayolları seçilemez.
- Aynı kombinasyon iki işleve atanamaz.
- Başka bir uygulama kombinasyonu kullanıyorsa satırda uyarı çıkar. O durumda başka bir kombinasyon seçin.

Tray ikonu (saat yanındaki bildirim alanı) sağ tık menüsü: Ayarlar, Gösterimi Başlat/Durdur, Sonraki Öğe, Çıkış.
Çift tık ayarları açar. Ayarlar penceresini kapatmak uygulamayı kapatmaz, yalnızca gizler. İlk seferde
bunu anlatan bir Windows bildirimi gösterilir.

---

## Kullanım

### Medya ekleme

- **Dosya Ekle** butonu veya dosya/klasörleri pencereye sürükleyip bırakma.
- Dosyalar uygulamanın kendi klasörüne kopyalanır. Orijinal dosyaya dokunulmaz.
- Kopyalanan her dosya hemen listeye eklenir. İçe aktarmayı iptal ederseniz o ana kadar kopyalananlar listede kalır.
- **Kopya dosya:** Aynı içerik (adı farklı olsa bile) ikinci kez eklenmez.
- **Disk alanı:** Disk dolmak üzereyse dosya eklenmez. Medya sekmesinin sağ üstünde medya klasörünün kapladığı alan ve diskteki boş alan görünür.
- **PDF:** Her sayfa ayrı bir görsel öğe olur (örneğin "Menü · 1/3").
- **Uyarılar:**
  - HEVC videolar ve 4K videolar için uyarı verilir. iPhone'da **Ayarlar → Kamera → Formatlar → "En Uyumlu"** seçilirse videolar H.264 kaydedilir.
  - Bilgisayarda gerekli codec yoksa HEIC ve WEBP resimler için uyarı verilir. Çözüm: Microsoft Store'dan "HEIF Görüntü Uzantıları" ve "Webp Görüntü Uzantıları".

### Liste ve planlama

- Bir öğe seçilince sağdaki panelde önizlemesi ve tüm ayarları açılır: görünen ad, aktif/pasif, süre, tarih aralığı, gün ve saat planı.
- **Tarih aralığı:** Öğe yalnızca bu günler arasında gösterilir; iki gün de dahildir. Örneğin bir kampanya görseli 1–31 Ekim arasında kendiliğinden yayına girer ve yayından kalkar.
- **Gün ve saat planı:** Örneğin "Hafta içi 07:00–11:00" (kahvaltı menüsü). Bitiş saati başlangıçtan önceyse gece yarısını geçer (22:00–02:00).
- **Toplu düzenleme:** `Ctrl` veya `Shift` ile birden fazla öğe seçilince sağ panel toplu düzenlemeye geçer: aktif/pasif yap, süre, tarih aralığı, plan, sil.
- **Silme:** Öğe ve medya klasöründeki kopyası silinir. 10 saniye boyunca **Geri al** butonu görünür. Bu süre dolduktan veya pencere kapatıldıktan sonra dosya diskten kalıcı olarak silinir.
- **Yayın göstergesi:** O an TV'de oynayan öğe listede **YAYINDA** etiketiyle işaretlenir. Üst çubukta da adı ve kalan süresi yazar.
- **Önizle:** Gösterimi bu bilgisayarda küçük ve sessiz bir pencerede oynatır. TV'deki gösterimi etkilemez.

### Birden çok ekran (Ekranlar sekmesi)

Bilgisayara bağlı her ekran ayrı bir kart olarak listelenir ("5 ekran algılandı"):

- **Bu ekranda gösterim** anahtarıyla ekran açılır veya kapatılır. Kapalı ekranda hiçbir şey gösterilmez;
  ayarları ve medya atamaları saklanır.
- Her ekrana bir isim verilebilir (örneğin "Bar üstü", "Kasa", "Vitrin").
- Kartta ekranın durumu ("Oynuyor: menu.jpg", "Ekran bekleniyor") ve kaç medya atandığı görünür.
- **Görünüm** bölümünden ekrana özel ölçekleme, arka plan rengi ve video sesi seçilebilir.
  Varsayılan olarak genel ayarlar kullanılır.
- **Medyayı düzenle**, Medya sekmesini yalnızca o ekranın medyasını gösterecek şekilde açar.
  **Önizle**, o ekranın gösterimini küçük bir pencerede oynatır.
- Bağlı olmayan ama kayıtlı ekranlar "Bağlı değil" olarak listelenir. **Unut** ile kaldırılabilir.

**Hangi medya hangi ekranda?** Birden çok ekran varken Medya sekmesinde her satırda ekran düğmeleri
çıkar: **[1] [2] [3] [4] [5]**. Tıklayarak o medyayı o ekrana ekler veya çıkarırsınız. Örnek:

| Medya | Ekran 1 | Ekran 2 | Ekran 3 |
|---|---|---|---|
| a, b, c | ✔ | ✔ | |
| x, y, z | | ✔ | ✔ |

Bu durumda ekran 1 `a b c`, ekran 2 `a b c x y z`, ekran 3 `x y z` oynatır. Sıra tüm ekranlarda ortaktır;
her ekran kendi öğelerini bu sırayla oynatır.

- Üstteki **Gösterilen** filtresiyle yalnızca bir ekranın medyası listelenir. Filtre açıkken eklenen
  yeni dosyalar doğrudan o ekrana atanır.
- Detay panelinde ekranlar isimleriyle seçilir. **"Tüm ekranlar (sonradan eklenenler dahil)"** seçili
  medya, ileride eklenecek ekranlarda da gösterilir.
- Toplu düzenlemede seçili medyalar ekranlara atanabilir: yalnızca işaretli ekranlar / ekle / çıkar.
- Hiçbir açık ekrana atanmamış medya "hiçbir ekranda değil" uyarısıyla işaretlenir.

Eski sürümlerden gelen kurulumlarda daha önce seçilmiş ekran otomatik olarak **Ekran 1** olur ve tüm
medya tüm ekranlarda gösterilmeye devam eder.

### İzlenen klasör (Genel sekmesi)

Seçilen klasördeki medya dosyaları listeye otomatik eklenir: yeni dosyalar eklenir, değişen dosyalar
güncellenir (öğenin süre ve plan gibi ayarları korunur), klasörden silinenler listeden de kalkar.
Klasöre ulaşılamıyorsa, örneğin USB bellek çıkarılmışsa, hiçbir şey silinmez. Örnek kullanım: kafe
sahibi menü görselini telefondan OneDrive klasörüne atar, bir dakika içinde TV'de görünür.

### Çalışma saatleri (Genel sekmesi)

Açılınca bu saatlerin dışında TV'de siyah ekran gösterilir. İsteğe bağlı olarak ekranın Windows güç
ayarlarına göre uykuya geçmesine izin verilir; bunun için Windows'ta "ekranı kapat" süresi ayarlı olmalıdır.

### Ekran bağlantısı kesilirse (Ekranlar sekmesi)

TV kapatıldığında veya splitter geç tanındığında Windows o ekranı kaybedebilir. Varsayılan
**"Gizle ve bekle"** ayarında o ekranın gösterimi başka bir ekranı (örneğin kasa monitörünü) kaplamaz; ekran
geri gelince kendiliğinden devam eder. Diğer ekranlar etkilenmez. İstenirse **"Geçici olarak ana ekranda
göster"** seçilebilir; ana ekran başka bir ekrana atanmamışsa kullanılır.

### Güvenlik ve yedekleme (Genel sekmesi)

- **PIN:** Ayarları açmak, gösterimi durdurmak ve uygulamadan çıkmak PIN ister. 5 yanlış denemeden sonra 30 saniye beklenir.
  PIN unutulursa `settings.json` dosyasındaki `"pinHash"` satırı silinir.
- **Yedekle / geri yükle:** Ayarlar, liste ve tüm medya tek bir `.zip` dosyasına yazılır. Yeni PC'ye geçişte geri yüklenir; uygulama ardından kendini yeniden başlatır.

---

## Sistem gereksinimleri

| | |
|---|---|
| İşletim sistemi | **Windows 11** veya **Windows 10 sürüm 1809 ve üzeri**, 64 bit (x64) |
| Desteklenmeyen | **Windows 7, Windows 8, Windows 8.1**, 32 bit Windows, ARM |
| Donanım | 1080p video oynatabilen herhangi bir PC (4 GB RAM önerilir), HDMI çıkışı |
| Ek kurulum | Gerekmez: .NET çalışma ortamı exe'nin içindedir. HEIC/WEBP için Microsoft Store'daki ücretsiz görüntü uzantıları gerekebilir |

**Windows 7, 8 ve 8.1'de neden çalışmaz?** Uygulama .NET 8 ile yazılmıştır ve Microsoft .NET 8'i
yalnızca Windows 10 ve üzerinde destekler. Bu sistemlerde uygulama açılırken hata verir. Ayrıca bazı
özellikler (ekran başına DPI uyumu, HEIC, yerleşik PDF motoru) Windows 10'a özgüdür. Windows 7 ve 8.1
yıllardır güvenlik güncellemesi almadığından internete bağlı bir kafe PC'sinde kullanılmaları zaten önerilmez.

**Eski bir bilgisayar için öneri:** Windows 10 ve 11 eski donanımda da genelde rahat çalışır. Windows 10'un
normal sürümünün desteği sona erdiğinden yeni kurulumlarda **Windows 11** tercih edin. Donanım Windows 11'i
desteklemiyorsa, signage cihazları için tasarlanmış uzun süreli destekli **Windows 10 IoT Enterprise LTSC**
sürümleri bir seçenektir (genelde kurumsal lisansla satılır).

## Derleme

Gereksinim: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
dotnet build                 # tüm çözüm
dotnet test                  # xUnit testleri
dotnet run --project src/KodizSignage   # yalnızca Windows'ta çalışır
```

Proje macOS/Linux'ta da derlenir (`EnableWindowsTargeting`). İş mantığının tamamı platformdan bağımsız
`KodizSignage.Core` kütüphanesinde olduğu için testler Mac'te de çalışır. Testler şunları kapsar:
oynatma sırası, tarih/gün/saat planı, çalışma saatleri, süre, JSON, MP4 analizi, içe aktarma, kopya
tespiti, geri alma, klasör eşitleme, yedekleme, PIN, çökme koruması ve kısayol kuralları.

## Publish (tek dosya .exe)

```powershell
.\publish.ps1              # önce testler, sonra publish\KodizSignage.exe
.\publish.ps1 -SkipTests
.\publish.ps1 -CertificatePath kod-imza.pfx -CertificatePassword ****   # + kod imzalama
```

macOS / Linux'ta `./publish.sh` aynı işi yapar ve yine Windows .exe'si üretir. Bu şekilde üretilen
exe dosyasının kendisinde simge ve sürüm bilgisi bulunmaz; bu bir .NET kısıtıdır. Uygulama içi
simgeler ve Başlat menüsü kısayolu yine doğru görünür. Windows'ta veya CI'da üretilen exe'de simge
doğrudan exe'nin içindedir.

**Kod imzalama:** İmzasız exe ilk açılışta SmartScreen'in "Windows bilgisayarınızı korudu" uyarısını
gösterir. Bir kod imzalama sertifikasıyla `publish.ps1 -CertificatePath ...` (veya
`-CertificateThumbprint ...`) kullanıldığında exe imzalanır ve uyarı kalkar.

## CI (GitHub Actions)

[.github/workflows/build.yml](.github/workflows/build.yml) her push'ta bir Windows makinesinde şu adımları çalıştırır:

1. Birim testleri.
2. Tek dosya exe üretimi.
3. **UI duman testi:** Exe `--smoke-test` parametresiyle gerçekten açılır. Örnek medya ekler, gösterimi oynatır, tüm ayar sekmelerini ve önizlemeyi açar, dil değiştirir.
   - İşlenmemiş bir hata, XAML bağlama (binding) hatası veya takılma olursa build başarısız olur.
   - Çıkış kodları: 1 = hata, 2 = bağlama hatası, 3 = zaman aşımı.

Exe ve loglar artifact olarak indirilebilir. Duman testi Windows'ta elle de çalıştırılabilir:
`KodizSignage.exe --smoke-test`. Gerçek verilere dokunmaz; geçici bir veri klasörü kullanır.

---

## Kurulum (kafe PC'si)

1. `KodizSignage.exe` dosyasını PC'ye kopyalayıp çalıştırın.
   - SmartScreen uyarı verirse: **Ek bilgi → Yine de çalıştır** (imzasız exe).
2. **"Bilgisayara kurulsun mu?"** sorusuna **Evet** deyin.
   - Uygulama `%LocalAppData%\kodiz-signage\app\` klasörüne kopyalanır ve Başlat menüsüne eklenir.
   - Bu sayede İndirilenler klasörü temizlense bile Windows açılışında başlamaya devam eder.
   - Yönetici izni gerekmez.
3. İlk açılışta ayarlar penceresi açılır. **Medya** sekmesinden dosyaları ekleyin.
4. **Ekran** sekmesinde TV'nin (splitter'ın) bağlı olduğu çıkışı seçin. Emin değilseniz
   **Ekranları Tanımla**'ya basın; her ekranda 3 saniye büyük bir numara görünür.
5. **Genel** sekmesinde "Windows açılışında otomatik başlat" açık kalsın (varsayılan).

**Güncelleme:** Yeni `KodizSignage.exe` dosyasını çalıştırmanız yeterlidir. Uygulama kurulu sürümü
güncellemeyi teklif eder; çalışan eski sürüm kendiliğinden kapanır. Ayarlar ve medya korunur.

Tüm veriler kullanıcı profilinde tutulur:

```
%LocalAppData%\kodiz-signage\
├── app\              kurulu exe, simge, sürüm bilgisi
├── media\            içe aktarılan dosyaların kopyaları (GUID adlı) ve thumbs\ önizlemeleri
├── settings.json     ayarlar
├── playlist.json     oynatma listesi
└── logs\             günlük log dosyaları (son 7 gün; saatlik bellek kullanımı dahil)
```

Yedek için Genel → **Yedek al** kullanılabilir ya da bu klasör kopyalanabilir.

### Video önerisi

En sorunsuz oynatma için **H.264 video + AAC ses** (MP4 veya MOV) ve **1080p** kullanın. Dönüştürmek
için [HandBrake](https://handbrake.fr) ("Fast 1080p30" ön ayarı) veya ffmpeg kullanılabilir:

```bash
ffmpeg -i girdi.mov -vf "scale='min(1920,iw)':-2" -c:v libx264 -preset slow -crf 20 -pix_fmt yuv420p -c:a aac -b:a 160k -movflags +faststart cikti.mp4
```

---

## Kafe PC'si için önerilen Windows ayarları

Amaç: PC elektrik kesintisinden sonra açıldığında kimse dokunmadan gösterimin kendiliğinden başlaması
ve hiçbir pop-up'ın ekrana gelmemesi.

### 1. BIOS: elektrik gelince otomatik aç

BIOS/UEFI'de **"Restore on AC Power Loss" / "AC Back" → Power On** ayarını açın (adı anakarta göre değişir).

### 2. Otomatik oturum açma

Uygulama kullanıcı oturumu açılınca başlar, bu yüzden otomatik oturum açma gerekir.

- **Önerilen:** Microsoft'un [Sysinternals Autologon](https://learn.microsoft.com/sysinternals/downloads/autologon)
  aracı (parolayı şifreli saklar). Çalıştırın, kullanıcı adı ve parolayı girin, **Enable**'a basın.
- Alternatif: `Win+R → netplwiz` → "Kullanıcıların bu bilgisayarı kullanmak için bir kullanıcı adı ve
  parola girmesi gerekir" kutusunu kaldırın.
  - Windows 11'de bu kutu görünmüyorsa: **Ayarlar → Hesaplar → Oturum açma seçenekleri →**
    "Gelişmiş güvenlik için ... yalnızca Windows Hello oturum açmaya izin ver" ayarını kapatın.
- Bu PC'ye özel, yönetici olmayan bir yerel hesap kullanmanız önerilir.

### 3. Güç ve ekran

Uygulama gösterim sırasında uykuyu ve ekran kapanmasını zaten engeller. Yine de:

- **Ayarlar → Sistem → Güç**: Uyku **Hiçbir zaman**. Çalışma saatlerini kullanıp TV'lerin gece uyumasını
  istiyorsanız "Ekranı kapat" için örneğin 10 dakika seçin; yoksa **Hiçbir zaman**.
- Ekran koruyucuyu kapatın (**Kişiselleştirme → Kilit ekranı → Ekran koruyucu → (Yok)**).
- **Ayarlar → Hesaplar → Oturum açma seçenekleri → "Uzakta olduysanız ne zaman yeniden oturum açılması
  gereksin?" → Hiçbir zaman.**

### 4. Windows Update

- **Ayarlar → Windows Update → Gelişmiş seçenekler → Etkin saatler**: "Manuel" seçip kafenin çalışma
  saatlerini girin (örneğin 07:00–23:00, en fazla 18 saat). Windows bu saatlerde otomatik yeniden başlatma yapmaz.
- "Güncelleştirmeyi tamamlamak için yeniden başlatma gerektiğinde bana bildir" ayarını **kapalı** tutun.
- "Güncelleştirmeler hazır olur olmaz alın" ayarını kapatın.
- Güncellemeleri tamamen kapatmayın; güvenlik yamaları önemlidir. Gece yeniden başlasa bile otomatik
  oturum açma ve otomatik başlatma sayesinde gösterim kendiliğinden geri gelir.

### 5. Bildirimler ve pop-up'lar

- **Ayarlar → Sistem → Bildirimler**: Bildirimleri **kapatın**. Windows 11'de "Rahatsız etmeyin"
  özelliğini **açın**; Windows 10'da **Odak yardımı → Yalnızca alarmlar** seçin.
- Aynı sayfadaki "Ek ayarlar" altında **"Windows'u kurmanın yollarını öner"**, **"Windows'u kullanırken
  ipuçları ve öneriler al"** ve **"Güncelleştirmelerden sonra Windows karşılama deneyimini göster"**
  seçeneklerini kapatın.
- OneDrive (izlenen klasör için kullanılmıyorsa), Teams ve Skype gibi gereksiz başlangıç uygulamalarını
  **Görev Yöneticisi → Başlangıç uygulamaları** sekmesinden devre dışı bırakın.
  **Kodiz Signage'ı devre dışı bırakmayın.** Bırakılırsa uygulama bunu fark eder ve Genel sekmesinde
  "Yeniden etkinleştir" butonuyla uyarır.

### 6. Ekran / HDMI splitter

- TV'leri ve splitter'ı mümkünse PC'den **önce** açın. Splitter geç tanınırsa gösterim, ekran gelene
  kadar gizli bekler ve ekran gelince 5 saniye içinde başlar.
- Splitter'daki TV'ler farklı çözünürlükteyse splitter genellikle en düşük ortak çözünürlüğü bildirir.
  Görüntü bulanıksa splitter'ın EDID anahtarını (varsa) **1080p** konumuna alın.
- Windows'ta **Ekran → Ölçek** değerinin %100 olması zorunlu değildir; uygulama Per-Monitor DPI
  uyumludur ve ayarlar penceresi küçük ekranlara sığacak şekilde boyutlanır.

### 7. Ses

Video sesi varsayılan olarak kapalıdır. Açmak için **Genel → Video sesi**. Ses HDMI üzerinden TV'ye
gidecekse Windows ses çıkışını TV veya splitter olarak seçin.

---

## Sorun giderme

| Belirti | Çözüm |
|---|---|
| Gösterim yanlış ekranda | Ayarlar → Ekranlar → "Ekranları Tanımla" ile numaraları görün; istenmeyen ekranı kapatın, doğru ekranı açın |
| Bir ekranda içerik yok | Medya sekmesinde filtreyi o ekrana alın; medyaların o ekranın düğmesi işaretli mi bakın |
| Durum "Seçili ekran bekleniyor" | TV kapalı veya HDMI bağlantısı yok. TV açılınca gösterim kendiliğinden başlar |
| Bir video oynamıyor | Log'a bakın (Genel → Log klasörünü aç). H.264 MP4'e dönüştürün |
| HEIC/WEBP resim görünmüyor | Microsoft Store'dan "HEIF Görüntü Uzantıları" veya "Webp Görüntü Uzantıları" yükleyin |
| Kısayol çalışmıyor | Ayarlar → Kısayollar sekmesinde durumuna bakın. Başka bir uygulama kullanıyorsa farklı bir kombinasyon atayın |
| "Gösterilecek içerik yok" ekranı | Öğeler pasif ya da tarih/gün/saat planlarının dışında olabilir (listede soluk görünen satırlar) |
| Siyah ekran | Çalışma saatleri dışında olabilir (Genel → Çalışma saatleri) |
| Ayarlar penceresi görünmüyor | Ayarlar kısayolu (varsayılan `Ctrl+Shift+S`), Başlat menüsündeki Kodiz Signage veya exe'yi tekrar çalıştırın |
| Üstte sarı "güvenli mod" uyarısı | Uygulama kısa sürede birkaç kez çöktü. Gösterim 10 dakika sonra kendiliğinden başlar; log klasörüne bakın |
| PIN unutuldu | Uygulamayı kapatın, `settings.json` dosyasından `"pinHash"` satırını silin |

Loglar: `%LocalAppData%\kodiz-signage\logs\kodiz-signage-YYYYMMDD.log`

---

## Mimari

```
KodizSignage.sln
├── src/KodizSignage.Core/          net8.0 – platformdan bağımsız iş mantığı
│   ├── Models/                     PlaylistItem, AppSettings, OperatingHours, HotkeySettings, enum'lar
│   ├── Storage/                    AtomicJsonFile (tmp → File.Replace + .bak), JsonStore (birleştirilmiş arka plan yazımı)
│   ├── Playback/                   PlaylistScheduler (filtre, sıra, sonraki öğe, süre), ScheduleRules (gün/saat penceresi)
│   ├── Media/                      MediaFormats, Mp4Probe (codec, süre, çözünürlük için MP4/MOV okuyucu)
│   ├── Hotkeys/                    kısayol ayrıştırma, doğrulama ve çakışma kuralları
│   ├── Displays/DisplayMatcher     kayıtlı ekranı bulma kuralları
│   └── Services/                   Settings, Playlist, MediaImport, FolderSync, Backup, CrashGuard, PinHasher
├── src/KodizSignage/               net8.0-windows WPF uygulaması
│   ├── Services/                   PlaybackManager, DisplayService, StartupService, InstallService, PowerService,
│   │                               HotkeyService, ShortcutService, SingleInstanceService, TrayService, PinGate,
│   │                               LocalizationService, ThumbnailService, WpfMediaInspector (PDF, codec), SmokeTest
│   ├── Views/Player/               PlaybackEngine (döngü), MediaLayer (A/B katmanı), GifAnimation
│   ├── Views/                      PlayerWindow, SettingsWindow, PinWindow, IdentifyWindow
│   ├── ViewModels/                 Settings, Media (+ toplu düzenleme), Display, General, Shortcuts, ScheduleEditor
│   ├── Localization/               strings.py → Strings.tr.xaml / Strings.en.xaml
│   └── Themes/Dark.xaml
└── tests/KodizSignage.Tests/       xUnit
```

**Oynatma döngüsü.** `PlaybackEngine` UI thread'inde yalnızca async/await ile çalışır. İki katman (A/B)
üst üste durur. Öndeki katman gösterilirken sıradaki öğe arka katmana yüklenir (resim arka plan
thread'inde decode edilir, video açılıp ilk karede bekletilir). Geçişte arka katman üste alınır ve hâlâ
görünen eski katmanın üzerine fade ile gelir. Eski katman ancak bundan sonra boşaltılır, bu yüzden araya
siyah kare girmez ve bellek birikmez. Açılamayan dosyalar loglanıp atlanır. Döngü en geç 20 saniyede bir
çalışma saatlerini ve planları yeniden kontrol eder.

**Canlı güncelleme.** Ayar ve liste servisleri değişikliği önce bellekte uygular ve olay yayınlar,
ardından diske arka planda atomik olarak yazar. Oynatıcı bu değişiklikleri yeniden başlatmadan uygular.

### Dil dosyaları

Metinler tek kaynaktan üretilir: `src/KodizSignage/Localization/strings.py`. Bir metni değiştirmek veya
eklemek için bu dosyayı düzenleyip `python3 strings.py` komutunu çalıştırın.

---

## Lisans

Kodiz Signage **[PolyForm Noncommercial License 1.0.0](LICENSE)** ile lisanslanmıştır. Kaynak kodu
açıktır, ancak bu bir OSI açık kaynak lisansı değildir:

- **Serbest:** kişisel kullanım, öğrenme ve deneme; okullar, dernekler, vakıflar ve kamu kurumları gibi
  ticari olmayan kuruluşlar. Bu amaçlarla değiştirip dağıtabilirsiniz.
- **İzne tabi:** her türlü ticari kullanım. Bir işletmede (kafe, mağaza, restoran…) çalıştırmak, satmak
  veya ticari bir ürüne/hizmete dahil etmek bu kapsamdadır. Ticari lisans için proje sahibiyle iletişime geçin.
- **Atıf zorunlu:** yazılımın tamamını veya bir kısmını alan herkes [LICENSE](LICENSE) dosyasındaki
  `Required Notice:` satırını ve lisans koşullarını (veya bağlantısını) korumak zorundadır.

Kullanılan üçüncü taraf bileşenler kendi lisanslarına tabidir: [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

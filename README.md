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
| Kütüphane | Her dosya diskte **bir kez** saklanır, ekranlar paylaşır; arama, görünen ad, varsayılan süre/plan, 10 sn içinde geri alınabilir silme |
| Ekran listeleri | Her ekranın **kendi oynatma listesi**: bağımsız sıra, süre, aktif/pasif, geçiş, tarih ve gün/saat planı; aynı medya bir listede birden çok kez; başka ekrandan kopyala, listeyi bağla, **senkron oynat** |
| Kopya kontrolü | Birebir aynı dosya (SHA-256), görsel olarak aynı resim (algısal parmak izi), muhtemelen aynı video ve aynı ad/yeni sürüm tespit edilir; ne yapılacağı sorulur |
| Planlama | Öğe başına tarih aralığı + gün + saat (gece yarısını geçebilir); genel **çalışma saatleri** (dışında siyah ekran) |
| İçe aktarma | İlerleme göstergesi, kopya dosya tespiti, disk alanı kontrolü, codec/çözünürlük/HEIC uyarıları, **izlenen klasör** (USB / OneDrive / ağ klasörü) |
| Çoklu ekran | Bağlı tüm ekranları algılar; her ekran açılıp kapatılabilir, isim ve renk alır. Ekrana özel döndürme (dikey TV), ölçekleme, arka plan, ses/ses seviyesi, varsayılan süre, geçiş ve çalışma saatleri; sesin tek ekrandan çıkması |
| Ekran | "Ekranları Tanımla", ekran kaybolursa **gizle ve bekle** (veya ana ekranda göster) + 5 sn'de bir yeniden deneme |
| Sistem | Kurulum, **GitHub'dan otomatik güncelleme** (doğrulanmış indirme, sessiz saatte kurulum, sorunlu sürümde otomatik geri dönüş), Başlat menüsü kısayolu, Windows ile otomatik başlama, tek örnek, tray ikonu, değiştirilebilir global kısayollar, uyku engelleme, ayarlar için PIN, yedekle / geri yükle |
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

### Kütüphane: medyayı bir kez ekleyin

- **Dosya Ekle** butonu veya dosya/klasörleri pencereye sürükleyip bırakma. Dosyalar uygulamanın kendi
  klasörüne **bir kez** kopyalanır; orijinal dosyaya dokunulmaz.
- Aynı medyayı 5 ekranda göstermek 5 kopya değil, 5 küçük kayıt demektir; disk şişmez.
- **"Yeni medya otomatik eklensin: [1] [2] [3]"**: Birden çok ekran varken kütüphanenin üstünde çıkar.
  İşaretli ekranlar, kütüphaneye eklenen her yeni medyayı listelerinin sonuna otomatik alır.
- Satırlardaki renkli ekran düğmeleri mevcut bir medyayı tek tıkla bir ekranın listesine ekler veya
  çıkarır. Kopya oluşturulmaz.
- Kütüphanedeki süre, aktif/pasif, tarih ve plan değerleri **varsayılandır**. Bir ekran kendi değerini
  belirlemediyse bu değerler kullanılır.
- Kütüphaneden silinen medya tüm ekranlardan da kalkar. 10 saniye boyunca **Geri al** butonu görünür;
  ardından dosya diskten silinir.
- **PDF:** Her sayfa ayrı bir görsel olur. **Video:** H.264 MP4/MOV önerilir. HEVC, 4K ve eksik
  HEIC/WEBP codec'i için uyarı verilir.

### Kopya kontrolü

Eklenen her dosya kütüphaneyle karşılaştırılır:

| Durum | Nasıl anlaşılır | Önerilen |
|---|---|---|
| Birebir aynı dosya | İçerik özeti (SHA-256), dosya adı farklı olsa bile | Mevcut olanı kullan |
| Görsel olarak aynı resim | Algısal parmak izi: yeniden kaydedilmiş, küçültülmüş veya farklı formatta kopyalar | Mevcut olanı kullan |
| Muhtemelen aynı video | Aynı süre, aynı çözünürlük, benzer boyut | Mevcut olanı kullan |
| Aynı ad, farklı içerik | Muhtemelen güncellenmiş sürüm | Eskisinin yerine koy |

İki dosya küçük resimleriyle yan yana gösterilir. Seçenekler:

- **Mevcut olanı kullan:** Diske bir şey kopyalanmaz; gerekiyorsa medya hedef ekrana eklenir.
- **Yine de ayrı ekle:** Ne kadar ek yer tutacağı yazar.
- **Eskisinin yerine koy:** Ekranlardaki yeri ve ekrana özel ayarları korunur, eski dosya silinir.
- **Atla.**

"Bu içe aktarmadaki benzer durumlar için de aynısını yap" seçeneği de vardır.

### Ekranlar: her ekran için ayrı ve detaylı ayar

Soldaki listede bağlı tüm ekranlar ("5 ekran algılandı") renkleriyle görünür. Her ekranın kartında
şunlar vardır: açma/kapatma anahtarı, durum, o an oynayan medyanın küçük resmi ve atanmış medya sayısı.
Kullanılmayan monitörler "+" ile listelenir ve tek tıkla gösterime açılır.

Bir ekran seçildiğinde sağda **o ekranın renginde bir başlık** çıkar: *"Ekran 2 · Bar düzenleniyor –
buradaki değişiklikler yalnızca bu ekranı etkiler"*.

**Oynatma listesi** (her ekranın kendisine ait):

- **Ekleme:** **Kütüphaneden ekle** (küçük resimli, aramalı, çoklu seçim), **Bu ekrana dosya ekle**
  ya da **Başka ekrandan kopyala**.
- **Bağımsız sıra:** Sürükle-bırak veya yukarı/aşağı ile sıralanır; diğer ekranlar etkilenmez. Aynı
  medya listede birden fazla kez yer alabilir (örneğin logo baş ve ortada).
- **Ekrana özel değerler:** Her öğe için bu ekrandaki aktif/pasif durum, süre, geçiş efekti, tarih
  aralığı ve gün/saat planı ayarlanabilir. Değişen öğeler **"bu ekrana özel"** etiketi alır. Her
  alanın altında kütüphanedeki varsayılan değer yazar ve tek tıkla varsayılana dönülür.
- **Toplu düzenleme:** Birden çok öğe seçilerek yalnızca bu ekranda aktif/pasif, süre, tarih, plan
  ayarı veya listeden çıkarma yapılabilir. Çıkarılan öğe kütüphanede kalır ve geri alınabilir.
- **Liste bağlama:** "Ekran 1'in listesini oynatır" seçeneği seçilirse iki ekran aynı listeyi paylaşır.
  **Senkron oynat** işaretlenirse aynı öğeye aynı anda geçerler (yan yana TV'ler için).

**Ekran ayarları:** İsim; döndürme (0° / 90° / 180° / 270°, dikey TV'ler için); ölçekleme; arka plan
rengi; video sesi ve ses seviyesi; varsayılan resim süresi; geçiş efekti ve süresi; çalışma saatleri.
İşaretlenmeyen ayarlar Genel sekmesindeki değerleri kullanır.

**Tüm ekranlar için** (sol alt):

- Ekran bağlantısı kesilince ne yapılacağı.
- Sesin hangi ekrandan çıkacağı. Örneğin "yalnızca Ekran 2"; böylece beş ekran aynı anda ses çıkarmaz.
- Videoyu önceden yükleme (performans). Çok sayıda ekran aynı anda video oynatıyorsa uyarı çıkar.

Eski sürümlerden gelen kurulumlarda ortak liste ve ekran atamaları otomatik olarak ekran listelerine
dönüştürülür.

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

### Güncellemeler (Genel sekmesi)

- Uygulama günde bir kez GitHub'daki son sürümü kontrol eder. **Güncellemeleri kontrol et** butonu
  bunu hemen yapar.
- Yeni sürüm arka planda indirilir ve SHA-256 ile doğrulanır. Doğrulanamayan dosya kurulmaz.
- "Sessiz bir anda kendiliğinden kur" açıksa güncelleme şu anlardan birinde kurulur: çalışma saatleri
  dışında, gösterim kapalıyken veya gece 03:00–05:00. Kapalıysa **Şimdi güncelle** butonu ve bir
  bildirim çıkar.
- Kurulumdan önce mevcut sürüm, ayarlar ve liste yedeklenir. Yeni sürüm ilk 30 dakikada art arda
  çökerse önceki sürüm ve veriler kendiliğinden geri yüklenir; o sürüm bir daha önerilmez.
- İnternet yoksa kontrol sessizce atlanır; gösterim etkilenmez.

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

## Yeni sürüm yayınlama

1. `Directory.Build.props` içindeki `<Version>` değerini artırın (örneğin `1.3.0` → `1.4.0`) ve commit'leyin.
2. Etiket gönderin:

   ```bash
   git tag v1.4.0
   git push origin main --tags
   ```

3. [.github/workflows/release.yml](.github/workflows/release.yml) şu adımları kendisi yapar:
   - Windows'ta testleri çalıştırır ve exe'yi derler. Exe'nin sürüm numarası etiketten alınır.
   - Duman testini çalıştırır.
   - `KodizSignage.exe`, `KodizSignage.exe.sha256`, lisans ve bildirim dosyalarıyla bir GitHub
     Release yayınlar.
4. Kurulu uygulamalar yeni sürümü bir sonraki günlük kontrolde bulur ve sessiz bir anda kendini
   günceller. Elle kurulum için Release sayfasındaki exe'yi çalıştırmak yeterlidir; kurulu sürümü
   güncellemeyi teklif eder.

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
| Bir ekranda içerik yok | Ekranlar sekmesinde o ekranı seçin: listesi boş mu, öğeler bu ekranda pasif mi ya da planlarının dışında mı (soluk satırlar) bakın |
| Güncelleme gelmiyor | Genel → Güncellemeler → "Güncellemeleri kontrol et"; durum satırında hata yazar (örneğin internet yok) |
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
│   ├── Models/                     PlaylistItem (kütüphane), ScreenPlaylist/ScreenEntry (ekran listeleri), ScreenConfig, AppSettings
│   ├── Storage/                    AtomicJsonFile (tmp → File.Replace + .bak), JsonStore (birleştirilmiş arka plan yazımı)
│   ├── Playback/                   PlaylistScheduler (filtre, sıra, sonraki öğe, süre), ScheduleRules (gün/saat penceresi)
│   ├── Media/                      MediaFormats, Mp4Probe (MP4/MOV codec/süre/çözünürlük), PerceptualHash (dHash)
│   ├── Hotkeys/                    kısayol ayrıştırma, doğrulama ve çakışma kuralları
│   ├── Displays/DisplayMatcher     kayıtlı ekranı bulma kuralları
│   └── Services/                   Settings, Playlist (kütüphane + ekran listeleri), MediaImport (kopya kontrolü),
│                                   FolderSync, Backup, CrashGuard, PinHasher, ReleaseInfo
├── src/KodizSignage/               net8.0-windows WPF uygulaması
│   ├── Services/                   PlaybackManager, DisplayService, StartupService, InstallService, PowerService,
│   │                               HotkeyService, ShortcutService, SingleInstanceService, TrayService, PinGate,
│   │                               LocalizationService, ThumbnailService, WpfMediaInspector (PDF, codec, parmak izi),
│   │                               UpdateService, DuplicateResolver, ScreenColors, SmokeTest
│   ├── Views/Player/               PlaybackEngine (döngü), MediaLayer (A/B katmanı), GifAnimation
│   ├── Views/                      PlayerWindow, SettingsWindow, LibraryPickerWindow, DuplicateWindow, PinWindow, IdentifyWindow
│   ├── ViewModels/                 Settings, Media (kütüphane), Display + ScreenEditor + Entry (ekranlar), General, Shortcuts
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

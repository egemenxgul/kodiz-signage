namespace KodizSignage.Core.Web;

/// <summary>The single-page phone panel (no external files: works without internet).</summary>
public static class WebPanelPage
{
    public const string Html = """
<!doctype html>
<html lang="tr">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover">
<meta name="theme-color" content="#0f1117">
<title>Kodiz Signage</title>
<style>
:root{--bg:#0f1117;--card:#181b24;--line:#272b36;--text:#eef0f4;--muted:#9aa3b2;--accent:#6366f1;--ok:#22c55e;--warn:#f59e0b;--danger:#ef4444}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:15px/1.4 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif;padding:0 16px calc(24px + env(safe-area-inset-bottom))}
header{position:sticky;top:0;background:var(--bg);display:flex;align-items:center;gap:10px;padding:14px 0 10px;z-index:5}
header h1{font-size:18px;margin:0;flex:1}
.logo{width:30px;height:30px;border-radius:9px;background:linear-gradient(135deg,#4f46e5,#14b8a6);display:grid;place-items:center}
.logo:after{content:"";border-left:10px solid #fff;border-top:6px solid transparent;border-bottom:6px solid transparent;margin-left:3px}
.card{background:var(--card);border:1px solid var(--line);border-radius:14px;padding:14px;margin:0 0 12px}
h2{font-size:13px;text-transform:uppercase;letter-spacing:.06em;color:var(--muted);margin:18px 2px 8px}
button{font:inherit;border:0;border-radius:10px;padding:10px 14px;background:#262a35;color:var(--text);cursor:pointer}
button.primary{background:var(--accent);color:#fff;font-weight:600}
button.danger{background:transparent;color:var(--danger);padding:8px}
button:disabled{opacity:.5}
.row{display:flex;align-items:center;gap:10px}
.grow{flex:1;min-width:0}
.muted{color:var(--muted);font-size:13px}
.ellipsis{white-space:nowrap;overflow:hidden;text-overflow:ellipsis}
.dot{width:9px;height:9px;border-radius:50%;background:var(--muted);flex:none}
.dot.on{background:var(--ok)}.dot.warn{background:var(--warn)}
.switch{position:relative;width:46px;height:28px;flex:none}
.switch input{opacity:0;width:0;height:0}
.switch span{position:absolute;inset:0;background:#3a3f4c;border-radius:20px;transition:.2s}
.switch span:before{content:"";position:absolute;width:22px;height:22px;left:3px;top:3px;background:#fff;border-radius:50%;transition:.2s}
.switch input:checked+span{background:var(--accent)}
.switch input:checked+span:before{transform:translateX(18px)}
.media{display:flex;gap:12px;align-items:center;padding:10px 0;border-top:1px solid var(--line)}
.media:first-child{border-top:0}
.media img{width:72px;height:41px;object-fit:cover;border-radius:6px;background:#000;flex:none}
.chips{display:flex;gap:6px;flex-wrap:wrap;margin-top:6px}
.chip{font-size:12px;padding:4px 9px;border-radius:20px;background:#262a35;color:var(--muted)}
.chip.on{background:rgba(99,102,241,.25);color:#c7c9ff}
input[type=password],input[type=tel]{width:100%;font:inherit;font-size:22px;letter-spacing:.3em;text-align:center;padding:12px;border-radius:10px;border:1px solid var(--line);background:#0b0d12;color:var(--text)}
#login{max-width:360px;margin:12vh auto 0;text-align:center}
#toast{position:fixed;left:16px;right:16px;bottom:calc(16px + env(safe-area-inset-bottom));background:#2b3040;border-radius:12px;padding:12px 14px;display:none;z-index:9}
.progress{height:6px;background:#262a35;border-radius:3px;overflow:hidden;margin-top:8px;display:none}
.progress div{height:100%;width:0;background:var(--accent)}
.hidden{display:none!important}
</style>
</head>
<body>
<div id="login" class="hidden">
  <div class="logo" style="width:56px;height:56px;margin:0 auto 18px"></div>
  <h1 style="font-size:20px" data-t="title"></h1>
  <p class="muted" data-t="pinHint"></p>
  <form id="loginForm"><input id="pin" type="password" inputmode="numeric" autocomplete="current-password" maxlength="12" required>
  <p id="loginError" class="muted" style="color:var(--danger);min-height:20px"></p>
  <button class="primary" style="width:100%" data-t="signIn"></button></form>
</div>
<div id="app" class="hidden">
  <header><div class="logo"></div><h1 data-t="title"></h1><button id="logout" data-t="signOut"></button></header>
  <div class="card row">
    <div class="dot" id="runDot"></div>
    <div class="grow"><div id="runText" style="font-weight:600"></div><div class="muted" id="summary"></div></div>
    <button class="primary" id="toggle"></button>
  </div>
  <h2 data-t="screens"></h2>
  <div id="screens"></div>
  <h2 data-t="library"></h2>
  <div class="card">
    <div class="row"><div class="grow muted" data-t="uploadHint"></div>
      <button class="primary" id="uploadBtn" data-t="upload"></button></div>
    <input id="file" type="file" multiple accept="image/*,video/*,.pdf,.pptx,.ppt,.odp,.heic,.heif" class="hidden">
    <div class="muted" id="uploadText"></div>
    <div class="progress" id="progress"><div></div></div>
  </div>
  <div class="card" id="library"></div>
</div>
<div id="toast"></div>
<script>
const T={tr:{title:"Kodiz Signage",pinHint:"Bilgisayardaki Ayarlar › Genel › Telefondan yönetim bölümünde belirlediğiniz PIN'i girin.",signIn:"Giriş",signOut:"Çıkış",wrongPin:"PIN yanlış",locked:"Çok fazla deneme. {0} sn bekleyin.",screens:"Ekranlar",library:"Medya",playing:"Gösterim açık",stopped:"Gösterim durdu",start:"Başlat",stop:"Durdur",next:"Sonraki",onScreens:"{0} ekran · {1} medya",upload:"Yükle",uploadHint:"Telefondan resim, video veya PDF ekleyin. Yeni medya 'otomatik ekle' açık ekranlara gelir.",uploading:"Yükleniyor {0}/{1}: {2}",uploaded:"Yüklendi",deleteConfirm:"\"{0}\" silinsin mi? Tüm ekranlardan kaldırılır.",off:"Kapalı",active:"Etkin",notPlaying:"Şu an oynamıyor",empty:"Kütüphane boş",offline:"Bağlantı yok – yeniden deneniyor…",error:"İşlem başarısız"},
en:{title:"Kodiz Signage",pinHint:"Enter the PIN set on the PC under Settings › General › Phone control.",signIn:"Sign in",signOut:"Sign out",wrongPin:"Wrong PIN",locked:"Too many attempts. Wait {0} s.",screens:"Screens",library:"Media",playing:"Playing",stopped:"Stopped",start:"Start",stop:"Stop",next:"Next",onScreens:"{0} screens · {1} media",upload:"Upload",uploadHint:"Add images, videos or PDFs from your phone. New media goes to screens with 'add automatically' on.",uploading:"Uploading {0}/{1}: {2}",uploaded:"Uploaded",deleteConfirm:"Delete \"{0}\"? It is removed from all screens.",off:"Off",active:"Active",notPlaying:"Not playing now",empty:"The library is empty",offline:"Offline – retrying…",error:"Action failed"}};
let lang=(navigator.language||"tr").toLowerCase().startsWith("tr")?"tr":"en";
const $=id=>document.getElementById(id);
const t=(k,...a)=>(T[lang][k]||k).replace(/\{(\d)\}/g,(_,i)=>a[i]);
function el(tag,cls,text){const e=document.createElement(tag);if(cls)e.className=cls;if(text!=null)e.textContent=text;return e}
function applyTexts(){document.documentElement.lang=lang;document.querySelectorAll("[data-t]").forEach(e=>e.textContent=t(e.dataset.t))}
let toastTimer;function toast(m){const x=$("toast");x.textContent=m;x.style.display="block";clearTimeout(toastTimer);toastTimer=setTimeout(()=>x.style.display="none",3000)}
async function api(path,opts={}){const r=await fetch(path,{credentials:"same-origin",...opts,headers:{"X-Kodiz":"1",...(opts.headers||{})}});
 if(r.status===401&&path!=="/api/login"){showLogin();throw new Error("auth")}return r}
async function post(path){try{const r=await api(path,{method:"POST"});if(!r.ok)throw 0;await refresh()}catch(e){if(e.message!=="auth")toast(t("error"))}}
function showLogin(){$("app").classList.add("hidden");$("login").classList.remove("hidden");$("pin").focus()}
function showApp(){$("login").classList.add("hidden");$("app").classList.remove("hidden")}
$("loginForm").onsubmit=async e=>{e.preventDefault();$("loginError").textContent="";
 const r=await api("/api/login",{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify({pin:$("pin").value})});
 if(r.ok){$("pin").value="";showApp();refresh(true)}else{const j=await r.json().catch(()=>({}));$("loginError").textContent=j.error==="locked"?t("locked",j.wait):t("wrongPin")}};
$("logout").onclick=async()=>{await api("/api/logout",{method:"POST"}).catch(()=>{});showLogin()};
$("toggle").onclick=()=>post("/api/toggle");
let status=null,library=[];
function switchEl(checked,onchange){const l=el("label","switch"),i=el("input");i.type="checkbox";i.checked=checked;i.onchange=()=>onchange(i.checked);l.append(i,el("span"));return l}
function renderStatus(){const s=status;if(!s)return;if(s.language&&T[s.language]&&lang!==s.language){lang=s.language;applyTexts()}
 $("runDot").className="dot"+(s.running?" on":"");$("runText").textContent=s.running?t("playing"):t("stopped");
 $("summary").textContent=t("onScreens",s.screens.filter(x=>x.enabled).length,s.mediaCount);
 $("toggle").textContent=s.running?t("stop"):t("start");
 const box=$("screens");box.replaceChildren();
 for(const sc of s.screens){const c=el("div","card"),r=el("div","row");
  const d=el("div","dot"+(sc.enabled&&s.running?" on":""));const info=el("div","grow");
  info.append(el("div","ellipsis",sc.name),el("div","muted ellipsis",sc.enabled?(sc.nowPlaying||sc.status):t("off")));
  const nx=el("button",null,t("next"));nx.disabled=!s.running||!sc.enabled;nx.onclick=()=>post("/api/next?screen="+sc.number);
  r.append(d,info,nx,switchEl(sc.enabled,on=>post(`/api/screen?n=${sc.number}&on=${on?1:0}`)));c.append(r);box.append(c)}}
function renderLibrary(){const box=$("library");box.replaceChildren();if(!library.length){box.append(el("div","muted",t("empty")));return}
 const screens=status?status.screens:[];
 for(const m of library){const row=el("div","media"),img=el("img");img.loading="lazy";img.alt="";img.src="/api/thumb?id="+m.id;
  const info=el("div","grow");info.append(el("div","ellipsis",m.title));
  const sub=el("div","muted");sub.textContent=m.playableNow?m.type:t("notPlaying");if(m.hasWarning)sub.style.color="var(--warn)";info.append(sub);
  if(screens.length>1){const chips=el("div","chips");for(const sc of screens){const on=m.screens.includes(sc.number);const c=el("button","chip"+(on?" on":""),String(sc.number));c.title=sc.name;c.onclick=()=>post(`/api/media/screen?id=${m.id}&n=${sc.number}&on=${on?0:1}`);chips.append(c)}info.append(chips)}
  const del=el("button","danger","✕");del.setAttribute("aria-label","delete");del.onclick=()=>{if(confirm(t("deleteConfirm",m.title)))post("/api/media/delete?id="+m.id)};
  row.append(img,info,switchEl(m.active,on=>post(`/api/media/active?id=${m.id}&on=${on?1:0}`)),del);box.append(row)}}
async function refresh(withLibrary=true){try{const r=await api("/api/status");if(!r.ok)throw 0;status=await r.json();showApp();renderStatus();
 if(withLibrary){const l=await api("/api/library");if(l.ok){library=await l.json();renderLibrary()}}}catch(e){if(e.message!=="auth")toast(t("offline"))}}
$("uploadBtn").onclick=()=>$("file").click();
$("file").onchange=async()=>{const files=[...$("file").files];$("file").value="";if(!files.length)return;
 const bar=$("progress"),fill=bar.firstElementChild;bar.style.display="block";$("uploadBtn").disabled=true;
 for(let i=0;i<files.length;i++){const f=files[i];$("uploadText").textContent=t("uploading",i+1,files.length,f.name);
  const msg=await new Promise(res=>{const x=new XMLHttpRequest();x.open("POST","/api/upload?name="+encodeURIComponent(f.name));x.setRequestHeader("X-Kodiz","1");
   x.upload.onprogress=e=>{if(e.lengthComputable)fill.style.width=(100*e.loaded/e.total)+"%"};
   x.onload=()=>{if(x.status===401){showLogin();res(null)}else{try{res(JSON.parse(x.responseText).message)}catch{res(t("error"))}}};x.onerror=()=>res(t("error"));x.send(f)});
  if(msg)toast(msg)}
 bar.style.display="none";fill.style.width="0";$("uploadText").textContent="";$("uploadBtn").disabled=false;refresh()};
applyTexts();refresh();let tick=0;setInterval(()=>{if(!document.hidden&&!$("app").classList.contains("hidden"))refresh(++tick%6===0)},5000);
</script>
</body>
</html>
""";
}

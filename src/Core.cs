using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ReFix {
 public class Asset { public string id; public string title; public string url; public string sha256; public long bytes; public string group; }
 public class LauncherAsset : Asset {public string version;}
 public class Catalog { public int schema; public string version; public string notes; public Asset patch; public List<Asset> addons; public LauncherAsset launcher; public List<InstallProfile> profiles; }
 public class FileRow { public string file; public long bytes; public string sha256; }
 public class ActiveAddon { public string id; public string archive; public Dictionary<string,string> originals = new Dictionary<string,string>(); }
 public class LocalState { public string version = "Не определена"; public string patchHash = ""; public List<ActiveAddon> addons = new List<ActiveAddon>(); }
 public class Change { public string path; public string backup; }
 public class Journal { public string status = "pending"; public List<Change> changes = new List<Change>(); }
 public static class OperationLog {
  static readonly object gate=new object();
  public static void Write(string root,string stage,string detail="") {
   lock(gate) {
    string path=Data.Safe(root,".refix-launcher/operations.log");Directory.CreateDirectory(Path.GetDirectoryName(path));
    byte[] data=Encoding.UTF8.GetBytes(DateTime.UtcNow.ToString("o")+" | "+stage+" | "+detail.Replace("\r"," ").Replace("\n"," ")+"\r\n");
    using(var f=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read)){f.Write(data,0,data.Length);f.Flush(true);}
   }
  }
 }
 public class PreparedPatch {
  public string Archive {get;internal set;}
  public string Hash {get;internal set;}
  public string Version {get;internal set;}
  public string Root {get;internal set;}
  public string[] Changes {get;internal set;}
 }

 public static class Data {
  public static readonly Encoding Legacy = Encoding.GetEncoding(1251);
  public static string Json(object value) { return new JavaScriptSerializer { MaxJsonLength = 16000000 }.Serialize(value); }
  public static T Read<T>(string text) { return new JavaScriptSerializer { MaxJsonLength = 16000000 }.Deserialize<T>(text); }
  public static string Hash(byte[] bytes) { using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
  public static string HashFile(string path) { using(var f=File.OpenRead(path)) using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant(); }
  public static string Safe(string root, string relative) {
   if(String.IsNullOrWhiteSpace(relative) || relative.Contains("\\") || relative.Contains(":") || relative.StartsWith("/") || relative.Split('/').Any(p=>p==".." || p=="." || p.Length==0 || p.EndsWith(".") || p.EndsWith(" ")))
    throw new InvalidDataException("Недопустимый путь: " + relative);
   string full=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
   string prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
   if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь вне папки игры");
   for(string p=full; p!=null && p.Length>=prefix.Length-1; p=Path.GetDirectoryName(p))
    if((File.Exists(p)||Directory.Exists(p)) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0) throw new IOException("Ссылки и перенаправления папок не поддерживаются: "+p);
   return full;
  }
  public static void Atomic(string path, byte[] bytes) {
   Directory.CreateDirectory(Path.GetDirectoryName(path)); string tmp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
   using(var f=new FileStream(tmp,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { f.Write(bytes,0,bytes.Length); f.Flush(true); }
   if(File.Exists(path)) File.Replace(tmp,path,null); else File.Move(tmp,path);
  }
  public static void WriteJson(string path,object value) { Atomic(path,Encoding.UTF8.GetBytes(Json(value))); }
 }

 // Every mutation is journaled before writing; interrupted operations roll back on next launch.
 public sealed class Transaction : IDisposable {
  readonly string root, folder; Journal journal;
  public Transaction(string root) {
   this.root=root; folder=Data.Safe(root,".refix-launcher/transactions/"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(folder); journal=new Journal(); Save();
  }
  void Save() { Data.WriteJson(Path.Combine(folder,"journal.json"),journal); }
  void Remember(string relative) {
   string path=Data.Safe(root,relative);
   if(journal.changes.Any(c=>String.Equals(c.path,relative,StringComparison.OrdinalIgnoreCase))) return;
   var change=new Change {path=relative};
   if(File.Exists(path)) { change.backup="before/"+journal.changes.Count; string b=Data.Safe(folder,change.backup);Directory.CreateDirectory(Path.GetDirectoryName(b));File.Copy(path,b); }
   journal.changes.Add(change); Save();
  }
  public void Put(string relative, byte[] data) { Remember(relative); OperationLog.Write(root,"FILE_WRITE_BEGIN",relative); Data.Atomic(Data.Safe(root,relative),data); OperationLog.Write(root,"FILE_WRITE_END",relative); }
  public void Remove(string relative) {
   Remember(relative); string path=Data.Safe(root,relative);
   if(File.Exists(path)) { string dest=Path.Combine(folder,"removed",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.GetDirectoryName(dest));File.Move(path,dest); }
  }
  public void Commit() { journal.status="committed"; Save(); }
  public void Dispose() { if(journal.status=="pending") Rollback(root,folder,journal); }
  static void Rollback(string root,string folder,Journal journal) {
   OperationLog.Write(root,"ROLLBACK_BEGIN",Path.GetFileName(folder));
   foreach(var c in journal.changes.AsEnumerable().Reverse()) {
    string target=Data.Safe(root,c.path);
    if(c.backup!=null) Data.Atomic(target,File.ReadAllBytes(Data.Safe(folder,c.backup)));
    else if(File.Exists(target)) { string dest=Path.Combine(folder,"discarded",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.GetDirectoryName(dest));File.Move(target,dest); }
   }
   journal.status="rolled-back"; Data.WriteJson(Path.Combine(folder,"journal.json"),journal);
   OperationLog.Write(root,"ROLLBACK_COMPLETE",Path.GetFileName(folder));
  }
  public static void Recover(string root) {
   string dir=Data.Safe(root,".refix-launcher/transactions"); if(!Directory.Exists(dir)) return;
   foreach(string folder in Directory.GetDirectories(dir)) {
    string f=Path.Combine(folder,"journal.json"); if(!File.Exists(f)) continue;
    var journal=Data.Read<Journal>(File.ReadAllText(f)); if(journal.status=="pending") Rollback(root,folder,journal);
   }
  }
 }

 public class Package {
  public Dictionary<string,byte[]> Files = new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
  public static Package Load(string path, bool addon) {
   var package=new Package();
   using(var zip=ZipFile.OpenRead(path)) {
    var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(var e in zip.Entries) {
     if(e.FullName.EndsWith("/")) continue;
     Data.Safe(Path.GetTempPath(),e.FullName);
     if(!names.Add(e.FullName)) throw new InvalidDataException("Повторяющийся файл в архиве");
    }
    var manifest=zip.GetEntry("MANIFEST.json"); if(manifest==null || manifest.Length>16000000) throw new InvalidDataException("Нет манифеста пакета");
    List<FileRow> rows; using(var r=new StreamReader(manifest.Open(),Encoding.UTF8)) rows=Data.Read<List<FileRow>>(r.ReadToEnd());
    var expected=new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total=0;
    foreach(var row in rows) {
     Data.Safe(Path.GetTempPath(),row.file);
     if(row.file=="MANIFEST.json" || !expected.Add(row.file) || row.bytes<0 || row.bytes>268435456 || !Regex.IsMatch(row.sha256??"","^[a-f0-9]{64}$")) throw new InvalidDataException("Неверный манифест");
     total+=row.bytes; if(total>2147483648L) throw new InvalidDataException("Пакет слишком велик");
     var entry=zip.GetEntry(row.file); if(entry==null || entry.Length!=row.bytes) throw new InvalidDataException("Размер файла не совпал: "+row.file);
     byte[] bytes; using(var stream=entry.Open()) using(var mem=new MemoryStream()) { stream.CopyTo(mem);bytes=mem.ToArray(); }
     if(Data.Hash(bytes)!=row.sha256) throw new InvalidDataException("Контрольная сумма не совпала: "+row.file);
     string n=row.file.ToLowerInvariant();
     bool allowed=n.StartsWith("gamedata/") || (!addon && n.StartsWith("bin/") && (n.EndsWith(".dll")||n.EndsWith(".exe")));
     if(n=="gamedata/config/bz_lord.ltx") continue;
     if(allowed) package.Files.Add(row.file,bytes);
     else if(!n.StartsWith("docs/") && !n.EndsWith("readme.txt") && !n.EndsWith("readme.md")) throw new InvalidDataException("Файл не разрешён в пакете: "+row.file);
    }
    expected.Add("MANIFEST.json"); if(!expected.SetEquals(names)) throw new InvalidDataException("Состав пакета не совпал с манифестом");
   }
   return package;
  }
 }

 public static class Config {
  public static string Value(string text,string key,string section=null) {
   string active=""; string value=null;
   foreach(string line in text.Replace("\r","").Split('\n')) {
    var sec=Regex.Match(line,@"^\s*\[([^\]]+)\]"); if(sec.Success) {active=sec.Groups[1].Value;continue;}
    if(section!=null && active!=section) continue;
    var m=Regex.Match(line,@"^\s*"+Regex.Escape(key)+@"(?:\s*=\s*|\s+)([^;\r\n]+)");if(m.Success)value=m.Groups[1].Value.Trim();
   }return value;
  }
  public static string Set(string text,string key,string value,string section=null) {
   if(value.Contains("\n")||value.Contains("\r"))throw new ArgumentException("Неверное значение");
   var lines=text.Replace("\r","").Split('\n').ToList();string active="";bool found=false;int insert=-1;
   for(int i=0;i<lines.Count;i++) {
    var sec=Regex.Match(lines[i],@"^\s*\[([^\]]+)\]");
    if(sec.Success) { if(active==section && insert<0)insert=i;active=sec.Groups[1].Value;continue; }
    if(section!=null && active!=section)continue;
    var m=Regex.Match(lines[i],@"^(\s*"+Regex.Escape(key)+@"(?:\s*=\s*|\s+))([^;]*)(;.*)?$");
    if(m.Success) {lines[i]=m.Groups[1].Value+value+(m.Groups[3].Success?" "+m.Groups[3].Value:""); found=true;}
   }
   if(!found) {
    string line=key+(section==null?" ":" = ")+value;
    if(section==null)lines.Add(line);
    else if(insert>=0)lines.Insert(insert,line);
    else if(active==section)lines.Add(line);
    else {lines.Add("["+section+"]");lines.Add(line);}
   }
   return String.Join("\r\n",lines).TrimEnd('\r','\n')+"\r\n";
  }
 }

 public class GameService {
  public const string Repository="SunChessss/Bolshaya-Zona-ReFix";
  public string Root {get;private set;}
  public LocalState State {get;private set;}
  public Action<string> Progress = delegate{};
  public void Log(string stage,string detail="") {OperationLog.Write(Root,stage,detail);}
  public GameService(string root) {
   Root=Path.GetFullPath(root);
   if(!File.Exists(Data.Safe(Root,"bin/XR_3DA.exe")) || !File.Exists(Data.Safe(Root,"gamedata/config/bz_lord.ltx")) || !File.Exists(Data.Safe(Root,"fsgame.ltx"))) throw new IOException("Выберите корень сборки ReFix: рядом должны быть bin, gamedata и fsgame.ltx.");
   EnsureStopped(); Transaction.Recover(Root); LoadState();
  }
  public void LoadState() { string p=Data.Safe(Root,".refix-launcher/state.json");State=File.Exists(p)?Data.Read<LocalState>(File.ReadAllText(p)):new LocalState(); }
  public void EnsureStopped() {
   // Conservatively refuse mutations while any X-Ray game is running, including inaccessible processes.
   if(Process.GetProcessesByName("XR_3DA").Length!=0)throw new IOException("Сначала закройте игру. Изменения доступны после её завершения.");
  }
  static HttpClient Client() { ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;var c=new HttpClient();c.Timeout=TimeSpan.FromMinutes(20);c.DefaultRequestHeaders.UserAgent.ParseAdd("ReFixLauncher/1.0");return c; }
  public static void ValidateUrl(string url) {
   Uri u; if(!Uri.TryCreate(url,UriKind.Absolute,out u) || u.Scheme!="https" || !String.Equals(u.Host,"github.com",StringComparison.OrdinalIgnoreCase) || !u.AbsolutePath.StartsWith("/"+Repository+"/releases/download/",StringComparison.Ordinal)) throw new InvalidDataException("Пакет должен находиться в GitHub Releases сборки.");
  }
  public async Task<Catalog> Check() {
   using(var client=Client()) {
    client.Timeout=TimeSpan.FromSeconds(30);
    string json=await client.GetStringAsync("https://api.github.com/repos/"+Repository+"/releases/latest");
    var release=Data.Read<Dictionary<string,object>>(json);
    var assets=(System.Collections.IEnumerable)release["assets"];string url=null;
    foreach(Dictionary<string,object> a in assets) if((string)a["name"]=="launcher-manifest.json") url=(string)a["browser_download_url"];
    if(url==null)throw new IOException("В последнем релизе пока нет каталога лаунчера."); ValidateUrl(url);
    var catalog=Data.Read<Catalog>(await client.GetStringAsync(url));
    if(catalog.schema!=1 || catalog.patch==null || catalog.addons==null || catalog.addons.Count>12)throw new InvalidDataException("Неподдерживаемый каталог обновлений");
    var ids=new HashSet<string>();
    foreach(var a in catalog.addons.Concat(new[]{catalog.patch})) {
     ValidateUrl(a.url);
     if(!Regex.IsMatch(a.sha256??"","^[a-f0-9]{64}$") || a.bytes<=0 || a.bytes>1073741824 || !Regex.IsMatch(a.id??"","^[a-z0-9_-]+$") || !ids.Add(a.id))throw new InvalidDataException("Неверное описание пакета");
    }
    Installation.Validate(catalog.profiles);   // 1.4: reference file lists of the builds come with the catalog
    return catalog;
   }
  }
  public async Task<string> Download(Asset asset) {
   OperationLog.Write(Root,"DOWNLOAD_BEGIN",asset.id);
   ValidateUrl(asset.url); string relative=".refix-launcher/cache/"+asset.sha256+".zip";string path=Data.Safe(Root,relative);
   if(File.Exists(path) && new FileInfo(path).Length==asset.bytes && Data.HashFile(path)==asset.sha256){OperationLog.Write(Root,"DOWNLOAD_CACHE_VERIFIED",asset.id);return relative;}
   Directory.CreateDirectory(Path.GetDirectoryName(path)); string part=path+"."+Guid.NewGuid().ToString("N")+".part";
   using(var client=Client()) using(var response=await client.GetAsync(asset.url,HttpCompletionOption.ResponseHeadersRead)) {
    response.EnsureSuccessStatusCode();
    using(var input=await response.Content.ReadAsStreamAsync()) using(var output=new FileStream(part,FileMode.CreateNew)) {
     byte[] buffer=new byte[131072];long count=0;int n;long last=-1;
     while((n=await input.ReadAsync(buffer,0,buffer.Length))>0) {count+=n;if(count>asset.bytes)throw new InvalidDataException("Размер загрузки превышен");await output.WriteAsync(buffer,0,n);long percent=count*100/asset.bytes;if(percent!=last){Progress("Загрузка «"+asset.title+"»: "+percent+"%");last=percent;}}
     if(count!=asset.bytes)throw new InvalidDataException("Загрузка не завершена");
    }
   }
   OperationLog.Write(Root,"DOWNLOAD_RECEIVED",asset.id);
   if(Data.HashFile(part)!=asset.sha256)throw new InvalidDataException("Загруженный архив повреждён. Попробуйте ещё раз.");
   if(File.Exists(path))File.Move(path,path+".invalid-"+Guid.NewGuid().ToString("N"));File.Move(part,path);OperationLog.Write(Root,"DOWNLOAD_VERIFIED",asset.id);return relative;
  }
  public PreparedPatch PreparePatch(string archive,string version,string hash) {
   OperationLog.Write(Root,"PREVIEW_BEGIN",archive);
   string path=Data.Safe(Root,archive);
   if(Data.HashFile(path)!=hash)throw new InvalidDataException("Архив изменился. Скачайте обновление заново.");
   var patch=Package.Load(path,false);
   var lines=patch.Files.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>
    (File.Exists(Data.Safe(Root,p.Key))?(Data.HashFile(Data.Safe(Root,p.Key))==Data.Hash(p.Value)?"Без изменений: ":"Заменить: "):"Добавить: ")+p.Key).ToList();
   foreach(var a in State.addons)lines.Add("Снять и повторно установить дополнение: "+a.id);
   OperationLog.Write(Root,"PREVIEW_READY",patch.Files.Count.ToString());
   return new PreparedPatch {Root=Root,Archive=archive,Version=version,Hash=hash,Changes=lines.ToArray()};
  }
  public void InstallPreparedPatch(PreparedPatch prepared) {
   if(prepared==null || prepared.Root!=Root)throw new InvalidOperationException("Сначала скачайте и проверьте обновление для этой папки игры.");
   EnsureStopped();OperationLog.Write(Root,"INSTALL_REVALIDATE",prepared.Archive);
   if(Data.HashFile(Data.Safe(Root,prepared.Archive))!=prepared.Hash)throw new InvalidDataException("Архив изменился после просмотра. Скачайте обновление заново.");
   ApplyPatch(prepared.Archive,prepared.Version,prepared.Hash);
  }
  void Restore(ActiveAddon addon,Transaction tx) {
   foreach(var item in addon.originals) {if(item.Value==null)tx.Remove(item.Key);else tx.Put(item.Key,File.ReadAllBytes(Data.Safe(Root,item.Value)));}
  }
  ActiveAddon Activate(string id,string archive,Package package,Transaction tx) {
   var active=new ActiveAddon{id=id,archive=archive}; string prefix=".refix-launcher/bases/"+Guid.NewGuid().ToString("N")+"/";
   foreach(var item in package.Files) {
    string p=Data.Safe(Root,item.Key);string backup=null;
    if(File.Exists(p)) {backup=prefix+item.Key;tx.Put(backup,File.ReadAllBytes(p));}
    active.originals.Add(item.Key,backup);tx.Put(item.Key,item.Value);
   }return active;
  }
  public void ApplyPatch(string archive,string version,string hash) {
   OperationLog.Write(Root,"INSTALL_BEGIN",version);
   EnsureStopped();var patch=Package.Load(Data.Safe(Root,archive),false);
   var layers=State.addons.Select(a=>new {a.id,a.archive,package=Package.Load(Data.Safe(Root,a.archive),true)}).ToList();
   using(var tx=new Transaction(Root)) {
    foreach(var a in State.addons.AsEnumerable().Reverse()) Restore(a,tx);
    foreach(var item in patch.Files) tx.Put(item.Key,item.Value);
    var next=new LocalState {version=version,patchHash=hash};
    foreach(var a in layers) next.addons.Add(Activate(a.id,a.archive,a.package,tx));
    tx.Put(".refix-launcher/state.json",Encoding.UTF8.GetBytes(Data.Json(next)));tx.Commit();State=next;
   }
   OperationLog.Write(Root,"INSTALL_COMPLETE",version);
  }
  public void ApplyAddons(Dictionary<string,string> selected) {
   EnsureStopped();var packages=selected.ToDictionary(p=>p.Key,p=>Package.Load(Data.Safe(Root,p.Value),true));
   var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var p in packages.Values)foreach(string n in p.Files.Keys)if(!names.Add(n))throw new IOException("Выбранные дополнения меняют одни и те же файлы. Установите один вариант.");
   using(var tx=new Transaction(Root)) {
    foreach(var a in State.addons.AsEnumerable().Reverse())Restore(a,tx);
    var next=new LocalState {version=State.version,patchHash=State.patchHash};
    foreach(var item in selected)next.addons.Add(Activate(item.Key,item.Value,packages[item.Key],tx));
    tx.Put(".refix-launcher/state.json",Encoding.UTF8.GetBytes(Data.Json(next)));tx.Commit();State=next;
   }
  }
  public string ReadConfig(string path) {string p=Data.Safe(Root,path);return File.Exists(p)?Data.Legacy.GetString(File.ReadAllBytes(p)):"";}
  public void CheckUserdata() {
   string f=ReadConfig("fsgame.ltx");
   if(!Regex.IsMatch(f,@"(?im)^\s*\$app_data_root\$\s*=\s*true\s*\|\s*false\s*\|\s*\$fs_root\$\s*\|\s*userdata\\?\s*$"))throw new IOException("Ожидается штатная папка userdata сборки. Проверьте fsgame.ltx.");
  }
  // 1.6: пресет дропа лута с трупов NPC — [bz_loot] preset в bz_lord.ltx (читается скриптом death_manager при загрузке игры).
  public static readonly string[] LootPresets={"mod","reduced","vanilla","scavenger"};
  public void SaveSettings(Dictionary<string,string> graphics,decimal weight,decimal maximum,decimal speed,string loot="mod") {
   EnsureStopped();CheckUserdata();
   if(weight<1 || maximum<weight || maximum>10000 || speed<0.25m || speed>5)throw new ArgumentException("Проверьте вес и скорость: предел должен быть не меньше комфортного веса.");
   if(Array.IndexOf(LootPresets,loot)<0)throw new ArgumentException("Неизвестный пресет дропа лута: "+loot);
   string user=ReadConfig("userdata/user.ltx"), lord=ReadConfig("gamedata/config/bz_lord.ltx");
   foreach(var p in graphics) user=Config.Set(user,p.Key,p.Value);
   lord=Config.Set(lord,"walk_accel",(17m*speed).ToString("0.##",CultureInfo.InvariantCulture),"bz_lord_actor");
   lord=Config.Set(lord,"max_walk_weight",maximum.ToString(CultureInfo.InvariantCulture),"bz_lord_actor_condition");
   lord=Config.Set(lord,"max_weight",weight.ToString(CultureInfo.InvariantCulture),"bz_lord_inventory");
   lord=Config.Set(lord,"preset",loot,"bz_loot");
   using(var tx=new Transaction(Root)) {tx.Put("userdata/user.ltx",Data.Legacy.GetBytes(user));tx.Put("gamedata/config/bz_lord.ltx",Data.Legacy.GetBytes(lord));tx.Commit();}
  }
  public void Launch() {EnsureStopped();Process.Start(new ProcessStartInfo(Data.Safe(Root,"bin/XR_3DA.exe"),"-fsltx fsgame.ltx"){WorkingDirectory=Root,UseShellExecute=true});}
 }
}

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
 public class ActiveAddon { public string id; public string archive; public Dictionary<string,string> originals = new Dictionary<string,string>(); public Dictionary<string,string> files = new Dictionary<string,string>(); }   // 1.7: files = sha256 of the addon files (verify without reading the archive)
 public class LocalState { public string version = "Не определена"; public string patchHash = ""; public List<ActiveAddon> addons = new List<ActiveAddon>(); public List<string> declined = new List<string>(); public string grass; public Dictionary<string,string> grassOriginal = new Dictionary<string,string>(); }   // 1.7: declined = add-ons the player refused when offered automatically; 1.9: grass = radius of the installed xrRender pair (null = files of the build)   // 1.7: declined = add-ons the player refused when offered automatically
 public class Change { public string path; public string backup; public string aside; public string movedFrom; }   // 1.9: aside = original moved into the transaction folder, movedFrom = file moved here inside the game folder
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
  // 1.7: a root without the trailing separator ("D:\\Games\\X\\" -> "D:\\Games\\X"); "D:\\" stays. A trailing
  // backslash before the closing quote of --game "..." was read as an escaped quote and the launcher lost its game folder.
  public static string Root(string path) {
   string full=Path.GetFullPath(path.Trim().TrimEnd('"'));
   string trimmed=full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
   return trimmed.EndsWith(":")?trimmed+Path.DirectorySeparatorChar:trimmed;
  }
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
 // 1.9: the file being replaced is moved aside (a rename on the same volume, no copy) and every change is appended
 // to changes.log, so an operation over 1 400 texture files no longer copies 1.4 GB into the transaction folder and
 // no longer rewrites an ever-growing journal after each file. Journals of 1.8 and older (journal.json with
 // "changes" and copied "backup" files) are still rolled back.
 public sealed class Transaction : IDisposable {
  readonly string root, folder; Journal journal; readonly HashSet<string> seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase); int asideCount;
  public System.Threading.CancellationToken Token = System.Threading.CancellationToken.None;
  public Transaction(string root) {
   this.root=root; folder=Data.Safe(root,".refix-launcher/transactions/"+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N"));
   Directory.CreateDirectory(folder); journal=new Journal(); SaveStatus();
  }
  public string Folder { get { return folder; } }
  void SaveStatus() { Data.WriteJson(Path.Combine(folder,"journal.json"),new Journal{status=journal.status}); }
  void Record(Change c) {
   journal.changes.Add(c);
   byte[] line=Encoding.UTF8.GetBytes(Data.Json(c)+"\n");
   using(var f=new FileStream(Path.Combine(folder,"changes.log"),FileMode.Append,FileAccess.Write,FileShare.Read)){f.Write(line,0,line.Length);f.Flush(true);}
  }
  // The current file (if any) leaves its place before the first write to this path in the transaction.
  void Displace(string relative) {
   if(!seen.Add(relative)) return;   // already recorded: the file there now was written by this transaction
   string path=Data.Safe(root,relative);
   if(File.Exists(path)) {
    string aside="aside/"+(asideCount++).ToString(CultureInfo.InvariantCulture); string dest=Data.Safe(folder,aside);
    Directory.CreateDirectory(Path.GetDirectoryName(dest));
    Record(new Change{path=relative,aside=aside}); File.Move(path,dest);
   } else Record(new Change{path=relative});
  }
  public void Put(string relative, byte[] data) {
   Token.ThrowIfCancellationRequested(); Displace(relative);
   OperationLog.Write(root,"FILE_WRITE_BEGIN",relative); Data.Atomic(Data.Safe(root,relative),data); OperationLog.Write(root,"FILE_WRITE_END",relative);
  }
  public void Remove(string relative) { Token.ThrowIfCancellationRequested(); Displace(relative); }
  // 1.9: move a file inside the game folder (an original into .refix-launcher/bases and back); undone by moving back.
  public void Move(string fromRelative, string toRelative) {
   Token.ThrowIfCancellationRequested();
   string from=Data.Safe(root,fromRelative), to=Data.Safe(root,toRelative);
   if(!File.Exists(from)) throw new IOException("Нет файла для переноса: "+fromRelative);
   Displace(toRelative); Directory.CreateDirectory(Path.GetDirectoryName(to));
   Record(new Change{path=toRelative,movedFrom=fromRelative}); File.Move(from,to);
  }
  public void Commit() { journal.status="committed"; SaveStatus(); }
  public void Dispose() { if(journal.status=="pending") { Rollback(root,folder,journal.changes); journal.status="rolled-back"; SaveStatus(); } }
  static void Discard(string folder,string target) {
   string dest=Path.Combine(folder,"discarded",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(Path.GetDirectoryName(dest));File.Move(target,dest);
  }
  static void Rollback(string root,string folder,List<Change> changes) {
   OperationLog.Write(root,"ROLLBACK_BEGIN",Path.GetFileName(folder));
   foreach(var c in changes.AsEnumerable().Reverse()) {
    string target=Data.Safe(root,c.path);
    if(c.movedFrom!=null) {
     string source=Data.Safe(root,c.movedFrom);
     if(File.Exists(target) && !File.Exists(source)){Directory.CreateDirectory(Path.GetDirectoryName(source));File.Move(target,source);}
    } else if(c.backup!=null) {            // journal of 1.8 and older: a copy of the original
     Data.Atomic(target,File.ReadAllBytes(Data.Safe(folder,c.backup)));
    } else if(c.aside!=null) {
     string aside=Data.Safe(folder,c.aside);
     if(File.Exists(aside)) { if(File.Exists(target))Discard(folder,target); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Move(aside,target); }
    } else if(File.Exists(target)) Discard(folder,target);   // the file did not exist before
   }
   OperationLog.Write(root,"ROLLBACK_COMPLETE",Path.GetFileName(folder));
  }
  static List<Change> ReadChanges(string folder,Journal journal) {
   var list=new List<Change>(journal.changes??new List<Change>());
   string log=Path.Combine(folder,"changes.log");
   if(File.Exists(log)) foreach(string line in File.ReadAllText(log,Encoding.UTF8).Split('\n')) {
    if(line.Trim().Length==0) continue;
    try { list.Add(Data.Read<Change>(line)); } catch { }   // a line cut by a crash: its file operation did not start
   }
   return list;
  }
  public static void Recover(string root) {
   string dir=Data.Safe(root,".refix-launcher/transactions"); if(!Directory.Exists(dir)) return;
   foreach(string folder in Directory.GetDirectories(dir)) {
    string f=Path.Combine(folder,"journal.json"); if(!File.Exists(f)) continue;
    var journal=Data.Read<Journal>(File.ReadAllText(f)); if(journal.status!="pending") continue;
    Rollback(root,folder,ReadChanges(folder,journal));
    journal.status="rolled-back"; journal.changes=new List<Change>(); Data.WriteJson(f,journal);
   }
  }
  public static bool Pending(string root) {
   string dir=Data.Safe(root,".refix-launcher/transactions"); if(!Directory.Exists(dir)) return false;
   foreach(string folder in Directory.GetDirectories(dir)) {
    string f=Path.Combine(folder,"journal.json");
    try { if(File.Exists(f) && Data.Read<Journal>(File.ReadAllText(f)).status=="pending") return true; } catch { }
   }
   return false;
  }
 }

 // 1.7: packages are validated by streaming (hashes of every entry) and read file by file while applying, so a
 // 1.4 GB add-on (textures) does not have to sit in memory. Rows = manifest (file -> bytes/sha256) of allowed files.
 public class Package {
  public string Archive;
  public Dictionary<string,FileRow> Rows = new Dictionary<string,FileRow>(StringComparer.OrdinalIgnoreCase);
  public static Package Load(string path, bool addon) { return Load(path,addon,true); }
  public static Package Load(string path, bool addon, bool verifyEntries) {
   var package=new Package{Archive=path};
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
     if(verifyEntries) { string hash; using(var stream=entry.Open()) using(var h=SHA256.Create()) hash=BitConverter.ToString(h.ComputeHash(stream)).Replace("-","").ToLowerInvariant(); if(hash!=row.sha256) throw new InvalidDataException("Контрольная сумма не совпала: "+row.file); }
     string n=row.file.ToLowerInvariant();
     bool allowed=n.StartsWith("gamedata/") || (!addon && n.StartsWith("bin/") && (n.EndsWith(".dll")||n.EndsWith(".exe")));
     if(n=="gamedata/config/bz_lord.ltx") continue;
     if(allowed) package.Rows.Add(row.file,row);
     else if(!n.StartsWith("docs/") && !n.EndsWith("readme.txt") && !n.EndsWith("readme.md")) throw new InvalidDataException("Файл не разрешён в пакете: "+row.file);
    }
    expected.Add("MANIFEST.json"); if(!expected.SetEquals(names)) throw new InvalidDataException("Состав пакета не совпал с манифестом");
   }
   return package;
  }
  public Dictionary<string,string> Hashes() { return Rows.ToDictionary(p=>p.Key,p=>p.Value.sha256,StringComparer.OrdinalIgnoreCase); }
  public PackageReader Open() { return new PackageReader(this); }
 }
 public sealed class PackageReader : IDisposable {
  readonly Package package; readonly ZipArchive zip;
  public PackageReader(Package p) { package=p; zip=ZipFile.OpenRead(p.Archive); }
  public byte[] Read(string file) {
   FileRow row; if(!package.Rows.TryGetValue(file,out row)) throw new InvalidDataException("Файла нет в пакете: "+file);
   var entry=zip.GetEntry(row.file); if(entry==null) throw new InvalidDataException("Файла нет в архиве: "+file);
   byte[] bytes; using(var stream=entry.Open()) using(var mem=new MemoryStream()) { stream.CopyTo(mem);bytes=mem.ToArray(); }
   if(Data.Hash(bytes)!=row.sha256) throw new InvalidDataException("Контрольная сумма не совпала: "+file);
   return bytes;
  }
  public void Dispose() { zip.Dispose(); }
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
   Root=Data.Root(root);
   if(!File.Exists(Data.Safe(Root,"bin/XR_3DA.exe")) || !File.Exists(Data.Safe(Root,"gamedata/config/bz_lord.ltx")) || !File.Exists(Data.Safe(Root,"fsgame.ltx"))) throw new IOException("Выберите корень сборки ReFix: рядом должны быть bin, gamedata и fsgame.ltx.");
   EnsureStopped(); LoadState();   // 1.9: recovery of an interrupted operation runs in the background (Recover)
  }
  public System.Threading.CancellationToken Token = System.Threading.CancellationToken.None;   // 1.9: "Отмена" in the window
  // 1.9: roll back an interrupted operation (killed launcher, power loss) and drop finished transactions.
  public void Recover() { Transaction.Recover(Root); LoadState(); Cleanup(); }
  // 1.9: transaction folders of finished operations and bases no add-on refers to are deleted (1.8 kept them all:
  // every re-application of the textures add-on left another 1.4 GB in .refix-launcher).
  public void Cleanup() {
   try {
    string tx=Data.Safe(Root,".refix-launcher/transactions");
    if(Directory.Exists(tx)) foreach(string d in Directory.GetDirectories(tx)) {
     string j=Path.Combine(d,"journal.json"); string status="";
     try { if(File.Exists(j)) status=Data.Read<Journal>(File.ReadAllText(j)).status; } catch { status="pending"; }
     if(status!="pending") try { Directory.Delete(d,true); } catch { }
    }
    string bases=Data.Safe(Root,".refix-launcher/bases");
    if(Directory.Exists(bases)) {
     var used=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
     foreach(var a in State.addons) foreach(var v in a.originals.Values) if(v!=null) { var parts=v.Split('/'); if(parts.Length>2) used.Add(parts[2]); }
     foreach(string d in Directory.GetDirectories(bases)) if(!used.Contains(Path.GetFileName(d))) try { Directory.Delete(d,true); } catch { }
    }
    string cache=Data.Safe(Root,".refix-launcher/cache");
    if(Directory.Exists(cache)) foreach(string f in Directory.GetFiles(cache,"*.part")) try { File.Delete(f); } catch { }
   } catch(Exception ex) { Log("CLEANUP_ERROR",ex.Message); }
  }
  // 1.9: refuse before touching files when the disk cannot hold the new files
  public void CheckSpace(long bytes) {
   try {
    var drive=new DriveInfo(Path.GetPathRoot(Root)); long need=bytes+268435456L;
    if(drive.IsReady && drive.AvailableFreeSpace<need) throw new IOException("Недостаточно места на диске "+drive.Name+": нужно около "+(need/1048576)+" МБ, свободно "+(drive.AvailableFreeSpace/1048576)+" МБ.");
   } catch(ArgumentException) { }
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
     if(!Regex.IsMatch(a.sha256??"","^[a-f0-9]{64}$") || a.bytes<=0 || a.bytes>2147483648L || !Regex.IsMatch(a.id??"","^[a-z0-9_-]+$") || !ids.Add(a.id))throw new InvalidDataException("Неверное описание пакета");
    }
    Installation.Validate(catalog.profiles);   // 1.4: reference file lists of the builds come with the catalog
    return catalog;
   }
  }
  public async Task<string> Download(Asset asset) {
   OperationLog.Write(Root,"DOWNLOAD_BEGIN",asset.id);
   ValidateUrl(asset.url); string relative=".refix-launcher/cache/"+asset.sha256+".zip";string path=Data.Safe(Root,relative);
   // 1.9: a cached archive verified once is not hashed again while its size and time stay the same (.ok marker)
   if(File.Exists(path) && new FileInfo(path).Length==asset.bytes) {
    var info=new FileInfo(path); string stamp=asset.sha256+"|"+info.Length+"|"+info.LastWriteTimeUtc.Ticks; string ok=path+".ok";
    if(File.Exists(ok) && File.ReadAllText(ok)==stamp){OperationLog.Write(Root,"DOWNLOAD_CACHE_TRUSTED",asset.id);return relative;}
    Progress("Проверка загруженного архива «"+asset.title+"»…");
    if(Data.HashFile(path)==asset.sha256){File.WriteAllText(ok,stamp);OperationLog.Write(Root,"DOWNLOAD_CACHE_VERIFIED",asset.id);return relative;}
   }
   Directory.CreateDirectory(Path.GetDirectoryName(path)); string part=path+"."+Guid.NewGuid().ToString("N")+".part";
   using(var client=Client()) using(var response=await client.GetAsync(asset.url,HttpCompletionOption.ResponseHeadersRead)) {
    response.EnsureSuccessStatusCode();
    using(var input=await response.Content.ReadAsStreamAsync()) using(var output=new FileStream(part,FileMode.CreateNew)) {
     byte[] buffer=new byte[131072];long count=0;int n;long last=-1;
     while((n=await input.ReadAsync(buffer,0,buffer.Length,Token))>0) {count+=n;if(count>asset.bytes)throw new InvalidDataException("Размер загрузки превышен");await output.WriteAsync(buffer,0,n);long percent=count*100/asset.bytes;if(percent!=last){Progress("Загрузка «"+asset.title+"»: "+percent+"%");last=percent;}}
     if(count!=asset.bytes)throw new InvalidDataException("Загрузка не завершена");
    }
   }
   OperationLog.Write(Root,"DOWNLOAD_RECEIVED",asset.id);
   if(Data.HashFile(part)!=asset.sha256)throw new InvalidDataException("Загруженный архив повреждён. Попробуйте ещё раз.");
   if(File.Exists(path))File.Move(path,path+".invalid-"+Guid.NewGuid().ToString("N"));File.Move(part,path);
   var fi=new FileInfo(path);File.WriteAllText(path+".ok",asset.sha256+"|"+fi.Length+"|"+fi.LastWriteTimeUtc.Ticks);
   OperationLog.Write(Root,"DOWNLOAD_VERIFIED",asset.id);return relative;
  }
  public PreparedPatch PreparePatch(string archive,string version,string hash) {
   OperationLog.Write(Root,"PREVIEW_BEGIN",archive);
   string path=Data.Safe(Root,archive);
   if(Data.HashFile(path)!=hash)throw new InvalidDataException("Архив изменился. Скачайте обновление заново.");
   var patch=Package.Load(path,false);
   var lines=patch.Rows.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).Select(p=>
    (File.Exists(Data.Safe(Root,p.Key))?(Data.HashFile(Data.Safe(Root,p.Key))==p.Value.sha256?"Без изменений: ":"Заменить: "):"Добавить: ")+p.Key).ToList();
   foreach(var a in State.addons)lines.Add("Снять и повторно установить дополнение: "+a.id);
   OperationLog.Write(Root,"PREVIEW_READY",patch.Rows.Count.ToString());
   return new PreparedPatch {Root=Root,Archive=archive,Version=version,Hash=hash,Changes=lines.ToArray()};
  }
  public void InstallPreparedPatch(PreparedPatch prepared) {
   if(prepared==null || prepared.Root!=Root)throw new InvalidOperationException("Сначала скачайте и проверьте обновление для этой папки игры.");
   EnsureStopped();OperationLog.Write(Root,"INSTALL_REVALIDATE",prepared.Archive);
   if(Data.HashFile(Data.Safe(Root,prepared.Archive))!=prepared.Hash)throw new InvalidDataException("Архив изменился после просмотра. Скачайте обновление заново.");
   ApplyPatch(prepared.Archive,prepared.Version,prepared.Hash);
  }
  // 1.9: originals return by moving them back from .refix-launcher/bases (the add-on file is moved aside)
  void Restore(ActiveAddon addon,Transaction tx) {
   int done=0,total=addon.originals.Count;
   foreach(var item in addon.originals) {
    if(item.Value==null)tx.Remove(item.Key);
    else if(File.Exists(Data.Safe(Root,item.Value)))tx.Move(item.Value,item.Key);
    else throw new IOException("Нет сохранённого оригинала файла "+item.Key+" (дополнение «"+addon.id+"»). Проверьте установку.");
    if(++done%100==0 || done==total)Progress("Снятие «"+addon.id+"»: "+done+" из "+total);
   }
  }
  // 1.9: the original is moved into .refix-launcher/bases instead of being copied twice
  ActiveAddon Activate(string id,string archive,Package package,Transaction tx) {
   var active=new ActiveAddon{id=id,archive=archive,files=package.Hashes()}; string prefix=".refix-launcher/bases/"+Guid.NewGuid().ToString("N")+"/";
   int done=0,total=package.Rows.Count;
   using(var reader=package.Open()) foreach(var name in package.Rows.Keys.OrderBy(k=>k,StringComparer.OrdinalIgnoreCase)) {
    string p=Data.Safe(Root,name);string backup=null;
    if(File.Exists(p)) {backup=prefix+name;tx.Move(name,backup);}
    active.originals.Add(name,backup);tx.Put(name,reader.Read(name));
    if(++done%50==0 || done==total)Progress("Установка «"+id+"»: "+done+" из "+total);
   }return active;
  }
  LocalState NextState(string version,string hash) {
   return new LocalState {version=version,patchHash=hash,declined=State.declined??new List<string>(),grass=State.grass,grassOriginal=State.grassOriginal??new Dictionary<string,string>()};
  }
  static long Bytes(Package p){return p.Rows.Values.Sum(r=>r.bytes);}
  public void ApplyPatch(string archive,string version,string hash) {
   OperationLog.Write(Root,"INSTALL_BEGIN",version);
   EnsureStopped();var patch=Package.Load(Data.Safe(Root,archive),false,false);   // entries are verified while reading
   // 1.9: only add-ons that overlay files of the patch are taken off and put back (textures and seasons stay in place)
   var patchFiles=new HashSet<string>(patch.Rows.Keys,StringComparer.OrdinalIgnoreCase);
   var reapply=State.addons.Where(a=>a.originals.Keys.Any(patchFiles.Contains)).ToList();
   var layers=reapply.ToDictionary(a=>a.id,a=>Package.Load(Data.Safe(Root,a.archive),true,false));
   CheckSpace(Bytes(patch)+layers.Values.Sum(p=>Bytes(p)));
   using(var tx=new Transaction(Root)) {
    tx.Token=Token;
    foreach(var a in State.addons.AsEnumerable().Reverse()) if(reapply.Contains(a)) Restore(a,tx);
    int done=0,total=patch.Rows.Count;
    using(var reader=patch.Open()) foreach(var name in patch.Rows.Keys.OrderBy(k=>k,StringComparer.OrdinalIgnoreCase)) {tx.Put(name,reader.Read(name));if(++done%20==0 || done==total)Progress("Установка обновления: "+done+" из "+total);}
    var next=NextState(version,hash);
    foreach(var a in State.addons) next.addons.Add(reapply.Contains(a)?Activate(a.id,a.archive,layers[a.id],tx):a);
    if(patchFiles.Any(f=>Array.IndexOf(Grass.Files,f.ToLowerInvariant())>=0)) {next.grass=null;next.grassOriginal=new Dictionary<string,string>();}   // the patch brings its own renderer
    tx.Put(".refix-launcher/state.json",Encoding.UTF8.GetBytes(Data.Json(next)));tx.Commit();State=next;
   }
   Cleanup();
   OperationLog.Write(Root,"INSTALL_COMPLETE",version);
  }
  // 1.9: incremental - add-ons that stay selected (same archive) are not touched; 1.8 took off and put back every
  // add-on (1.4 GB of textures) on any change, e.g. when only the HUD was switched.
  public void ApplyAddons(Dictionary<string,string> selected) {
   EnsureStopped();
   var keep=State.addons.Where(a=>selected.ContainsKey(a.id) && String.Equals(selected[a.id],a.archive,StringComparison.OrdinalIgnoreCase)).ToList();
   var remove=State.addons.Where(a=>!keep.Contains(a)).ToList();
   var packages=new Dictionary<string,Package>();
   foreach(var p in selected) if(!keep.Any(k=>k.id==p.Key)) packages[p.Key]=Package.Load(Data.Safe(Root,p.Value),true,false);
   // a kept add-on sharing files with one that is taken off is put back as well
   bool changed=true;
   while(changed) {
    changed=false;
    foreach(var k in keep.ToList()) if(remove.Any(r=>r.originals.Keys.Any(k.originals.ContainsKey))) {keep.Remove(k);remove.Add(k);packages[k.id]=Package.Load(Data.Safe(Root,k.archive),true,false);changed=true;}
   }
   var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var k in keep)foreach(string n in k.originals.Keys)names.Add(n);
   foreach(var p in packages.Values)foreach(string n in p.Rows.Keys)if(!names.Add(n))throw new IOException("Выбранные дополнения меняют одни и те же файлы. Установите один вариант.");
   if(remove.Count==0 && packages.Count==0){OperationLog.Write(Root,"ADDONS_UNCHANGED",String.Join(",",selected.Keys));return;}
   CheckSpace(packages.Values.Sum(p=>Bytes(p)));
   OperationLog.Write(Root,"ADDONS_BEGIN","remove="+String.Join(",",remove.Select(a=>a.id))+"; add="+String.Join(",",packages.Keys));
   using(var tx=new Transaction(Root)) {
    tx.Token=Token;
    foreach(var a in State.addons.AsEnumerable().Reverse()) if(remove.Contains(a)) Restore(a,tx);
    var next=NextState(State.version,State.patchHash);
    foreach(var a in State.addons) if(keep.Contains(a)) next.addons.Add(a);
    foreach(var item in selected) if(packages.ContainsKey(item.Key)) next.addons.Add(Activate(item.Key,item.Value,packages[item.Key],tx));
    tx.Put(".refix-launcher/state.json",Encoding.UTF8.GetBytes(Data.Json(next)));tx.Commit();State=next;
   }
   Cleanup();
   OperationLog.Write(Root,"ADDONS_COMPLETE",String.Join(",",State.addons.Select(a=>a.id)));
  }
  public void Decline(string addonId) {   // 1.7: remember that the automatically offered add-on was refused
   if(State.declined==null)State.declined=new List<string>();
   if(State.declined.Contains(addonId))return;
   State.declined.Add(addonId);SaveState();
  }
  public void SaveState() { Data.WriteJson(Data.Safe(Root,".refix-launcher/state.json"),State); }
  public void SetState(LocalState next) { State=next; }
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

 // 1.9: grass draw distance. The author of the build ships pairs of xrRender_R1/R2.dll with the detail radius built in
 // (49 = SoC default ... 401). The launcher installs one pair; the files that were in bin before the first change are
 // kept in .refix-launcher/grass-original and come back with "Как в сборке". Only these two DLLs are ever touched
 // (xrGame.dll with the ReFix patches is not), and the choice is refused when the installed ReFix patch itself ships a
 // renderer (its patched DLL must not be overwritten by a variant made from the unpatched one).
 public static class Grass {
  public const string AddonId="optional_grass", BackupDir=".refix-launcher/grass-original";
  public static readonly string[] Files={"bin/xrrender_r1.dll","bin/xrrender_r2.dll"};
  static readonly string[] Names={"xrRender_R1.dll","xrRender_R2.dll"};
  static readonly Regex Entry=new Regex(@"^grass/(\d{3})/(xrRender_R[12]\.dll)$");
  // radius -> dll name -> manifest row
  public static Dictionary<string,Dictionary<string,FileRow>> Load(string archive) {
   var result=new Dictionary<string,Dictionary<string,FileRow>>();
   using(var zip=ZipFile.OpenRead(archive)) {
    var manifest=zip.GetEntry("MANIFEST.json"); if(manifest==null || manifest.Length>1000000) throw new InvalidDataException("Нет манифеста пакета травы");
    List<FileRow> rows; using(var r=new StreamReader(manifest.Open(),Encoding.UTF8)) rows=Data.Read<List<FileRow>>(r.ReadToEnd());
    var names=new HashSet<string>(zip.Entries.Where(e=>!e.FullName.EndsWith("/")).Select(e=>e.FullName));
    var listed=new HashSet<string>{"MANIFEST.json"};
    foreach(var row in rows) {
     var m=Entry.Match(row.file??""); if(!m.Success || row.bytes<1 || row.bytes>16777216 || !Regex.IsMatch(row.sha256??"","^[a-f0-9]{64}$") || !listed.Add(row.file)) throw new InvalidDataException("Неверный манифест пакета травы: "+row.file);
     var e=zip.GetEntry(row.file); if(e==null || e.Length!=row.bytes) throw new InvalidDataException("Размер файла не совпал: "+row.file);
     Dictionary<string,FileRow> pair; if(!result.TryGetValue(m.Groups[1].Value,out pair)) result[m.Groups[1].Value]=pair=new Dictionary<string,FileRow>();
     pair[m.Groups[2].Value]=row;
    }
    if(!listed.SetEquals(names)) throw new InvalidDataException("Состав пакета травы не совпал с манифестом");
    foreach(var pair in result) if(pair.Value.Count!=2) throw new InvalidDataException("В пакете травы нет пары DLL для радиуса "+pair.Key);
   }
   return result;
  }
  static byte[] Read(string archive,FileRow row) {
   using(var zip=ZipFile.OpenRead(archive)) using(var stream=zip.GetEntry(row.file).Open()) using(var mem=new MemoryStream()) {
    stream.CopyTo(mem); byte[] b=mem.ToArray(); if(Data.Hash(b)!=row.sha256) throw new InvalidDataException("Контрольная сумма не совпала: "+row.file); return b;
   }
  }
  public static string Rel(int i){return "bin/"+Names[i];}
  // the installed ReFix patch must not contain its own renderer
  public static void EnsureAllowed(GameService game,List<InstallProfile> profiles) {
   var current=profiles==null?null:profiles.FirstOrDefault(p=>p.patchHash==game.State.patchHash && !String.IsNullOrEmpty(p.patchHash));
   if(current!=null && current.files.Any(f=>Array.IndexOf(Files,f.file.ToLowerInvariant())>=0))
    throw new IOException("Установленный патч ReFix содержит собственный рендер — варианты дальности травы для него ещё не выпущены.");
  }
  // radius == null: the files of the build
  public static void Apply(GameService game,string archive,string radius,List<InstallProfile> profiles) {
   game.EnsureStopped(); EnsureAllowed(game,profiles);
   var state=game.State; var variants=Load(Data.Safe(game.Root,archive));
   if(radius!=null && !variants.ContainsKey(radius)) throw new IOException("В пакете нет варианта травы "+radius);
   var known=new HashSet<string>(variants.Values.SelectMany(p=>p.Values).Select(r=>r.sha256));
   var current=new string[2];
   for(int i=0;i<2;i++){string p=Data.Safe(game.Root,Rel(i));if(!File.Exists(p))throw new IOException("Нет файла "+Rel(i));current[i]=Data.HashFile(p);}
   bool haveOriginal=state.grassOriginal!=null && state.grassOriginal.Count==2 && Enumerable.Range(0,2).All(i=>File.Exists(Data.Safe(game.Root,BackupDir+"/"+Names[i])));
   using(var tx=new Transaction(game.Root)) {
    tx.Token=game.Token;
    var next=Data.Read<LocalState>(Data.Json(state));
    if(!haveOriginal || (state.grass==null && Enumerable.Range(0,2).Any(i=>current[i]!=state.grassOriginal[Rel(i)]))) {
     // first use, or the renderer was replaced outside the launcher while "Как в сборке" was selected: the files in bin
     // become the originals of the build
     next.grassOriginal=new Dictionary<string,string>();
     for(int i=0;i<2;i++){tx.Put(BackupDir+"/"+Names[i],File.ReadAllBytes(Data.Safe(game.Root,Rel(i))));next.grassOriginal[Rel(i)]=current[i];}
    } else if(state.grass!=null) {
     for(int i=0;i<2;i++) if(!known.Contains(current[i]) && current[i]!=state.grassOriginal[Rel(i)])
      throw new IOException("Файл "+Rel(i)+" изменён не лаунчером (обновление или ручная замена). Сначала верните его, затем выберите дальность травы.");
    }
    for(int i=0;i<2;i++) {
     byte[] data=radius==null?File.ReadAllBytes(Data.Safe(game.Root,BackupDir+"/"+Names[i])):Read(Data.Safe(game.Root,archive),variants[radius][Names[i]]);
     string want=radius==null?next.grassOriginal[Rel(i)]:variants[radius][Names[i]].sha256;
     if(Data.Hash(data)!=want) throw new InvalidDataException("Сохранённый оригинал "+Names[i]+" повреждён.");
     tx.Put(Rel(i),data);
    }
    next.grass=radius;
    tx.Put(".refix-launcher/state.json",Encoding.UTF8.GetBytes(Data.Json(next)));tx.Commit();game.SetState(next);
   }
   game.Cleanup(); game.Log("GRASS",radius??"original");
  }
 }
}

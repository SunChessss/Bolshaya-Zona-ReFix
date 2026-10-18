using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReFix {
 public class InstallProfile {public string version;public string title;public string patchHash;public List<FileRow> files;}
 public class InstallationReport {
  public string Title; public string PatchHash="";public List<string> Differences=new List<string>();public int Checked;
 }
 public static class Installation {
  public static List<InstallProfile> Profiles() {
   using(var s=typeof(Installation).Assembly.GetManifestResourceStream("ReFix.InstallProfiles.json"))
   using(var r=new StreamReader(s))return Data.Read<List<InstallProfile>>(r.ReadToEnd());
  }
  public const string CacheFile=".refix-launcher/install-profiles.json";
  // 1.4: profiles of new builds are published in launcher-manifest.json ("profiles"), so a new patch does not need
  // a new launcher. They are cached in the game folder for offline checks. Embedded profiles stay as a fallback.
  public static List<InstallProfile> Profiles(string root,Catalog catalog) {
   var result=new List<InstallProfile>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   List<InstallProfile> cached=null;
   if(root!=null)try{string path=Data.Safe(root,CacheFile);if(File.Exists(path)){cached=Data.Read<List<InstallProfile>>(File.ReadAllText(path));Validate(cached);}}catch{cached=null;}
   var fromCatalog=catalog==null?null:catalog.profiles;
   foreach(var list in new[]{fromCatalog,cached,Profiles()})if(list!=null)foreach(var p in list)if(p!=null && seen.Add(p.patchHash))result.Add(p);
   if(root!=null && fromCatalog!=null && fromCatalog.Count>0)try{Data.WriteJson(Data.Safe(root,CacheFile),result);}catch{}
   return result;
  }
  public static void Validate(List<InstallProfile> profiles) {
   if(profiles==null)return;
   if(profiles.Count>64)throw new System.IO.InvalidDataException("Слишком много эталонов сборки в каталоге");
   foreach(var p in profiles) {
    if(p==null || String.IsNullOrWhiteSpace(p.title) || p.files==null || p.files.Count==0 || p.files.Count>4096 || !System.Text.RegularExpressions.Regex.IsMatch(p.patchHash??"","^([a-f0-9]{64})?$"))throw new System.IO.InvalidDataException("Неверный эталон сборки в каталоге");
    foreach(var row in p.files) {
     Data.Safe(Path.GetTempPath(),row.file);
     if(!System.Text.RegularExpressions.Regex.IsMatch(row.sha256??"","^[a-f0-9]{64}$"))throw new System.IO.InvalidDataException("Неверный эталон сборки в каталоге");
    }
   }
  }
  public static InstallationReport Verify(GameService game,List<InstallProfile> profiles) {
   return Verify(game.Root,game.State,profiles);
  }
  // 1.8: the add-on overlay (up to 1.4 GB of textures) is no longer hashed on every start; it is checked separately
  // by VerifyAddons (add-ons page / "Проверить установку"). Verify only looks at the patch files and the stored bases.
  public static InstallationReport Verify(string root,LocalState state,List<InstallProfile> profiles) { return Verify(root,state,profiles,false); }
  public static List<string> VerifyAddons(string root,LocalState state) { return VerifyAddons(root,state,System.Threading.CancellationToken.None); }
  public static List<string> VerifyAddons(string root,LocalState state,System.Threading.CancellationToken token) {
   var overlays=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   foreach(var addon in state.addons) {
    // 1.7: hashes of the add-on files are kept in state.json; archives of older installs are read (manifest only)
    var files=addon.files!=null && addon.files.Count>0?addon.files:Package.Load(Data.Safe(root,addon.archive),true,false).Hashes();
    foreach(var file in files)overlays[file.Key]=file.Value;
   }
   return overlays.Where(p=>{token.ThrowIfCancellationRequested();return !File.Exists(Data.Safe(root,p.Key)) || Data.HashFile(Data.Safe(root,p.Key))!=p.Value;}).Select(p=>"Дополнение изменено или отсутствует: "+p.Key).ToList();
  }
  public static int AddonFileCount(LocalState state) { return state.addons.Sum(a=>a.files==null?0:a.files.Count); }
  public static InstallationReport Verify(string root,LocalState state,List<InstallProfile> profiles,bool withAddons) {
   var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   foreach(string file in profiles.SelectMany(p=>p.files).Select(r=>r.file).Distinct(StringComparer.OrdinalIgnoreCase)) {
    string path=Data.Safe(root,file);hashes[file]=File.Exists(path)?Data.HashFile(path):null;
   }
   // Check the stored base beneath managed add-ons (patch files only); the visible overlay is optional (slow).
   foreach(var addon in state.addons) {
    foreach(var original in addon.originals)if(hashes.ContainsKey(original.Key)) {
     string backup=original.Value==null?null:Data.Safe(root,original.Value);
     hashes[original.Key]=backup!=null && File.Exists(backup)?Data.HashFile(backup):null;
    }
   }
   var overlayErrors=withAddons?VerifyAddons(root,state):new List<string>();
   InstallationReport nearest=null;
   foreach(var profile in profiles) {
    var report=new InstallationReport {Title=profile.title,Checked=hashes.Count};
    foreach(var row in profile.files)if(hashes[row.file]!=row.sha256)report.Differences.Add((hashes[row.file]==null?"Отсутствует: ":"Отличается: ")+row.file);
    report.Differences.AddRange(overlayErrors);
    if(report.Differences.Count==0){report.PatchHash=profile.patchHash;return report;}
    if(nearest==null || report.Differences.Count<nearest.Differences.Count)nearest=report;
   }
   nearest.Title="Сборка изменена или патч установлен не полностью";return nearest;
  }
 }
}

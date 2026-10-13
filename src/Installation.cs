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
  public static InstallationReport Verify(GameService game,List<InstallProfile> profiles) {
   return Verify(game.Root,game.State,profiles);
  }
  public static InstallationReport Verify(string root,LocalState state,List<InstallProfile> profiles) {
   var hashes=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
   foreach(string file in profiles.SelectMany(p=>p.files).Select(r=>r.file).Distinct(StringComparer.OrdinalIgnoreCase)) {
    string path=Data.Safe(root,file);hashes[file]=File.Exists(path)?Data.HashFile(path):null;
   }
   // Check the stored base beneath managed add-ons, and verify the visible overlay too.
   var overlays=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
   foreach(var addon in state.addons) {
    foreach(var file in Package.Load(Data.Safe(root,addon.archive),true).Files)overlays[file.Key]=file.Value;
    foreach(var original in addon.originals)if(hashes.ContainsKey(original.Key)) {
     string backup=original.Value==null?null:Data.Safe(root,original.Value);
     hashes[original.Key]=backup!=null && File.Exists(backup)?Data.HashFile(backup):null;
    }
   }
   var overlayErrors=overlays.Where(p=>!File.Exists(Data.Safe(root,p.Key)) || Data.HashFile(Data.Safe(root,p.Key))!=Data.Hash(p.Value)).Select(p=>"Дополнение изменено или отсутствует: "+p.Key).ToList();
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

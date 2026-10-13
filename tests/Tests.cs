using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using ReFix;
class Tests {
 static int count;
 static void Check(bool ok,string message){if(!ok)throw new Exception(message);count++;Console.WriteLine("PASS "+message);}
 static void Reject(Action action,string message){try{action();}catch{Check(true,message);return;}throw new Exception("Not rejected: "+message);}
 static string root;
 static string Package(string name,Dictionary<string,string> files,bool corrupt=false) {
  string path=Data.Safe(root,".refix-launcher/cache/"+name+".zip");Directory.CreateDirectory(Path.GetDirectoryName(path));var rows=new List<FileRow>();
  using(var z=ZipFile.Open(path,ZipArchiveMode.Create)) {
   foreach(var item in files) {byte[] b=Encoding.UTF8.GetBytes(item.Value);rows.Add(new FileRow{file=item.Key,bytes=b.Length,sha256=corrupt?new string('a',64):Data.Hash(b)});using(var s=z.CreateEntry(item.Key).Open())s.Write(b,0,b.Length);}
   using(var w=new StreamWriter(z.CreateEntry("MANIFEST.json").Open()))w.Write(Data.Json(rows));
  }return ".refix-launcher/cache/"+name+".zip";
 }
 static void Main(string[] args) {
  root=Path.GetFullPath(Path.Combine("test-work",Guid.NewGuid().ToString("N")));Directory.CreateDirectory(root);
  Data.Atomic(Data.Safe(root,"bin/XR_3DA.exe"),new byte[]{0});
  string original="; личные настройки\r\n[bz_lord_actor]\r\nwalk_accel = 17.00\r\n[bz_lord_actor_condition]\r\nmax_walk_weight = 6000\r\n[bz_lord_inventory]\r\nmax_weight = 5700\r\n";
  Data.Atomic(Data.Safe(root,"gamedata/config/bz_lord.ltx"),Data.Legacy.GetBytes(original));
  Data.Atomic(Data.Safe(root,"fsgame.ltx"),Encoding.ASCII.GetBytes("$app_data_root$ = true|false|$fs_root$|userdata\\\r\n"));
  Data.Atomic(Data.Safe(root,"gamedata/config/ui/test.xml"),Encoding.ASCII.GetBytes("base"));
  var game=new GameService(root);
  string previewArchive=Package("preview",new Dictionary<string,string>{{"bin/xrGame.dll","fixture dll"},{"gamedata/config/ui/test.xml","preview update"}});
  string previewPath=Data.Safe(root,previewArchive),previewHash=Data.HashFile(previewPath);
  var asset=new Asset{id="preview",url="https://github.com/"+GameService.Repository+"/releases/download/test/preview.zip",sha256=previewHash,bytes=new FileInfo(previewPath).Length};
  string cached=Data.Safe(root,".refix-launcher/cache/"+previewHash+".zip");File.Copy(previewPath,cached);
  string downloaded=game.Download(asset).GetAwaiter().GetResult();
  var plan=game.PreparePatch(downloaded,"test",previewHash);
  Check(game.ReadConfig("gamedata/config/ui/test.xml")=="base" && !File.Exists(Data.Safe(root,"bin/xrGame.dll")),"download and preview do not install files");
  Check(!File.Exists(Data.Safe(root,".refix-launcher/state.json")),"preview does not register installed version");
  Check(Array.Exists(plan.Changes,s=>s.Contains("bin/xrGame.dll")) && Array.Exists(plan.Changes,s=>s.Contains("Заменить:")),"preview lists DLL and replacements");
  byte[] archiveBytes=File.ReadAllBytes(cached);File.AppendAllText(cached,"tampered");
  Reject(()=>game.InstallPreparedPatch(plan),"reject archive modified after preview");
  Check(game.ReadConfig("gamedata/config/ui/test.xml")=="base","revalidation failure leaves game unchanged");
  File.WriteAllBytes(cached,archiveBytes);game.InstallPreparedPatch(plan);
  Check(game.ReadConfig("gamedata/config/ui/test.xml")=="preview update","explicit installation applies prepared patch");
  string log=File.ReadAllText(Data.Safe(root,".refix-launcher/operations.log"));
  Check(log.Contains("PREVIEW_READY") && log.Contains("INSTALL_COMPLETE") && log.IndexOf("FILE_WRITE_BEGIN")<log.IndexOf("FILE_WRITE_END"),"durable log contains preview and installation stages");
  string pack=Package("patch",new Dictionary<string,string>{{"gamedata/config/ui/test.xml","updated"},{"gamedata/config/bz_lord.ltx","DO NOT OVERWRITE"}});
  game.ApplyPatch(pack,"2026.10.13.1","fixture");
  Check(game.ReadConfig("gamedata/config/ui/test.xml")=="updated","patch installs");
  Check(game.ReadConfig("gamedata/config/bz_lord.ltx")==original,"patch preserves personal weight/speed");
  string addon=Package("addon",new Dictionary<string,string>{{"gamedata/config/ui/test.xml","addon"},{"gamedata/new.xml","new"}});
  game.ApplyAddons(new Dictionary<string,string>{{"test",addon}});
  Check(game.ReadConfig("gamedata/config/ui/test.xml")=="addon","addon applies");
  string patch2=Package("patch2",new Dictionary<string,string>{{"gamedata/config/ui/test.xml","updated again"}});
  game.ApplyPatch(patch2,"2026.10.13.2","fixture2");
  Check(game.ReadConfig("gamedata/config/ui/test.xml")=="addon","patch reapplies active addon");
  game.ApplyAddons(new Dictionary<string,string>());
  Check(game.ReadConfig("gamedata/config/ui/test.xml")=="updated again","disable restores NEW patched baseline");
  Check(!File.Exists(Data.Safe(root,"gamedata/new.xml")),"disable removes only addon-created file");
  using(var tx=new Transaction(root)){tx.Put("gamedata/config/ui/test.xml",Encoding.ASCII.GetBytes("failed"));tx.Put("gamedata/transient.xml",new byte[]{1});}
  Check(game.ReadConfig("gamedata/config/ui/test.xml")=="updated again" && !File.Exists(Data.Safe(root,"gamedata/transient.xml")),"rollback restores existing and absent files");
  var interrupted=new Transaction(root);interrupted.Put("gamedata/config/ui/test.xml",new byte[]{1});Transaction.Recover(root);
  Check(game.ReadConfig("gamedata/config/ui/test.xml")=="updated again","interrupted install recovery");
  string bad=Package("corrupt",new Dictionary<string,string>{{"gamedata/corrupt.xml","broken"}},true);
  Reject(()=>ReFix.Package.Load(Data.Safe(root,bad),true),"reject corrupted archive");
  Reject(()=>Data.Safe(root,"../escape"),"reject traversal");Reject(()=>Data.Safe(root,"C:/escape"),"reject absolute paths");Reject(()=>Data.Safe(root,"gamedata/test.xml:stream"),"reject alternate streams");
  Reject(()=>GameService.ValidateUrl("https://evil.example/patch.zip"),"reject foreign download host");
  Reject(()=>GameService.ValidateUrl("https://github.com/evil/repo/releases/download/v1/p.zip"),"reject foreign repository");
  string dangerous=Package("danger",new Dictionary<string,string>{{"userdata/user.ltx","bad"}});
  Reject(()=>ReFix.Package.Load(Data.Safe(root,dangerous),false),"reject settings inside update");
  var graphics=new Dictionary<string,string>{{"renderer","renderer_r1"},{"rs_fullscreen","off"}};
  game.SaveSettings(graphics,5700,6000,1.5m);
  Check(Config.Value(game.ReadConfig("gamedata/config/bz_lord.ltx"),"walk_accel","bz_lord_actor")=="25.5","speed multiplier");
  Check(game.ReadConfig("gamedata/config/bz_lord.ltx").Contains("; личные настройки"),"preserve comments and cp1251");
  Check(Config.Value(game.ReadConfig("userdata/user.ltx"),"renderer")=="renderer_r1","graphics save");
  Reject(()=>game.SaveSettings(graphics,100,50,1),"reject inconsistent weight");
  string unknown="bind foo bar\r\nrenderer renderer_r1\r\ncustom 9\r\n";string updated=Config.Set(unknown,"renderer","renderer_r2");
  Check(updated.Contains("bind foo bar")&&updated.Contains("custom 9"),"preserve unrelated settings");
  Console.WriteLine("TOTAL "+count+" passed. Fixture: "+root);
 }
}

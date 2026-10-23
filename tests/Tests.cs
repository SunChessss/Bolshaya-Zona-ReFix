using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Linq;
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
  Check(Config.Value(game.ReadConfig("gamedata/config/bz_lord.ltx"),"preset","bz_loot")=="mod","loot preset defaults to the mod tables");
  game.SaveSettings(graphics,5700,6000,1.5m,"scavenger");
  Check(Config.Value(game.ReadConfig("gamedata/config/bz_lord.ltx"),"preset","bz_loot")=="scavenger" && game.ReadConfig("gamedata/config/bz_lord.ltx").Contains("; личные настройки"),"loot preset written to [bz_loot] without losing comments");
  game.SaveSettings(graphics,5700,6000,1.5m,"vanilla");
  string lordText=game.ReadConfig("gamedata/config/bz_lord.ltx");
  Check(Config.Value(lordText,"preset","bz_loot")=="vanilla" && lordText.Split(new[]{"[bz_loot]"},StringSplitOptions.None).Length==2,"loot preset replaced in place, section not duplicated");
  Reject(()=>game.SaveSettings(graphics,5700,6000,1.5m,"everything"),"reject unknown loot preset");
  string unknown="bind foo bar\r\nrenderer renderer_r1\r\ncustom 9\r\n";string updated=Config.Set(unknown,"renderer","renderer_r2");
  Check(updated.Contains("bind foo bar")&&updated.Contains("custom 9"),"preserve unrelated settings");
  var known=new System.Collections.Generic.List<InstallProfile>{new InstallProfile{title="Known release",version="test",patchHash="verified",files=new List<FileRow>{new FileRow{file="gamedata/config/ui/test.xml",sha256=Data.HashFile(Data.Safe(root,"gamedata/config/ui/test.xml"))}}}};
  var detected=Installation.Verify(game,known);Check(detected.PatchHash=="verified","detect installed files independently of recorded version");
  Data.Atomic(Data.Safe(root,"gamedata/config/ui/test.xml"),Encoding.ASCII.GetBytes("changed"));
  Check(Installation.Verify(game,known).Differences.Count==1,"detect changed installation despite recorded version");
  known[0].files.Add(new FileRow{file="gamedata/missing",sha256=new string('a',64)});
  Check(Installation.Verify(game,known).Differences.Count==2,"report missing and changed files separately");
  Check(Installation.Profiles().Count>=4 && Installation.Profiles()[0].version=="2026.10.23.2" && Installation.Profiles()[0].files.Count==217 && Installation.Profiles()[1].version=="2026.10.23.1" && Installation.Profiles().Exists(x=>x.files.Count==59),"embedded current and pre-patch profiles available offline");
  // 1.4: reference profiles published in the release catalog, cached for offline checks
  Data.Atomic(Data.Safe(root,"gamedata/config/ui/test.xml"),Encoding.ASCII.GetBytes("catalog build"));
  var fresh=new InstallProfile{title="Catalog release",version="2099.1.1.1",patchHash=new string('c',64),files=new List<FileRow>{new FileRow{file="gamedata/config/ui/test.xml",sha256=Data.HashFile(Data.Safe(root,"gamedata/config/ui/test.xml"))}}};
  var cat=new Catalog{profiles=new List<InstallProfile>{fresh}};
  var merged=Installation.Profiles(root,cat);
  Check(merged[0].title=="Catalog release" && merged.Count==Installation.Profiles().Count+1,"catalog profiles come first, embedded ones kept");
  Check(Installation.Verify(game,merged).PatchHash==new string('c',64),"build from catalog recognised without a new launcher");
  Check(File.Exists(Data.Safe(root,Installation.CacheFile)) && Installation.Profiles(root,null)[0].title=="Catalog release","catalog profiles cached for offline checks");
  Check(Installation.Profiles(root,new Catalog{profiles=new List<InstallProfile>{fresh,fresh}}).Count==merged.Count,"duplicate profiles merged by patch hash");
  Reject(()=>Installation.Validate(new List<InstallProfile>{new InstallProfile{title="bad",patchHash=new string('d',64),files=new List<FileRow>{new FileRow{file="../escape",sha256=new string('a',64)}}}}),"reject traversal in catalog profile");
  Reject(()=>Installation.Validate(new List<InstallProfile>{new InstallProfile{title="bad",patchHash=new string('d',64),files=new List<FileRow>{new FileRow{file="gamedata/x",sha256="nothex"}}}}),"reject malformed hash in catalog profile");
  Data.Atomic(Data.Safe(root,Installation.CacheFile),Encoding.ASCII.GetBytes("{broken"));
  Check(Installation.Profiles(root,null).Count==Installation.Profiles().Count,"broken cache ignored, embedded profiles used");
  string font="; исходный шрифт\r\n[ui_font_letterica16_russian]\r\ntexture = ui\\ui_font_letter_16_1024\r\n";
  Data.Atomic(Data.Safe(root,"gamedata/config/fonts.ltx"),Data.Legacy.GetBytes(font));
  foreach(string n in new[]{"16","18","25"})foreach(string ext in new[]{"dds","ini"})Data.Atomic(Data.Safe(root,"gamedata/textures/ui/ui_font_letter_"+n+"_1024."+ext),new byte[]{0});
  GameFonts.Save(game,"large");Check(game.ReadConfig("gamedata/config/fonts.ltx").Contains("letter_18_1024"),"large preset uses native larger font atlas");
  GameFonts.Save(game,"largest");Check(game.ReadConfig("gamedata/config/fonts.ltx").Contains("letter_25_1024"),"largest preset uses original base without cumulative substitutions");
  GameFonts.Save(game,"original");Check(game.ReadConfig("gamedata/config/fonts.ltx")==font,"font restore preserves exact original bytes");
  Data.Atomic(Data.Safe(root,"gamedata/config/fonts.ltx"),Data.Legacy.GetBytes(font+"; manual\r\n"));Reject(()=>GameFonts.Save(game,"large"),"font setting refuses to overwrite external edits");
  // 1.5: light (non-bold) large atlas letter_25r when shipped with the patch; the letterica25 font itself follows
  Data.Atomic(Data.Safe(root,"gamedata/config/fonts.ltx"),Data.Legacy.GetBytes(font));
  Check(!GameFonts.LightAvailable(game),"light atlas not available before the patch");
  foreach(string r in new[]{"800","1024","1600"})foreach(string ext in new[]{"dds","ini"})Data.Atomic(Data.Safe(root,"gamedata/textures/ui/ui_font_letter_25r_"+r+"."+ext),new byte[]{0});
  Check(!GameFonts.LightAvailable(game),"light atlas needs both 25r and 18r");
  foreach(string r in new[]{"800","1024","1600"})foreach(string ext in new[]{"dds","ini"})Data.Atomic(Data.Safe(root,"gamedata/textures/ui/ui_font_letter_18r_"+r+"."+ext),new byte[]{0});
  Check(GameFonts.LightAvailable(game),"light atlas detected");
  GameFonts.Save(game,"large");Check(game.ReadConfig("gamedata/config/fonts.ltx").Contains("letter_18r_1024"),"large preset uses the light 18 atlas when available");
  GameFonts.Save(game,"largest");Check(game.ReadConfig("gamedata/config/fonts.ltx").Contains("letter_25r_1024") && !game.ReadConfig("gamedata/config/fonts.ltx").Contains("letter_25_1024"),"largest preset uses the light atlas when available");
  Check(GameFonts.Transform("texture = ui\\ui_font_letter_25_1024\r\ntexture800 = ui\\ui_font_letter_18_800\r\ntexture1600 = ui\\ui_font_letter_16_1024\r\n","large",true)=="texture = ui\\ui_font_letter_25r_1024\r\ntexture800 = ui\\ui_font_letter_25r_800\r\ntexture1600 = ui\\ui_font_letter_18r_1024\r\n","light preset replaces letterica25/18/16 atlases");
  Check(GameFonts.Transform("texture = ui\\ui_font_letter_16_1024\r\n","largest",true)=="texture = ui\\ui_font_letter_25r_1024\r\n","largest light preset maps letterica16 to 25r");
  Check(GameFonts.Transform("texture = ui\\ui_font_letter_25_1024\r\n","large",false)=="texture = ui\\ui_font_letter_25_1024\r\n","without the light atlas letterica25 stays untouched");
  GameFonts.Save(game,"original");Check(game.ReadConfig("gamedata/config/fonts.ltx")==font,"font restore after light preset preserves exact original bytes");
  // 1.5: UI scale preset — alternative window layouts from gamedata/config/ui_large
  Check(!GameUiScale.Available(game) && GameUiScale.Current(game)=="original","ui scale unavailable before the patch");
  Reject(()=>GameUiScale.Save(game,"large"),"ui scale refuses without the shipped layouts");
  byte[] small=Data.Legacy.GetBytes("<w cell=\"19\"/>\r\n"),big=Data.Legacy.GetBytes("<w cell=\"25\"/>\r\n");
  foreach(string n in new[]{"inventory_new.xml","trade.xml"}){Data.Atomic(Data.Safe(root,"gamedata/config/ui/"+n),small);Data.Atomic(Data.Safe(root,"gamedata/config/ui_large/"+n),big);}
  Check(GameUiScale.Available(game) && GameUiScale.Files(game).Count==2,"ui scale layouts detected");
  GameUiScale.Save(game,"original");Check(!File.Exists(Data.Safe(root,GameUiScale.StatePath)),"ui scale original without state is a no-op");
  GameUiScale.Save(game,"large");
  Check(Data.Hash(File.ReadAllBytes(Data.Safe(root,"gamedata/config/ui/trade.xml")))==Data.Hash(big) && Data.Hash(File.ReadAllBytes(Data.Safe(root,"gamedata/config/ui/inventory_new.xml")))==Data.Hash(big),"large ui preset copies the shipped layouts");
  Check(GameUiScale.Current(game)=="large" && Data.Hash(File.ReadAllBytes(Data.Safe(root,GameUiScale.BackupDir+"/trade.xml")))==Data.Hash(small),"large ui preset recognised and originals backed up");
  GameUiScale.Save(game,"large");Check(Data.Hash(File.ReadAllBytes(Data.Safe(root,GameUiScale.BackupDir+"/trade.xml")))==Data.Hash(small),"re-applying the preset keeps the first backup");
  GameUiScale.Save(game,"original");Check(Data.Hash(File.ReadAllBytes(Data.Safe(root,"gamedata/config/ui/trade.xml")))==Data.Hash(small) && GameUiScale.Current(game)=="original","ui scale restore preserves exact original bytes");
  GameUiScale.Save(game,"large");Data.Atomic(Data.Safe(root,"gamedata/config/ui/trade.xml"),Data.Legacy.GetBytes("manual"));
  Check(GameUiScale.Current(game)=="custom","external edit of a layout reported as custom");
  Reject(()=>GameUiScale.Save(game,"original"),"ui scale refuses to overwrite external edits");
  Data.Atomic(Data.Safe(root,"gamedata/config/ui/trade.xml"),big);GameUiScale.Save(game,"original");
  Check(Data.Hash(File.ReadAllBytes(Data.Safe(root,"gamedata/config/ui/trade.xml")))==Data.Hash(small),"ui scale restored after the edit was reverted");
  var launcherAsset=new LauncherAsset{version="9.9.0",url="https://github.com/"+GameService.Repository+"/releases/download/test/ReFixLauncher.zip",sha256=new string('a',64),bytes=10};
  Check(SelfUpdate.IsNewer(launcherAsset),"detect newer launcher version");launcherAsset.version="1.2.0";Check(!SelfUpdate.IsNewer(launcherAsset),"never downgrade launcher");
  Check(!SelfUpdate.IsNewer(null),"older catalog without launcher field remains compatible");launcherAsset.version="invalid";Reject(()=>SelfUpdate.IsNewer(launcherAsset),"reject malformed launcher version");
  string selfZip=Data.Safe(root,"self-valid.zip");using(var z=ZipFile.Open(selfZip,ZipArchiveMode.Create)){using(var s=z.CreateEntry("ReFixLauncher.exe").Open()){s.WriteByte(77);s.WriteByte(90);}using(var w=new StreamWriter(z.CreateEntry("ReFixLauncher.exe.config").Open()))w.Write("<configuration/>");}
  Check(SelfUpdate.ReadPackage(selfZip).Count==2,"accept launcher-only package");
  using(var z=ZipFile.Open(selfZip,ZipArchiveMode.Update)){using(var w=new StreamWriter(z.CreateEntry("../escape.exe").Open()))w.Write("bad");}
  Reject(()=>SelfUpdate.ReadPackage(selfZip),"reject extra files and traversal in self-update");
  // 1.7: game folder normalisation (self-update passed "D:\\X\\" which the command line turned into D:\X")
  if(Path.DirectorySeparatorChar=='\\') {   // Windows paths (the suite also runs under Mono on Linux)
  Check(Data.Root(root+"\\")==root && Data.Root(root+"\\\"")==root && Data.Root(" "+root+" ")==root,"root path normalised (trailing separator and stray quote)");
  Check(Data.Root("C:\\")=="C:\\","drive root keeps its separator");
  Check(new GameService(root+"\\").Root==root,"game service strips the trailing separator");
  }
  // 1.7: add-on hashes are stored in state.json and verification does not need the archive
  string base17=game.ReadConfig("gamedata/config/ui/test.xml");string baseHash17=Data.HashFile(Data.Safe(root,"gamedata/config/ui/test.xml"));
  string addon17=Package("addon17",new Dictionary<string,string>{{"gamedata/config/ui/test.xml","addon17"},{"gamedata/textures/t.dds","dds"}});
  game.ApplyAddons(new Dictionary<string,string>{{"optional_textures",addon17}});
  Check(game.State.addons[0].files!=null && game.State.addons[0].files.Count==2 && game.State.addons[0].files["gamedata/textures/t.dds"]==Data.Hash(Encoding.UTF8.GetBytes("dds")),"addon file hashes kept in state");
  string moved=Data.Safe(root,".refix-launcher/cache/addon17.zip");File.Move(moved,moved+".away");
  var profile=new InstallProfile{title="t",patchHash=new string('b',64),files=new List<FileRow>{new FileRow{file="gamedata/config/ui/test.xml",sha256=baseHash17}}};
  var report=Installation.Verify(root,game.State,new List<InstallProfile>{profile},true);
  Check(report.Differences.Count==0,"verification of an installed addon works without its archive");
  Data.Atomic(Data.Safe(root,"gamedata/textures/t.dds"),Encoding.UTF8.GetBytes("changed"));
  Check(Installation.Verify(root,game.State,new List<InstallProfile>{profile},true).Differences.Count==1,"modified addon file reported");
  Check(Installation.Verify(root,game.State,new List<InstallProfile>{profile}).Differences.Count==0,"1.8: start-up check skips add-on files");
  Check(Installation.VerifyAddons(root,game.State).Count==1 && Installation.AddonFileCount(game.State)==2,"1.8: add-on files verified separately");
  File.Move(moved+".away",moved);game.ApplyAddons(new Dictionary<string,string>());
  Check(game.ReadConfig("gamedata/config/ui/test.xml")==base17 && !File.Exists(Data.Safe(root,"gamedata/textures/t.dds")),"textures addon removable");
  game.Decline("optional_textures");game.LoadState();
  Check(game.State.declined.Contains("optional_textures"),"declined offer is remembered in state.json");
  game.Decline("optional_textures");Check(game.State.declined.Count==1,"decline is idempotent");
  // 1.7: streaming package reader
  var pkg=ReFix.Package.Load(Data.Safe(root,addon17),true);
  using(var reader=pkg.Open())Check(Encoding.UTF8.GetString(reader.Read("gamedata/textures/t.dds"))=="dds","package reader returns verified entry");
  // ------------------------------------------------------------------ 1.9
  Tests19(game);
  Console.WriteLine("TOTAL "+count+" passed. Fixture: "+root);
 }
 static string Text(string rel){return File.ReadAllText(Data.Safe(root,rel));}
 static DateTime Stamp(string rel){return File.GetLastWriteTimeUtc(Data.Safe(root,rel));}
 static int Pending(){string d=Data.Safe(root,".refix-launcher/transactions");return Directory.Exists(d)?Directory.GetDirectories(d).Length:0;}
 static void Tests19(GameService game) {
  game.ApplyAddons(new Dictionary<string,string>());
  var tex=new Dictionary<string,string>();for(int i=0;i<60;i++)tex["gamedata/textures/t"+i+".dds"]="tex"+i;
  Data.Atomic(Data.Safe(root,"gamedata/textures/t0.dds"),Encoding.UTF8.GetBytes("orig0"));
  string texZip=Package("tex19",tex),hudA=Package("huda19",new Dictionary<string,string>{{"gamedata/config/ui/hud.xml","A"}}),hudB=Package("hudb19",new Dictionary<string,string>{{"gamedata/config/ui/hud.xml","B"}});
  Data.Atomic(Data.Safe(root,"gamedata/config/ui/hud.xml"),Encoding.UTF8.GetBytes("orig hud"));
  game.ApplyAddons(new Dictionary<string,string>{{"optional_textures",texZip},{"optional_minimal_hud",hudA}});
  Check(Text("gamedata/textures/t0.dds")=="tex0" && Text("gamedata/config/ui/hud.xml")=="A","1.9: two add-ons applied");
  Check(Pending()==0,"1.9: finished transactions are deleted (no copies of originals left behind)");
  string texBase=game.State.addons.First(a=>a.id=="optional_textures").originals["gamedata/textures/t0.dds"];
  Check(Text(texBase)=="orig0","1.9: original moved into bases");
  System.Threading.Thread.Sleep(30);DateTime t0=Stamp("gamedata/textures/t5.dds");
  game.ApplyAddons(new Dictionary<string,string>{{"optional_textures",texZip},{"optional_new_hud",hudB}});
  Check(Text("gamedata/config/ui/hud.xml")=="B" && Stamp("gamedata/textures/t5.dds")==t0 && game.State.addons.First(a=>a.id=="optional_textures").originals["gamedata/textures/t0.dds"]==texBase,"1.9: switching the HUD does not reinstall the textures add-on");
  string log=File.ReadAllText(Data.Safe(root,".refix-launcher/operations.log"));int before=log.Length;
  game.ApplyAddons(new Dictionary<string,string>{{"optional_textures",texZip},{"optional_new_hud",hudB}});
  Check(File.ReadAllText(Data.Safe(root,".refix-launcher/operations.log")).Substring(before).Contains("ADDONS_UNCHANGED"),"1.9: unchanged selection does nothing");
  game.ApplyAddons(new Dictionary<string,string>{{"optional_new_hud",hudB}});
  Check(Text("gamedata/textures/t0.dds")=="orig0" && !File.Exists(Data.Safe(root,"gamedata/textures/t1.dds")) && !Directory.Exists(Path.GetDirectoryName(Data.Safe(root,texBase))),"1.9: removing an add-on moves originals back and deletes its base folder");
  // cancellation in the middle of an activation rolls everything back
  var stateBefore=Data.Json(game.State);var cts=new System.Threading.CancellationTokenSource();
  game.Token=cts.Token;game.Progress=m=>{if(m.Contains("50 из"))cts.Cancel();};
  Reject(()=>game.ApplyAddons(new Dictionary<string,string>{{"optional_textures",texZip},{"optional_new_hud",hudB}}),"1.9: cancellation stops the operation");
  game.Token=System.Threading.CancellationToken.None;game.Progress=m=>{};game.LoadState();
  Check(Text("gamedata/textures/t0.dds")=="orig0" && !File.Exists(Data.Safe(root,"gamedata/textures/t30.dds")) && Text("gamedata/config/ui/hud.xml")=="B" && Data.Json(game.State)==stateBefore && !Transaction.Pending(root),"1.9: cancelled operation rolled back by moving files back");
  // interrupted operation (launcher killed): recovered on the next start
  var tx=new Transaction(root);tx.Put("gamedata/config/ui/hud.xml",Encoding.UTF8.GetBytes("half"));tx.Move("gamedata/textures/t0.dds",".refix-launcher/bases/x/t0.dds");
  Check(Transaction.Pending(root),"1.9: interrupted transaction detected");
  new GameService(root).Recover();
  Check(Text("gamedata/config/ui/hud.xml")=="B" && Text("gamedata/textures/t0.dds")=="orig0" && !Transaction.Pending(root) && Pending()==0,"1.9: recovery moves files back and cleans up");
  // a journal of 1.8 (copied backups) is still rolled back
  string old=Data.Safe(root,".refix-launcher/transactions/old18");Directory.CreateDirectory(Path.Combine(old,"before"));
  File.WriteAllText(Path.Combine(old,"before","0"),"B");Data.Atomic(Data.Safe(root,"gamedata/config/ui/hud.xml"),Encoding.UTF8.GetBytes("written by 1.8"));
  Data.WriteJson(Path.Combine(old,"journal.json"),new Journal{changes=new List<Change>{new Change{path="gamedata/config/ui/hud.xml",backup="before/0"},new Change{path="gamedata/created18.xml"}}});
  Data.Atomic(Data.Safe(root,"gamedata/created18.xml"),new byte[]{1});
  Transaction.Recover(root);
  Check(Text("gamedata/config/ui/hud.xml")=="B" && !File.Exists(Data.Safe(root,"gamedata/created18.xml")),"1.9: journals of 1.8 still roll back");
  // a crash between the journal line and the move leaves the original in place
  string cut=Data.Safe(root,".refix-launcher/transactions/cut");Directory.CreateDirectory(cut);
  Data.WriteJson(Path.Combine(cut,"journal.json"),new Journal());File.WriteAllText(Path.Combine(cut,"changes.log"),Data.Json(new Change{path="gamedata/config/ui/hud.xml",aside="aside/0"})+"\n{\"path\":\"gamedata/x");
  Transaction.Recover(root);
  Check(Text("gamedata/config/ui/hud.xml")=="B","1.9: record without moved file keeps the original");
  // patch: only add-ons overlaying patch files are taken off and put back; declined offers survive
  game.ApplyAddons(new Dictionary<string,string>{{"optional_textures",texZip},{"optional_new_hud",hudB}});
  game.Decline("optional_season_spring");
  System.Threading.Thread.Sleep(30);t0=Stamp("gamedata/textures/t5.dds");
  string p19=Package("patch19",new Dictionary<string,string>{{"gamedata/config/ui/hud.xml","patched base"},{"gamedata/scripts/x.script","x"}});
  game.ApplyPatch(p19,"2026.10.18.1",new string('e',64));
  Check(Stamp("gamedata/textures/t5.dds")==t0 && Text("gamedata/config/ui/hud.xml")=="B" && game.State.declined.Contains("optional_season_spring") && game.State.declined.Contains("optional_textures"),"1.9: patch keeps non-overlapping add-ons and declined offers");
  game.ApplyAddons(new Dictionary<string,string>{{"optional_textures",texZip}});
  Check(Text("gamedata/config/ui/hud.xml")=="patched base","1.9: overlapping add-on re-applied over the new patch base");
  // cache: a verified archive is trusted by its marker
  string data=Data.Safe(root,texZip);string h=Data.HashFile(data);string c19=Data.Safe(root,".refix-launcher/cache/"+h+".zip");File.Copy(data,c19,true);
  var a19=new Asset{id="tex",url="https://github.com/"+GameService.Repository+"/releases/download/t/tex.zip",sha256=h,bytes=new FileInfo(c19).Length};
  game.Download(a19).GetAwaiter().GetResult();game.Download(a19).GetAwaiter().GetResult();
  Check(File.Exists(c19+".ok") && File.ReadAllText(Data.Safe(root,".refix-launcher/operations.log")).Contains("DOWNLOAD_CACHE_TRUSTED"),"1.9: cached archive verified once");
  Reject(()=>game.CheckSpace(long.MaxValue/4),"1.9: refuse when the disk is too small");
  // grass
  byte[] r1=Encoding.UTF8.GetBytes("R1 build"),r2=Encoding.UTF8.GetBytes("R2 build");
  Data.Atomic(Data.Safe(root,"bin/xrRender_R1.dll"),r1);Data.Atomic(Data.Safe(root,"bin/xrRender_R2.dll"),r2);
  Data.Atomic(Data.Safe(root,"bin/xrGame.dll"),Encoding.UTF8.GetBytes("patched xrGame"));
  var g=new Dictionary<string,string>();foreach(string r in new[]{"049","121","241"})foreach(string n in new[]{"R1","R2"})g["grass/"+r+"/xrRender_"+n+".dll"]=n+" "+r;
  string gz=Package("grass19",g);
  Reject(()=>Grass.Load(Data.Safe(root,Package("grassbad",new Dictionary<string,string>{{"grass/049/xrGame.dll","x"}}))),"1.9: grass package may contain only xrRender DLLs");
  Reject(()=>Grass.Load(Data.Safe(root,Package("grasshalf",new Dictionary<string,string>{{"grass/049/xrRender_R1.dll","x"}}))),"1.9: grass package needs both DLLs");
  Grass.Apply(game,gz,"121",null);
  Check(Text("bin/xrRender_R1.dll")=="R1 121" && Text("bin/xrRender_R2.dll")=="R2 121" && game.State.grass=="121" && Text("bin/xrGame.dll")=="patched xrGame","1.9: grass pair installed, xrGame.dll untouched");
  Check(File.ReadAllText(Data.Safe(root,Grass.BackupDir+"/xrRender_R1.dll"))=="R1 build","1.9: renderer of the build kept");
  Grass.Apply(game,gz,"241",null);Check(Text("bin/xrRender_R2.dll")=="R2 241" && File.ReadAllText(Data.Safe(root,Grass.BackupDir+"/xrRender_R2.dll"))=="R2 build","1.9: switching variants keeps the first originals");
  game.LoadState();Check(game.State.grass=="241","1.9: grass choice stored in state.json");
  Grass.Apply(game,gz,null,null);Check(Text("bin/xrRender_R1.dll")=="R1 build" && Text("bin/xrRender_R2.dll")=="R2 build" && game.State.grass==null,"1.9: build renderer restored");
  Grass.Apply(game,gz,"049",null);Data.Atomic(Data.Safe(root,"bin/xrRender_R1.dll"),Encoding.UTF8.GetBytes("someone else"));
  Reject(()=>Grass.Apply(game,gz,"121",null),"1.9: renderer changed outside the launcher is not overwritten");
  Data.Atomic(Data.Safe(root,"bin/xrRender_R1.dll"),Encoding.UTF8.GetBytes("R1 049"));
  var withRender=new List<InstallProfile>{new InstallProfile{title="p",patchHash=game.State.patchHash,files=new List<FileRow>{new FileRow{file="bin/xrRender_R2.dll",sha256=new string('a',64)}}}};
  Reject(()=>Grass.Apply(game,gz,"121",withRender),"1.9: grass refused when the installed patch ships its own renderer");
  string pr=Package("patchrender",new Dictionary<string,string>{{"bin/xrRender_R2.dll","patched R2"}});
  game.ApplyPatch(pr,"2026.10.18.2",new string('f',64));
  Check(game.State.grass==null && game.State.grassOriginal.Count==0 && Text("bin/xrRender_R2.dll")=="patched R2","1.9: a patch with a renderer resets the grass choice");
 }
}


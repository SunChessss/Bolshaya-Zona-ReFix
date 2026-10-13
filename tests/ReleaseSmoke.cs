using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ReFix;
class ReleaseSmoke {
 static void Main(string[] args) {
  var game=new GameService(args[0]);
  var catalog=game.Check().GetAwaiter().GetResult();
  Console.WriteLine("Catalog: "+catalog.version);
  string lord=Data.HashFile(Data.Safe(game.Root,"gamedata/config/bz_lord.ltx"));
  string user=Data.HashFile(Data.Safe(game.Root,"userdata/user.ltx"));
  string patch=game.Download(catalog.patch).GetAwaiter().GetResult();
  game.ApplyPatch(patch,catalog.version,catalog.patch.sha256);
  var expected=Package.Load(Data.Safe(game.Root,patch),false);
  foreach(var p in expected.Files) if(Data.HashFile(Data.Safe(game.Root,p.Key))!=Data.Hash(p.Value)) throw new Exception("Patch mismatch: "+p.Key);
  Console.WriteLine("PASS live GitHub patch: "+expected.Files.Count+" files");
  foreach(var addon in catalog.addons) {
   string archive=game.Download(addon).GetAwaiter().GetResult();
   var package=Package.Load(Data.Safe(game.Root,archive),true);
   var before=package.Files.Keys.ToDictionary(p=>p,p=>File.Exists(Data.Safe(game.Root,p))?Data.HashFile(Data.Safe(game.Root,p)):null);
   game.ApplyAddons(new Dictionary<string,string>{{addon.id,archive}});
   foreach(var p in package.Files) if(Data.HashFile(Data.Safe(game.Root,p.Key))!=Data.Hash(p.Value))throw new Exception("Addon mismatch: "+p.Key);
   game.ApplyAddons(new Dictionary<string,string>());
   foreach(var p in before) if((File.Exists(Data.Safe(game.Root,p.Key))?Data.HashFile(Data.Safe(game.Root,p.Key)):null)!=p.Value)throw new Exception("Restore mismatch: "+p.Key);
   Console.WriteLine("PASS live addon and restore: "+addon.id+" ("+package.Files.Count+")");
  }
  if(lord!=Data.HashFile(Data.Safe(game.Root,"gamedata/config/bz_lord.ltx")) || user!=Data.HashFile(Data.Safe(game.Root,"userdata/user.ltx")))throw new Exception("Personal settings changed");
  Console.WriteLine("PASS personal settings unchanged; version "+game.State.version);
 }
}

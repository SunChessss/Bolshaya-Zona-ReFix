using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace ReFix {
 // 1.5: «Крупный интерфейс» — альтернативные XML окон (gamedata/config/ui_large, поставляются с патчем)
 // копируются поверх gamedata/config/ui; исходные файлы сохраняются побайтно в .refix-launcher/ui-original.
 public class UiScaleSetting {public string preset;public Dictionary<string,string> hashes;public Dictionary<string,string> originals;}
 public static class GameUiScale {
  public const string SourceDir="gamedata/config/ui_large",TargetDir="gamedata/config/ui",StatePath=".refix-launcher/ui-scale.json",BackupDir=".refix-launcher/ui-original";
  public static List<string> Files(GameService game) {
   string dir=Data.Safe(game.Root,SourceDir);if(!Directory.Exists(dir))return new List<string>();
   return Directory.GetFiles(dir,"*.xml").Select(Path.GetFileName).Where(n=>n.IndexOfAny(new[]{'\\','/',':'})<0).OrderBy(n=>n,StringComparer.Ordinal).ToList();
  }
  public static bool Available(GameService game){return Files(game).Count>0;}
  static bool Intact(GameService game,Dictionary<string,string> hashes) {
   foreach(var kv in hashes){string p=Data.Safe(game.Root,TargetDir+"/"+kv.Key);if(!File.Exists(p) || Data.HashFile(p)!=kv.Value)return false;}
   return true;
  }
  public static string Current(GameService game) {
   string path=Data.Safe(game.Root,StatePath);if(!File.Exists(path))return "original";
   var state=Data.Read<UiScaleSetting>(File.ReadAllText(path));return Intact(game,state.hashes)?state.preset:"custom";
  }
  public static void Save(GameService game,string preset) {
   if(preset!="original" && preset!="large")throw new ArgumentException("Неизвестный масштаб интерфейса");
   game.EnsureStopped();
   var files=Files(game);if(files.Count==0)throw new IOException("В этой сборке нет файлов крупного интерфейса (gamedata/config/ui_large). Установите актуальный патч.");
   string statePath=Data.Safe(game.Root,StatePath);UiScaleSetting previous=File.Exists(statePath)?Data.Read<UiScaleSetting>(File.ReadAllText(statePath)):null;
   if(previous!=null && !Intact(game,previous.hashes))throw new IOException("Файлы интерфейса изменены вручную или дополнением. Автоматическая замена остановлена, чтобы сохранить ваши правки.");
   if(previous==null && preset=="original")return;
   var originals=new Dictionary<string,byte[]>();
   foreach(string n in files) {
    bool backed=previous!=null && previous.originals.ContainsKey(n);
    string src=Data.Safe(game.Root,(backed?BackupDir:TargetDir)+"/"+n);
    if(!File.Exists(src))throw new IOException("Нет файла интерфейса: "+n);
    byte[] b=File.ReadAllBytes(src);
    if(backed && Data.Hash(b)!=previous.originals[n])throw new IOException("Резервная копия интерфейса повреждена: "+n);
    originals[n]=b;
   }
   var next=new Dictionary<string,byte[]>();
   foreach(string n in files)next[n]=preset=="original"?originals[n]:File.ReadAllBytes(Data.Safe(game.Root,SourceDir+"/"+n));
   using(var tx=new Transaction(game.Root)) {
    foreach(string n in files) {
     if(previous==null || !previous.originals.ContainsKey(n))tx.Put(BackupDir+"/"+n,originals[n]);
     tx.Put(TargetDir+"/"+n,next[n]);
    }
    var state=new UiScaleSetting{preset=preset,hashes=files.ToDictionary(n=>n,n=>Data.Hash(next[n])),originals=files.ToDictionary(n=>n,n=>Data.Hash(originals[n]))};
    tx.Put(StatePath,System.Text.Encoding.UTF8.GetBytes(Data.Json(state)));tx.Commit();
   }
  }
 }
}

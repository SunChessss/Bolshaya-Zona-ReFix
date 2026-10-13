using System;
using System.IO;
using System.Text.RegularExpressions;
namespace ReFix {
 public class FontSetting {public string preset;public string hash;public string original;}
 public static class GameFonts {
  const string ConfigPath="gamedata/config/fonts.ltx",StatePath=".refix-launcher/font-settings.json",BackupPath=".refix-launcher/font-original.ltx";
  public static string Current(GameService game) {
   string path=Data.Safe(game.Root,StatePath);if(!File.Exists(path))return "original";
   var state=Data.Read<FontSetting>(File.ReadAllText(path));return Data.HashFile(Data.Safe(game.Root,ConfigPath))==state.hash?state.preset:"custom";
  }
  public static string Transform(string original,string preset) {
   if(preset=="original")return original;
   if(preset!="large" && preset!="largest")throw new ArgumentException("Неизвестный размер шрифта");
   return Regex.Replace(original,@"(?im)^(\s*texture(?:800|1600)?\s*=\s*ui\\ui_font_)(letter_(?:16|18)|arial_14)(_(?:800|1024|1600))(?=\s*(?:;|$))",m=>m.Groups[1].Value+(m.Groups[2].Value=="arial_14"?"arial_21":m.Groups[2].Value=="letter_16" && preset=="large"?"letter_18":"letter_25")+m.Groups[3].Value);
  }
  public static void Save(GameService game,string preset) {
   game.EnsureStopped();string config=Data.Safe(game.Root,ConfigPath);byte[] current=File.ReadAllBytes(config);
   string statePath=Data.Safe(game.Root,StatePath);FontSetting previous=File.Exists(statePath)?Data.Read<FontSetting>(File.ReadAllText(statePath)):null;
   if(previous!=null && Data.Hash(current)!=previous.hash)throw new IOException("fonts.ltx изменён вручную или дополнением. Автоматическая замена остановлена, чтобы сохранить ваши правки.");
   if(previous==null && preset=="original")return;
   byte[] original=previous==null?current:File.ReadAllBytes(Data.Safe(game.Root,BackupPath));
   if(previous!=null && Data.Hash(original)!=previous.original)throw new IOException("Резервная копия шрифтов повреждена.");
   string text=Data.Legacy.GetString(original),updated=Transform(text,preset);
   if(preset!="original" && text==updated)throw new IOException("В этой сборке не найдены поддерживаемые шрифты Arial/Letterica.");
   foreach(Match m in Regex.Matches(updated,@"(?im)^\s*texture(?:800|1600)?\s*=\s*([^;\r\n]+)")) {
    string texture=m.Groups[1].Value.Trim().Replace('\\','/');
    if(!File.Exists(Data.Safe(game.Root,"gamedata/textures/"+texture+".dds")) || !File.Exists(Data.Safe(game.Root,"gamedata/textures/"+texture+".ini")))throw new IOException("Нет ресурса шрифта: "+texture);
   }
   byte[] next=preset=="original"?original:Data.Legacy.GetBytes(updated);
   using(var tx=new Transaction(game.Root)) {
    if(previous==null)tx.Put(BackupPath,original);
    tx.Put(ConfigPath,next);tx.Put(StatePath,System.Text.Encoding.UTF8.GetBytes(Data.Json(new FontSetting{preset=preset,hash=Data.Hash(next),original=Data.Hash(original)})));tx.Commit();
   }
  }
 }
}

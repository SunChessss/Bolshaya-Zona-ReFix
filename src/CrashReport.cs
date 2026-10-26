using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace ReFix {
 public class CrashReportResult { public string Zip; public int Dumps, Logs; public string Save; }
 // 1.10: data for a crash without a log - the engine writes xray_*.log only on a clean exit or a Lua error, so a native
 // crash leaves an empty log; the Windows dump (WER LocalDumps) is what shows the module and the call stack.
 public static class CrashReport {
  public const string DumpKey=@"SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps";
  public const string Exe="XR_3DA.exe";
  public static string DumpDir {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"CrashDumps");}}
  // the key of the game or the global one (Windows reads the 64-bit view)
  public static bool DumpsEnabled() {
   try {
    using(var hklm=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine,RegistryView.Registry64)) {
     using(var k=hklm.OpenSubKey(DumpKey+"\\"+Exe))if(k!=null)return true;
     using(var k=hklm.OpenSubKey(DumpKey))return k!=null && k.GetValueNames().Length>0;
    }
   } catch { return false; }
  }
  // mini dumps in the default folder %LOCALAPPDATA%\CrashDumps, the last 5; UAC asks for administrator rights.
  // false - the player declined the UAC prompt.
  public static bool EnableDumps() {
   string key="\"HKLM\\"+DumpKey+"\\"+Exe+"\"";
   string args="/c reg add "+key+" /v DumpType /t REG_DWORD /d 1 /f /reg:64 && reg add "+key+" /v DumpCount /t REG_DWORD /d 5 /f /reg:64";
   try {
    using(var p=Process.Start(new ProcessStartInfo("cmd.exe",args){Verb="runas",UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden})) {
     p.WaitForExit();
     if(p.ExitCode!=0)throw new IOException("Не удалось включить запись дампов (код "+p.ExitCode+").");
    }
   } catch(Win32Exception ex) { if(ex.NativeErrorCode==1223)return false; throw; }
   return true;
  }
  static IEnumerable<FileInfo> Newest(string dir,string pattern,int count) {
   if(!Directory.Exists(dir))return new FileInfo[0];
   return new DirectoryInfo(dir).GetFiles(pattern).OrderByDescending(f=>f.LastWriteTimeUtc).Take(count);
  }
  static void Add(ZipArchive zip,string file,string name) {
   // the log of a running game is open for writing
   using(var src=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
   using(var dst=zip.CreateEntry(name,CompressionLevel.Optimal).Open()) src.CopyTo(dst);
  }
  static void AddText(ZipArchive zip,string name,string text) {
   byte[] b=Encoding.UTF8.GetBytes(text);using(var dst=zip.CreateEntry(name).Open())dst.Write(b,0,b.Length);
  }
  // Windows "Application Error" (1000) for the game over the last 30 days: module, offset, exception code
  public static string Events() {
   var sb=new StringBuilder();
   try {
    var q=new EventLogQuery("Application",PathType.LogName,"*[System[(EventID=1000) and TimeCreated[timediff(@SystemTime) <= 2592000000]]]"){ReverseDirection=true};
    using(var r=new EventLogReader(q)) {
     int n=0;
     for(EventRecord e=r.ReadEvent();e!=null && n<10;e=r.ReadEvent()) using(e) {
      string text;try{text=e.FormatDescription();}catch{text=null;}
      if(text==null)text=string.Join(" | ",e.Properties.Select(p=>Convert.ToString(p.Value)));
      if(text.IndexOf(Exe,StringComparison.OrdinalIgnoreCase)<0)continue;
      sb.AppendLine(e.TimeCreated+"\r\n"+text+"\r\n");n++;
     }
    }
   } catch(Exception ex) { sb.AppendLine("Журнал событий недоступен: "+ex.Message); }
   return sb.Length==0?"Событий 1000 для "+Exe+" за 30 дней нет.\r\n":sb.ToString();
  }
  // zip: the 2 newest dumps, game logs and WER reports, the newest save with its picture, Windows events, info.txt
  public static CrashReportResult Collect(string root,string dumpDir,string zipPath,string info,string events) {
   string ud=Data.Safe(root,"userdata");
   var r=new CrashReportResult{Zip=zipPath};
   Directory.CreateDirectory(Path.GetDirectoryName(zipPath));
   string part=zipPath+".part";if(File.Exists(part))File.Delete(part);
   using(var zip=ZipFile.Open(part,ZipArchiveMode.Create)) {
    foreach(var f in Newest(dumpDir,Exe+".*.dmp",2)){Add(zip,f.FullName,"dumps/"+f.Name);r.Dumps++;}
    string logs=Path.Combine(ud,"logs");
    foreach(var f in Newest(logs,"*.log",3).Concat(Newest(logs,"*.wer",3))){Add(zip,f.FullName,"logs/"+f.Name);r.Logs++;}
    var sav=Newest(Path.Combine(ud,"savedgames"),"*.sav",1).FirstOrDefault();
    if(sav!=null) {
     Add(zip,sav.FullName,"save/"+sav.Name);r.Save=sav.Name;
     string dds=Path.ChangeExtension(sav.FullName,".dds");if(File.Exists(dds))Add(zip,dds,"save/"+Path.GetFileName(dds));
    }
    string user=Path.Combine(ud,"user.ltx");if(File.Exists(user))Add(zip,user,"user.ltx");
    string ops=Data.Safe(root,".refix-launcher/operations.log");if(File.Exists(ops))Add(zip,ops,"launcher/operations.log");
    AddText(zip,"events_1000.txt",events??"");
    AddText(zip,"info.txt",info+"\r\nДампов: "+r.Dumps+", логов: "+r.Logs+", сейв: "+(r.Save??"нет")+"\r\n");
   }
   if(File.Exists(zipPath))File.Delete(zipPath);File.Move(part,zipPath);
   return r;
  }
 }
}

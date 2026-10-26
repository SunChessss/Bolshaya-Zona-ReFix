using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace ReFix {
 public class SelfUpdateJob {public string root;public string game;public int parent;public long started;public string exeHash;public string configHash;}
 public static class SelfUpdate {
  public const string VersionString="1.10.0";
  public static bool IsNewer(LauncherAsset asset) {
   if(asset==null)return false;
   Version version;if(!Version.TryParse(asset.version,out version))throw new InvalidDataException("Неверная версия лаунчера в каталоге.");
   GameService.ValidateUrl(asset.url);
   if(asset.bytes<1 || asset.bytes>10485760 || !System.Text.RegularExpressions.Regex.IsMatch(asset.sha256??"","^[a-f0-9]{64}$"))throw new InvalidDataException("Неверное описание обновления лаунчера.");
   return version>new Version(VersionString);
  }
  public static Dictionary<string,byte[]> ReadPackage(string archive) {
   var files=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
   using(var zip=ZipFile.OpenRead(archive)) {
    foreach(var e in zip.Entries) {
     if(e.FullName!="ReFixLauncher.exe" && e.FullName!="ReFixLauncher.exe.config" && e.FullName!="README.md" && e.FullName!="ЛАУНЧЕР_README.md")throw new InvalidDataException("Посторонний файл в обновлении лаунчера.");
     if(e.Length<1 || e.Length>10485760 || files.ContainsKey(e.FullName))throw new InvalidDataException("Неверный состав обновления лаунчера.");
     using(var input=e.Open())using(var output=new MemoryStream()){input.CopyTo(output);files.Add(e.FullName,output.ToArray());}
    }
   }
   if(!files.ContainsKey("ReFixLauncher.exe") || !files.ContainsKey("ReFixLauncher.exe.config"))throw new InvalidDataException("В обновлении отсутствуют файлы лаунчера.");
   byte[] exe=files["ReFixLauncher.exe"];if(exe.Length<2 || exe[0]!=77 || exe[1]!=90)throw new InvalidDataException("Неверный формат EXE.");
   return files;
  }
  public static async Task<string> Prepare(LauncherAsset asset,string root,string game) {
   if(!IsNewer(asset))throw new InvalidOperationException("Новая версия лаунчера не требуется.");
   string dir=Data.Safe(root,".refix-self-update/"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
   string archive=Path.Combine(dir,"package.zip");OperationLog.Write(root,"SELF_DOWNLOAD_BEGIN",asset.version);
   ServicePointManager.SecurityProtocol|=SecurityProtocolType.Tls12;
   using(var client=new HttpClient()){client.DefaultRequestHeaders.UserAgent.ParseAdd("ReFixLauncher/"+VersionString);client.Timeout=TimeSpan.FromMinutes(5);
    using(var response=await client.GetAsync(asset.url,HttpCompletionOption.ResponseHeadersRead)) {
     response.EnsureSuccessStatusCode();using(var input=await response.Content.ReadAsStreamAsync())using(var output=new FileStream(archive,FileMode.CreateNew)) {
      byte[] b=new byte[65536];long total=0;int n;while((n=await input.ReadAsync(b,0,b.Length))>0){total+=n;if(total>asset.bytes)throw new InvalidDataException("Размер обновления превышен.");await output.WriteAsync(b,0,n);}if(total!=asset.bytes)throw new InvalidDataException("Неполная загрузка лаунчера.");
     }
    }
   }
   if(Data.HashFile(archive)!=asset.sha256)throw new InvalidDataException("Контрольная сумма обновления лаунчера не совпала.");
   var files=ReadPackage(archive);
   Data.Atomic(Path.Combine(dir,"next.exe"),files["ReFixLauncher.exe"]);Data.Atomic(Path.Combine(dir,"next.config"),files["ReFixLauncher.exe.config"]);
   File.Copy(Path.Combine(root,"ReFixLauncher.exe"),Path.Combine(dir,"apply.exe"));File.Copy(Path.Combine(root,"ReFixLauncher.exe.config"),Path.Combine(dir,"apply.exe.config"));
   var parent=Process.GetCurrentProcess();
   Data.WriteJson(Path.Combine(dir,"job.json"),new SelfUpdateJob{root=Data.Root(root),game=Data.Root(game),parent=parent.Id,started=parent.StartTime.ToUniversalTime().Ticks,exeHash=Data.Hash(files["ReFixLauncher.exe"]),configHash=Data.Hash(files["ReFixLauncher.exe.config"])});
   OperationLog.Write(root,"SELF_DOWNLOAD_VERIFIED",asset.version);return Path.Combine(dir,"job.json");
  }
  public static void Begin(string job) {
   Process.Start(new ProcessStartInfo(Path.Combine(Path.GetDirectoryName(job),"apply.exe"),"--apply-self-update \""+job+"\""){UseShellExecute=false});
  }
  public static void Apply(string jobPath) {
   string dir=Path.GetDirectoryName(Path.GetFullPath(jobPath));var job=Data.Read<SelfUpdateJob>(File.ReadAllText(jobPath));
   string allowed=Data.Safe(job.root,".refix-self-update")+Path.DirectorySeparatorChar;
   if(!dir.StartsWith(allowed,StringComparison.OrdinalIgnoreCase) || Path.GetFileName(jobPath)!="job.json" || !String.Equals(Path.GetFullPath(AppDomain.CurrentDomain.BaseDirectory).TrimEnd('\\'),dir.TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Неверная папка обновления.");
   try {var p=Process.GetProcessById(job.parent);if(p.StartTime.ToUniversalTime().Ticks==job.started && !p.WaitForExit(60000))throw new IOException("Лаунчер не закрылся. Обновление отложено.");}catch(ArgumentException){}
   byte[] exe=File.ReadAllBytes(Path.Combine(dir,"next.exe")),config=File.ReadAllBytes(Path.Combine(dir,"next.config"));
   if(Data.Hash(exe)!=job.exeHash || Data.Hash(config)!=job.configHash)throw new InvalidDataException("Подготовленное обновление изменилось.");
   OperationLog.Write(job.root,"SELF_INSTALL_BEGIN");
   using(var tx=new Transaction(job.root)){tx.Put("ReFixLauncher.exe",exe);tx.Put("ReFixLauncher.exe.config",config);tx.Commit();}
   OperationLog.Write(job.root,"SELF_INSTALL_COMPLETE");
   using(var launched=Process.Start(new ProcessStartInfo(Path.Combine(job.root,"ReFixLauncher.exe"),"--game \""+job.game+"\""){WorkingDirectory=job.root,UseShellExecute=false})) {
    OperationLog.Write(job.root,"SELF_RESTART_STARTED",launched.Id.ToString());
    if(launched.WaitForExit(3000))throw new IOException("Новая версия завершилась сразу после запуска. Код: "+launched.ExitCode);
    OperationLog.Write(job.root,"SELF_RESTART_RUNNING");
   }
  }
 }
}

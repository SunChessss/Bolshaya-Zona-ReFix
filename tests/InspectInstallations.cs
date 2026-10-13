using System;
using ReFix;
class InspectInstallations {
 static void Main(string[] args) {
  foreach(string root in args) {
   // Intentionally ignore launcher state: this tests detection of manual installations.
   var report=Installation.Verify(root,new LocalState(),Installation.Profiles());
   Console.WriteLine(root+"\n"+report.Title+"; verified="+report.Checked+"; differences="+report.Differences.Count+"; hash="+report.PatchHash);
   foreach(string line in report.Differences)Console.WriteLine(line);
  }
 }
}

using System;
using System.IO;
using System.Diagnostics;
namespace SS.Core {
 public static class ServiceOwnership {
  public static bool Matches(int pid,DateTime startUtc,string executable){
   try{using(var p=Process.GetProcessById(pid)){
    if(p.HasExited||Math.Abs((p.StartTime.ToUniversalTime()-startUtc.ToUniversalTime()).TotalSeconds)>.5)return false;
    return Path.GetFullPath(p.MainModule.FileName).Equals(Path.GetFullPath(executable),StringComparison.OrdinalIgnoreCase);
   }}catch{return false;}
  }
 }
}
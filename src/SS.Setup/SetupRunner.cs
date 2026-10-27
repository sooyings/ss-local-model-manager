using System;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace SSSetup {
 public sealed class RunResult {
  public int ExitCode; public int CoreExitCode; public int CoreProcessId; public string Status; public string Error; public string SessionDirectory; public string Target;
 }
 public static class SetupRunner {
  static string Q(string value) {
   if(value==null||value.IndexOf('"')>=0||value.EndsWith("\\",StringComparison.Ordinal))throw new ArgumentException("unsafe argument");
   return "\""+value+"\"";
  }
  static void EnsureSafeDirectory(string target) {
   string full=Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
   if(full.Length==0 || String.Equals(full,Path.GetPathRoot(full).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))throw new IOException("不能将程序安装在磁盘根目录");
   string p=full;
   while(!String.IsNullOrEmpty(p)) {
    if((Directory.Exists(p)||File.Exists(p))&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("安装路径含有链接或重解析点");
    string next=Path.GetDirectoryName(p);if(next==p)break;p=next;
   }
   if(File.Exists(full))throw new IOException("安装路径是文件");
   if(Directory.Exists(full))using(var it=Directory.EnumerateFileSystemEntries(full).GetEnumerator()){if(it.MoveNext())throw new IOException("安装目录不为空，请选择新的空目录");}
  }
  static string NewPrivateSession(out string id) {
   id=Guid.NewGuid().ToString("N");
   string parent=Path.Combine(Path.GetTempPath(),"SSLocalModelManager-Sessions"); Directory.CreateDirectory(parent);
   if((File.GetAttributes(parent)&FileAttributes.ReparsePoint)!=0)throw new IOException("会话目录是重解析点");
   string dir=Path.Combine(parent,id);
   var current=WindowsIdentity.GetCurrent().User;
   var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);acl.SetOwner(current);
   acl.AddAccessRule(new FileSystemAccessRule(current,FileSystemRights.FullControl,InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit,PropagationFlags.None,AccessControlType.Allow));
   Directory.CreateDirectory(dir);
#if NET8_0_OR_GREATER
   new DirectoryInfo(dir).SetAccessControl(acl);
#else
   Directory.SetAccessControl(dir,acl);
#endif
   return dir;
  }
  static void Poll(string eventsFile,ProgressTracker tracker,Action<int> progress) {
   try {
    if(!File.Exists(eventsFile)||new FileInfo(eventsFile).Length>4*1024*1024)return;
    string raw;
    using(var fs=new FileStream(eventsFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete))
    using(var sr=new StreamReader(fs,Encoding.UTF8,true))raw=sr.ReadToEnd();
    int end=raw.LastIndexOf('\n');if(end<0)return;
    string[] lines=raw.Substring(0,end+1).Split('\n');
    foreach(var line in lines) if(tracker.Accept(line.TrimEnd('\r')) && progress!=null)progress(tracker.Percent);
   } catch(IOException) { }
  }
  static void WriteResult(Options o,RunResult result) {
   string path=String.IsNullOrEmpty(o.Report)?Path.Combine(result.SessionDirectory,"install-result.json"):Path.GetFullPath(o.Report);
   File.WriteAllText(path,new JavaScriptSerializer().Serialize(new {schemaVersion=1,status=result.Status,exitCode=result.ExitCode,coreExitCode=result.CoreExitCode,coreProcessId=result.CoreProcessId,error=result.Error,installRoot=result.Target,sessionDirectory=result.SessionDirectory}),new UTF8Encoding(false));
  }
  public static RunResult Run(Options o,string corePath,Action<int> progress) {
   var result=new RunResult{ExitCode=26,CoreExitCode=-1,Status="failed",Target=o.Target};
   try {
    EnsureSafeDirectory(o.Target);
    if(!File.Exists(corePath))throw new IOException("缺少同目录的 SS.CoreSetup.exe");
    string id; result.SessionDirectory=NewPrivateSession(out id);
    string eventsFile=Path.Combine(result.SessionDirectory,"events.jsonl"); var tracker=new ProgressTracker(id);
    string target=o.Target.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
    var info=new ProcessStartInfo{FileName=corePath,Arguments="/VERYSILENT /SUPPRESSMSGBOXES /SP- /NORESTART /NOCLOSEAPPLICATIONS /NOFORCECLOSEAPPLICATIONS "+(o.NoIcons?"/NOICONS ":"")+"/DIR="+Q(target)+" /SSSESSION="+Q(result.SessionDirectory)+" /SSID="+id,UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(corePath)};
    using(var p=Process.Start(info)) {
     if(p==null)throw new IOException("安装内核启动失败");
     result.CoreProcessId=p.Id; var timer=Stopwatch.StartNew();bool pending=false;
     while(!p.WaitForExit(200)) {Poll(eventsFile,tracker,progress);if(timer.Elapsed>TimeSpan.FromMinutes(15)){pending=true;break;}}
     if(pending){result.Status="pending";result.Error="安装内核仍在运行；未强制终止，请稍后检查安装状态。";}
     else {Poll(eventsFile,tracker,progress);result.CoreExitCode=p.ExitCode;
      if(!tracker.Completed(p.ExitCode,target)){result.Error="安装内核失败或未完成必要的回读检查";result.Status="failed";}
      else {result.ExitCode=0;result.Status="installed_model_not_verified";if(progress!=null)progress(100);}}
    }
   } catch(Exception e){result.Error=e.Message;result.Status="failed";}
   try {if(result.SessionDirectory!=null || !String.IsNullOrEmpty(o.Report))WriteResult(o,result);}catch(Exception e){result.ExitCode=26;result.Status="failed";result.Error="结果文件写入失败："+e.Message;}
   return result;
  }
 }
}

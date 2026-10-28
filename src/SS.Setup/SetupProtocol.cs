using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace SSSetup {
 public sealed class Options {
  public bool Ok; public bool Unattended; public bool AcceptedLicense; public bool NoIcons; public string Target; public string Report; public string Error;
  static Options Fail(string m) {return new Options{Ok=false,Error=m};}
  public static Options Parse(string[] args) {
   var o=new Options{Target=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),@"Programs\SSLocalModelManager")};
   string config=null; bool dirSeen=false,reportSeen=false,licenseSeen=false,unattendedSeen=false;
   for(int i=0;i<args.Length;i++) {
    switch(args[i]) {
     case "--unattended": if(unattendedSeen)return Fail("重复的 --unattended");unattendedSeen=true;o.Unattended=true;break;
     case "--accept-license": if(licenseSeen)return Fail("重复的 --accept-license");licenseSeen=true;o.AcceptedLicense=true;break;
     case "--no-icons": if(o.NoIcons)return Fail("重复的 --no-icons");o.NoIcons=true;break;
     case "--dir": if(dirSeen||++i>=args.Length)return Fail("--dir 参数错误");dirSeen=true;o.Target=args[i];break;
     case "--report": if(reportSeen||++i>=args.Length)return Fail("--report 参数错误");reportSeen=true;o.Report=args[i];break;
     case "--config": if(config!=null||++i>=args.Length)return Fail("--config 参数错误");config=args[i];break;
     default: return Fail("不支持的参数："+args[i]);
    }
   }
   if(config!=null) {
    try {
     var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(config));
     if(d==null)return Fail("配置为空");
     foreach(string key in d.Keys)if(key!="schemaVersion"&&key!="installRoot"&&key!="reportPath")return Fail("不支持的配置项："+key);
     object v; if(!d.TryGetValue("schemaVersion",out v)||Convert.ToInt32(v)!=1)return Fail("配置版本不支持");
     if(d.TryGetValue("installRoot",out v)&&!dirSeen){string path=Convert.ToString(v);if(path!="auto")o.Target=path;}
     if(d.TryGetValue("reportPath",out v)&&!reportSeen)o.Report=Convert.ToString(v);
    } catch {return Fail("无法读取配置");}
   }
   if(o.Unattended&&!o.AcceptedLicense)return Fail("无人值守模式必须指定 --accept-license");
   try {if(!Path.IsPathRooted(o.Target))return Fail("安装路径必须是绝对路径");o.Target=Path.GetFullPath(o.Target);}
   catch{return Fail("安装路径无效");}
   o.Ok=true;return o;
  }
 }
 public sealed class ProgressTracker {
  readonly string session; long lastSeq; int percent; bool hadDeploy,installed;
  public int Percent {get{return percent;}}
  public ProgressTracker(string id){session=id;}
  public bool Accept(string line) {
   try {
    if(String.IsNullOrEmpty(line)||line.Length>4096)return false;
    var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(line);
    if(d==null || !d.ContainsKey("sessionId") || !d.ContainsKey("seq") || !d.ContainsKey("stage") || !d.ContainsKey("completed") || !d.ContainsKey("total"))return false;
    if(Convert.ToString(d["sessionId"])!=session)return false;
    long seq=Convert.ToInt64(d["seq"]), done=Convert.ToInt64(d["completed"]), total=Convert.ToInt64(d["total"]);
    string stage=Convert.ToString(d["stage"]);
    if(seq<=lastSeq||total<=0||done<0||done>total||installed)return false;
    if(stage=="deploy") {
     int next=(int)Math.Min(99,Math.Floor(100.0*done/total));if(next<percent)return false;
     hadDeploy=true;percent=next;
    } else if(stage=="installed") {
     if(!hadDeploy||done!=total)return false;installed=true;
    } else return false;
    lastSeq=seq;return true;
   } catch{return false;}
  }
  public bool Completed(int exitCode,string root) {return exitCode==0&&hadDeploy&&installed&&File.Exists(Path.Combine(root,"SSLocalModelManager.exe"));}
 }
}

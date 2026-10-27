using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using LocalMonitor;
namespace InferenceMonitor {
public class Snapshot {
 public bool Online; public string Model="",Name="模型未运行",Runtime="",Phase="offline",Scope="",TtftKind="",Note="";
 public double? Requests,Input,Output,Decode,Prefill,LastDecode,LastPrefill,Average,Peak,TtftMs,Context,UsedTokens,KvPercent,Prefix,Draft,Slots,ActiveSlots,Queued,VramUsed,VramTotal,GpuUtil,Temp,Power;
 public double? KvResidentCells,KvTotalCells,KvHostGiB,KvHitPercent,KvGpuMiB,KvCpuMiB;public string KvFormat="";
 public double At; public string GpuName="GPU";
}
public static class Adapter {
 public static Dictionary<string,object> Dict(object o){return o as Dictionary<string,object>??new Dictionary<string,object>();}
 public static object Get(object o,string k){object v;return Dict(o).TryGetValue(k,out v)?v:null;}
 public static string Str(object o,string k){return Convert.ToString(Get(o,k)??"");}
 public static List<object> List(object o){var a=o as IEnumerable;return a==null||o is string||o is IDictionary?new List<object>():a.Cast<object>().ToList();}
 public static double? Num(object o,string k){object v=Get(o,k);double n;if(v==null||v is bool||!double.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out n)||double.IsNaN(n)||double.IsInfinity(n)||n<0)return null;return n;}
 public static double? Ratio(double? a,double? b){return a.HasValue&&b>0?Math.Min(100,a.Value/b.Value*100):(double?)null;}
 public static Snapshot Strata(string model,object raw){
  var engine=Get(raw,"engine");if(Str(engine,"model")!=model)return new Snapshot{Note="模型身份正在变化，等待下一次采样"};
  var s=new Snapshot{Online=true,Model=model,Runtime="Strata",Phase=Str(Get(raw,"live"),"state"),Scope="当前服务累计",At=Num(raw,"time")??0};
  var totals=Get(raw,"totals");var live=Get(raw,"live");var hardware=Get(raw,"hardware");
  s.KvFormat=Str(engine,"kv");s.Requests=Num(totals,"requests");s.Input=Num(totals,"prompt_tokens");s.Output=Num(totals,"output_tokens");s.Context=Num(engine,"max_context");
  s.Prefix=Ratio(Num(totals,"reused"),s.Input);s.Draft=Ratio(Num(totals,"drafts_accepted"),Num(totals,"drafts_offered"));s.Average=Num(totals,"decode_ms")>0?s.Output*1000/Num(totals,"decode_ms"):null;
  s.Decode=s.Phase=="generating"?Num(live,"tok_s"):null;s.Prefill=s.Phase=="reading"?Num(live,"prefill_tok_s_mean"):null;
  s.Queued=Num(live,"queued");s.Slots=Num(live,"parallel")??1;s.ActiveSlots=Num(live,"running")??(s.Phase=="generating"||s.Phase=="reading"?1:0);
  var requests=List(Get(raw,"requests"));if(requests.Count>0){var r=requests[0];s.LastDecode=Num(r,"decode_tok_s");double? read=Num(r,"prompt_read"),ms=Num(r,"prompt_ms");s.LastPrefill=ms>0&&read.HasValue?read*1000/ms:null;s.TtftMs=Num(r,"first_engine_token_s")*1000;s.TtftKind="首推理 token";s.UsedTokens=Num(r,"prompt_total")+Num(r,"engine_generated");var peaks=requests.Select(x=>Num(x,"decode_tok_s")).Where(x=>x.HasValue).ToList();s.Peak=peaks.Count>0?peaks.Max():null;}
  s.VramUsed=Num(hardware,"gpu_mem_used")/1073741824;s.VramTotal=Num(hardware,"gpu_mem_total")/1073741824;s.GpuUtil=Num(hardware,"gpu_util");s.Temp=Num(hardware,"gpu_temp");s.Power=Num(hardware,"gpu_power");
  // KV capacity/allocation is read from the current process log. Prompt length is not used KV bytes.
  return s;
 }
 public static void ApplySlots(Snapshot s,object raw){
  var slots=List(raw);if(slots.Count==0)return;
  s.Slots=slots.Count;s.ActiveSlots=slots.Count(x=>Get(x,"is_processing") is bool&&(bool)Get(x,"is_processing"));
  s.UsedTokens=null;s.KvPercent=null;s.Prefix=null;
  double cached=0,processed=0;bool hasReuse=false;double best=-1;
  foreach(var slot in slots){
   var capacity=Num(slot,"n_ctx")??s.Context;var past=Num(slot,"n_past");var used=past??Num(slot,"n_prompt_tokens");
   double score=Ratio(used,capacity)??-1;
   if(score>best||(best<0&&capacity.HasValue)){best=score;s.Context=capacity??s.Context;s.UsedTokens=used;s.KvPercent=Ratio(past,capacity);}
   var c=Num(slot,"n_prompt_tokens_cache");var n=Num(slot,"n_prompt_tokens_processed");
   if(c.HasValue&&n.HasValue){cached+=c.Value;processed+=n.Value;hasReuse=true;}
  }
  if(hasReuse)s.Prefix=Ratio(cached,cached+processed);
  if(s.ActiveSlots>0)s.Phase="processing";
 }

}
public class ObservedTotals {
 public double Requests;public double? Input=0,Output=0;string model="";double since;HashSet<string> ids=new HashSet<string>();
 public void Reset(string m,double time){model=m;since=time;Requests=0;Input=0;Output=0;ids.Clear();}
 public void Apply(object raw){if(Adapter.Str(raw,"kind")!="sample"||Adapter.Str(raw,"model")!=model||Adapter.Str(raw,"error")!=""||(Adapter.Num(raw,"ts")??0)<since)return;string id=Adapter.Str(raw,"request_id");if(id==""||!ids.Add(id))return;Requests++;var n=Adapter.Num(raw,"prompt_tokens");Input=Input.HasValue&&n.HasValue?Input+n:null;n=Adapter.Num(raw,"predicted_tokens");Output=Output.HasValue&&n.HasValue?Output+n:null;}
}
public class DecodeLogState {
 string active="";double stamp;double? live;public double? Completed;
 static Regex task=new Regex(@"id\s+(\d+)\s*\|\s*task\s+(\d+)");
 static Regex rate=new Regex(@"n_gen\s*=\s*(\d+),\s*tg\s*=\s*([\d.]+) t/s");
 static Regex done=new Regex(@"\|\s+eval time\s*=\s*[\d.]+ ms /\s*(\d+) tokens.*?,\s*([\d.]+) tokens per second");
 public void Feed(string line,double now){var t=task.Match(line);if(!t.Success)return;string key=t.Groups[1].Value+":"+t.Groups[2].Value;
  if(line.Contains("launch_slot_")){active=key;live=null;}
  if(line.Contains("release:")&&key==active){active="";live=null;}
  var d=done.Match(line);if(d.Success){if(int.Parse(d.Groups[1].Value)>=32)Completed=double.Parse(d.Groups[2].Value,CultureInfo.InvariantCulture);if(key==active){live=null;active="";}return;}
  var r=rate.Match(line);if(r.Success&&key==active&&int.Parse(r.Groups[1].Value)>=32){live=double.Parse(r.Groups[2].Value,CultureInfo.InvariantCulture);stamp=now;}
 }
 public double? Current(double now){return active!=""&&now-stamp<3?live:null;}
}
public class Collector {
 static readonly string Root=SS.Core.AppPaths.DataRoot;readonly JavaScriptSerializer json=new JavaScriptSerializer{MaxJsonLength=4194304};
 KvLogReader kvLog=new KvLogReader();DecodeLogState decodeLog=new DecodeLogState();Metrics core=new Metrics();ObservedTotals observed=new ObservedTotals();TailReader logs,records;string model="",session="";double acceptedAfter=Now();Dictionary<string,object> registry;double? logRequests,logInput,logOutput,lastLogPrefill;HashSet<string> logIds=new HashSet<string>();
 static double Now(){return (DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds;}
 public Collector():this(Root+@"\logs\llama.log",Root+@"\monitor\telemetry.jsonl"){}
 public Collector(string logPath,string recordsPath){logs=new TailReader(logPath);records=new TailReader(recordsPath);}
 public virtual object Read(string path){if(SS.Core.NativeHost.RunningAlias()=="")throw new InvalidOperationException("no_owned_service");var req=(HttpWebRequest)WebRequest.Create(SS.Core.AppPaths.DirectBaseUrl+path);req.Proxy=null;req.Timeout=1100;req.ReadWriteTimeout=1100;using(var response=req.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){return json.DeserializeObject(reader.ReadToEnd());}}
 object TryRead(string path){try{return Read(path);}catch{return null;}}
 protected virtual string Session(){try{return File.ReadAllText(Root+@"\logs\llama.pid").Trim();}catch{return "";}}
 void Reset(string actual,string current){model=actual;session=current;kvLog=new KvLogReader();core.Reset(model);decodeLog=new DecodeLogState();observed.Reset(model,Now());acceptedAfter=Now();logs.Skip();records.Skip();logRequests=0;logInput=0;logOutput=0;lastLogPrefill=null;logIds.Clear();pending.Clear();}
 public Snapshot Poll(){
  object ids=TryRead("/v1/models");var models=Adapter.List(Adapter.Get(ids,"data"));string actual=models.Count>0?Adapter.Str(models[0],"id"):"";
  string current=Session();if(current!=""&&session!=current)Reset("",current);if(actual!=""&&actual!=model)Reset(actual,current);
  if(actual==""){var offline=new Snapshot{Phase=model!=""?"reconnecting":Loading()?"loading":"offline",Note="身份暂不可读，保留已采集累计等待恢复"};SampleHardware(offline);return offline;}
  object entry=null;try{registry=Adapter.Dict(json.DeserializeObject(File.ReadAllText(Root+@"\config\models.json")));foreach(var kv in Adapter.Dict(Adapter.Get(registry,"models"))){if(Adapter.Str(kv.Value,"alias")==actual||kv.Key==actual){entry=kv.Value;break;}}}catch{}
  object raw=TryRead("/metrics");if(raw==null&&Adapter.Str(entry,"runtime")=="strata")return new Snapshot{Phase="reconnecting",Note="推理指标暂不可读，保留累计等待恢复"};Snapshot s=Adapter.Get(raw,"engine")!=null?Adapter.Strata(actual,raw):new Snapshot{Online=true,Model=actual,Runtime="Prism / llama.cpp",Phase="idle",Scope="监控观察累计 · 未缓存输入"};
  if(!s.Online){SampleHardware(s);return s;}
  try{registry=Adapter.Dict(json.DeserializeObject(File.ReadAllText(Root+@"\config\models.json")));var registered=Adapter.Dict(Adapter.Get(registry,"models"));foreach(var kv in registered){if(Adapter.Str(kv.Value,"alias")!=actual&&kv.Key!=actual)continue;s.Name=Adapter.Str(kv.Value,"display");if(!s.Context.HasValue)s.Context=Adapter.Num(kv.Value,"context");break;}}catch{}
  if(s.Name=="模型未运行")s.Name=actual;
  if(s.Runtime!="Strata"){
   object props=TryRead("/props");var defaults=Adapter.Get(props,"default_generation_settings");s.Context=Adapter.Num(defaults,"n_ctx")??s.Context;Adapter.ApplySlots(s,TryRead("/slots"));
   foreach(var line in logs.Read()){core.FeedLog(line,Now());decodeLog.Feed(line,Now());CountLog(line);}
   foreach(var line in records.Read())try{var record=json.DeserializeObject(line);double stamp=Adapter.Num(record,"ts")??0;if(stamp>=acceptedAfter&&stamp<=Now()+5){core.Apply(Adapter.Dict(record));observed.Apply(record);}}catch{}
   // Router records and server logs describe overlapping requests: never add them together.
   s.Requests=logRequests>0?logRequests:observed.Requests;s.Input=logRequests>0?logInput:observed.Input;s.Output=logRequests>0?logOutput:observed.Output;
   s.LastDecode=decodeLog.Completed??(core.Items[0].Source!="log"?core.Items[0].Value:null);s.LastPrefill=lastLogPrefill??core.Items[2].Value;if(s.ActiveSlots==1)s.Decode=decodeLog.Current(Now());if(s.Decode.HasValue)s.Phase="generating";
   if(core.Items[1].Value.HasValue){s.TtftMs=core.Items[1].Value*1000;s.TtftKind="首正文 · 路由";}
   s.Note="槽位上下文来自/slots；前缀为本次提示词复用率；动态KV已用字节与投机接受率未暴露；累计从监控开始观察";
  }
  ReadKv(s,entry,current);
  string verifiedSession=Session();if(verifiedSession!=""&&current!=""&&verifiedSession!=current){Reset("",verifiedSession);return new Snapshot{Phase="reconnecting",Note="服务正在切换，等待新采样"};}var verifyModels=Adapter.List(Adapter.Get(TryRead("/v1/models"),"data"));if(verifyModels.Count==0)return new Snapshot{Phase="reconnecting",Note="复核身份暂不可读，保留累计等待恢复"};string verifiedAlias=Adapter.Str(verifyModels[0],"id");if(verifiedAlias!=""&&verifiedAlias!=actual){Reset(verifiedAlias,current);return new Snapshot{Phase="reconnecting",Note="模型正在切换，等待新采样"};}if(!s.VramTotal.HasValue)SampleHardware(s);return s;
 }
 void ReadKv(Snapshot s,object entry,string pid){
  try {
   DateTime started=Process.GetProcessById(int.Parse(pid)).StartTime;string file=Root+@"\logs\llama.log";
   if(s.Runtime=="Strata"){
    string configPath=Path.GetFullPath(Path.Combine(Root,Adapter.Str(entry,"runtimeConfig")));
    if(!configPath.StartsWith(Root+@"\",StringComparison.OrdinalIgnoreCase))return;
    var cfg=json.DeserializeObject(File.ReadAllText(configPath));
    file=Path.GetFullPath(Adapter.Str(cfg,"log"));
    if(!file.StartsWith(Root+@"\",StringComparison.OrdinalIgnoreCase))return;
   }
   long begin=0;bool verified=s.Runtime=="Strata";
   if(!verified){
    var record=json.DeserializeObject(File.ReadAllText(Root+@"\logs\llama-session.json"));
    double processStart=(started.ToUniversalTime()-new DateTime(1970,1,1)).TotalSeconds;
    double fileCreated=(File.GetCreationTimeUtc(file)-new DateTime(1970,1,1)).TotalSeconds;
    var after=Adapter.Num(record,"spawned_after");var before=Adapter.Num(record,"spawned_before");
    var created=Adapter.Num(record,"log_created");var position=Adapter.Num(record,"offset");
    verified=Adapter.Str(record,"pid")==pid&&Adapter.Str(record,"alias")==s.Model
      &&Path.GetFullPath(Adapter.Str(record,"log")).Equals(file,StringComparison.OrdinalIgnoreCase)
      &&after.HasValue&&before.HasValue&&processStart>=after.Value-1&&processStart<=before.Value+1
      &&created.HasValue&&Math.Abs(fileCreated-created.Value)<.01&&position.HasValue&&position.Value<=long.MaxValue;
    if(!verified)return;begin=(long)position.Value;
   }
   kvLog.Read(file,s.Runtime=="Strata",Adapter.Str(entry,"main"),started,begin,verified);if(kvLog.Ready)kvLog.State.Apply(s);
  }catch{}
 }
 static Regex logPrompt=new Regex(@"prompt eval time\s*=\s*[\d.]+ ms /\s*(\d+) tokens.*?,\s*([\d.]+) tokens per second");static Regex logDecode=new Regex(@"\|\s+eval time\s*=\s*[\d.]+ ms /\s*(\d+) tokens");static Regex task=new Regex(@"id\s+(\d+)\s*\|\s*task\s+(\d+)");Dictionary<string,double> pending=new Dictionary<string,double>();
 void CountLog(string line){var key=task.Match(line);if(!key.Success)return;string id=key.Groups[1].Value+":"+key.Groups[2].Value;var p=logPrompt.Match(line);if(p.Success){pending[id]=double.Parse(p.Groups[1].Value,CultureInfo.InvariantCulture);lastLogPrefill=double.Parse(p.Groups[2].Value,CultureInfo.InvariantCulture);}var d=logDecode.Match(line);if(d.Success&&logIds.Add(id)){logRequests=(logRequests??0)+1;double prompt;if(pending.TryGetValue(id,out prompt))logInput+=prompt;else logInput=null;logOutput+=double.Parse(d.Groups[1].Value,CultureInfo.InvariantCulture);pending.Remove(id);}}
 protected virtual bool Loading(){try{int pid=int.Parse(Session());var p=Process.GetProcessById(pid);return !p.HasExited;}catch{return false;}}
 protected virtual void SampleHardware(Snapshot s){Hardware(s);}
 public static void Hardware(Snapshot s){try{var info=new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"nvidia-smi.exe"),"--query-gpu=name,utilization.gpu,memory.used,memory.total,temperature.gpu,power.draw --format=csv,noheader,nounits"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};using(var p=Process.Start(info)){if(!p.WaitForExit(1800)){p.Kill();return;}string[] a=p.StandardOutput.ReadToEnd().Split('\n')[0].Trim().Split(',');if(a.Length<6)return;s.GpuName=a[0].Trim();double v;if(double.TryParse(a[1],NumberStyles.Float,CultureInfo.InvariantCulture,out v))s.GpuUtil=v;if(double.TryParse(a[2],NumberStyles.Float,CultureInfo.InvariantCulture,out v))s.VramUsed=v/1024;if(double.TryParse(a[3],NumberStyles.Float,CultureInfo.InvariantCulture,out v))s.VramTotal=v/1024;if(double.TryParse(a[4],NumberStyles.Float,CultureInfo.InvariantCulture,out v))s.Temp=v;if(double.TryParse(a[5],NumberStyles.Float,CultureInfo.InvariantCulture,out v))s.Power=v;}}catch{}}
}
}






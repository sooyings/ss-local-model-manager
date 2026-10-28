using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

// JSON 解析由调用方注入：.NET Framework 可传 JavaScriptSerializer.DeserializeObject；
// .NET 10 可传返回 Dictionary<string,object>/object[] 的 System.Text.Json 适配器。
namespace SsTuning {
 public sealed class HardwareBudget {
  public long? TotalRamMiB, AvailableRamMiB, TotalVramMiB, AvailableVramMiB;
  public long? RamPeakEstimateMiB, VramPeakEstimateMiB, BackendGpuMinimumReserveMiB;
  public int? LogicalThreads;
  public bool GpuRequired;
 }
 public static class TuningAdvice {
  static readonly Dictionary<string,string[]> Models=new Dictionary<string,string[]>(StringComparer.Ordinal){
   {"qwen3.6-35b-a3b-uncensored",new[]{"Q4_K_M","prism-fork"}},
   {"qwen3.5-9b",new[]{"UD-Q4_K_XL","prism-fork"}},
   {"qwen3.8-flash-next-iq3_xxs",new[]{"IQ3_XXS","strata"}},
   {"qwen3.8-flash-next-swift-iq2_xs",new[]{"IQ2_XS","strata"}},
   {"qwen3-0.6b-hub",new[]{"Q8_0","prism-fork"}}
  };
  static readonly string[] Common={"temperature","top_p","top_k","min_p","repeat_penalty","reasoning_budget"};
  static readonly HashSet<string> Usages=new HashSet<string>(new[]{"balanced","creative","code","long","quiet"},StringComparer.Ordinal);
  static readonly HashSet<string> Evidence=new HashSet<string>(new[]{"model_guidance","local_baseline","starting_point"},StringComparer.Ordinal);
  static Dictionary<string,object> D(object value){return value as Dictionary<string,object>;}
  static string Str(object value){return value as string;}
  static object Get(Dictionary<string,object> map,string key){object value;return map!=null&&map.TryGetValue(key,out value)?value:null;}
  static string StatusZh(string basis){return basis=="model_guidance"?"作者建议，非本机最优证明。":basis=="starting_point"?"起始参考，尚未专项验证。":basis=="rule_derived"?"按输入硬件条件推导，未经本机实测。":"单机基线，非逐项最优。";}
  static string StatusEn(string basis){return basis=="model_guidance"?"Author guidance, not a local optimum.":basis=="starting_point"?"Starting point, not benchmarked.":basis=="rule_derived"?"Derived from supplied hardware; not locally measured.":"Single-machine baseline, not an optimum.";}
  static Dictionary<string,object> Pending(string reason){return new Dictionary<string,object>{{"state","pending"},{"reason",reason},{"identity_state","unverified"},{"complete_profile",false},{"profile",null},{"fields",new Dictionary<string,object>()},{"resource_rules",null},{"automatic_apply_allowed",false}};}
  static bool Number(object x,out double v){
   v=0;if(!(x is int || x is long || x is double || x is decimal || x is float || x is short || x is byte))return false;
   try{v=Convert.ToDouble(x,CultureInfo.InvariantCulture);return !Double.IsNaN(v)&&!Double.IsInfinity(v);}catch{return false;}
  }
  static bool InRange(string key,double value){
   double max=key=="temperature"?2:key=="top_p"||key=="min_p"?1:key=="repeat_penalty"?2:key=="top_k"?1000:key=="reasoning_budget"?131072:key=="threads"||key=="workers"?1024:1048576;
   double min=key=="threads"||key=="workers"?1:0;
   bool integer=key=="top_k"||key=="reasoning_budget"||key=="threads"||key=="workers"||key=="cacheRam"||key=="reserve";
   return value>=min&&value<=max&&(!integer||Math.Floor(value)==value);
  }
  static bool ValidBudget(long? total,long? available){return total.HasValue&&available.HasValue&&total.Value>0&&total.Value<=1073741824&&available.Value>=0&&available.Value<=total.Value;}
  static bool Nonnegative(long? n){return n.HasValue&&n.Value>=0&&n.Value<=1073741824;}
  static long Reserve(long total,long floor){return Math.Max(floor,(long)Math.Ceiling(total*0.15));}
  static bool PublicSources(object input,out object[] urls){
   urls=null;var seq=input as IEnumerable;if(seq==null||input is string)return false;var list=new List<object>();
   foreach(object entry in seq){var s=entry as string;Uri u;if(s==null||!Uri.TryCreate(s,UriKind.Absolute,out u)||u.Scheme!=Uri.UriSchemeHttps||!String.Equals(u.Host,"huggingface.co",StringComparison.OrdinalIgnoreCase)||u.UserInfo!="")return false;list.Add(s);if(list.Count>20)return false;}
   if(list.Count==0)return false;urls=list.ToArray();return true;
  }
  static string[] Expected(string engine){var keys=new List<string>(Common);if(engine=="strata")keys.AddRange(new[]{"workers","reserve"});else keys.AddRange(new[]{"threads","cacheRam"});return keys.ToArray();}
  static Dictionary<string,object> LoadFields(string path,string model,string engine,Func<string,object> deserialize){
   if(deserialize==null)throw new ArgumentException("decoder unavailable");
   var file=new FileInfo(path);if(!file.Exists||file.Length>1048576)throw new InvalidDataException("seed unavailable");
   var root=D(deserialize(File.ReadAllText(path)));if(Str(Get(root,"version"))!="3.2.0")throw new InvalidDataException("version");
   var models=D(Get(root,"models"));var modelFields=D(Get(models,model));var keys=Expected(engine);if(modelFields==null||modelFields.Count!=keys.Length)throw new InvalidDataException("profile fields");
   var result=new Dictionary<string,object>(StringComparer.Ordinal);
   foreach(string key in keys){
    var item=D(Get(modelFields,key));double val;if(!Number(Get(item,"value"),out val)||!InRange(key,val))throw new InvalidDataException("value");
    string basis=Str(Get(item,"basis")),zh=Str(Get(item,"why_zh")),en=Str(Get(item,"why_en"));object[] links;
    if(!Evidence.Contains(basis??"")||String.IsNullOrWhiteSpace(zh)||String.IsNullOrWhiteSpace(en)||zh.Length>4096||en.Length>4096||!PublicSources(Get(item,"sources"),out links))throw new InvalidDataException("evidence");
    result.Add(key,new Dictionary<string,object>{{"value",val},{"basis",basis},{"why_zh",zh},{"why_en",en},{"status_zh",StatusZh(basis)},{"status_en",StatusEn(basis)},{"sources",links}});
   }return result;
  }
  static Dictionary<string,object> CopyFields(Dictionary<string,object> source){var result=new Dictionary<string,object>(StringComparer.Ordinal);foreach(var entry in source)result.Add(entry.Key,new Dictionary<string,object>((Dictionary<string,object>)entry.Value));return result;}
  static double Value(Dictionary<string,object> fields,string key){return Convert.ToDouble(((Dictionary<string,object>)fields[key])["value"],CultureInfo.InvariantCulture);}
  static void SetDerived(Dictionary<string,object> fields,string key,double value,string zh,string en){
   var item=(Dictionary<string,object>)fields[key];if(Convert.ToDouble(item["value"],CultureInfo.InvariantCulture)==value)return;
   item["value"]=value;item["basis"]="rule_derived";item["why_zh"]=zh;item["why_en"]=en;item["status_zh"]=StatusZh("rule_derived");item["status_en"]=StatusEn("rule_derived");
  }
  // 新建表单行并附加 recommendation；源行与用户配置均不变。
  public static List<object> DecorateRows(IEnumerable rows,Dictionary<string,object> evaluation){
   if(rows==null||rows is string)throw new ArgumentException("rows");
   var fields=D(Get(evaluation,"fields"));var output=new List<object>();
   foreach(object entry in rows){
    var row=D(entry);if(row==null)throw new ArgumentException("invalid row");
    var copy=new Dictionary<string,object>(row);string key=Str(Get(row,"key"));
    var found=key==null?null:D(Get(fields,key));
    copy["recommendation"]=found==null
      ? new Dictionary<string,object>{{"value",null},{"basis","pending"},{"why_zh","该模型或参数的建议尚未评估。"},{"why_en","This model or parameter is not reviewed."},{"status_zh","待评估"},{"status_en","Not reviewed"},{"sources",new object[0]}}
      : new Dictionary<string,object>(found);
    output.Add(copy);
   }
   return output;
  }
  // 输入预算为调用方真实探测的数值/估算；本组件不读硬件、不启动模型、不写任何配置。
  public static Dictionary<string,object> Evaluate(string seedPath,string modelId,string quantization,string engineId,HardwareBudget h,string usage,Func<string,object> deserialize){
   string[] identity;if(modelId==null||!Models.TryGetValue(modelId,out identity)||identity[0]!=quantization||identity[1]!=engineId)return Pending("unknown_model_identity");
   if(usage==null||!Usages.Contains(usage))return Pending("unknown_usage");
   Dictionary<string,object> fields;try{fields=LoadFields(seedPath,modelId,engineId,deserialize);}catch(Exception){return Pending("seed_invalid_or_unavailable");}
   var result=new Dictionary<string,object>{{"state","reference_only"},{"reason","resource_inputs_missing_or_insufficient"},{"identity_state","unverified"},{"complete_profile",false},{"profile",null},{"fields",fields},{"resource_rules",null},{"automatic_apply_allowed",false}};
   if(h==null||!ValidBudget(h.TotalRamMiB,h.AvailableRamMiB)||!Nonnegative(h.RamPeakEstimateMiB)||!h.LogicalThreads.HasValue||h.LogicalThreads.Value<1||h.LogicalThreads.Value>1024)return result;
   if(engineId=="strata"&&!h.GpuRequired)return result;
   long ramReserve=Reserve(h.TotalRamMiB.Value,4096),gpuReserve=0;
   if(h.RamPeakEstimateMiB.Value>h.AvailableRamMiB.Value-ramReserve)return result;
   if(h.GpuRequired){
    if(!ValidBudget(h.TotalVramMiB,h.AvailableVramMiB)||!Nonnegative(h.VramPeakEstimateMiB)||!Nonnegative(h.BackendGpuMinimumReserveMiB))return result;
    gpuReserve=Reserve(h.TotalVramMiB.Value,Math.Max(1024,h.BackendGpuMinimumReserveMiB.Value));
    // 同一套配置的引擎预留也必须纳入预算，不能只计算通用下限。
    if(engineId=="strata")gpuReserve=Math.Max(gpuReserve,(long)Value(fields,"reserve"));
    if(gpuReserve>h.TotalVramMiB.Value||h.VramPeakEstimateMiB.Value>h.AvailableVramMiB.Value-gpuReserve)return result;
   }
   var candidate=CopyFields(fields);string worker=engineId=="strata"?"workers":"threads";
   int divisor=usage=="quiet"?4:2;double threads=Math.Max(1,Math.Min(Value(candidate,worker),Math.Max(1,h.LogicalThreads.Value/divisor)));
   SetDerived(candidate,worker,threads,"基于逻辑线程数和场景的保守上限；实际速度未测。","Conservative cap from logical threads and usage; performance is unmeasured.");
   double cap=usage=="quiet"?512:usage=="long"?2048:1024;
   SetDerived(candidate,"reasoning_budget",Math.Min(Value(candidate,"reasoning_budget"),cap),"按所选场景限制思考预算；复杂任务可能需要增加。","Thinking budget cap for the chosen use; complex work may require more.");
   if(engineId=="prism-fork"){
    double cache=Math.Min(Value(candidate,"cacheRam"),Math.Min(2048,Math.Max(0,(h.AvailableRamMiB.Value-ramReserve)/16)));
    SetDerived(candidate,"cacheRam",cache,"限制空闲缓存以保留系统内存；不是KV显存。","Limits idle cache to leave system memory; not KV VRAM.");
   } else if(h.GpuRequired) {
    double reserve=Math.Max(Value(candidate,"reserve"),gpuReserve);
    if(!InRange("reserve",reserve)||reserve>h.TotalVramMiB.Value)return result;
    SetDerived(candidate,"reserve",reserve,"按显存容量和后端最低要求保留空间；不是实际占用。","Reserve based on GPU capacity and backend minimum; not actual usage.");
   }
   var profile=new Dictionary<string,object>(StringComparer.Ordinal);foreach(var pair in candidate)profile.Add(pair.Key,((Dictionary<string,object>)pair.Value)["value"]);
   result["state"]="rule_derived";result["reason"]="estimated_only_not_validated";result["fields"]=candidate;result["profile"]=profile;
   result["resource_rules"]=new Dictionary<string,object>{{"basis","rule_derived"},{"ram_reserve_mib",ramReserve},{"gpu_reserve_mib",h.GpuRequired?(object)gpuReserve:null},{"usage",usage}};
   return result;
  }
 }
}

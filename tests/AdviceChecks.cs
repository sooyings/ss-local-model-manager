using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;
using SsTuning;
class AdviceChecks {
 static int n=0;
 static void Check(bool ok,string message){ n++; if(!ok) throw new Exception(message); }
 static Dictionary<string,object> Dict(object x){return (Dictionary<string,object>)x;}
 static double Num(object x){return Convert.ToDouble(x);}
 static object Parse(string x){return new JavaScriptSerializer().DeserializeObject(x);}
 static HardwareBudget H(){ return new HardwareBudget{TotalRamMiB=65536,AvailableRamMiB=45000,TotalVramMiB=12288,AvailableVramMiB=11000, RamPeakEstimateMiB=20000,VramPeakEstimateMiB=6000,BackendGpuMinimumReserveMiB=512,LogicalThreads=20,GpuRequired=true}; }
 static Dictionary<string,object> Call(string model,string quant,string engine,HardwareBudget h,string usage){ return TuningAdvice.Evaluate("seed-public.json",model,quant,engine,h,usage,Parse); }
 static void Main(){
  var x=Call("qwen3.8-flash-next-iq3_xxs","IQ3_XXS","strata",H(),"balanced");
  var s=Call("qwen3.8-flash-next-swift-iq2_xs","IQ2_XS","strata",H(),"balanced");
  Check((string)x["state"]=="rule_derived", "eligible rule-derived");
  Check(Num(Dict(x["profile"])["workers"])==6, "XXS workers");
  Check(Num(Dict(s["profile"])["workers"])==10, "Swift independently bounded workers");
  Check(Dict(x["fields"]).Count==8,"profile fields");
  Check(!(bool)x["automatic_apply_allowed"],"no writes/automatic application");
  Check((string)Dict(Dict(x["fields"])["workers"])["basis"]=="local_baseline", "unchanged source level");
  Check((string)Dict(Dict(s["fields"])["workers"])["basis"]=="rule_derived", "adapted value level");
  var unknown=Call("unknown","Q4_K_M","prism-fork",H(),"balanced");
  Check((string)unknown["state"]=="pending" && Dict(unknown["fields"]).Count==0,"unknown model");
  var wrong=Call("qwen3.5-9b","Q8_0","prism-fork",H(),"balanced");
  Check((string)wrong["state"]=="pending","different quant");
  var wrongEngine=Call("qwen3.5-9b","UD-Q4_K_XL","strata",H(),"balanced");
  Check((string)wrongEngine["state"]=="pending","different engine");
  var missing=H();missing.RamPeakEstimateMiB=null;
  var missingResult=Call("qwen3.5-9b","UD-Q4_K_XL","prism-fork",missing,"balanced");
  Check((string)missingResult["state"]=="reference_only" && missingResult["profile"]==null,"unknown budget stays reference");
  var low=H();low.AvailableRamMiB=9000;
  var lowResult=Call("qwen3.5-9b","UD-Q4_K_XL","prism-fork",low,"balanced");
  Check((string)lowResult["state"]=="reference_only" && lowResult["profile"]==null,"insufficient RAM");
  var tight=H(); tight.AvailableVramMiB=9000; tight.VramPeakEstimateMiB=7000;
  var tightResult=Call("qwen3.8-flash-next-iq3_xxs","IQ3_XXS","strata",tight,"balanced");
  Check((string)tightResult["state"]=="reference_only" && tightResult["profile"]==null,"original model reserve must be included");
  var invalid=H(); invalid.AvailableRamMiB=70000;
  Check((string)Call("qwen3.5-9b","UD-Q4_K_XL","prism-fork",invalid,"balanced")["state"]=="reference_only","available cannot exceed total");
  var missingGpu=H(); missingGpu.VramPeakEstimateMiB=null;
  Check((string)Call("qwen3.5-9b","UD-Q4_K_XL","prism-fork",missingGpu,"balanced")["state"]=="reference_only","missing GPU estimate");
  var bad=Call("qwen3.5-9b","UD-Q4_K_XL","prism-fork",H(),"other");
  Check((string)bad["state"]=="pending","unknown usage");
  var quiet=Call("qwen3.5-9b","UD-Q4_K_XL","prism-fork",H(),"quiet");
  Check(Num(Dict(quiet["profile"])["reasoning_budget"])==512,"quiet bounded budget");
  Check(Num(Dict(quiet["profile"])["threads"])<=5,"quiet threads");
  var p=Dict(Dict(x["fields"])["workers"]);
  Check(p.ContainsKey("why_zh")&&p.ContainsKey("status_zh")&&p.ContainsKey("sources"),"tooltip keys");
  Check((string)Dict(x["resource_rules"])["basis"]=="rule_derived","rules labelled");
  Check(Num(Dict(x["resource_rules"])["ram_reserve_mib"])==9831,"ram reserve rounded");
  Check(Num(Dict(x["resource_rules"])["gpu_reserve_mib"])==2560,"vram reserve uses profile minimum");
  Check((string)x["identity_state"]=="unverified", "no fake exact hash");
  var row=new Dictionary<string,object>{{"key","workers"},{"value",17}};
  var decorated=TuningAdvice.DecorateRows(new object[]{row},x);
  Check(!row.ContainsKey("recommendation"),"do not mutate existing row");
  Check(Num(Dict(Dict(decorated[0])["recommendation"])["value"])==6,"recommendation field shape");
  var unknownDecorated=TuningAdvice.DecorateRows(new object[]{row},unknown);
  Check(Dict(Dict(unknownDecorated[0])["recommendation"])["value"]==null,"unknown model field pending");
  Check(!(bool)x["complete_profile"],"eight fields are not a full engine configuration");
  var noGpu=H(); noGpu.GpuRequired=false;
  Check((string)Call("qwen3.8-flash-next-iq3_xxs","IQ3_XXS","strata",noGpu,"balanced")["state"]=="reference_only","Strata CPU not qualified");
  var neg=H();neg.VramPeakEstimateMiB=-1;
  Check((string)Call("qwen3.5-9b","UD-Q4_K_XL","prism-fork",neg,"balanced")["state"]=="reference_only","reject negative estimate");
  var broken=TuningAdvice.Evaluate("seed-public.json","qwen3.5-9b","UD-Q4_K_XL","prism-fork",H(),"balanced",delegate(string raw){
   var root=Dict(Parse(raw));var models=Dict(root["models"]);Dict(models["qwen3.5-9b"]).Remove("temperature");return root;
  });
  Check((string)broken["state"]=="pending" && Dict(broken["fields"]).Count==0,"missing seed field refuses whole candidate");
  var evil=TuningAdvice.Evaluate("seed-public.json","qwen3.5-9b","UD-Q4_K_XL","prism-fork",H(),"balanced",delegate(string raw){
   var root=Dict(Parse(raw));var models=Dict(root["models"]);var field=Dict(Dict(models["qwen3.5-9b"])["temperature"]);field["sources"]=new object[]{"https://evil.invalid/"};return root;
  });
  Check((string)evil["state"]=="pending","untrusted source rejected");
  Console.WriteLine("C# tuning checks passed: "+n);
 }
}

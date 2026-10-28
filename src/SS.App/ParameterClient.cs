using System;using System.Collections;using System.Collections.Generic;using System.Globalization;using System.IO;using System.Linq;using System.Security.Cryptography;using System.Text;using System.Threading;using System.Web.Script.Serialization;using SS.Core;
namespace ModelConsole {
 public static class ParameterClient {
  static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4194304};
  static readonly string[] Keys={"temperature","top_p","top_k","min_p","repeat_penalty","threads","cacheRam","reasoning_budget"};
  static readonly string[] Zh={"随机程度","概率范围","候选数量","最小概率","重复惩罚","CPU线程","缓存内存 MiB","思考预算 tokens"};
  static readonly string[] En={"Temperature","Top P","Top K","Min P","Repeat penalty","CPU threads","Cache RAM MiB","Thinking tokens"};
  static readonly double[] Min={0,.001,0,0,.8,1,0,0},Max={2,1,200,1,2,1024,32768,16384},Defaults={.7,.9,20,0,1,4,0,1024};
  static string Text(object o){return Convert.ToString(o,CultureInfo.InvariantCulture)??"";}
  static object Get(Dictionary<string,object> d,string k){object v;return d!=null&&d.TryGetValue(k,out v)?v:null;}
  static string Hash(){using(var s=SHA256.Create())return BitConverter.ToString(s.ComputeHash(File.ReadAllBytes(AppPaths.ModelsJson))).Replace("-","").ToLowerInvariant();}
  static double Number(object raw,int i){double n;if(raw is bool||!double.TryParse(Text(raw),NumberStyles.Float,CultureInfo.InvariantCulture,out n)||double.IsNaN(n)||double.IsInfinity(n)||n<Min[i]||n>Max[i]||(i>=5||i==2)&&Math.Floor(n)!=n)throw new ArgumentException(Keys[i]+": out_of_range");if(i==5&&n>Environment.ProcessorCount)throw new ArgumentException("threads exceeds hardware");return n;}
  static Dictionary<string,object> Pending(){return new Dictionary<string,object>{{"value",null},{"basis","unreviewed"},{"why_zh","该模型尚未完成精确身份与硬件组合校验，不能套用其他模型建议。"},{"why_en","Exact model and hardware have not been validated."},{"status_zh","待评估"},{"status_en","Pending"},{"sources",new string[0]}};}
  public static Dictionary<string,object> Call(Dictionary<string,object> payload){
   using(var mutex=new Mutex(false,"Local\\SSLocalModelParameters")){
    if(!mutex.WaitOne(10000))throw new InvalidOperationException("parameter_busy");
    try{return Execute(payload);}finally{mutex.ReleaseMutex();}
   }
  }
  static Dictionary<string,object> Execute(Dictionary<string,object> payload){
   string alias=Text(Get(payload,"alias"));if(alias==""||NativeHost.RunningAlias()!=alias)throw new InvalidOperationException("model_changed");
   var root=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(AppPaths.ModelsJson));
   var models=Get(root,"models") as Dictionary<string,object>;if(models==null)throw new InvalidDataException("invalid_registry");
   var found=models.FirstOrDefault(x=>x.Value is Dictionary<string,object>&&Text(Get((Dictionary<string,object>)x.Value,"alias"))==alias);
   if(found.Key==null)throw new InvalidOperationException("unregistered_running_model");
   var model=(Dictionary<string,object>)found.Value;var sampling=Get(model,"parameters") as Dictionary<string,object>;
   if(sampling==null)sampling=new Dictionary<string,object>();
   string action=Text(Get(payload,"action"));
   if(action=="save"){
    if(Text(Get(payload,"fingerprint"))!=Hash())throw new InvalidOperationException("configuration_changed");
    var changes=Get(payload,"changes") as Dictionary<string,object>;if(changes==null)throw new ArgumentException("invalid_changes");
    foreach(var item in changes){int i=Array.IndexOf(Keys,item.Key);if(i<0||i>=6)throw new ArgumentException("unsupported_key");double n=Number(item.Value,i);if(i<5)sampling[item.Key]=n;else model[item.Key]=n;}
    model["parameters"]=sampling;
    if(NativeHost.RunningAlias()!=alias)throw new InvalidOperationException("model_changed");
    AppPaths.AtomicWrite(AppPaths.ModelsJson,Json.Serialize(root));
    bool restart=Get(payload,"restart") is bool&&(bool)Get(payload,"restart");bool restarted=false;string error=null;
    if(restart)try{NativeHost.Restart(found.Key,alias);restarted=true;}catch(Exception ex){error=ex.Message;}
    var result=new Dictionary<string,object>{{"ok",true},{"saved",true},{"restarted",restarted}};if(error!=null)result["error"]=error;return result;
   }
   if(action!="load")throw new ArgumentException("unsupported_action");
   var fields=new List<object>();
   for(int i=0;i<6;i++){object raw=i<5?Get(sampling,Keys[i]):Get(model,Keys[i]);double value=raw==null?Defaults[i]:Number(raw,i);
    fields.Add(new Dictionary<string,object>{{"key",Keys[i]},{"zh",Zh[i]},{"en",En[i]},{"min",Min[i]},{"max",i==5?Math.Min(Max[i],Environment.ProcessorCount):Max[i]},{"integer",i>=5||i==2},{"value",value},{"recommendation",Pending()}});
   }
   if(NativeHost.RunningAlias()!=alias)throw new InvalidOperationException("model_changed");
   return new Dictionary<string,object>{{"ok",true},{"id",found.Key},{"alias",alias},{"source","SS managed profile"},{"runtime",Text(Get(model,"runtime"))},{"fingerprint",Hash()},{"fields",fields}};
  }
 }
}
using System;using System.IO;using System.Diagnostics;using System.Collections.Generic;using System.Web.Script.Serialization;using SS.Core;
class RecoveryChecks {
 static int n;static void Check(bool x,string name){if(!x)throw new Exception(name);n++;}
 static int Main(){try{
  string root=Path.Combine(Path.GetTempPath(),"SS-recovery-"+Guid.NewGuid().ToString("N"));Environment.SetEnvironmentVariable("SS_MODEL_DATA_ROOT",root);AppPaths.Initialize();
  string lease=Path.Combine(root,"transactions","native-host.json");var json=new JavaScriptSerializer();
  using(var self=Process.GetCurrentProcess()){
   var d=new Dictionary<string,object>{{"sessionId",new string('a',32)},{"modelId","ss-test"},{"alias","ss-test"},{"port",AppPaths.DirectPort},{"hostPid",Int32.MaxValue},{"enginePid",Int32.MaxValue-1},{"hostStartUtc",DateTime.UtcNow.AddDays(-1).ToString("o")},{"engineStartUtc",DateTime.UtcNow.AddDays(-1).ToString("o")},{"enginePath",Path.Combine(AppPaths.EngineRoot,"runtime","llama-server.exe")}};
   Action save=()=>File.WriteAllText(lease,json.Serialize(d));
   d["hostPid"]=self.Id;d["hostStartUtc"]=self.StartTime.ToUniversalTime().ToString("o");save();
   Check(!NativeHost.RecoverStoppedService()&&File.Exists(lease),"live original host prevents discard");
   d["hostPid"]=Int32.MaxValue;d["enginePid"]=self.Id;d["engineStartUtc"]=self.StartTime.ToUniversalTime().ToString("o");save();
   Check(!NativeHost.RecoverStoppedService()&&File.Exists(lease),"live original engine prevents discard");
   d["enginePid"]=Int32.MaxValue-1;save();Check(NativeHost.RecoverStoppedService()&&!File.Exists(lease),"both original processes gone permits recovery");
   d["hostPid"]=self.Id;d["hostStartUtc"]=self.StartTime.ToUniversalTime().AddDays(-1).ToString("o");save();
   Check(NativeHost.RecoverStoppedService()&&!File.Exists(lease),"PID reuse does not preserve dead original");
   Check(!self.HasExited,"recovery never kills reused PID");
   d["enginePath"]=Path.Combine(root,"foreign.exe");save();Check(!NativeHost.RecoverStoppedService()&&File.Exists(lease),"foreign engine identity refused");
   d["enginePath"]=Path.Combine(AppPaths.EngineRoot,"runtime","llama-server.exe");d["hostStartUtc"]="invalid";save();
   Check(!NativeHost.RecoverStoppedService()&&File.Exists(lease),"unverifiable process start refused");
   d["hostStartUtc"]=DateTime.UtcNow.AddDays(-1).ToString("o");d["sessionId"]="invalid";save();
   Check(!NativeHost.RecoverStoppedService()&&File.Exists(lease),"malformed session refused");
   File.WriteAllText(lease,"not json");Check(!NativeHost.RecoverStoppedService()&&File.Exists(lease),"unreadable lease preserved");
  }
  Console.WriteLine("PASS recovery "+n);return 0;
 }catch(Exception ex){Console.WriteLine(ex);return 1;}}
}
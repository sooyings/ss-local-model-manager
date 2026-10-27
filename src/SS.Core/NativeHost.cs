using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;


namespace SS.Core {
 public static class NativeHost {
  static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4194304};
  static readonly string[] ExternalNames={"llama-server","llama","strata","strata-vision"};
  static string LeasePath {get{return Path.Combine(AppPaths.DataRoot,"transactions","native-host.json");}}
  static string ActivePath {get{return Path.Combine(AppPaths.DataRoot,"config","active_model.json");}}
  static string RuntimePath {get{return Path.Combine(AppPaths.EngineRoot,"runtime");}}
  static string HostExe {get{return Path.GetFullPath(Process.GetCurrentProcess().MainModule.FileName);}}
  static Dictionary<string,object> Obj(object x){return x as Dictionary<string,object>;}
  static string Str(Dictionary<string,object> d,string k){object x;return d!=null&&d.TryGetValue(k,out x)&&x!=null?Convert.ToString(x,CultureInfo.InvariantCulture):"";}
  static int Int(Dictionary<string,object> d,string k){int n;return Int32.TryParse(Str(d,k),NumberStyles.Integer,CultureInfo.InvariantCulture,out n)?n:0;}
  static Dictionary<string,object> Read(string p){return Obj(Json.DeserializeObject(File.ReadAllText(p,Encoding.UTF8)));}
  static void Fail(string s){throw new InvalidOperationException(s);}
  public static bool ValidId(string value){return value!=null&&Regex.IsMatch(value,@"^[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}$");}
  static string Quote(string s){if(s==null||s.IndexOf('"')>=0||s.IndexOf('\r')>=0||s.IndexOf('\n')>=0)Fail("invalid_argument");return "\""+s+"\"";}
  public static string BuildArguments(string model,string alias,int context,int threads,int port){
   if(!Path.IsPathRooted(model)||!ValidId(alias)||context<256||context>262144||threads<1||threads>256)Fail("invalid_model_settings");
   try{AppPaths.ValidatePort(port);}catch{Fail("invalid_port");}
   return "-m "+Quote(model)+" --host 127.0.0.1 --port "+port+" --alias "+alias+" -c "+context+" -t "+threads+" -ngl 0 -np 1 --slots --metrics";
  }
  public static string BuildArguments(string model,string alias,int context,int threads,int port,Dictionary<string,object> settings){
   string args=BuildArguments(model,alias,context,threads,port);
   if(settings==null)return args;
   foreach(var kv in settings){string flag;double min,max;
    switch(kv.Key){case "temperature":flag="--temp";min=0;max=2;break;case "top_p":flag="--top-p";min=0.001;max=1;break;case "top_k":flag="--top-k";min=0;max=200;break;case "min_p":flag="--min-p";min=0;max=1;break;case "repeat_penalty":flag="--repeat-penalty";min=0.8;max=2;break;default:Fail("parameter_not_supported");return null;}
    double n;if(!Double.TryParse(Convert.ToString(kv.Value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out n)||Double.IsNaN(n)||Double.IsInfinity(n)||n<min||n>max||(kv.Key=="top_k"&&Math.Floor(n)!=n))Fail("invalid_parameter");
    args+=" "+flag+" "+n.ToString("R",CultureInfo.InvariantCulture);
   }
   return args;
  }
  public static bool StopAdmissionSupported {get{return true;}}
  static long ModelBytes(string p){
   if(!File.Exists(p))Fail("model_shard_missing");var info=new FileInfo(p);if(info.Length<24)Fail("invalid_gguf");
   using(var stream=new FileStream(p,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var reader=new BinaryReader(stream)){
    if(reader.ReadUInt32()!=0x46554747)Fail("invalid_gguf");uint version=reader.ReadUInt32();if(version<2||version>3)Fail("unsupported_gguf_version");
   }return info.Length;
  }
  public static long ValidateModelFiles(string main){
   try{if(!Path.IsPathRooted(main)||!main.EndsWith(".gguf",StringComparison.OrdinalIgnoreCase))Fail("invalid_model_path");
    string name=Path.GetFileName(main);var match=Regex.Match(name,@"^(.*)-(\d{5})-of-(\d{5})\.gguf$",RegexOptions.IgnoreCase);
    if(!match.Success){if(name.IndexOf("-of-",StringComparison.OrdinalIgnoreCase)>=0)Fail("invalid_model_shard_name");return ModelBytes(main);}
    int first=Int32.Parse(match.Groups[2].Value,CultureInfo.InvariantCulture),total=Int32.Parse(match.Groups[3].Value,CultureInfo.InvariantCulture);
    if(first!=1||total<2||total>999)Fail("invalid_model_shards");long sum=0;
    for(int i=1;i<=total;i++){string f=match.Groups[1].Value+"-"+i.ToString("00000")+"-of-"+total.ToString("00000")+".gguf";sum=checked(sum+ModelBytes(Path.Combine(Path.GetDirectoryName(main),f)));}return sum;
   }catch(InvalidOperationException){throw;}catch{Fail("model_validation_failed");return 0;}
  }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Auto)]sealed class MemoryInfo {public uint length,load;public ulong totalPhysical,availablePhysical,totalPageFile,availablePageFile,totalVirtual,availableVirtual,availableExtendedVirtual;public MemoryInfo(){length=(uint)Marshal.SizeOf(typeof(MemoryInfo));}}
  [DllImport("kernel32.dll",CharSet=CharSet.Auto,SetLastError=true)]static extern bool GlobalMemoryStatusEx([In,Out]MemoryInfo info);
  static void CheckMemory(long modelBytes){var memory=new MemoryInfo();if(!GlobalMemoryStatusEx(memory))Fail("memory_unavailable");ulong needed=checked((ulong)modelBytes+Math.Max(4294967296UL,(ulong)modelBytes/5));if(memory.availablePhysical<needed)Fail("insufficient_available_ram");}
  static bool Reparse(string p){return (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0;}
  public static string VerifyEngine(string dir){
   try{
    dir=Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar);
    string mf=Path.Combine(dir,"engine-manifest.json");
    if(!Directory.Exists(dir)||Reparse(dir)||!File.Exists(mf)||Reparse(mf))Fail("engine_manifest_missing");
    var data=Read(mf);if(Int(data,"schemaVersion")!=1)Fail("engine_manifest_schema");
    var files=data["files"] as IList;if(files==null||files.Count<1||files.Count>1024)Fail("engine_manifest_files");
    var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach(object obj in files){var item=Obj(obj);string name=Str(item,"path"),expected=Str(item,"sha256");
     if(String.IsNullOrEmpty(name)||Path.GetFileName(name)!=name||name=="."||name==".."||name.IndexOf(':')>=0||!Regex.IsMatch(expected,@"\A[0-9a-fA-F]{64}\z")||!names.Add(name))Fail("engine_manifest_entry");
     string file=Path.Combine(dir,name);if(!File.Exists(file)||Reparse(file))Fail("engine_file_missing");
     string actual;using(var stream=File.OpenRead(file))using(var sha=SHA256.Create())actual=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","");
     if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase))Fail("engine_hash_mismatch");
    }
    if(!names.Contains("llama-server.exe"))Fail("engine_not_listed");
    foreach(string f in Directory.GetFiles(dir)){string name=Path.GetFileName(f);if(name.Equals("engine-manifest.json",StringComparison.OrdinalIgnoreCase))continue;if(!names.Contains(name))Fail("engine_unlisted_file");}
    if(Directory.GetDirectories(dir).Length!=0)Fail("engine_unlisted_directory");
    return Path.Combine(dir,"llama-server.exe");
   }catch(InvalidOperationException){throw;}catch(Exception){Fail("engine_verification_failed");return null;}
  }
  static Dictionary<string,object> Model(string id){
   if(!ValidId(id))Fail("invalid_model_id");var root=Read(AppPaths.ModelsJson);var models=Obj(root["models"]);object value=null;
   if(models==null||!models.TryGetValue(id,out value)||Obj(value)==null)Fail("model_missing");
   var m=Obj(value);string runtime=Str(m,"runtime");if(runtime!="llama-cpp"&&runtime!="prism-fork")Fail("runtime_not_supported");
   string main=Str(m,"main"),alias=Str(m,"alias");if(!Path.IsPathRooted(main)||!File.Exists(main)||!ValidId(alias))Fail("model_incomplete");ValidateModelFiles(main);
   object raw;Dictionary<string,object> parameters=null;if(m.TryGetValue("parameters",out raw)){parameters=Obj(raw);if(parameters==null)Fail("invalid_parameters");}
   BuildArguments(main,alias,Int(m,"context"),Int(m,"threads"),AppPaths.DirectPort,parameters);
   return m;
  }
  public static bool SlotsIdle(string text){
   try{var slots=Json.DeserializeObject(text) as IList;if(slots==null||slots.Count==0)return false;
    foreach(object item in slots){var d=Obj(item);object value;if(d==null||!d.TryGetValue("is_processing",out value)||!(value is bool)||(bool)value)return false;}return true;
   }catch{return false;}
  }
  static bool AnyExternalEngine(){bool found=false;foreach(string name in ExternalNames){var processes=Process.GetProcessesByName(name);if(processes.Length>0)found=true;foreach(var p in processes)p.Dispose();}return found;}
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]struct ProcessEntry {public uint size,usage,pid;public IntPtr heap;public uint module,threads,parent;public int priority;public uint flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string exe;}
  [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool Process32FirstW(IntPtr snapshot,ref ProcessEntry entry);
  [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool Process32NextW(IntPtr snapshot,ref ProcessEntry entry);
  [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
  static int ParentOf(int pid){IntPtr snapshot=CreateToolhelp32Snapshot(2,0);if(snapshot==new IntPtr(-1))return 0;try{var e=new ProcessEntry{size=(uint)Marshal.SizeOf(typeof(ProcessEntry))};if(Process32FirstW(snapshot,ref e))do{if(e.pid==(uint)pid)return (int)e.parent;}while(Process32NextW(snapshot,ref e));return 0;}finally{CloseHandle(snapshot);}}
  static DateTime Utc(Dictionary<string,object> d,string k){DateTime t;return DateTime.TryParse(Str(d,k),CultureInfo.InvariantCulture,DateTimeStyles.AdjustToUniversal|DateTimeStyles.AssumeUniversal,out t)?t:DateTime.MinValue;}
  static bool Owned(Dictionary<string,object> l){
   if(l==null||!Regex.IsMatch(Str(l,"sessionId"),@"\A[0-9a-f]{32}\z")||!ValidId(Str(l,"modelId"))||!ValidId(Str(l,"alias")))return false;
   int host=Int(l,"hostPid"),engine=Int(l,"enginePid"),port=Int(l,"port");if(port!=AppPaths.DirectPort||host<=0||engine<=0)return false;
   return ServiceOwnership.Matches(host,Utc(l,"hostStartUtc"),HostExe)&&ServiceOwnership.Matches(engine,Utc(l,"engineStartUtc"),Path.Combine(RuntimePath,"llama-server.exe"))&&ParentOf(engine)==host;
  }
  static Dictionary<string,object> Lease(){try{return Read(LeasePath);}catch{return null;}}
  public static string RunningAlias(){var l=Lease();return Owned(l)?Str(l,"alias"):"";}
  public static string OwnedSessionId(){var l=Lease();return Owned(l)?Str(l,"sessionId"):"";}
  public static int OwnedEnginePid(){var l=Lease();return Owned(l)?Int(l,"enginePid"):0;}
  static string StatusPath(string session,string prefix){return Path.Combine(AppPaths.DataRoot,"transactions",prefix+session+".json");}
  static Mutex Gate(){return new Mutex(false,@"Local\SSLocalModelManager.ServiceControl");}
  static bool Lock(Mutex m){try{return m.WaitOne(0);}catch(AbandonedMutexException){return true;}}
  static bool WaitGate(Mutex m,int milliseconds){try{return m.WaitOne(milliseconds);}catch(AbandonedMutexException){return true;}}
  public static void Use(string id){
   using(var gate=Gate()){if(!Lock(gate))Fail("service_busy");try{Model(id);AppPaths.AtomicWrite(ActivePath,Json.Serialize(new Dictionary<string,object>{{"id",id}}));}finally{gate.ReleaseMutex();}}
  }
  static bool OriginalProcessGone(int pid,DateTime started){
   if(pid<=0||started==DateTime.MinValue)return false;
   try{using(var p=Process.GetProcessById(pid)){if(p.HasExited)return true;return Math.Abs((p.StartTime.ToUniversalTime()-started.ToUniversalTime()).TotalSeconds)>.5;}}
   catch(ArgumentException){return true;}catch{return false;}
  }
  static bool DiscardDeadLease(Dictionary<string,object> lease){
   if(lease==null||!Regex.IsMatch(Str(lease,"sessionId"),@"\A[0-9a-f]{32}\z")||!ValidId(Str(lease,"modelId"))||!ValidId(Str(lease,"alias"))||Int(lease,"port")!=AppPaths.DirectPort)return false;
   string expected=Path.Combine(RuntimePath,"llama-server.exe");string path=Str(lease,"enginePath");
   try{if(!Path.GetFullPath(path).Equals(expected,StringComparison.OrdinalIgnoreCase))return false;}catch{return false;}
   if(!OriginalProcessGone(Int(lease,"hostPid"),Utc(lease,"hostStartUtc"))||!OriginalProcessGone(Int(lease,"enginePid"),Utc(lease,"engineStartUtc")))return false;
   File.Delete(LeasePath);return true;
  }
  public static bool RecoverStoppedService(){
   using(var gate=Gate()){if(!Lock(gate))return false;try{return DiscardDeadLease(Lease());}finally{gate.ReleaseMutex();}}
  }
  public static void Start(string id){
   AppPaths.Initialize();using(var gate=Gate()){if(!Lock(gate))Fail("service_busy");try{
    var old=Lease();if(old!=null){if(Owned(old)&&Str(old,"modelId")==id)return;if(!DiscardDeadLease(old))Fail("existing_or_stale_service");}
    var m=Model(id);if(AnyExternalEngine())Fail("external_model_running");RuntimeTrust.Require(RuntimePath);VerifyEngine(RuntimePath);
    var probe=new System.Net.Sockets.TcpListener(IPAddress.Loopback,AppPaths.DirectPort);try{probe.Start();}catch{Fail("port_in_use");}finally{probe.Stop();}
    var p=Process.Start(new ProcessStartInfo(HostExe,"--host "+id){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden});
    if(p==null)Fail("host_start_failed");
   }finally{gate.ReleaseMutex();}}
   var until=DateTime.UtcNow.AddSeconds(15);while(DateTime.UtcNow<until){var l=Lease();if(Owned(l)&&Str(l,"modelId")==id)return;Thread.Sleep(100);}Fail("host_start_not_confirmed");
  }
  [DllImport("iphlpapi.dll",SetLastError=true)]static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool sort,int family,int kind,int reserved);
  [StructLayout(LayoutKind.Sequential)]struct TcpRow {public uint state,localAddr,localPort,remoteAddr,remotePort,pid;}
  static bool OwnsLoopbackPort(int pid,int port){
   int n=0;GetExtendedTcpTable(IntPtr.Zero,ref n,false,2,3,0);if(n<=4||n>16777216)return false;IntPtr p=Marshal.AllocHGlobal(n);
   try{if(GetExtendedTcpTable(p,ref n,false,2,3,0)!=0)return false;int count=Marshal.ReadInt32(p);int width=Marshal.SizeOf(typeof(TcpRow));if(count<0||count>(n-4)/width)return false;
    for(int i=0;i<count;i++){var row=(TcpRow)Marshal.PtrToStructure(IntPtr.Add(p,4+i*width),typeof(TcpRow));int rp=(int)(((row.localPort&255)<<8)|((row.localPort>>8)&255));if(row.pid==pid&&rp==port&&row.localAddr==0x0100007f)return true;}return false;
   }finally{Marshal.FreeHGlobal(p);}
  }
  static bool ServerIdle(int pid,int port,string key){
   if(!OwnsLoopbackPort(pid,port))return false;
   try{var req=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+port+"/slots");req.Proxy=null;req.Timeout=2500;req.ReadWriteTimeout=2500;req.Headers[HttpRequestHeader.Authorization]="Bearer "+key;
    using(var res=req.GetResponse())using(var r=new StreamReader(res.GetResponseStream()))return SlotsIdle(r.ReadToEnd())&&OwnsLoopbackPort(pid,port);
   }catch{return false;}
  }
  public static void Stop(){
   if(!StopAdmissionSupported)Fail("stop_admission_control_required");
   AppPaths.Initialize();using(var gate=Gate()){if(!Lock(gate))Fail("service_busy");try{
    var lease=Lease();if(!Owned(lease))Fail("service_not_owned");string session=Str(lease,"sessionId"),request=Guid.NewGuid().ToString("N");
    string path=StatusPath(session,"stop-request-");if(File.Exists(path))Fail("stop_already_pending");
    AppPaths.AtomicWrite(path,Json.Serialize(new Dictionary<string,object>{{"sessionId",session},{"requestId",request},{"deadlineUtc",DateTime.UtcNow.AddSeconds(6).ToString("o")}}));
    string response=StatusPath(session,"stop-result-");var until=DateTime.UtcNow.AddSeconds(15);
    while(DateTime.UtcNow<until){Dictionary<string,object> result=null;try{result=Read(response);}catch{}if(Str(result,"requestId")==request){if(Str(result,"status")=="stopped"){
      var cleaned=DateTime.UtcNow.AddSeconds(5);while(DateTime.UtcNow<cleaned){var remaining=Lease();if(!File.Exists(LeasePath))return;if(remaining!=null&&Str(remaining,"sessionId")!=session)Fail("service_session_changed");Thread.Sleep(25);}Fail("stop_cleanup_pending");
     }Fail(Str(result,"status"));}Thread.Sleep(100);}
    Fail("stop_not_confirmed");
   }finally{gate.ReleaseMutex();}}
  }
  public static void MainHost(string id){
   AppPaths.Initialize();string session=Guid.NewGuid().ToString("N"),requestPath=StatusPath(session,"stop-request-"),responsePath=StatusPath(session,"stop-result-");Process engine=null;GatewayProxy proxy=null;bool published=false;int backendPort=0;string apiKey="";var gateRequests=new AdmissionGate();
   try{using(var gate=Gate()){if(!WaitGate(gate,5000))Fail("service_busy");try{
    if(Lease()!=null||AnyExternalEngine())Fail("external_model_running");var m=Model(id);CheckMemory(ValidateModelFiles(Str(m,"main")));RuntimeTrust.Require(RuntimePath);string executable=VerifyEngine(RuntimePath);
    var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();backendPort=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
    if(backendPort==8081||backendPort==8090||backendPort==AppPaths.DirectPort)Fail("backend_port_reserved");
    byte[] secret=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(secret);apiKey=BitConverter.ToString(secret).Replace("-","").ToLowerInvariant();
    object raw;var parameters=m.TryGetValue("parameters",out raw)?Obj(raw):null;
    string arguments=BuildArguments(Str(m,"main"),Str(m,"alias"),Int(m,"context"),Int(m,"threads"),backendPort,parameters)+" --api-key "+apiKey;
    var info=new ProcessStartInfo(executable,arguments){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=RuntimePath,RedirectStandardOutput=true,RedirectStandardError=true};
    info.EnvironmentVariables["PATH"]=RuntimePath+";"+Environment.GetFolderPath(Environment.SpecialFolder.System);
    var log=AppPaths.Log("llama.log");long offset;using(var stream=new FileStream(log,FileMode.OpenOrCreate,FileAccess.Write,FileShare.ReadWrite))offset=stream.Length;
    double created=(File.GetCreationTimeUtc(log)-new DateTime(1970,1,1)).TotalSeconds;
    double lower=(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds;
    engine=Process.Start(info);if(engine==null)Fail("engine_start_failed");
    double upper=(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds;
    AppPaths.AtomicWrite(AppPaths.Log("llama-session.json"),Json.Serialize(new Dictionary<string,object>{{"pid",engine.Id.ToString(CultureInfo.InvariantCulture)},{"alias",Str(m,"alias")},{"log",log},{"offset",offset},{"log_created",created},{"spawned_after",lower},{"spawned_before",upper}}));
    AppPaths.AtomicWrite(AppPaths.Log("llama.pid"),engine.Id.ToString(CultureInfo.InvariantCulture));DataReceivedEventHandler append=(sender,e)=>{if(e.Data!=null)try{lock(Json)File.AppendAllText(log,e.Data.Replace(apiKey,"[redacted]")+Environment.NewLine,Encoding.UTF8);}catch{}};
    engine.OutputDataReceived+=append;engine.ErrorDataReceived+=append;engine.BeginOutputReadLine();engine.BeginErrorReadLine();
    int engineId=engine.Id;proxy=new GatewayProxy(AppPaths.DirectPort,backendPort,apiKey,gateRequests,()=>OwnsLoopbackPort(engineId,backendPort));proxy.Start();
    using(var self=Process.GetCurrentProcess())AppPaths.AtomicWrite(LeasePath,Json.Serialize(new Dictionary<string,object>{{"sessionId",session},{"modelId",id},{"alias",Str(m,"alias")},{"hostPid",self.Id},{"hostStartUtc",self.StartTime.ToUniversalTime().ToString("o")},{"enginePid",engine.Id},{"engineStartUtc",engine.StartTime.ToUniversalTime().ToString("o")},{"enginePath",executable},{"port",AppPaths.DirectPort},{"backendPort",backendPort}}));published=true;
   }finally{gate.ReleaseMutex();}}
   while(!engine.HasExited){Thread.Sleep(250);if(!File.Exists(requestPath))continue;string rid="",status="stop_request_invalid";
    try{var req=Read(requestPath);rid=Str(req,"requestId");if(Str(req,"sessionId")==session&&Regex.IsMatch(rid,@"\A[0-9a-f]{32}\z")){
      if(Utc(req,"deadlineUtc")<DateTime.UtcNow)status="stop_request_expired";
      else if(!gateRequests.TryClose())status="model_busy";
      else if(!Owned(Lease())||!ServerIdle(engine.Id,backendPort,apiKey)){gateRequests.Open();status="model_busy_or_status_unknown";}
      else{engine.Kill();if(engine.WaitForExit(5000))status="stopped";else{gateRequests.Open();status="stop_timeout";}}
    }}catch{}
    try{File.Delete(requestPath);AppPaths.AtomicWrite(responsePath,Json.Serialize(new Dictionary<string,object>{{"requestId",rid},{"status",status}}));}catch{}
    if(status=="stopped")break;
   }
   }catch(Exception ex){try{File.AppendAllText(AppPaths.Log("native-host-error.log"),DateTime.UtcNow.ToString("o")+" "+ex.Message+Environment.NewLine);}catch{}}
   finally{if(proxy!=null)proxy.Dispose();if(engine!=null){try{if(!published&&!engine.HasExited){engine.Kill();engine.WaitForExit(5000);}if(engine.HasExited){var lease=Lease();if(lease!=null&&Str(lease,"sessionId")==session)File.Delete(LeasePath);}}catch{}engine.Dispose();}}
  }
 }
}

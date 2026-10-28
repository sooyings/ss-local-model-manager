using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Text;
using System.Web.Script.Serialization;
namespace ModelConsole {
 public class PowerState {
  public string Uuid="",Name="";public double? Draw,Limit,Enforced,Default,Min,Max,Core,Hotspot,Memory;
  public DateTime Sampled=DateTime.UtcNow;
  public PowerState Copy(){return (PowerState)MemberwiseClone();}
 }
 public static class PowerPolicy {
  public static readonly int[] Presets={60,70,80,90,100};
  public static bool Number(double? n){return n.HasValue&&!double.IsNaN(n.Value)&&!double.IsInfinity(n.Value)&&n>=0;}
  public static bool Supported(PowerState s){
   return s!=null&&Regex.IsMatch(s.Uuid,@"^GPU-[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$")
    &&Number(s.Limit)&&Number(s.Enforced)&&Number(s.Default)&&Number(s.Min)&&Number(s.Max)
    &&s.Min>0&&s.Min<=s.Default&&s.Default<=s.Max&&s.Limit>=s.Min&&s.Limit<=s.Max&&s.Enforced>0;
  }
  public static int Target(PowerState s,int percent){
   if(!Supported(s))throw new InvalidOperationException("power_unsupported");
   if(!Presets.Contains(percent))throw new InvalidOperationException("power_preset_rejected");
   int watts=checked((int)Math.Floor(s.Default.Value*percent/100.0));
   if(watts<s.Min||watts>s.Default||watts>s.Max)throw new InvalidOperationException("power_out_of_range");
   return watts;
  }
  public static bool Cool(PowerState s){return Number(s.Core)&&Number(s.Hotspot)&&Number(s.Memory)&&s.Core<80&&s.Hotspot<95&&s.Memory<95;}
  public static int Validate(PowerState expected,PowerState fresh,int percent){return ValidateWatts(expected,fresh,Target(fresh,percent));}
  public static int ValidateWatts(PowerState expected,PowerState fresh,int watts){
   if(!Supported(fresh)||!Presets.Any(p=>{try{return Target(fresh,p)==watts;}catch{return false;}}))throw new InvalidOperationException("power_preset_rejected");
   if(!Supported(expected)||expected.Uuid!=fresh.Uuid)throw new InvalidOperationException("power_gpu_changed");
   if(Math.Abs(expected.Default.Value-fresh.Default.Value)>.5||Math.Abs(expected.Min.Value-fresh.Min.Value)>.5||Math.Abs(expected.Max.Value-fresh.Max.Value)>.5||Math.Abs(expected.Limit.Value-fresh.Limit.Value)>.5)
    throw new InvalidOperationException("power_settings_changed");
   if(watts>fresh.Limit+.5&&!Cool(fresh))throw new InvalidOperationException("power_temperature_guard");
   return watts;
  }
  public static int ReducedTarget(PowerState fresh,int ceiling){
   if(!Supported(fresh)||ceiling<fresh.Min)throw new InvalidOperationException("power_out_of_range");
   int watts=checked((int)Math.Floor(Math.Min(ceiling,Math.Min(fresh.Limit.Value,fresh.Default.Value))));
   if(watts<fresh.Min)throw new InvalidOperationException("power_out_of_range");return watts;
  }
  public static bool Matches(PowerState s,string uuid,int watts){return Supported(s)&&s.Uuid==uuid&&Math.Abs(s.Limit.Value-watts)<=.5&&s.Enforced<=watts+.5;}
 }
 public interface IPowerDriver { PowerState Read();void Set(PowerState expected,int watts);void Reduce(string uuid,int ceiling); }
 public class PowerResult { public bool Success,NoChange,FallbackConfirmed,RestoredPrevious;public string Code="";public PowerState Actual; }
 public class PowerController {
  readonly IPowerDriver driver;public PowerController(IPowerDriver driver){this.driver=driver;}
  public PowerResult Apply(PowerState expected,int percent){
   using(var gate=new Mutex(false,@"Local\ModelConsoleGpuPower")){
    bool locked=false;try{try{locked=gate.WaitOne(0);}catch(AbandonedMutexException){locked=true;}
     if(!locked)return new PowerResult{Code="power_busy"};
     var before=driver.Read();int watts=PowerPolicy.Validate(expected,before,percent);
     if(PowerPolicy.Matches(before,before.Uuid,watts))return new PowerResult{Success=true,NoChange=true,Actual=before,Code="power_unchanged"};
     try{
      driver.Set(before,watts);var after=driver.Read();
      for(int attempt=0;attempt<8&&!PowerPolicy.Matches(after,before.Uuid,watts);attempt++){Thread.Sleep(150);after=driver.Read();}
      if(PowerPolicy.Matches(after,before.Uuid,watts)&&(watts<=before.Limit+.5||PowerPolicy.Cool(after)))
       return new PowerResult{Success=true,Actual=after,Code="power_applied"};
      return Fallback(before,watts,"power_verify_failed");
     }catch(Win32Exception e){if(e.NativeErrorCode==1223)return new PowerResult{Code="power_cancelled",Actual=ReadSafe()};return Fallback(before,watts,"power_driver_failed");}
     catch(Exception e){if(e.Message=="power_temperature_guard"||e.Message=="power_settings_changed"||e.Message=="power_gpu_changed")return new PowerResult{Code=e.Message,Actual=ReadSafe()};return Fallback(before,watts,e.Message.StartsWith("power_helper_exit_")?e.Message:"power_driver_failed");}
    }catch(Exception e){return new PowerResult{Code=e.Message,Actual=ReadSafe()};}
    finally{if(locked)gate.ReleaseMutex();}
   }
  }
  PowerState ReadSafe(){try{return driver.Read();}catch{return null;}}
  PowerResult Fallback(PowerState before,int target,string reason){
   var result=new PowerResult{Code=reason,Actual=ReadSafe()};
   // Automatic recovery may only reduce power, never raise an uncertain state.
   if(!PowerPolicy.Supported(result.Actual)||result.Actual.Uuid!=before.Uuid)return result;
   int safe=(int)Math.Floor(Math.Min(Math.Min(before.Limit.Value,target),Math.Min(before.Default.Value,result.Actual.Limit.Value)));
   if(safe<before.Min)return result;
   try{if(!PowerPolicy.Matches(result.Actual,before.Uuid,safe))driver.Reduce(before.Uuid,safe);
    result.Actual=driver.Read();result.FallbackConfirmed=PowerPolicy.Supported(result.Actual)&&result.Actual.Uuid==before.Uuid&&result.Actual.Limit<=safe+.5&&result.Actual.Enforced<=result.Actual.Limit+.5;
    result.RestoredPrevious=result.FallbackConfirmed&&Math.Abs(result.Actual.Limit.Value-before.Limit.Value)<=.5;
   }catch{result.Actual=ReadSafe();}
   return result;
  }
 }
 public class NvidiaPowerDriver:IPowerDriver {
  public static readonly string Exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"nvidia-smi.exe");
  readonly NvidiaThermals thermals=new NvidiaThermals();readonly object readGate=new object();
  static double? N(string s){double n;return double.TryParse(s.Trim(),NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&PowerPolicy.Number(n)?(double?)n:null;}
  public PowerState Read(){lock(readGate)return ReadCore();}
  PowerState ReadCore(){
   if(!File.Exists(Exe))throw new InvalidOperationException("power_tool_missing");
   var start=new ProcessStartInfo(Exe,"--query-gpu=uuid,name,power.draw,power.limit,enforced.power.limit,power.default_limit,power.min_limit,power.max_limit,temperature.gpu --format=csv,noheader,nounits"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   using(var p=Process.Start(start)){var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();
    if(!p.WaitForExit(5000)){try{p.Kill();}catch{}throw new InvalidOperationException("power_query_timeout");}
    if(p.ExitCode!=0)throw new InvalidOperationException("power_query_failed");
    string[] rows=output.Result.Trim().Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries);
    if(rows.Length!=1)throw new InvalidOperationException("power_gpu_selection_required");
    string[] v=rows[0].Split(',');if(v.Length!=9)throw new InvalidOperationException("power_query_invalid");
    thermals.Poll();var s=new PowerState{Uuid=v[0].Trim(),Name=v[1].Trim(),Draw=N(v[2]),Limit=N(v[3]),Enforced=N(v[4]),Default=N(v[5]),Min=N(v[6]),Max=N(v[7]),Core=N(v[8]),Hotspot=thermals.Hotspot,Memory=thermals.Memory};
    if(!PowerPolicy.Supported(s))throw new InvalidOperationException("power_unsupported");return s;
   }
  }
  public void Set(PowerState expected,int watts){
   PowerPolicy.ValidateWatts(expected,Read(),watts);
   RunElevated("apply",new PowerRequest{Expected=expected,Uuid=expected.Uuid,Watts=watts});
  }
  public void Reduce(string uuid,int ceiling){RunElevated("reduce",new PowerRequest{Uuid=uuid,Watts=ceiling});}
  static void RunElevated(string mode,PowerRequest request){
   string helper=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ModelConsolePower.exe");
   if(!File.Exists(helper)||FileVersionInfo.GetVersionInfo(helper).FileVersion!=System.Reflection.Assembly.GetExecutingAssembly().GetName().Version.ToString())
    throw new InvalidOperationException("power_helper_version_mismatch");
   string payload=Convert.ToBase64String(Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(request)));
   var start=new ProcessStartInfo(helper,mode+" "+payload){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
   using(var p=Process.Start(start)){if(p==null)throw new InvalidOperationException("power_driver_failed");p.WaitForExit();
    switch(p.ExitCode){case 0:return;case 21:throw new InvalidOperationException("power_temperature_guard");case 22:throw new InvalidOperationException("power_settings_changed");case 23:throw new InvalidOperationException("power_gpu_changed");default:throw new InvalidOperationException("power_helper_exit_"+p.ExitCode);}
   }
  }
  internal static void DirectSet(string uuid,int watts){
   if(!Regex.IsMatch(uuid,@"^GPU-[a-fA-F0-9-]{36}$")||watts<=0)throw new InvalidOperationException("power_command_rejected");
   var start=new ProcessStartInfo(Exe,"-i "+uuid+" --power-limit="+watts.ToString(CultureInfo.InvariantCulture)){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
   using(var p=Process.Start(start)){var output=p.StandardOutput.ReadToEndAsync();var error=p.StandardError.ReadToEndAsync();p.WaitForExit();if(p.ExitCode!=0)throw new InvalidOperationException("power_driver_failed");}
  }
 }
 public class PowerRequest {public PowerState Expected;public string Uuid="";public int Watts;}
 public static class PowerPrivilege {
  public static int Run(string[] args){
   try{
    if(args.Length!=2||(args[0]!="apply"&&args[0]!="reduce")||args[1].Length>12000)return 64;
    if(!(new WindowsPrincipal(WindowsIdentity.GetCurrent())).IsInRole(WindowsBuiltInRole.Administrator))return 65;
    var data=new JavaScriptSerializer{MaxJsonLength=10000}.Deserialize<PowerRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(args[1])));
    if(data==null||!Regex.IsMatch(data.Uuid,@"^GPU-[a-fA-F0-9-]{36}$"))return 64;
    using(var gate=new Mutex(false,@"Local\ModelConsoleGpuPowerDriver")){
     bool locked=false;try{try{locked=gate.WaitOne(0);}catch(AbandonedMutexException){locked=true;}if(!locked)return 66;
      var driver=new NvidiaPowerDriver();var fresh=driver.Read();if(fresh.Uuid!=data.Uuid)return 23;
      // This read and both safety decisions take place AFTER the UAC prompt.
      int watts=args[0]=="apply"?PowerPolicy.ValidateWatts(data.Expected,fresh,data.Watts):PowerPolicy.ReducedTarget(fresh,data.Watts);
      if(!PowerPolicy.Matches(fresh,data.Uuid,watts))NvidiaPowerDriver.DirectSet(data.Uuid,watts);
      var after=driver.Read();for(int attempt=0;attempt<8&&!PowerPolicy.Matches(after,data.Uuid,watts);attempt++){Thread.Sleep(150);after=driver.Read();}
      return PowerPolicy.Matches(after,data.Uuid,watts)?0:67;
     }finally{if(locked)gate.ReleaseMutex();}
    }
   }catch(Exception e){switch(e.Message){case "power_temperature_guard":return 21;case "power_settings_changed":return 22;case "power_gpu_changed":return 23;default:return 68;}}
  }
 }
}

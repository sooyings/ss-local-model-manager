using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using InferenceMonitor;
namespace ModelConsole {
 public static class ConsoleMetrics {
  public static double? Clean(double? n){return n.HasValue&&!double.IsNaN(n.Value)&&!double.IsInfinity(n.Value)&&n>=0?n:null;}
  public static double? Ratio(double? n,double? d){n=Clean(n);d=Clean(d);return n.HasValue&&d>0?Math.Min(100,n.Value/d.Value*100):(double?)null;}
  public static string N(double? n,string format="0"){n=Clean(n);return n.HasValue?n.Value.ToString(format,CultureInfo.InvariantCulture):"—";}
  public static string Count(double? n){return !n.HasValue?"—":n>=10000?N(n/10000,"0.##")+"万":N(n,"N0");}
 }
 public class TelemetryHistory {
  public List<double?> Decode=new List<double?>(),Prefill=new List<double?>(),Gpu=new List<double?>();
  public List<double?> Cpu=new List<double?>(),Vram=new List<double?>(),Ram=new List<double?>(); public List<double?> CoreTemp=new List<double?>(),Hotspot=new List<double?>(),MemoryTemp=new List<double?>(),Power=new List<double?>();public string Model=""; double stamp;
  public void Add(Snapshot s,double at,HostSample host=null,NvidiaThermals thermals=null,double? power=null){
   if(at<stamp)return;stamp=at;
   if(s.Online&&s.Model!=""&&s.Model!=Model){Model=s.Model;Decode.Clear();Prefill.Clear();Gpu.Clear();Cpu.Clear();Vram.Clear();Ram.Clear();CoreTemp.Clear();Hotspot.Clear();MemoryTemp.Clear();Power.Clear();}
   Decode.Add(s.Online?ConsoleMetrics.Clean(s.Decode):null);
   Prefill.Add(s.Online?ConsoleMetrics.Clean(s.Prefill):null);Gpu.Add(ConsoleMetrics.Clean(s.GpuUtil));
   Cpu.Add(host==null?null:ConsoleMetrics.Clean(host.Cpu));Vram.Add(ConsoleMetrics.Clean(s.VramUsed));Ram.Add(host==null?null:ConsoleMetrics.Clean(host.RamUsed));
   CoreTemp.Add(thermals==null?s.Temp:thermals.Core??s.Temp);Hotspot.Add(thermals==null?null:thermals.Hotspot);MemoryTemp.Add(thermals==null?null:thermals.Memory);Power.Add(ConsoleMetrics.Clean(power));
   if(Decode.Count>60){Decode.RemoveAt(0);Prefill.RemoveAt(0);Gpu.RemoveAt(0);Cpu.RemoveAt(0);Vram.RemoveAt(0);Ram.RemoveAt(0);CoreTemp.RemoveAt(0);Hotspot.RemoveAt(0);MemoryTemp.RemoveAt(0);Power.RemoveAt(0);}
  }
 }
 public class Preferences {
  public bool ReducedMotion,Topmost,Light; public string Language="zh";
  static readonly string FilePath=Path.Combine(SS.Core.AppPaths.DataRoot,@"config\console-ui.json");
  public static Preferences Load(){try{return new JavaScriptSerializer().Deserialize<Preferences>(File.ReadAllText(FilePath))??new Preferences();}catch{return new Preferences();}}
  public void Save(){try{File.WriteAllText(FilePath,new JavaScriptSerializer().Serialize(this));}catch{}}
 }
 public class HostSample {
  public double? Cpu,RamUsed,RamTotal;
  [StructLayout(LayoutKind.Sequential)]class Memory {public uint Length=64,Load;public ulong Total,Avail,Page,AvailPage,Virtual,AvailVirtual,Extended;}
  [DllImport("kernel32.dll")]static extern bool GlobalMemoryStatusEx([In,Out]Memory m);
  [DllImport("kernel32.dll")]static extern bool GetSystemTimes(out long idle,out long kernel,out long user);
  long prevIdle,prevTotal;
  public void Poll(){var m=new Memory();if(GlobalMemoryStatusEx(m)){RamTotal=m.Total/1073741824.0;RamUsed=(m.Total-m.Avail)/1073741824.0;}
   long idle,kernel,user;if(GetSystemTimes(out idle,out kernel,out user)){long total=kernel+user;if(prevTotal>0&&total>prevTotal)Cpu=Math.Max(0,Math.Min(100,100.0*(1.0-(double)(idle-prevIdle)/(total-prevTotal))));prevIdle=idle;prevTotal=total;}
  }
 }
}


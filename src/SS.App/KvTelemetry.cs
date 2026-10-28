using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
namespace InferenceMonitor {
 public class KvLogState {
  public double? ResidentCells,TotalCells,HostGiB,HitPercent,GpuMiB,CpuMiB;
  static Regex streaming=new Regex(@"KV streaming: (\d+) of (\d+) cells.*?K/V in ([\d.]+) GiB of pinned RAM");
  static Regex hit=new Regex(@"KV streaming: ([\d.]+)% of \d+ block reads hit VRAM");
  static Regex elastic=new Regex(@"elastic K/V: (\d+) of (\d+) cells in VRAM \(([\d.]+) of ([\d.]+) GiB\)");
  static Regex allocation=new Regex(@"(?<device>CUDA\d*|CPU|Vulkan\d*|SYCL\d*|Metal)\s+KV buffer size\s*=\s*(?<size>[\d.]+) MiB");
  public void Reset(){ResidentCells=TotalCells=HostGiB=HitPercent=GpuMiB=CpuMiB=null;}
  static double D(string s){return double.Parse(s,CultureInfo.InvariantCulture);}
  public void Feed(string line){
   if(line.Contains("engine started:")||line.Contains("common_params_print_info:"))Reset();
   var m=streaming.Match(line);if(m.Success){double r=D(m.Groups[1].Value),t=D(m.Groups[2].Value);if(t>0&&r<=t){ResidentCells=r;TotalCells=t;HostGiB=D(m.Groups[3].Value);}}
   m=hit.Match(line);if(m.Success&&D(m.Groups[1].Value)<=100)HitPercent=D(m.Groups[1].Value);
   m=elastic.Match(line);if(m.Success){double r=D(m.Groups[1].Value),t=D(m.Groups[2].Value);if(t>0&&r<=t){ResidentCells=r;TotalCells=t;GpuMiB=D(m.Groups[3].Value)*1024;}}
   m=allocation.Match(line);if(m.Success){double n=D(m.Groups["size"].Value);if(m.Groups["device"].Value=="CPU")CpuMiB=(CpuMiB??0)+n;else GpuMiB=(GpuMiB??0)+n;}
  }
  public void Apply(Snapshot s){s.KvResidentCells=ResidentCells;s.KvTotalCells=TotalCells;s.KvHostGiB=HostGiB;s.KvHitPercent=HitPercent;s.KvGpuMiB=GpuMiB;s.KvCpuMiB=CpuMiB;}
 }
 public class KvLogReader {
  public readonly KvLogState State=new KvLogState();public bool Ready;
  string path="",pending="";long offset;DateTime created;bool accepted;long sessionOffset=-1;
  public void Read(string file,bool strata,string modelFile,DateTime serviceStart,long startOffset=0,bool sessionVerified=false){
   if(!strata&&!sessionVerified){Ready=false;State.Reset();return;}
   var info=new FileInfo(file);if(!info.Exists){Ready=false;return;}
   if(startOffset<0||startOffset>info.Length){Ready=false;State.Reset();return;}
   bool reset=startOffset!=sessionOffset||file!=path||info.Length<offset||info.CreationTimeUtc!=created;
   using(var f=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){
    if(reset){path=file;sessionOffset=startOffset;offset=startOffset;Ready=false;created=info.CreationTimeUtc;State.Reset();pending="";accepted=false;}
    f.Seek(offset,SeekOrigin.Begin);int size=(int)Math.Min(!Ready?8388608:524288,f.Length-offset);
    byte[] bytes=new byte[size];int count=f.Read(bytes,0,size);offset+=count;Ready=offset>=info.Length;
    string text=pending+Encoding.UTF8.GetString(bytes,0,count);int end=text.LastIndexOf('\n');if(end<0){pending=text.Length<65536?text:"";return;}
    pending=text.Substring(end+1);
    foreach(string line in text.Substring(0,end).Split('\n')){
     if(strata&&line.Contains("engine started:")){
      State.Reset();accepted=false;var m=Regex.Match(line,@"\[strata\] (\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) engine started:");
      DateTime stamp;if(m.Success&&DateTime.TryParseExact(m.Groups[1].Value,"yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture,DateTimeStyles.None,out stamp))
       accepted=stamp>=serviceStart.AddSeconds(-3);
     }
     if(!strata&&line.Contains("common_params_print_info:")){accepted=false;State.Reset();}
     if(!strata&&line.Contains("load_model: loading model")){accepted=modelFile!=""&&line.IndexOf(modelFile,StringComparison.OrdinalIgnoreCase)>=0&&info.LastWriteTime>=serviceStart;State.Reset();}
     if(accepted)State.Feed(line);
    }
   }
  }
 }
}

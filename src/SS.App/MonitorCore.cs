using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.IO;
using System.Text.RegularExpressions;
namespace LocalMonitor {
public class Metric {
 public double? Value;public double Stamp;public string Source="";public int Count;public int Sessions,Slots;public List<Tuple<double,double>> History=new List<Tuple<double,double>>();
 public void Put(double v,double ts,string source,int count=0){if(double.IsNaN(v)||double.IsInfinity(v)||v<0||ts<Stamp)return;Value=v;Stamp=ts;Source=source;Count=count;History.Add(Tuple.Create(ts,v));if(History.Count>240)History.RemoveAt(0);}
}
public class Metrics {
 public Metric[] Items={new Metric(),new Metric(),new Metric(),new Metric()};public string Model="";HashSet<string> firstIds=new HashSet<string>();HashSet<string> records=new HashSet<string>();Dictionary<int,int> slots=new Dictionary<int,int>();HashSet<int> overlapped=new HashSet<int>();
 static Regex slot=new Regex(@"id\s+(\d+)\s*\|\s*task\s+(-?\d+)");static Regex prompt=new Regex(@"prompt eval time\s*=\s*([\d.]+) ms /\s*(\d+) tokens.*?,\s*([\d.]+) tokens per second");static Regex decode=new Regex(@"\|\s+eval time\s*=\s*([\d.]+) ms /\s*(\d+) tokens.*?,\s*([\d.]+) tokens per second");static Regex live=new Regex(@"n_gen\s*=\s*(\d+),\s*tg\s*=\s*([\d.]+) t/s");
 public static double? Number(Dictionary<string,object> r,string key){object v;double d;if(!r.TryGetValue(key,out v)||v==null||!double.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out d)||double.IsNaN(d)||double.IsInfinity(d))return null;return d;}
 public static string String(Dictionary<string,object> r,string key){object v;return r.TryGetValue(key,out v)&&v!=null?Convert.ToString(v):"";}
 public static string Band(double value,double green,double amber,bool lower){return lower?(value<=green?"green":value<=amber?"amber":"red"):(value>=green?"green":value>=amber?"amber":"red");}
 public void Reset(string model){Model=model;Items=new[]{new Metric(),new Metric(),new Metric(),new Metric()};firstIds.Clear();records.Clear();slots.Clear();overlapped.Clear();}
 public void Apply(Dictionary<string,object> r){
  if(Model==""||!string.Equals(String(r,"model"),Model,StringComparison.OrdinalIgnoreCase))return;
  string kind=String(r,"kind"),id=String(r,"request_id"),err=String(r,"error");if(err!="")return;double? stamp=Number(r,"ts");if(!stamp.HasValue)return;
  string dedup=kind+":"+id;if(id!=""&&!records.Add(dedup))return;if(records.Count>4000)records.Clear();string source=String(r,"source");
  if(kind=="first"||kind=="sample"){
   double? ttft=Number(r,"ttft_s");if(ttft.HasValue&&(id==""||firstIds.Add(id)))Items[1].Put(ttft.Value,stamp.Value,source);
   if(kind=="first")return;
   double? dec=Number(r,"decode_tps"),p=Number(r,"prefill_tps"),n=Number(r,"prompt_tokens"),outn=Number(r,"predicted_tokens");
   double sessions=Number(r,"concurrency")??1;
   if(dec.HasValue&&outn>=32&&sessions<=1)Items[0].Put(dec.Value,stamp.Value,source,(int)outn.Value);
   if(p.HasValue&&n>=4096)Items[2].Put(p.Value,stamp.Value,source,(int)n.Value);
  }else if(kind=="batch"){
   double? t=Number(r,"total_tps");if(!t.HasValue)return;Items[3].Put(t.Value,stamp.Value,source);Items[3].Sessions=(int)(Number(r,"concurrency")??0);Items[3].Slots=(int)(Number(r,"slots")??0);
  }
 }
 public void FeedLog(string line,double ts){
  if(Model=="")return;if(line.Contains("loading model '")){Reset(Model);return;}
  var key=slot.Match(line);if(!key.Success)return;int s=int.Parse(key.Groups[1].Value),task=int.Parse(key.Groups[2].Value);
  if(line.Contains("launch_slot_")&&task>=0){slots[s]=task;overlapped.Remove(task);if(slots.Count>1)foreach(int running in slots.Values)overlapped.Add(running);}
  if(line.Contains("release:")){slots.Remove(s);overlapped.Remove(task);return;}
  int activeTask;if(!slots.TryGetValue(s,out activeTask)||activeTask!=task)return;
  var p=prompt.Match(line);if(p.Success){int n=int.Parse(p.Groups[2].Value);if(n>=4096)Items[2].Put(double.Parse(p.Groups[3].Value,CultureInfo.InvariantCulture),ts,"log",n);return;}
  if(slots.Count>1||overlapped.Contains(task))return;
  var dec=decode.Match(line);if(dec.Success){int n=int.Parse(dec.Groups[2].Value);if(n>=32)Items[0].Put(double.Parse(dec.Groups[3].Value,CultureInfo.InvariantCulture),ts,"log",n);return;}
  var rt=live.Match(line);if(rt.Success){int n=int.Parse(rt.Groups[1].Value);if(n>=32)Items[0].Put(double.Parse(rt.Groups[2].Value,CultureInfo.InvariantCulture),ts,"log",n);}
 }
}
public class TailReader {
 public string Path;long offset;DateTime creation;Decoder decoder=Encoding.UTF8.GetDecoder();StringBuilder pending=new StringBuilder();bool initialized;
 public TailReader(string path){Path=path;Skip();}
 public void Skip(){try{var f=new FileInfo(Path);offset=f.Exists?f.Length:0;creation=f.Exists?f.CreationTimeUtc:DateTime.MinValue;initialized=f.Exists;}catch{offset=0;initialized=false;}decoder.Reset();pending.Clear();}
 public List<string> Read(){var lines=new List<string>();if(!File.Exists(Path))return lines;
  try{var info=new FileInfo(Path);if(!initialized||info.Length<offset||info.CreationTimeUtc!=creation){offset=0;decoder.Reset();pending.Clear();creation=info.CreationTimeUtc;initialized=true;}
   using(var file=new FileStream(Path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete)){file.Seek(offset,SeekOrigin.Begin);int size=(int)Math.Min(1048576,file.Length-offset);if(size<=0)return lines;byte[] bytes=new byte[size];int read=file.Read(bytes,0,size);offset+=read;char[] chars=new char[Encoding.UTF8.GetMaxCharCount(read)];int count=decoder.GetChars(bytes,0,read,chars,0,false);pending.Append(chars,0,count);}
   string all=pending.ToString();int end=all.LastIndexOf('\n');if(end>=0){lines.AddRange(all.Substring(0,end).Split('\n'));pending.Clear();pending.Append(all.Substring(end+1));}if(pending.Length>262144)pending.Clear();
  }catch(IOException){}catch(UnauthorizedAccessException){}return lines;
 }
}
}

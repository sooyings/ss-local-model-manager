using System;using System.IO;using System.Text;using System.Linq;using System.Collections.Generic;using System.Security.Cryptography;using System.Text.RegularExpressions;using System.Web.Script.Serialization;using System.Diagnostics;
namespace SS.Core {
 public sealed class GgufHeader {
  public string Architecture="",Name="",TemplateHash="";public long Context; public long Bytes;
  static string ReadString(BinaryReader r,int limit){ulong n=r.ReadUInt64();if(n>(ulong)limit||n>(ulong)(r.BaseStream.Length-r.BaseStream.Position))throw new InvalidDataException("GGUF string exceeds budget");return Encoding.UTF8.GetString(r.ReadBytes((int)n));}
  static void Skip(BinaryReader r,uint type,int depth){
   if(r.BaseStream.Position>33554432)throw new InvalidDataException("GGUF header exceeds budget");if(depth>2)throw new InvalidDataException("GGUF nested array");
   int[] sizes={1,1,2,2,4,4,4,1,0,0,8,8,8};
   if(type>=sizes.Length)throw new InvalidDataException("GGUF unknown type");
   if(type==8){ulong n=r.ReadUInt64();if(n>16777216||n>(ulong)(r.BaseStream.Length-r.BaseStream.Position))throw new InvalidDataException("GGUF invalid string");r.BaseStream.Seek((long)n,SeekOrigin.Current);}
   else if(type==9){uint item=r.ReadUInt32();ulong n=r.ReadUInt64();if(n>1000000)throw new InvalidDataException("GGUF array exceeds budget");for(ulong i=0;i<n;i++)Skip(r,item,depth+1);}
   else{if(r.BaseStream.Position+sizes[type]>r.BaseStream.Length)throw new InvalidDataException("GGUF truncated");r.BaseStream.Seek(sizes[type],SeekOrigin.Current);}
  }
  public static GgufHeader Read(string path){
   try{using(var r=new BinaryReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite))){var h=new GgufHeader{Bytes=r.BaseStream.Length};
    if(r.ReadUInt32()!=0x46554747)throw new InvalidDataException("Not GGUF");uint v=r.ReadUInt32();if(v<2||v>3)throw new InvalidDataException("Unsupported GGUF version");
    r.ReadUInt64();ulong count=r.ReadUInt64();if(count>20000)throw new InvalidDataException("GGUF metadata exceeds budget");
    for(ulong i=0;i<count;i++){if(r.BaseStream.Position>33554432)throw new InvalidDataException("GGUF header exceeds budget");string key=ReadString(r,4096);uint type=r.ReadUInt32();
     if((key=="general.architecture"||key=="general.name")&&type==8){string value=ReadString(r,65536);if(key.EndsWith("architecture"))h.Architecture=value;else h.Name=value;}
     else if(key.EndsWith(".context_length")&&type==4)h.Context=r.ReadUInt32();
     else if(key.EndsWith(".context_length")&&type==10){ulong n=r.ReadUInt64();if(n<=int.MaxValue)h.Context=(long)n;}
     else Skip(r,type,0);
    }if(String.IsNullOrWhiteSpace(h.Architecture))throw new InvalidDataException("GGUF architecture missing");return h;
   }}catch(EndOfStreamException e){throw new InvalidDataException("Truncated GGUF",e);}
  }
 }
 public static class ModelDiscovery {
  static readonly JavaScriptSerializer Json=new JavaScriptSerializer{MaxJsonLength=4194304};
  static readonly object Gate=new object();
  public static Dictionary<string,object> ImportFile(string path){
   string full=Path.GetFullPath(path);if(!String.Equals(Path.GetExtension(full),".gguf",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Only GGUF is supported");
   if((File.GetAttributes(full)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Confirm linked model through its real location");
   string name=Path.GetFileNameWithoutExtension(full);if(name.StartsWith("mmproj",StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Projection is not a language model");
   var shard=Regex.Match(Path.GetFileName(full),@"^(.*)-(\d{5})-of-(\d{5})\.gguf$",RegexOptions.IgnoreCase);
   if(shard.Success){int part=int.Parse(shard.Groups[2].Value),total=int.Parse(shard.Groups[3].Value);if(part!=1)throw new InvalidDataException("Select the first shard");if(total<1||total>128)throw new InvalidDataException("Invalid shard count");for(int i=1;i<=total;i++)if(!File.Exists(Path.Combine(Path.GetDirectoryName(full),shard.Groups[1].Value+"-"+i.ToString("00000")+"-of-"+total.ToString("00000")+".gguf")))throw new InvalidDataException("Model shard missing");}
   var info=GgufHeader.Read(full);string digest;using(var sha=SHA256.Create())digest=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(full.ToUpperInvariant()))).Replace("-","").Substring(0,12).ToLowerInvariant();
   string id="ss-"+digest;var quant=Regex.Match(name,@"(?:IQ\d+_[A-Z0-9_]+|Q\d+_[A-Z0-9_]+|BF16|F16)",RegexOptions.IgnoreCase);
   string label=String.IsNullOrWhiteSpace(info.Name)?name:info.Name;label=label.Length>24?label.Substring(0,24):label;
   return new Dictionary<string,object>{{"id",id},{"alias",id},{"short",label},{"display",name},{"main",full},{"dir",Path.GetDirectoryName(full)},{"ownership","external"},{"identityState","metadata_only"},{"architecture",info.Architecture},{"quant",quant.Success?quant.Value.ToUpperInvariant():"unknown"},{"runtime","llama-cpp"},{"context",(int)Math.Min(info.Context>0?info.Context:4096,4096)},{"threads",Math.Max(1,Math.Min(8,Environment.ProcessorCount/2))},{"cacheRam",0},{"sub",info.Architecture+" · "+(quant.Success?quant.Value:"GGUF")},{"reasoning_budget",1024},{"parameters",new Dictionary<string,object>{{"temperature",.7},{"top_p",.9},{"top_k",20},{"min_p",0.0},{"repeat_penalty",1.0}}}};
  }
  public static int AddFiles(IEnumerable<string> paths){
   lock(Gate){var root=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(AppPaths.ModelsJson));var models=root["models"] as Dictionary<string,object>;if(models==null)throw new InvalidDataException("Invalid registry");int added=0;
    foreach(string p in paths){var m=ImportFile(p);string id=(string)m["id"];if(models.ContainsKey(id))continue;models[id]=m;added++;if(Convert.ToString(root["default"])=="")root["default"]=id;}
    if(added>0)AppPaths.AtomicWrite(AppPaths.ModelsJson,Json.Serialize(root));return added;
   }
  }
  public static List<string> QuickCandidates(){
   var roots=new List<string>();string profile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
   foreach(string value in new[]{Environment.GetEnvironmentVariable("HF_HUB_CACHE"),Path.Combine(profile,".cache","huggingface","hub"),Path.Combine(profile,".lmstudio","models")})if(!String.IsNullOrWhiteSpace(value)&&Directory.Exists(value))roots.Add(value);
   foreach(var drive in DriveInfo.GetDrives())try{if(drive.DriveType==DriveType.Fixed&&drive.IsReady)foreach(string folder in new[]{"Models","models","LLM","MODLE"}){string p=Path.Combine(drive.RootDirectory.FullName,folder);if(Directory.Exists(p))roots.Add(p);}}catch{}
   var found=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var queue=new Queue<Tuple<string,int>>();foreach(string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))queue.Enqueue(Tuple.Create(root,0));var timer=Stopwatch.StartNew();int visited=0;
   while(queue.Count>0&&timer.Elapsed<TimeSpan.FromSeconds(30)&&visited<5000){var next=queue.Dequeue();visited++;try{if((File.GetAttributes(next.Item1)&FileAttributes.ReparsePoint)!=0)continue;foreach(string p in Directory.EnumerateFiles(next.Item1,"*.gguf")){if(found.Count>=256)return found.ToList();found.Add(p);}if(next.Item2<6)foreach(string d in Directory.EnumerateDirectories(next.Item1))if(queue.Count<10000)queue.Enqueue(Tuple.Create(d,next.Item2+1));}catch{}}
   return found.OrderBy(x=>x,StringComparer.OrdinalIgnoreCase).ToList();
  }
  public static int ScanQuick(){int n=0;foreach(string p in QuickCandidates())try{n+=AddFiles(new[]{p});}catch(InvalidDataException){}catch(IOException){}catch(UnauthorizedAccessException){}return n;}
 }
}
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Web.Script.Serialization;
namespace SS.Core {
 public static class AppPaths {
  static AppPaths(){}
  static readonly string root=ResolveRoot();
  public static string DataRoot {get{return root;}}
  public static string AppRoot {get{return AppDomain.CurrentDomain.BaseDirectory;}}
  public static string ModelsJson {get{return Path.Combine(root,"config","models.json");}}
  public static string EngineRoot {get{return Path.Combine(AppRoot,"engines");}}
  public static string DirectBaseUrl {get{return "http://127.0.0.1:"+DirectPort;}}
  public static int DirectPort {get{
   try{var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(root,"config","ports.json")));
    int p=Convert.ToInt32(d["direct"]);ValidatePort(p);return p;}catch{return 17881;}
  }}
  static string ResolveRoot(){
   string candidate=Environment.GetEnvironmentVariable("SS_MODEL_DATA_ROOT");
   if(String.IsNullOrWhiteSpace(candidate))candidate=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SSLocalModelManager");
   if(!Path.IsPathRooted(candidate)||candidate.StartsWith(@"\\"))throw new ArgumentException("data_root_must_be_local_absolute");
   return Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar);
  }
  public static void ValidatePort(int p){if(p<1024||p>65535||p==8081||p==8090)throw new ArgumentException("port_reserved_or_invalid");}
  public static string Log(string name){
   if(String.IsNullOrWhiteSpace(name)||Path.GetFileName(name)!=name||name=="."||name=="..")throw new ArgumentException("invalid_log_name");
   return Path.Combine(root,"logs",name);
  }
  public static void Initialize(){
   foreach(string part in new[]{"config","logs","profiles","backups","transactions","tuning"})Directory.CreateDirectory(Path.Combine(root,part));
   CreateOnce(ModelsJson,"{\"version\":2,\"default\":\"\",\"models\":{}}");
   CreateOnce(Path.Combine(root,"config","ports.json"),"{\"direct\":17881}");
  }
  static void CreateOnce(string path,string text){
   try{using(var stream=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read))using(var writer=new StreamWriter(stream,new UTF8Encoding(false)))writer.Write(text);}
   catch(IOException){if(!File.Exists(path))throw;}
  }
  public static void AtomicWrite(string path,string text){
   string full=Path.GetFullPath(path);
   if(!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new ArgumentException("write_outside_data_root");
   string temp=full+"."+Guid.NewGuid().ToString("N")+".tmp";
   try{File.WriteAllText(temp,text,new UTF8Encoding(false));if(File.Exists(full)){
     string backup=Path.Combine(root,"backups",Path.GetFileName(full)+"."+DateTime.UtcNow.ToString("yyyyMMddHHmmssfff")+"."+Guid.NewGuid().ToString("N")+".bak");
     File.Replace(temp,full,backup);
    }else File.Move(temp,full);
   }finally{if(File.Exists(temp))File.Delete(temp);}
  }
 }
}
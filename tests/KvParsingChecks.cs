using System;using System.IO;using System.Text;using InferenceMonitor;
class KvParsingChecks {
 static int n;static void Check(bool ok,string name){if(!ok)throw new Exception(name);n++;}
 static int Main(){try{
  var state=new KvLogState();state.Feed("0.00.123.456 I llama_kv_cac: CPU KV buffer size = 64.00 MiB");
  Check(state.CpuMiB==64,"current upstream CPU allocation");state.Feed("llama_kv_cache: CUDA0 KV buffer size = 128.00 MiB");Check(state.GpuMiB==128,"GPU allocation separate from CPU");
  string file=Path.Combine(Path.GetTempPath(),"SS-kv-"+Guid.NewGuid().ToString("N")+".log");
  string model=@"D:\fixtures\model.gguf";
  string header="0.00.001.000 I srv load_model: loading model '"+model+"'\n";
  File.WriteAllText(file,header+"0.00.010.000 I llama_kv_cac: CPU KV buffer size = 64.00 MiB\n",new UTF8Encoding(false));
  DateTime start=DateTime.Now.AddSeconds(-2);var reader=new KvLogReader();
  reader.Read(file,false,model,start,0,false);Check(!reader.Ready&&reader.State.CpuMiB==null,"unverified sessions do not publish allocation");
  reader.Read(file,false,model,start,0,true);Check(reader.Ready&&reader.State.CpuMiB==64,"verified matching model publishes true allocation");
  reader.Read(file,false,model,start,0,true);Check(reader.State.CpuMiB==64,"repeated poll does not double count");
  var wrong=new KvLogReader();wrong.Read(file,false,@"D:\fixtures\other.gguf",start,0,true);Check(wrong.State.CpuMiB==null,"wrong model log rejected");
  long offset=new FileInfo(file).Length;
  File.AppendAllText(file,header+"0.00.020.000 I llama_kv_cac: CPU KV buffer size = 32.00 MiB\n",new UTF8Encoding(false));
  reader.Read(file,false,model,start,offset,true);Check(reader.Ready&&reader.State.CpuMiB==32,"new session offset excludes old allocation");
  reader.Read(file,false,model,start,-1,true);Check(!reader.Ready&&reader.State.CpuMiB==null,"invalid offset rejected");
  File.Delete(file);Console.WriteLine("PASS KV parsing "+n);return 0;
 }catch(Exception ex){Console.WriteLine(ex);return 1;}}
}
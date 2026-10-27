using System;using System.IO;using System.Text;using System.Collections.Generic;using SS.Core;
class DiscoveryChecks {
 static int n;static void C(bool v,string name){if(!v)throw new Exception("FAIL "+name);n++;}
 static void S(BinaryWriter w,string s){var b=Encoding.UTF8.GetBytes(s);w.Write((ulong)b.Length);w.Write(b);}
 static int Main(){try{
  string root=Path.Combine(Path.GetTempPath(),"ss-discover-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);Environment.SetEnvironmentVariable("SS_MODEL_DATA_ROOT",root);AppPaths.Initialize();
  string good=Path.Combine(root,"tiny-Q4_K_M.gguf");using(var w=new BinaryWriter(File.Create(good))){w.Write(0x46554747U);w.Write(3U);w.Write(0UL);w.Write(2UL);S(w,"general.architecture");w.Write(8U);S(w,"llama");S(w,"llama.context_length");w.Write(4U);w.Write(8192U);}
  var info=GgufHeader.Read(good);C(info.Architecture=="llama","architecture");C(info.Context==8192,"context");
  var model=ModelDiscovery.ImportFile(good);C((string)model["ownership"]=="external","external ownership");C(Convert.ToInt32(model["context"])==4096,"conservative context");C(File.Exists(good),"weight preserved");
  C(ModelDiscovery.ImportFile(good)["id"].Equals(model["id"]),"stable identity");C(ModelDiscovery.AddFiles(new[]{good})==1,"import once");C(ModelDiscovery.AddFiles(new[]{good})==0,"deduplicate");
  string bad=Path.Combine(root,"bad.gguf");File.WriteAllText(bad,"not-a-model");bool refused=false;try{GgufHeader.Read(bad);}catch(InvalidDataException){refused=true;}C(refused,"bad magic refused");
  string partial=Path.Combine(root,"weights-00001-of-00002.gguf");File.Copy(good,partial);refused=false;try{ModelDiscovery.ImportFile(partial);}catch(InvalidDataException){refused=true;}C(refused,"missing shard refused");
  Console.WriteLine("PASS "+n+" discovery checks");return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
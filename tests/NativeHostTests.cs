using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using SS.Core;
class NativeHostTests {
 static int passed;
 static void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;}
 static void Reject(Action a,string name){try{a();throw new Exception("expected rejection: "+name);}catch(InvalidOperationException){passed++;}}
 static string Hash(string p){using(var s=File.OpenRead(p))using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant();}
 static void Main(){
  string root=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"host-test-data");Directory.CreateDirectory(root);
  string exe=Path.Combine(root,"llama-server.exe"),dep=Path.Combine(root,"ggml.dll");File.WriteAllText(exe,"engine");File.WriteAllText(dep,"dependency");
  string manifest=Path.Combine(root,"engine-manifest.json");
  File.WriteAllText(manifest,"{\"schemaVersion\":1,\"files\":[{\"path\":\"llama-server.exe\",\"sha256\":\""+Hash(exe)+"\"},{\"path\":\"ggml.dll\",\"sha256\":\""+Hash(dep)+"\"}]}");
  Check(NativeHost.VerifyEngine(root)==exe,"valid manifest");
  File.WriteAllText(dep,"tampered");Reject(()=>NativeHost.VerifyEngine(root),"tampered dependency");
  File.WriteAllText(manifest,"{\"schemaVersion\":1,\"files\":[{\"path\":\"../llama-server.exe\",\"sha256\":\""+new string('0',64)+"\"}]}");Reject(()=>NativeHost.VerifyEngine(root),"path traversal");
  Check(NativeHost.ValidId("qwen3.6-35b_a3b"),"valid identity");
  Check(!NativeHost.ValidId("x\" --port 8081"),"injection identity");
  string modelRoot=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"host-model-data");Directory.CreateDirectory(modelRoot);string brokenModel=Path.Combine(modelRoot,"model-00001-of-00002.gguf");File.WriteAllBytes(brokenModel,new byte[]{71,71,85,70,3,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0});
  Reject(()=>NativeHost.ValidateModelFiles(brokenModel),"missing model shard");
  Reject(()=>NativeHost.ValidateModelFiles(exe),"not GGUF");
  Check(NativeHost.SlotsIdle("[{\"is_processing\":false}]"),"idle slots");
  Check(!NativeHost.SlotsIdle("[{\"is_processing\":true}]"),"busy slots");
  Check(!NativeHost.SlotsIdle("[]"),"empty slots fail closed");
  Check(!NativeHost.SlotsIdle("invalid"),"malformed slots fail closed");
  var args=NativeHost.BuildArguments(@"D:\Models\model.gguf","test-id",4096,4,17881);
  Check(args.Contains("127.0.0.1")&&args.Contains("17881")&&!args.Contains("8081"),"isolated port");
  Reject(()=>NativeHost.BuildArguments(@"D:\Models\model.gguf","a --host 0.0.0.0",4096,4,17881),"bad alias");
  Reject(()=>NativeHost.BuildArguments(@"D:\Models\model.gguf","test",4096,4,8081),"reserved port");
  var settings=new Dictionary<string,object>{{"temperature",0.7},{"top_p",0.9}};
  Check(NativeHost.BuildArguments(@"D:\Models\model.gguf","test",4096,4,17881,settings).Contains("--temp 0.7"),"allowed sampling");
  settings["temperature"]="1 --port 8081";Reject(()=>NativeHost.BuildArguments(@"D:\Models\model.gguf","test",4096,4,17881,settings),"parameter injection");
  var gate=new AdmissionGate();
  Check(gate.TryEnter(),"gate accepts request");
  Check(!gate.TryClose(),"busy gate refuses stop");
  Check(gate.TryEnter(),"gate still open after busy stop");
  gate.Exit();gate.Exit();Check(gate.TryClose(),"idle gate closes");
  Check(!gate.TryEnter(),"closed gate rejects new work");gate.Open();Check(gate.TryEnter(),"gate reopens");gate.Exit();
  using(var input=new MemoryStream(Encoding.UTF8.GetBytes("data: 你好\n\ndata: [DONE]\n\n")))using(var output=new MemoryStream()){
   GatewayProxy.CopyStreaming(input,output);Check(Encoding.UTF8.GetString(output.ToArray()).Contains("[DONE]"),"stream is not truncated");
  }
  Console.WriteLine("PASS "+passed);
 }
}

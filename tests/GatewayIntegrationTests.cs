using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using SS.Core;
class GatewayIntegrationTests {
 static int count;
 static void Check(bool pass,string name){if(!pass)throw new Exception(name);count++;}
 static int Port(){var s=new TcpListener(IPAddress.Loopback,0);s.Start();int port=((IPEndPoint)s.LocalEndpoint).Port;s.Stop();return port;}
 static HttpWebResponse Call(int port,string path){var r=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+port+path);r.Proxy=null;r.Timeout=5000;try{return (HttpWebResponse)r.GetResponse();}catch(WebException e){if(e.Response!=null)return (HttpWebResponse)e.Response;throw;}}
 static int Main(){try{Run();return 0;}catch(Exception ex){Console.WriteLine("FAIL "+ex.GetType().Name+" "+ex.Message);return 1;}}
 static void Run(){
  Console.WriteLine("STEP ports");int publicPort=Port(),backendPort=Port();while(backendPort==publicPort)backendPort=Port();string secret="fake-secret-do-not-expose";var seen=new ManualResetEvent(false);var release=new ManualResetEvent(false);var streamContinue=new ManualResetEvent(false);string auth="",receivedBody="";bool ownBackend=true;
  using(var fake=new HttpListener()){
   Console.WriteLine("STEP fake listener");fake.Prefixes.Add("http://127.0.0.1:"+backendPort+"/");fake.Start();var worker=new Thread(()=>{try{while(fake.IsListening){var ctx=fake.GetContext();auth=ctx.Request.Headers["Authorization"]??"";if(ctx.Request.HasEntityBody)using(var b=new StreamReader(ctx.Request.InputStream,Encoding.UTF8))receivedBody=b.ReadToEnd();if(ctx.Request.Url.AbsolutePath=="/v1/hold"){seen.Set();release.WaitOne(5000);}ctx.Response.ContentType="text/event-stream";ctx.Response.Headers["X-Backend-Secret"]=secret;byte[] first=Encoding.UTF8.GetBytes("data: hello\n\n");ctx.Response.OutputStream.Write(first,0,first.Length);ctx.Response.OutputStream.Flush();if(ctx.Request.Url.AbsolutePath=="/v1/stream")streamContinue.WaitOne(5000);byte[] last=Encoding.UTF8.GetBytes("data: [DONE]\n\n");ctx.Response.OutputStream.Write(last,0,last.Length);ctx.Response.Close();}}catch{}});worker.IsBackground=true;worker.Start();
   var gate=new AdmissionGate();using(var proxy=new GatewayProxy(publicPort,backendPort,secret,gate,()=>ownBackend)){
    proxy.Start();using(var r=Call(publicPort,"/v1/chat"))using(var rd=new StreamReader(r.GetResponseStream())){string body=rd.ReadToEnd();Check(r.StatusCode==HttpStatusCode.OK&&body.Contains("[DONE]"),"SSE response");Check(!body.Contains(secret)&&r.Headers["X-Backend-Secret"]==null,"key not exposed");}
    Check(auth=="Bearer "+secret,"backend authentication injected");
    var post=(HttpWebRequest)WebRequest.Create("http://127.0.0.1:"+publicPort+"/v1/chat");post.Proxy=null;post.Method="POST";post.ContentType="application/json";post.Headers[HttpRequestHeader.Authorization]="Bearer attacker";byte[] posted=Encoding.UTF8.GetBytes("{\"text\":\"你好\"}");post.ContentLength=posted.Length;using(var w=post.GetRequestStream())w.Write(posted,0,posted.Length);using(var r=(HttpWebResponse)post.GetResponse())using(var rd=new StreamReader(r.GetResponseStream()))rd.ReadToEnd();Check(receivedBody=="{\"text\":\"你好\"}"&&auth=="Bearer "+secret,"JSON body forwarded and client auth replaced");
    using(var r=Call(publicPort,"/v1/stream"))using(var rd=new StreamReader(r.GetResponseStream())){Check(rd.ReadLine()=="data: hello","SSE first event arrives before upstream completes");streamContinue.Set();Check(rd.ReadToEnd().Contains("[DONE]"),"SSE final event arrives");}
    var client=new Thread(()=>{using(var r=Call(publicPort,"/v1/hold"))using(var rd=new StreamReader(r.GetResponseStream()))rd.ReadToEnd();});client.IsBackground=true;client.Start();
    Check(seen.WaitOne(5000),"in-flight request reached backend");Check(!gate.TryClose(),"stop refused while active");release.Set();Check(client.Join(5000),"in-flight request completed");Check(gate.TryClose(),"stop accepted when idle");
    using(var r=Call(publicPort,"/v1/chat"))Check(r.StatusCode==HttpStatusCode.ServiceUnavailable,"new request rejected during stop");gate.Open();
    using(var r=Call(publicPort,"/not-allowed"))Check(r.StatusCode==HttpStatusCode.NotFound,"route allowlist");
    ownBackend=false;using(var r=Call(publicPort,"/v1/chat"))Check(r.StatusCode==HttpStatusCode.ServiceUnavailable,"unowned backend refused");
   }fake.Stop();worker.Join(2000);
  }
  Console.WriteLine("PASS gateway "+count);
 }
}

using System;
using System.IO;
using System.Net;
using System.Threading;
namespace SS.Core {
 public sealed class AdmissionGate {
  readonly object sync=new object();int active;bool accepting=true;
  public bool TryEnter(){lock(sync){if(!accepting)return false;active++;return true;}}
  public void Exit(){lock(sync){if(active<=0)throw new InvalidOperationException("gate_underflow");active--;}}
  public bool TryClose(){lock(sync){if(!accepting||active!=0)return false;accepting=false;return true;}}
  public void Open(){lock(sync){accepting=true;}}
 }
#if !NET8_0_OR_GREATER
 public sealed class GatewayProxy:IDisposable {
  readonly HttpListener listener=new HttpListener();readonly AdmissionGate gate;readonly string backend,key;readonly Func<bool> backendOwned;Thread thread;
  public GatewayProxy(int publicPort,int internalPort,string token,AdmissionGate admission,Func<bool> owned){
   if(admission==null||owned==null||String.IsNullOrWhiteSpace(token))throw new ArgumentException("invalid_gateway");
   AppPaths.ValidatePort(publicPort);AppPaths.ValidatePort(internalPort);if(publicPort==internalPort)throw new ArgumentException("same_port");
   listener.Prefixes.Add("http://127.0.0.1:"+publicPort+"/");backend="http://127.0.0.1:"+internalPort;key=token;gate=admission;backendOwned=owned;
  }
  public void Start(){listener.Start();thread=new Thread(Accept);thread.IsBackground=true;thread.Start();}
  void Accept(){while(listener.IsListening){try{var context=listener.GetContext();ThreadPool.QueueUserWorkItem(_=>Handle(context));}catch(HttpListenerException){break;}catch(ObjectDisposedException){break;}}}
  static bool Route(string path){return path.StartsWith("/v1/",StringComparison.Ordinal)||path=="/health"||path=="/props"||path=="/slots"||path=="/metrics";}
  static void Reject(HttpListenerContext c,int status){try{c.Response.StatusCode=status;c.Response.ContentLength64=0;c.Response.Close();}catch{}}
  public static void CopyStreaming(Stream input,Stream output){byte[] buf=new byte[8192];int n;while((n=input.Read(buf,0,buf.Length))>0){output.Write(buf,0,n);output.Flush();}}
  void Handle(HttpListenerContext c){
   if(!Route(c.Request.Url.AbsolutePath)){Reject(c,404);return;}
   if(!gate.TryEnter()){Reject(c,503);return;}
   HttpWebRequest request=null;
   try{
    if(!backendOwned()){Reject(c,503);return;}
    request=(HttpWebRequest)WebRequest.Create(backend+c.Request.RawUrl);request.Proxy=null;request.AllowAutoRedirect=false;request.Method=c.Request.HttpMethod;
    request.Timeout=300000;request.ReadWriteTimeout=300000;request.Headers[HttpRequestHeader.Authorization]="Bearer "+key;
    if(!String.IsNullOrEmpty(c.Request.ContentType))request.ContentType=c.Request.ContentType;
    if(!String.IsNullOrEmpty(c.Request.Headers["Accept"]))request.Accept=c.Request.Headers["Accept"];
    if(c.Request.HasEntityBody){if(c.Request.ContentLength64>=0)request.ContentLength=c.Request.ContentLength64;else request.SendChunked=true;using(var w=request.GetRequestStream())CopyStreaming(c.Request.InputStream,w);}
    HttpWebResponse response;try{response=(HttpWebResponse)request.GetResponse();}catch(WebException ex){response=ex.Response as HttpWebResponse;if(response==null)throw;}
    using(response){c.Response.StatusCode=(int)response.StatusCode;if(!String.IsNullOrEmpty(response.ContentType))c.Response.ContentType=response.ContentType;
     c.Response.SendChunked=true;c.Response.Headers["Cache-Control"]="no-store";
     using(var stream=response.GetResponseStream()){if(stream!=null&&c.Request.HttpMethod!="HEAD")CopyStreaming(stream,c.Response.OutputStream);}
     c.Response.Close();
    }
   }catch{if(request!=null)request.Abort();Reject(c,502);}finally{gate.Exit();}
  }
  public void Dispose(){try{listener.Stop();listener.Close();}catch{}if(thread!=null)thread.Join(2000);}
 }
#endif
}

#if NET8_0_OR_GREATER
using System;using System.IO;using System.Net;using System.Net.Http;using System.Net.Http.Headers;using System.Threading;using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;using Microsoft.AspNetCore.Hosting;using Microsoft.AspNetCore.Http;
namespace SS.Core {
 public sealed class GatewayProxy:IDisposable {
  readonly AdmissionGate gate;readonly string backend,key;readonly Func<bool> owned;readonly int port;WebApplication app;HttpClient http;
  public GatewayProxy(int publicPort,int internalPort,string token,AdmissionGate admission,Func<bool> backendOwned){
   if(admission==null||backendOwned==null||string.IsNullOrWhiteSpace(token))throw new ArgumentException("invalid_gateway");
   AppPaths.ValidatePort(publicPort);AppPaths.ValidatePort(internalPort);if(publicPort==internalPort)throw new ArgumentException("same_port");
   port=publicPort;backend="http://127.0.0.1:"+internalPort;key=token;gate=admission;owned=backendOwned;
  }
  static bool Route(string path){return path.StartsWith("/v1/",StringComparison.Ordinal)||path=="/health"||path=="/props"||path=="/slots"||path=="/metrics";}
  public static void CopyStreaming(Stream input,Stream output){byte[] b=new byte[8192];int n;while((n=input.Read(b,0,b.Length))>0){output.Write(b,0,n);output.Flush();}}
  public void Start(){
   var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=new string[0],ContentRootPath=AppPaths.AppRoot});
   builder.WebHost.ConfigureKestrel(k=>k.Listen(IPAddress.Loopback,port));
   app=builder.Build();http=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(5)};
   app.Run(Handle);app.StartAsync().GetAwaiter().GetResult();
  }
  async Task Handle(HttpContext c){
   if(!Route(c.Request.Path.Value)){c.Response.StatusCode=404;return;}if(!gate.TryEnter()){c.Response.StatusCode=503;return;}
   try{
    if(!owned()){c.Response.StatusCode=503;return;}
    using(var request=new HttpRequestMessage(new HttpMethod(c.Request.Method),backend+c.Request.Path+c.Request.QueryString)){
     request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",key);
     if(c.Request.Headers.ContainsKey("Accept"))request.Headers.TryAddWithoutValidation("Accept",c.Request.Headers["Accept"].ToString());
     if(c.Request.ContentLength>0||c.Request.Headers.ContainsKey("Transfer-Encoding")){
      request.Content=new StreamContent(c.Request.Body);if(!string.IsNullOrEmpty(c.Request.ContentType))request.Content.Headers.TryAddWithoutValidation("Content-Type",c.Request.ContentType);
     }
     using(var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,c.RequestAborted)){
      c.Response.StatusCode=(int)response.StatusCode;c.Response.Headers["Cache-Control"]="no-store";
      if(response.Content.Headers.ContentType!=null)c.Response.ContentType=response.Content.Headers.ContentType.ToString();
      if(c.Request.Method!="HEAD"){using(var stream=await response.Content.ReadAsStreamAsync(c.RequestAborted)){
       byte[] buffer=new byte[8192];int n;while((n=await stream.ReadAsync(buffer,0,buffer.Length,c.RequestAborted))>0){await c.Response.Body.WriteAsync(buffer,0,n,c.RequestAborted);await c.Response.Body.FlushAsync(c.RequestAborted);}
      }}
     }
    }
   }catch(OperationCanceledException){if(!c.Response.HasStarted)c.Response.StatusCode=504;}
   catch{if(!c.Response.HasStarted)c.Response.StatusCode=502;else c.Abort();}
   finally{gate.Exit();}
  }
  public void Dispose(){if(app!=null){using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(3)))try{app.StopAsync(timeout.Token).GetAwaiter().GetResult();}catch{}app.DisposeAsync().AsTask().GetAwaiter().GetResult();}if(http!=null)http.Dispose();}
 }
}
#endif

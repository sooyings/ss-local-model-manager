using System;using System.Diagnostics;using System.Net;using System.Net.Sockets;using System.Reflection;using SS.Core;
class NetworkOwnershipChecks {static int Main(){try{var listener=new TcpListener(IPAddress.Loopback,0);try{listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;var method=typeof(NativeHost).GetMethod("OwnsLoopbackPort",BindingFlags.Static|BindingFlags.NonPublic);int pid=Process.GetCurrentProcess().Id;
if(!(bool)method.Invoke(null,new object[]{pid,port}))throw new Exception("Current-process loopback listener ownership missing");
if((bool)method.Invoke(null,new object[]{Int32.MaxValue,port}))throw new Exception("Foreign PID accepted");
listener.Stop();if((bool)method.Invoke(null,new object[]{pid,port}))throw new Exception("Closed listener accepted");
Console.WriteLine("PASS network ownership 3");return 0;}finally{listener.Stop();}}catch(Exception e){Console.WriteLine(e);return 1;}}}

using System;
using System.IO;
using SSSetup;
class SetupProtocolChecks {
 static int n; static void Check(bool x,string msg){n++;if(!x)throw new Exception(msg);}
 static void Main(string[] args){
  string root=Path.GetFullPath(args[0]); Directory.CreateDirectory(root);
  string target=Path.Combine(root,"install");
  Check(!Options.Parse(new[]{"--unattended","--dir",target}).Ok,"license required");
  Check(!Options.Parse(new[]{"--unattended","--accept-license","--wat"}).Ok,"unknown flag rejected");
  var opts=Options.Parse(new[]{"--unattended","--accept-license","--dir",target});
  Check(opts.Ok && opts.Unattended && opts.Target==target,"explicit unattended target");
  Check(!Options.Parse(new[]{"--dir","relative"}).Ok,"relative target rejected");
  string session="0123456789abcdef0123456789abcdef";
  var tracker=new ProgressTracker(session);
  Check(!tracker.Accept("not json"),"malformed ignored");
  Check(!tracker.Accept("{\"sessionId\":\"other\",\"seq\":1,\"stage\":\"deploy\",\"completed\":1,\"total\":2}"),"wrong session ignored");
  Check(tracker.Accept("{\"sessionId\":\""+session+"\",\"seq\":1,\"stage\":\"deploy\",\"completed\":1,\"total\":2}"),"valid progress");
  Check(tracker.Percent==50,"percent from actual values");
  Check(!tracker.Accept("{\"sessionId\":\""+session+"\",\"seq\":1,\"stage\":\"deploy\",\"completed\":2,\"total\":2}"),"repeated event ignored");
  Check(tracker.Accept("{\"sessionId\":\""+session+"\",\"seq\":2,\"stage\":\"deploy\",\"completed\":2,\"total\":2}"),"last deploy event");
  Check(tracker.Percent==99,"not 100 before verification");
  Check(!tracker.Completed(0,target),"missing marker and executable fail");
  Directory.CreateDirectory(target);File.WriteAllText(Path.Combine(target,"SSLocalModelManager.exe"),"stub");
  Check(!tracker.Completed(0,target),"missing end marker fail");
  Check(tracker.Accept("{\"sessionId\":\""+session+"\",\"seq\":3,\"stage\":\"installed\",\"completed\":1,\"total\":1}"),"end marker accepted");
  Check(!tracker.Completed(3,target),"failed process cannot succeed");
  Check(tracker.Completed(0,target),"exit, marker and executable validate");
  Console.WriteLine("PASS "+n+" checks");
 }
}

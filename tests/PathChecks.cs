using System;
using System.IO;
using SS.Core;
class PathChecks {
 static int count;
 static void Check(bool condition,string name){if(!condition)throw new Exception("FAIL "+name);count++;}
 static int Main(){try{
  string testRoot=Path.Combine(Path.GetTempPath(),"SS-path-tests-"+Guid.NewGuid().ToString("N"));
  Environment.SetEnvironmentVariable("SS_MODEL_DATA_ROOT",testRoot);
  AppPaths.Initialize();
  Check(AppPaths.DataRoot==Path.GetFullPath(testRoot),"explicit data root");
  Check(File.Exists(AppPaths.ModelsJson),"new registry");
  Check(AppPaths.DirectPort!=8081&&AppPaths.DirectPort!=8090,"independent default port");
  Check(AppPaths.DirectPort>=1024&&AppPaths.DirectPort<=65535,"port range");
  Check(AppPaths.DataRoot.IndexOf("MODLE",StringComparison.OrdinalIgnoreCase)<0,"no developer model path");
  string registry=File.ReadAllText(AppPaths.ModelsJson);
  AppPaths.Initialize();Check(File.ReadAllText(AppPaths.ModelsJson)==registry,"idempotent initialization");
  Check(AppPaths.Log("test.log").StartsWith(AppPaths.DataRoot+Path.DirectorySeparatorChar),"own log root");
  bool rejected=false;try{AppPaths.Log(".."+Path.DirectorySeparatorChar+"outside.log");}catch(ArgumentException){rejected=true;}
  Check(rejected,"log traversal rejected");
  rejected=false;try{AppPaths.ValidatePort(8081);}catch(ArgumentException){rejected=true;}
  Check(rejected,"local console port reserved");
  Check(!ServiceOwnership.Matches(System.Diagnostics.Process.GetCurrentProcess().Id,DateTime.MinValue,"missing.exe"),"foreign process refused");
  Console.WriteLine("PASS "+count+" isolation checks");return 0;
 }catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}}
}
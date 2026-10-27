using System;using System.IO;using System.Linq;using System.Reflection;using System.Security.Cryptography;
namespace SS.Core {public static class RuntimeTrust {
 public static void Require(string directory){
  string expected=typeof(RuntimeTrust).Assembly.GetCustomAttributes(typeof(AssemblyMetadataAttribute),false).Cast<AssemblyMetadataAttribute>().Where(x=>x.Key=="EngineManifestSha256").Select(x=>x.Value).SingleOrDefault();
  if(string.IsNullOrEmpty(expected))throw new InvalidOperationException("engine_not_bundled_or_not_trusted");
  string path=Path.Combine(directory,"engine-manifest.json");string actual;using(var s=SHA256.Create())using(var f=File.OpenRead(path))actual=BitConverter.ToString(s.ComputeHash(f)).Replace("-","").ToLowerInvariant();
  if(!string.Equals(expected,actual,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("engine_manifest_untrusted");
 }
}}
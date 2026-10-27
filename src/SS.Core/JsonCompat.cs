#if NET8_0_OR_GREATER
using System;using System.Collections.Generic;using System.Text.Json;
namespace System.Web.Script.Serialization {
 public sealed class JavaScriptSerializer {
  public int MaxJsonLength=2097152;
  static readonly JsonSerializerOptions Options=new JsonSerializerOptions{IncludeFields=true,PropertyNameCaseInsensitive=false};
  public string Serialize(object value){string json=JsonSerializer.Serialize(value,Options);if(json.Length>MaxJsonLength)throw new ArgumentException("JSON exceeds budget");return json;}
  public T Deserialize<T>(string json){if(typeof(T)==typeof(Dictionary<string,object>)||typeof(T)==typeof(object))return (T)DeserializeObject(json);if(json==null||json.Length>MaxJsonLength)throw new ArgumentException("JSON exceeds budget");return JsonSerializer.Deserialize<T>(json,Options);}
  public object DeserializeObject(string json){if(json==null||json.Length>MaxJsonLength)throw new ArgumentException("JSON exceeds budget");using(var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=64}))return ConvertNode(doc.RootElement);}
  static object ConvertNode(JsonElement e){
   switch(e.ValueKind){case JsonValueKind.Object:var d=new Dictionary<string,object>();foreach(var p in e.EnumerateObject())d[p.Name]=ConvertNode(p.Value);return d;
    case JsonValueKind.Array:var list=new List<object>();foreach(var v in e.EnumerateArray())list.Add(ConvertNode(v));return list.ToArray();
    case JsonValueKind.String:return e.GetString();case JsonValueKind.Number:long n;return e.TryGetInt64(out n)?(object)n:e.GetDouble();
    case JsonValueKind.True:return true;case JsonValueKind.False:return false;default:return null;}
  }
 }
}
#endif

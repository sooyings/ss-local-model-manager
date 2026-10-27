// Independent SS model registry and owned-service backend.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace ModelLauncher {

    // ------------------------------------------------------------------ 注册表
    internal sealed class ModelEntry {
        public string Id;          // 注册表键，如 qwen3.6-35b-a3b-uncensored
        public string Display;     // 完整展示名
        public string Alias;       // llama-server --alias，也是探测用的身份
        public string Short;       // 卡片上的短名
        public string Sub;         // 卡片副标题
        public string CtxText;
        public int Context;
        public long SizeBytes;

        public string SizeText {
            get {
                if (SizeBytes <= 0) return "";
                return String.Format("{0:0.0} GB", SizeBytes / 1e9);
            }
        }
    }

    internal sealed class Registry {
        public static readonly string Root = SS.Core.AppPaths.DataRoot;
        public static readonly string ModelsJson = Path.Combine(Root, @"config\models.json");
        public static readonly string ActiveJson = Path.Combine(Root, @"config\active_model.json");

        public static void Rename(string id, string name) {
            name = (name ?? "").Trim();
            if (name.Length == 0) throw new ArgumentException("模型名称不能为空");
            if (name.Length > 24) throw new ArgumentException("模型名称最多24个字符");
            JavaScriptSerializer ser = new JavaScriptSerializer();
            Dictionary<string, object> registry = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(ModelsJson, Encoding.UTF8));
            Dictionary<string, object> model = Obj(Obj(registry, "models"), id);
            if (model == null) throw new ArgumentException("模型不存在，请重新打开启动器");
            model["short"] = name;
            string backupDir = Path.Combine(Root, @"backup\launcher-renames");
            Directory.CreateDirectory(backupDir);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N");
            string temp = ModelsJson + "." + stamp + ".tmp";
            try {
                File.WriteAllText(temp, ser.Serialize(registry), new UTF8Encoding(false));
                File.Replace(temp, ModelsJson, Path.Combine(backupDir, "models_" + stamp + ".json"));
            } finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        static string Str(Dictionary<string, object> d, string key) {
            object v;
            if (d != null && d.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return "";
        }

        static int Int(Dictionary<string, object> d, string key) {
            object v;
            int n;
            if (d != null && d.TryGetValue(key, out v) && v != null
                && Int32.TryParse(Convert.ToString(v), out n)) return n;
            return 0;
        }

        static Dictionary<string, object> Obj(Dictionary<string, object> d, string key) {
            object v;
            if (d != null && d.TryGetValue(key, out v)) return v as Dictionary<string, object>;
            return null;
        }

        static long FileSize(string path) {
            try {
                FileInfo fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0;
            } catch { return 0; }
        }

        /// <summary>注册表 default 模型排最前，其余按 id 字典序 —— 保证卡片顺序稳定。</summary>
        public static List<ModelEntry> Load() {
            List<ModelEntry> list = new List<ModelEntry>();
            try {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> root =
                    ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(ModelsJson, Encoding.UTF8));
                if (root == null) return list;
                string def = Str(root, "default");
                Dictionary<string, object> models = Obj(root, "models");
                if (models == null) return list;

                List<string> keys = new List<string>(models.Keys);
                keys.Sort(StringComparer.Ordinal);
                keys.Remove(def);
                keys.Insert(0, def);

                foreach (string key in keys) {
                    Dictionary<string, object> m = Obj(models, key);
                    if (m == null) continue;
                    ModelEntry e = new ModelEntry();
                    e.Id = key;
                    e.Display = Str(m, "display");
                    e.Alias = Str(m, "alias");
                    e.Context = Int(m, "context");
                    e.CtxText = e.Context > 0 ? String.Format("{0}K 上下文", e.Context / 1024) : "";
                    e.Short = Str(m, "short");
                    if (e.Short.Length == 0) e.Short = DeriveShort(key);
                    e.Sub = Str(m, "sub");
                    if (e.Sub.Length == 0) e.Sub = e.CtxText;
                    string dir = Str(m, "dir");
                    string main = Str(m, "main");
                    if (dir.Length > 0 && main.Length > 0)
                        e.SizeBytes = FileSize(Path.Combine(Path.Combine(Root, dir), main));
                    list.Add(e);
                }
            } catch {
                // 注册表读不到不该让程序打不开 —— 留空列表，界面会提示
            }
            return list;
        }

        /// <summary>没有 short 字段时的兜底：取 id 第一段并首字母大写。</summary>
        static string DeriveShort(string id) {
            if (String.IsNullOrEmpty(id)) return "?";
            int cut = id.IndexOf('-');
            string head = cut > 0 ? id.Substring(0, cut) : id;
            if (head.Length == 0) return id;
            return Char.ToUpperInvariant(head[0]) + head.Substring(1);
        }

        /// <summary>当前默认模型 id；读不到返回空串。</summary>
        public static string ActiveId() {
            try {
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> d =
                    ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(ActiveJson, Encoding.UTF8));
                return d == null ? "" : Str(d, "id");
            } catch { return ""; }
        }
    }

    // ------------------------------------------------------------------ 状态快照
    internal sealed class Snapshot {
        public bool Model;         // llama-server /health = ok
        public bool Router;
        public bool Loading;       // /health = loading model
        public string Mode = "local";
        public string RunningAlias = "";   // 独立接口实际自报的模型名（权威）
        public double Used, Total;

        public bool Any { get { return Model || Router || Loading; } }
        public bool Ready { get { return Model && Router; } }
    }

    // ------------------------------------------------------------------ 后端
    internal static class Backend {
        public static Snapshot Probe() {
            Snapshot s=new Snapshot(); if(SS.Core.NativeHost.RunningAlias()=="")return s;
            try {var request=(HttpWebRequest)WebRequest.Create(SS.Core.AppPaths.DirectBaseUrl+"/health");request.Proxy=null;request.Timeout=1200;
             using(var response=request.GetResponse())using(var reader=new StreamReader(response.GetResponseStream())){
              var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(reader.ReadToEnd());string state=d.ContainsKey("status")?Convert.ToString(d["status"]):"";
              s.Model=state=="ok";s.Loading=state.IndexOf("loading",StringComparison.OrdinalIgnoreCase)>=0;
             }
             s.RunningAlias=SS.Core.NativeHost.RunningAlias();s.Router=s.Model;
            }catch{}
            return s;
        }
        public static void Run(string action,string modelId){
            if(action=="start")SS.Core.NativeHost.Start(modelId);
            else if(action=="stop")SS.Core.NativeHost.Stop();
            else throw new ArgumentException("Invalid action");
        }
        public static void Use(string modelId){SS.Core.NativeHost.Use(modelId);}
    }

}
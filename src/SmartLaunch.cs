using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace DSHLauncher
{
    /// <summary>
    /// 智能启动: 遍历 apps/cli 的依赖闭包(含 devDeps), 比较每个 workspace
    /// 包的 src/ 与 lib/ 新鲜度。全部新鲜 -> 内置产物(快); 否则 -> tsx 源码。
    /// </summary>
    public static class SmartLaunch
    {
        public static string DecideMode(string repoRoot, out List<string> reasons)
        {
            reasons = StaleReasons(repoRoot);
            string builtBin = Path.Combine(repoRoot, "apps", "cli", "lib", "bin.js");
            if (!File.Exists(builtBin)) return "missing-build";
            if (reasons.Count > 0) return "source";
            return "built";
        }

        private static List<string> StaleReasons(string repoRoot)
        {
            List<string> reasons = new List<string>();
            foreach (string dir in DependencyClosure(repoRoot))
            {
                string srcDir = Path.Combine(dir, "src");
                if (!Directory.Exists(srcDir)) continue;
                Dictionary<string, object> doc = ReadJson(Path.Combine(dir, "package.json"));
                if (doc == null) continue;
                if (GetString(doc, "main") == null && !doc.ContainsKey("exports")) continue;
                // 无 main 且 exports 只暴露 ./dist/*(前端 app): 运行时产物是 dist/ 而非 lib/
                string artifactDir = "lib";
                if (GetString(doc, "main") == null && IsDistOnlyExports(doc)) artifactDir = "dist";
                DateTime srcM = NewestMtime(srcDir);
                if (srcM == DateTime.MinValue) continue;
                string outDir = Path.Combine(dir, artifactDir);
                if (!Directory.Exists(outDir))
                {
                    reasons.Add(dir + ": no " + artifactDir + "/ build output");
                    continue;
                }
                DateTime libM = NewestMtime(outDir);
                if (libM == DateTime.MinValue) reasons.Add(dir + ": empty " + artifactDir + "/");
                else if (srcM > libM) reasons.Add(dir + ": src/ newer than " + artifactDir + "/");
            }
            return reasons;
        }

        private static List<string> DependencyClosure(string repoRoot)
        {
            Dictionary<string, string> members = new Dictionary<string, string>();
            foreach (string manifest in WorkspaceManifests(repoRoot))
            {
                Dictionary<string, object> doc = ReadJson(manifest);
                string name = GetString(doc, "name");
                if (name != null) members[name] = Path.GetDirectoryName(manifest);
            }
            HashSet<string> seen = new HashSet<string>();
            Queue<string> queue = new Queue<string>();
            queue.Enqueue(Path.Combine(repoRoot, "apps", "cli"));
            while (queue.Count > 0)
            {
                string dir = queue.Dequeue();
                if (!seen.Add(dir)) continue;
                Dictionary<string, object> doc = ReadJson(Path.Combine(dir, "package.json"));
                if (doc == null) continue;
                string[] keys = { "dependencies", "peerDependencies", "devDependencies" };
                foreach (string key in keys)
                {
                    object depsObj;
                    Dictionary<string, object> deps = null;
                    if (doc.TryGetValue(key, out depsObj)) deps = depsObj as Dictionary<string, object>;
                    if (deps == null) continue;
                    foreach (string name in deps.Keys)
                    {
                        string target;
                        if (members.TryGetValue(name, out target)) queue.Enqueue(target);
                    }
                }
            }
            return new List<string>(seen);
        }

        private static List<string> WorkspaceManifests(string repoRoot)
        {
            List<string> found = new List<string>();
            Collect(Path.Combine(repoRoot, "apps"), found);
            string pkgs = Path.Combine(repoRoot, "packages");
            if (Directory.Exists(pkgs))
            {
                foreach (string group in Directory.GetDirectories(pkgs))
                {
                    Collect(group, found);
                }
            }
            Collect(Path.Combine(repoRoot, "vendor"), found);
            return found;
        }

        private static void Collect(string baseDir, List<string> found)
        {
            if (!Directory.Exists(baseDir)) return;
            foreach (string dir in Directory.GetDirectories(baseDir))
            {
                if (Path.GetFileName(dir) == "node_modules") continue;
                string pj = Path.Combine(dir, "package.json");
                if (File.Exists(pj)) found.Add(pj);
            }
        }

        private static Dictionary<string, object> ReadJson(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                return ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            }
            catch { return null; }
        }

        private static string GetString(Dictionary<string, object> doc, string key)
        {
            if (doc == null) return null;
            object v;
            if (doc.TryGetValue(key, out v) && v is string) return (string)v;
            return null;
        }

        private static bool IsDistOnlyExports(Dictionary<string, object> doc)
        {
            object exportsObj;
            if (!doc.TryGetValue("exports", out exportsObj)) return false;
            Dictionary<string, object> map = exportsObj as Dictionary<string, object>;
            if (map == null || map.Count == 0) return false;
            foreach (string key in map.Keys)
            {
                if (key.StartsWith("./dist") || key == "./package.json") continue;
                return false;
            }
            return true;
        }

        private static DateTime NewestMtime(string dir)
        {
            DateTime max = DateTime.MinValue;
            WalkFiles(dir, ref max);
            return max;
        }

        // 手动递归: 不进入 node_modules, 不跟随 reparse point(pnpm 链接环),
        // 每个目录单独 try/catch, 单目录异常不影响整体
        private static void WalkFiles(string dir, ref DateTime max)
        {
            string[] files;
            try { files = Directory.GetFiles(dir); } catch { return; }
            foreach (string file in files)
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".map") || name.EndsWith(".tsbuildinfo")) continue;
                try
                {
                    DateTime m = File.GetLastWriteTime(file);
                    if (m > max) max = m;
                }
                catch { }
            }
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); } catch { return; }
            foreach (string sub in subdirs)
            {
                string name = Path.GetFileName(sub);
                if (name == "node_modules") continue;
                try
                {
                    FileAttributes attrs = File.GetAttributes(sub);
                    if ((attrs & FileAttributes.ReparsePoint) != 0) continue;
                }
                catch { continue; }
                WalkFiles(sub, ref max);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace DSHLauncher
{
    /// <summary>无界面自检: DSHLauncher.exe --selftest，结果写 exe 同目录日志。</summary>
    internal static class Selftest
    {
        public static void Run()
        {
            AppLog.Write("SelfTest 开始, 目录: " + AppDomain.CurrentDomain.BaseDirectory);
            AppLog.Write("node: " + (HarnessService.FindOnPath("node.exe") ?? "NOT FOUND"));
            AppLog.Write("git: " + (HarnessService.FindOnPath("git.exe") ?? "NOT FOUND"));
            AppLog.Write("pnpm: " + (HarnessService.FindOnPath("pnpm.cmd") ?? "NOT FOUND"));
            AppLog.Write("端口 3080 监听: " + HarnessService.PortOpen(3080));
            string repo = LoadConfigRepo();
            if (repo.Length == 0)
            {
                AppLog.Write("未配置仓库目录，跳过智能启动模式检查");
            }
            else
            {
                List<string> reasons;
                string mode = SmartLaunch.DecideMode(repo, out reasons);
                AppLog.Write("智能启动模式: " + mode);
                foreach (string r in reasons) AppLog.Write("  stale: " + r);
            }
            AppLog.Write("日志路径: " + AppLog.LogFilePath);
            AppLog.Write("SelfTest 完成");
        }

        private static string LoadConfigRepo()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "DSHLauncher", "config.json");
                if (!File.Exists(path)) return "";
                JavaScriptSerializer ser = new JavaScriptSerializer();
                Dictionary<string, object> cfg = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                if (cfg != null && cfg.ContainsKey("repoRoot") && cfg["repoRoot"] is string)
                {
                    return (string)cfg["repoRoot"];
                }
            }
            catch { }
            return "";
        }
    }
}

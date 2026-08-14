using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DSHLauncher
{
    /// <summary>更新流程: init/fetch/代理探测/SSL 重试/落后统计/拉取(zip 副本 reset+checkout)。</summary>
    public static class GitUpdater
    {
        private const string OriginUrl = "https://github.com/deepseek-ai/deepseek-harness.git";

        public static void Run(MainForm form, string repo)
        {
            Task.Run(delegate { RunCore(form, repo); });
        }

        private static void RunCore(MainForm form, string repo)
        {
            if (HarnessService.FindOnPath("git.exe") == null)
            {
                form.Ui(delegate { form.Status("未找到 git，无法检查更新", "Red"); });
                return;
            }
            form.Ui(delegate { form.Status("正在检查更新...", "DarkOrange"); });

            // 1. 确保是 git 仓库
            if (!Directory.Exists(Path.Combine(repo, ".git")))
            {
                DialogResult go = form.Ask("当前目录不是 git 仓库。\n" + repo +
                    "\n\n将执行 git init 并添加 origin 远程（不会改动现有文件），以便后续检查更新。是否继续？",
                    "初始化 git", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (go != DialogResult.Yes) { form.Ui(delegate { form.Status("已取消更新检查", "Black"); }); return; }
                string tmp;
                RunGit(repo, "init", false, "", out tmp);
                string remotes;
                RunGit(repo, "remote", false, "", out remotes);
                if (!remotes.Contains("origin"))
                {
                    RunGit(repo, "remote add origin " + OriginUrl, true, "git: ", out tmp);
                }
                AppLog.Write("已初始化 git 并添加 origin 远程");
            }

            // 2. fetch(直连, 10s 连接超时)
            string output;
            AppLog.Write("git fetch origin ...");
            int code = RunGit(repo, "-c http.connectTimeout=10 -c https.connectTimeout=10 fetch origin", true, "fetch: ", out output);
            if (code != 0)
            {
                // 2a. 代理重试
                string proxy = FindProxyForGit(repo);
                if (proxy != null)
                {
                    DialogResult ok = form.Ask("git fetch 失败（可能是网络直连被限制）。\n检测到可用的本地代理: " + proxy +
                        "\n是否通过该代理重试？", "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (ok == DialogResult.Yes)
                    {
                        code = RunGit(repo, "-c \"http.proxy=" + proxy + "\" -c \"https.proxy=" + proxy + "\" fetch origin", true, "fetch(proxy): ", out output);
                        if (code == 0)
                        {
                            AppLog.Write("代理重试成功");
                            DialogResult persist = form.Ask("已通过代理获取更新成功。\n\n是否将代理写入 git 全局配置，今后免检测？\ngit config --global http.proxy " + proxy,
                                "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                            if (persist == DialogResult.Yes)
                            {
                                RunGit(repo, "config --global http.proxy " + proxy, false, "", out output);
                                RunGit(repo, "config --global https.proxy " + proxy, false, "", out output);
                                AppLog.Write("已写入 git 全局代理配置");
                            }
                        }
                        else
                        {
                            form.Ui(delegate { form.Status("git fetch 仍失败，见日志", "Red"); });
                            return;
                        }
                    }
                    else
                    {
                        form.Ui(delegate { form.Status("已取消（git fetch 失败）", "Red"); });
                        return;
                    }
                }
                // 2b. SSL 忽略证书重试
                else if (output != null && (output.Contains("SSL") || output.Contains("certificate")))
                {
                    DialogResult ok = form.Ask("git 证书校验失败（SSL）。\n常见原因: 代理/VPN 拦截了 TLS、或本机 git 缺少 CA 证书。\n\n是否用\"忽略证书校验\"重试一次？\n（仅建议在可信网络环境使用）",
                        "检查更新", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (ok == DialogResult.Yes)
                    {
                        code = RunGit(repo, "-c http.sslVerify=false fetch origin", true, "fetch(retry): ", out output);
                        if (code != 0) { form.Ui(delegate { form.Status("git fetch 仍失败，见日志", "Red"); }); return; }
                    }
                    else { form.Ui(delegate { form.Status("已取消（git fetch 失败）", "Red"); }); return; }
                }
                else
                {
                    form.Ui(delegate { form.Status("git fetch 失败（网络或凭据问题），见日志", "Red"); });
                    return;
                }
            }

            // 3. 本地/远端状态
            string headOut;
            int hc = RunGit(repo, "rev-parse --verify HEAD", false, "", out headOut);
            bool headExists = (hc == 0);

            string branch = "master";
            string branchOut;
            int bc = RunGit(repo, "symbolic-ref --short HEAD", false, "", out branchOut);
            if (bc == 0 && branchOut.Trim().Length > 0) branch = branchOut.Trim();

            string upstream = "";
            string upstreamOut;
            int uc = RunGit(repo, "rev-parse --abbrev-ref --symbolic-full-name @{u}", false, "", out upstreamOut);
            if (uc == 0) upstream = upstreamOut.Trim();
            if (upstream.Length == 0)
            {
                string[] candidates = { "origin/" + branch, "origin/master", "origin/main" };
                foreach (string cand in candidates)
                {
                    string so;
                    int sc2 = RunGit(repo, "show-ref --verify refs/remotes/" + cand, false, "", out so);
                    if (sc2 == 0) { upstream = cand; break; }
                }
            }
            if (upstream.Length == 0)
            {
                form.Ui(delegate { form.Status("未能确定远端分支，见日志", "Red"); });
                AppLog.Write("git fetch 完成，但无法确定上游分支");
                return;
            }

            string so2;
            string localSha = "(无提交)";
            int lc = RunGit(repo, "rev-parse --short HEAD", false, "", out so2);
            if (lc == 0) localSha = so2.Trim();
            string remoteSha = "";
            int rc = RunGit(repo, "rev-parse --short " + upstream, false, "", out so2);
            if (rc == 0) remoteSha = so2.Trim();

            int behind = 0;
            int ahead = 0;
            string countOut;
            int cc = RunGit(repo, "rev-list --count HEAD.." + upstream, false, "", out countOut);
            if (cc == 0) int.TryParse(countOut.Trim(), out behind);
            int ac = RunGit(repo, "rev-list --count " + upstream + "..HEAD", false, "", out countOut);
            if (ac == 0) int.TryParse(countOut.Trim(), out ahead);

            AppLog.Write(string.Format("本地 {0} / 远端 {1} / 落后 {2} / 领先 {3}", localSha, remoteSha, behind, ahead));

            // 4. 更新决策
            if (!headExists)
            {
                form.Ui(delegate { form.Status("远端最新: " + remoteSha + " (" + upstream + ")", "DarkOrange"); });
                DialogResult go = form.Ask("本地副本还没有提交记录（例如从 zip 解压的副本）。\n远端最新提交: " + remoteSha + " (" + upstream + ")" +
                    "\n\n确认后本地文件将被替换为远端版本（远端不存在的本地文件会保留）。\n是否继续？",
                    "获取更新", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (go == DialogResult.Yes)
                {
                    int r1 = RunGit(repo, "reset --hard " + upstream, true, "git: ", out output);
                    if (r1 == 0)
                    {
                        int r2 = RunGit(repo, "checkout -B " + branch + " " + upstream, true, "git: ", out output);
                        if (r2 == 0)
                        {
                            form.Ui(delegate { form.Status("更新完成，请重启服务", "Green"); });
                            AppLog.Write("已更新到远端版本，请重新启动服务以生效");
                        }
                        else
                        {
                            AppLog.Write("创建分支失败: " + output.Trim());
                            form.Ui(delegate { form.Status("更新失败，见日志", "Red"); });
                        }
                    }
                    else
                    {
                        AppLog.Write("更新失败(reset): " + output.Trim());
                        form.Ui(delegate { form.Status("更新失败，见日志", "Red"); });
                    }
                }
            }
            else if (behind > 0)
            {
                form.Ui(delegate { form.Status("发现 " + behind + " 个新提交", "DarkOrange"); });
                DialogResult go = form.Ask(string.Format("检测到 {0} 个新提交（本地领先 {1} 个）。\n本地: {2}\n远端: {3} ({4})\n\n是否拉取更新？",
                    behind, ahead, localSha, remoteSha, upstream), "获取更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (go == DialogResult.Yes)
                {
                    int p = RunGit(repo, "pull --ff-only", true, "git: ", out output);
                    if (p == 0)
                    {
                        form.Ui(delegate { form.Status("更新完成，请重启服务", "Green"); });
                        AppLog.Write("更新完成，请重新启动服务以生效");
                    }
                    else
                    {
                        AppLog.Write("git pull 失败: " + output.Trim());
                        form.Ui(delegate { form.Status("拉取失败，见日志", "Red"); });
                    }
                }
            }
            else
            {
                AppLog.Write("已是最新");
                form.Ui(delegate { form.Status("已是最新 (" + localSha + ")", "Green"); });
            }
        }

        private static string FindProxyForGit(string repo)
        {
            string[] ports = { "7897", "7890", "1080", "2080", "8888", "10809" };
            string env = Environment.GetEnvironmentVariable("DSH_HTTP_PROXY");
            int count = ports.Length + (string.IsNullOrEmpty(env) ? 0 : 1);
            string[] candidates = new string[count];
            int idx = 0;
            if (!string.IsNullOrEmpty(env)) candidates[idx++] = env;
            foreach (string port in ports) candidates[idx++] = "http://127.0.0.1:" + port;
            for (int i = 0; i < idx; i++)
            {
                string proxy = candidates[i];
                if (!proxy.StartsWith("http")) continue;
                AppLog.Write("探测代理: " + proxy);
                string output;
                int code = RunGit(repo,
                    "-c \"http.proxy=" + proxy + "\" -c \"https.proxy=" + proxy + "\" -c http.connectTimeout=5 -c https.connectTimeout=5 ls-remote " + OriginUrl + " HEAD",
                    false, "", out output);
                if (code == 0)
                {
                    AppLog.Write("代理可用: " + proxy);
                    return proxy;
                }
            }
            return null;
        }

        private static int RunGit(string repo, string arguments, bool logLines, string prefix, out string output)
        {
            string git = HarnessService.FindOnPath("git.exe");
            if (git == null) { output = ""; return -1; }
            return RunProcess(git, arguments, repo, logLines, prefix, out output);
        }

        private static int RunProcess(string exe, string args, string workDir, bool logLines, string prefix, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe, args);
            psi.WorkingDirectory = workDir;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            using (Process p = Process.Start(psi))
            {
                Task<string> errTask = p.StandardError.ReadToEndAsync();
                string stdout = p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                string stderr = errTask.Result;
                output = stdout + stderr;
                if (logLines && output.Length > 0)
                {
                    foreach (string line in output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        AppLog.Write(prefix + line.Trim());
                    }
                }
                return p.ExitCode;
            }
        }
    }
}

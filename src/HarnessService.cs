using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace DSHLauncher
{
    /// <summary>服务进程管理: 智能模式选择、启停、健康轮询、自动开页。</summary>
    public class HarnessService
    {
        public string RepoRoot = "";
        public int Port = 3080;
        public Action OnReady; // 服务就绪(UI 线程外回调, 需要自行 Invoke)
        public Func<int, bool> ConfirmKillExternal; // 确认终止外部进程(pid -> bool)

        private Process _proc;
        private Timer _healthTimer;
        private int _healthStarted;

        public bool IsRunning { get { return _proc != null && !_proc.HasExited; } }

        public static bool PortOpen(int port)
        {
            try
            {
                using (TcpClient c = new TcpClient())
                {
                    IAsyncResult ar = c.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(400)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        public void Start()
        {
            if (IsRunning) { AppLog.Write("服务进程已在运行"); return; }
            if (PortOpen(Port))
            {
                int owner = FindPortOwnerPid(Port);
                AppLog.Write("端口 " + Port + " 已有服务在监听 (pid " + owner + ")，视为已在运行");
                if (OnReady != null) OnReady();
                return;
            }
            string node = FindOnPath("node.exe");
            if (node == null) { AppLog.Write("未找到 node，请先安装 Node.js"); return; }

            List<string> reasons;
            string mode = SmartLaunch.DecideMode(RepoRoot, out reasons);
            string args;
            if (mode == "built")
            {
                args = "\"" + Path.Combine(RepoRoot, "apps", "cli", "lib", "bin.js") + "\" web --port " + Port;
                AppLog.Write("智能启动: 使用内置产物 (快速)");
            }
            else if (mode == "source")
            {
                args = "--import tsx/esm \"" + Path.Combine(RepoRoot, "apps", "cli", "src", "bin.ts") + "\" web --port " + Port;
                AppLog.Write("智能启动: 源码较新，使用 tsx 源码模式 (" + reasons.Count + " 个包过期)");
            }
            else
            {
                AppLog.Write("缺少构建产物 (apps/cli/lib/bin.js)，请先点击\"构建\"");
                return;
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = node;
            psi.Arguments = args;
            psi.WorkingDirectory = RepoRoot;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            Process p = new Process();
            p.StartInfo = psi;
            p.EnableRaisingEvents = true;
            p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) AppLog.Write(e.Data); };
            p.ErrorDataReceived += delegate(object s, DataReceivedEventArgs e) { if (e.Data != null) AppLog.Write(e.Data); };
            p.Exited += delegate
            {
                try { AppLog.Write("服务进程已退出 (code " + p.ExitCode + ")"); } catch { AppLog.Write("服务进程已退出"); }
                _proc = null;
            };
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                AppLog.Write("启动失败: " + ex.Message);
                return;
            }
            _proc = p;
            AppLog.Write("已启动: node " + args);
            _healthStarted = Environment.TickCount;
            _healthTimer = new Timer(delegate { HealthTick(); }, null, 0, 500);
        }

        private void HealthTick()
        {
            if (_proc == null) { StopTimer(); return; }
            if (_proc.HasExited) { StopTimer(); return; }
            if (PortOpen(Port))
            {
                StopTimer();
                AppLog.Write("服务就绪: http://127.0.0.1:" + Port);
                if (OnReady != null) OnReady();
            }
            else if (Environment.TickCount - _healthStarted > 300000)
            {
                StopTimer();
                AppLog.Write("服务启动超时（5 分钟）");
            }
        }

        private void StopTimer()
        {
            if (_healthTimer != null) { _healthTimer.Dispose(); _healthTimer = null; }
        }

        public void Stop()
        {
            // 1) 本启动器拉起的进程树
            if (_proc != null)
            {
                try
                {
                    KillTree(_proc.Id);
                    AppLog.Write("已请求停止服务 (pid " + _proc.Id + ")");
                }
                catch (Exception ex) { AppLog.Write("停止失败: " + ex.Message); }
                _proc = null;
                return;
            }
            // 2) 端口仍被占用: 外部进程(如 cmd 里 npx dsh web)占着端口
            if (PortOpen(Port))
            {
                int pid = FindPortOwnerPid(Port);
                if (pid <= 0 || pid == System.Diagnostics.Process.GetCurrentProcess().Id)
                {
                    AppLog.Write("端口 " + Port + " 被占用但无法确定进程，请手动关闭");
                    return;
                }
                if (ConfirmKillExternal != null && !ConfirmKillExternal(pid))
                {
                    AppLog.Write("已取消停止外部进程 (pid " + pid + ")");
                    return;
                }
                try
                {
                    KillTree(pid);
                    AppLog.Write("已请求停止外部服务进程 (pid " + pid + ")");
                }
                catch (Exception ex) { AppLog.Write("停止失败: " + ex.Message); }
                return;
            }
            AppLog.Write("没有正在运行的服务进程");
        }

        private static void KillTree(int pid)
        {
            ProcessStartInfo psi = new ProcessStartInfo("taskkill.exe", "/PID " + pid + " /T /F");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            Process kill = Process.Start(psi);
            kill.WaitForExit(10000);
        }

        /// <summary>netstat -ano 找到监听指定端口的进程 pid; 0 表示未找到。</summary>
        public static int FindPortOwnerPid(int port)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("netstat.exe", "-ano");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                using (Process p = Process.Start(psi))
                {
                    string output = p.StandardOutput.ReadToEnd();
                    p.WaitForExit();
                    string suffix = ":" + port;
                    foreach (string line in output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string t = line.Trim();
                        if (t.IndexOf("LISTENING", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        string[] parts = t.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length < 5) continue;
                        if (!parts[1].EndsWith(suffix)) continue;
                        int pid;
                        if (int.TryParse(parts[parts.Length - 1], out pid)) return pid;
                    }
                }
            }
            catch (Exception ex) { AppLog.Write("netstat 失败: " + ex.Message); }
            return 0;
        }

        public static string FindOnPath(string exe)
        {
            string path = Environment.GetEnvironmentVariable("PATH");
            if (path == null) return null;
            foreach (string dir in path.Split(';'))
            {
                if (dir.Length == 0) continue;
                try
                {
                    string full = Path.Combine(dir, exe);
                    if (File.Exists(full)) return full;
                }
                catch { }
            }
            return null;
        }
    }
}

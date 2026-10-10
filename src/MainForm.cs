using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DSHLauncher
{
    public class MainForm : Form
    {
        private const string DefaultRepo = "";
        private const string Version = "2.1.1";

        private TextBox _txtRepo;
        private Button _btnStart, _btnStop, _btnUpdate, _btnBuild, _btnClean, _btnOpen, _btnShortcut;
        private Label _status;
        private RichTextBox _log;
        private string _repoRoot = DefaultRepo;
        private int _port = 3080;
        private HarnessService _service = new HarnessService();
        private bool _busy;

        public MainForm()
        {
            LoadConfig();
            BuildUi();
            AppLog.Attach(_log);
            AppLog.Write("启动器进程启动 v" + Version);
            _service.OnReady = delegate { Ui(delegate { OpenPage(); Status("服务已启动: http://127.0.0.1:" + _port, "Green"); _btnOpen.Enabled = true; }); };
            _service.ConfirmKillExternal = delegate(int pid)
            {
                return Ask("端口 " + _port + " 由外部进程 (pid " + pid + ") 占用，\n" +
                    "将终止该进程及其子进程树。\n是否确认停止？", "停止服务",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
            };
        }

        // ---------- UI ----------
        private void BuildUi()
        {
            Text = "DSH 启动器";
            Size = new Size(680, 660);
            MinimumSize = new Size(620, 540);
            StartPosition = FormStartPosition.CenterScreen;
            string ico = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DSHLauncher.ico");
            try { if (File.Exists(ico)) Icon = new Icon(ico); } catch { }

            Label lblRepo = new Label();
            lblRepo.Text = "仓库目录:";
            lblRepo.Location = new Point(12, 16);
            lblRepo.AutoSize = true;

            _txtRepo = new TextBox();
            _txtRepo.Text = _repoRoot;
            _txtRepo.Location = new Point(90, 12);
            _txtRepo.Width = 440;

            Button btnBrowse = new Button();
            btnBrowse.Text = "浏览...";
            btnBrowse.Location = new Point(540, 10);
            btnBrowse.Click += delegate
            {
                FolderBrowserDialog dlg = new FolderBrowserDialog();
                dlg.Description = "选择 deepseek-harness 仓库目录";
                dlg.SelectedPath = _repoRoot;
                if (dlg.ShowDialog(this) == DialogResult.OK) _txtRepo.Text = dlg.SelectedPath;
            };

            FlowLayoutPanel flow = new FlowLayoutPanel();
            flow.Location = new Point(12, 45);
            flow.Size = new Size(640, 88);
            flow.FlowDirection = FlowDirection.LeftToRight;

            _btnStart = MakeButton("启动服务", 100, delegate { StartService(); });
            _btnStop = MakeButton("停止服务", 100, delegate { _service.Stop(); });
            _btnUpdate = MakeButton("检查/获取更新", 130, delegate
            {
                string repo = RepoRootFromUi();
                if (repo != null) GitUpdater.Run(this, repo);
            });
            _btnBuild = MakeButton("构建 (pnpm run build)", 170, delegate { StartBuild(); });
            _btnClean = MakeButton("修复 (pnpm run clean)", 160, delegate { StartClean(); });
            _btnOpen = MakeButton("打开页面", 100, delegate
            {
                if (HarnessService.PortOpen(_port)) OpenPage();
                else MessageBox.Show(this, "服务未启动，请先点击\"启动服务\"。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            _btnOpen.Enabled = false;
            _btnShortcut = MakeButton("创建桌面快捷方式", 150, delegate { CreateShortcut(); });

            flow.Controls.Add(_btnStart);
            flow.Controls.Add(_btnStop);
            flow.Controls.Add(_btnUpdate);
            flow.Controls.Add(_btnBuild);
            flow.Controls.Add(_btnClean);
            flow.Controls.Add(_btnOpen);
            flow.Controls.Add(_btnShortcut);

            _status = new Label();
            _status.Location = new Point(12, 142);
            _status.AutoSize = true;
            _status.Text = "就绪";

            _log = new RichTextBox();
            _log.Location = new Point(12, 168);
            _log.Size = new Size(640, 424);
            _log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            _log.Font = new Font("Consolas", 9f);
            _log.ReadOnly = true;
            _log.WordWrap = true;
            _log.ScrollBars = RichTextBoxScrollBars.ForcedVertical;
            _log.BorderStyle = BorderStyle.FixedSingle;
            _log.BackColor = Color.FromArgb(250, 250, 250);

            Controls.Add(lblRepo);
            Controls.Add(_txtRepo);
            Controls.Add(btnBrowse);
            Controls.Add(flow);
            Controls.Add(_status);
            Controls.Add(_log);

            FormClosing += delegate(object s, FormClosingEventArgs e)
            {
                if (_service.IsRunning)
                {
                    DialogResult r = MessageBox.Show(this, "服务仍在运行，是否同时停止？", "退出",
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) e.Cancel = true;
                    else if (r == DialogResult.Yes) _service.Stop();
                }
                SaveConfig();
            };

            Shown += delegate
            {
                if (_repoRoot.Length == 0 && !PickRepo())
                {
                    Status("请先选择 deepseek-harness 仓库目录", "Red");
                    return;
                }
                _txtRepo.Text = _repoRoot;
                AppLog.Write("仓库目录: " + _repoRoot + ", URL: http://127.0.0.1:" + _port);
                AppLog.Write("node: " + (HarnessService.FindOnPath("node.exe") ?? "NOT FOUND"));
                AppLog.Write("git: " + (HarnessService.FindOnPath("git.exe") ?? "NOT FOUND"));
                AppLog.Write("pnpm: " + (HarnessService.FindOnPath("pnpm.cmd") ?? "NOT FOUND"));
                CheckMode();
            };
        }

        private Button MakeButton(string text, int width, EventHandler handler)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = width;
            b.Click += delegate(object s, EventArgs e)
            {
                try { handler(s, e); }
                catch (Exception ex) { AppLog.Write("操作出错: " + ex.Message); Status("操作出错，见日志", "Red"); }
            };
            return b;
        }

        // ---------- actions ----------
        private string RepoRootFromUi()
        {
            string candidate = _txtRepo.Text.Trim();
            if (candidate.Length == 0)
            {
                if (_repoRoot.Length == 0)
                {
                    if (!PickRepo())
                    {
                        Status("请先选择 deepseek-harness 仓库目录", "Red");
                        return null;
                    }
                    _txtRepo.Text = _repoRoot;
                }
                return _repoRoot;
            }
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "apps", "cli", "src", "bin.ts")))
            {
                _repoRoot = candidate;
                return _repoRoot;
            }
            Status("无效的仓库目录: " + candidate, "Red");
            return null;
        }

        private bool PickRepo()
        {
            FolderBrowserDialog dlg = new FolderBrowserDialog();
            dlg.Description = "选择 deepseek-harness 仓库目录";
            if (dlg.ShowDialog(this) != DialogResult.OK) return false;
            string p = dlg.SelectedPath;
            if (Directory.Exists(Path.Combine(p, "apps", "cli", "src")))
            {
                _repoRoot = p;
                return true;
            }
            MessageBox.Show(this, "所选目录不是 deepseek-harness 仓库。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private void StartService()
        {
            string repo = RepoRootFromUi();
            if (repo == null) return;
            _service.RepoRoot = repo;
            _service.Port = _port;
            _service.Start();
            Status("服务启动中...", "DarkOrange");
        }

        private void OpenPage()
        {
            try
            {
                Process.Start("http://127.0.0.1:" + _port);
                AppLog.Write("已在浏览器打开: http://127.0.0.1:" + _port);
            }
            catch (Exception ex) { AppLog.Write("打开页面失败: " + ex.Message); }
        }

        private void CheckMode()
        {
            string repo = _repoRoot;
            Task.Run(delegate
            {
                List<string> reasons;
                string mode = SmartLaunch.DecideMode(repo, out reasons);
                Ui(delegate
                {
                    if (mode == "built") Status("就绪（将使用内置产物启动，快速）: http://127.0.0.1:" + _port, "Green");
                    else if (mode == "source") Status("就绪（源码较新，将使用 tsx 源码模式，共 " + reasons.Count + " 个包过期）: http://127.0.0.1:" + _port, "DarkOrange");
                    else Status("缺少构建产物，请先点击\"构建\"", "Red");
                });
                foreach (string r in reasons) AppLog.Write("stale: " + r);
            });
        }

        // ---------- build ----------
        private void StartBuild()
        {
            string repo = RepoRootFromUi();
            if (repo == null) return;
            if (_busy) { AppLog.Write("已有构建/清理任务在运行"); return; }
            _busy = true;
            _btnBuild.Enabled = false;
            _btnClean.Enabled = false;
            Status("构建中... (pnpm run build)", "DarkOrange");
            AppLog.Write("开始构建: pnpm run build (仓库: " + repo + ")");
            Task.Run(delegate
            {
                bool ok = RunPnpm(repo, "build");
                Ui(delegate
                {
                    _busy = false;
                    _btnBuild.Enabled = true;
                    _btnClean.Enabled = true;
                    if (ok)
                    {
                        Status("构建完成", "Green");
                        AppLog.Write("pnpm run build 完成");
                        MessageBox.Show(this, "pnpm run build 完成。\n如无报错，下次启动将使用内置产物（快速模式）。", "构建完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        CheckMode();
                    }
                    else
                    {
                        Status("构建失败，可尝试\"修复\"", "Red");
                        AppLog.Write("pnpm run build 失败（可点击\"修复 (pnpm run clean)\"清理构建残留后重试）");
                        DialogResult fix = MessageBox.Show(this,
                            "pnpm run build 失败。\n\n是否运行 pnpm run clean 清理构建残留后重试？",
                            "构建失败", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (fix == DialogResult.Yes) StartClean();
                    }
                });
            });
        }

        private void StartClean()
        {
            string repo = RepoRootFromUi();
            if (repo == null) return;
            if (_busy) { AppLog.Write("已有构建/清理任务在运行"); return; }
            _busy = true;
            _btnBuild.Enabled = false;
            _btnClean.Enabled = false;
            Status("清理中... (pnpm run clean)", "DarkOrange");
            AppLog.Write("开始清理: pnpm run clean (仓库: " + repo + ")");
            Task.Run(delegate
            {
                bool ok = RunPnpm(repo, "clean");
                Ui(delegate
                {
                    _busy = false;
                    _btnBuild.Enabled = true;
                    _btnClean.Enabled = true;
                    if (ok)
                    {
                        Status("清理完成", "Green");
                        AppLog.Write("pnpm run clean 完成");
                        DialogResult again = MessageBox.Show(this,
                            "pnpm run clean 完成，构建残留已清理。\n\n是否立即重新构建 (pnpm run build)？",
                            "修复完成", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                        if (again == DialogResult.Yes) StartBuild();
                        else CheckMode();
                    }
                    else
                    {
                        Status("清理失败，见日志", "Red");
                        AppLog.Write("pnpm run clean 失败");
                        MessageBox.Show(this, "pnpm run clean 失败，请查看日志。", "清理失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                });
            });
        }

        private bool RunPnpm(string repo, string script)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c pnpm run " + script);
                psi.WorkingDirectory = repo;
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (Process p = Process.Start(psi))
                {
                    DateTime lastOutput = DateTime.Now;
                    DateTime lastHeartbeat = DateTime.Now;
                    bool ended = false;
                    System.Threading.Timer heartbeat = new System.Threading.Timer(delegate(object state)
                    {
                        if (ended) return;
                        int silent = (int)(DateTime.Now - lastOutput).TotalSeconds;
                        if (silent >= 30 && (DateTime.Now - lastHeartbeat).TotalSeconds >= 30)
                        {
                            lastHeartbeat = DateTime.Now;
                            AppLog.Write("……仍在运行中（已 " + silent + " 秒无输出；完整构建通常需要数分钟）");
                        }
                    }, null, 30000, 15000);
                    DataReceivedEventHandler handler = delegate(object s2, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lastOutput = DateTime.Now;
                        AppLog.Write(e.Data);
                        if (e.Data.IndexOf("Corepack is about to download", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            AppLog.Write("提示: corepack 正在下载 pnpm，默认源 registry.npmjs.org 在国内可能很慢或超时。");
                            AppLog.Write("      解决: 设置环境变量 COREPACK_NPM_REGISTRY=https://registry.npmmirror.com 后重启启动器；或先执行 corepack install -g pnpm@<package.json 里的版本>。");
                        }
                    };
                    p.OutputDataReceived += handler;
                    p.ErrorDataReceived += handler;
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    ended = true;
                    heartbeat.Dispose();
                    return p.ExitCode == 0;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("pnpm run " + script + " 启动失败: " + ex.Message);
                return false;
            }
        }

        // ---------- shortcut ----------
        private void CreateShortcut()
        {
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                string lnkPath = Path.Combine(desktop, "DSH 启动器.lnk");
                Type wsType = Type.GetTypeFromProgID("WScript.Shell");
                if (wsType == null) { AppLog.Write("无法创建 WScript.Shell COM"); return; }
                object ws = Activator.CreateInstance(wsType);
                object sc = wsType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, ws, new object[] { lnkPath });
                SetProp(wsType, sc, "TargetPath", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DSHLauncher.exe"));
                SetProp(wsType, sc, "WorkingDirectory", _repoRoot);
                SetProp(wsType, sc, "IconLocation", Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DSHLauncher.ico") + ",0");
                SetProp(wsType, sc, "Description", "DSH 启动器");
                wsType.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                MessageBox.Show(this, "已创建桌面快捷方式:\n" + lnkPath, "完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { AppLog.Write("创建快捷方式失败: " + ex.Message); }
        }

        private static void SetProp(Type wsType, object sc, string name, string value)
        {
            wsType.InvokeMember(name, BindingFlags.SetProperty, null, sc, new object[] { value });
        }

        // ---------- helpers ----------
        public void Ui(Action action)
        {
            if (InvokeRequired) Invoke(action);
            else action();
        }

        public DialogResult Ask(string text, string caption, MessageBoxButtons buttons, MessageBoxIcon icon)
        {
            if (InvokeRequired)
            {
                return (DialogResult)Invoke((Func<DialogResult>)delegate { return Ask(text, caption, buttons, icon); });
            }
            return MessageBox.Show(this, text, caption, buttons, icon);
        }

        public void Status(string text, string colorName)
        {
            if (InvokeRequired) { Invoke((Action)delegate { Status(text, colorName); }); return; }
            _status.Text = text;
            try { _status.ForeColor = Color.FromName(colorName); } catch { }
        }

        private void LoadConfig()
        {
            try
            {
                string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSHLauncher", "config.json");
                if (File.Exists(path))
                {
                    JavaScriptSerializer ser = new JavaScriptSerializer();
                    Dictionary<string, object> cfg = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    if (cfg != null)
                    {
                        if (cfg.ContainsKey("repoRoot") && cfg["repoRoot"] is string) _repoRoot = (string)cfg["repoRoot"];
                        if (cfg.ContainsKey("port")) _port = Convert.ToInt32(cfg["port"]);
                    }
                }
            }
            catch { }
        }

        private void SaveConfig()
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSHLauncher");
                Directory.CreateDirectory(dir);
                Dictionary<string, object> cfg = new Dictionary<string, object>();
                cfg["repoRoot"] = _repoRoot;
                cfg["port"] = _port;
                JavaScriptSerializer ser = new JavaScriptSerializer();
                File.WriteAllText(Path.Combine(dir, "config.json"), ser.Serialize(cfg));
            }
            catch { }
        }
    }
}

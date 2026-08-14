using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace DSHLauncher
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            bool selfTest = false;
            foreach (string arg in args)
            {
                if (string.Equals(arg, "--selftest", StringComparison.OrdinalIgnoreCase)) selfTest = true;
            }
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "DSHLauncher.SingleInstance", out createdNew))
            {
                if (!createdNew)
                {
                    if (!selfTest)
                    {
                        MessageBox.Show("DSH 启动器已在运行。", "DSH 启动器", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (selfTest)
                {
                    AppLog.SelfTestPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DSHLauncher-selftest.log");
                }
                try
                {
                    if (selfTest)
                    {
                        Selftest.Run();
                    }
                    else
                    {
                        Application.Run(new MainForm());
                    }
                }
                catch (Exception ex)
                {
                    try
                    {
                        File.WriteAllText(
                            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DSHLauncher-crash.log"),
                            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine + ex.ToString());
                    }
                    catch { }
                    if (!selfTest)
                    {
                        MessageBox.Show("DSH 启动器发生错误:\n" + ex.Message + "\n\n详情已写入 DSHLauncher-crash.log", "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }
    }
}

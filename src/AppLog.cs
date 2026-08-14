using System;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace DSHLauncher
{
    /// <summary>界面彩色日志 + 文件双写。所有方法线程安全。</summary>
    public static class AppLog
    {
        public static string LogFilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "desktop-launcher", "launcher.log");
        /// <summary>非空时只写该文件（--selftest 模式）。</summary>
        public static string SelfTestPath = null;
        private static RichTextBox _box;

        public static void Attach(RichTextBox box) { _box = box; }

        public static void Write(string message)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message;
            try
            {
                if (SelfTestPath != null)
                {
                    File.AppendAllText(SelfTestPath, line + Environment.NewLine, new UTF8Encoding(false));
                    return;
                }
                string dir = Path.GetDirectoryName(LogFilePath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogFilePath, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { }
            if (_box == null) return;
            Color color = Color.Black;
            if (Regex.IsMatch(message, "失败|错误|fatal|error|Exception|超时", RegexOptions.IgnoreCase)) color = Color.Firebrick;
            else if (Regex.IsMatch(message, "提示|警告|SSL|certificate|探测|代理", RegexOptions.IgnoreCase)) color = Color.DarkOrange;
            else if (Regex.IsMatch(message, "成功|就绪|已启动|完成|已是最新|可用|构建", RegexOptions.IgnoreCase)) color = Color.Green;
            if (_box.IsHandleCreated)
            {
                _box.Invoke((MethodInvoker)delegate
                {
                    AppendColored(line, color);
                });
            }
            else
            {
                AppendColored(line, color);
            }
        }

        private static void AppendColored(string line, Color color)
        {
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
            _box.SelectionColor = color;
            _box.AppendText(line + Environment.NewLine);
            _box.SelectionColor = Color.Black;
            _box.ScrollToCaret();
        }
    }
}

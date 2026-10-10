# DSH 启动器 (DeepSeek Harness Launcher)

DeepSeek Harness 的桌面启动器：原生 WinForms EXE，基于 Windows 自带的 .NET Framework 4.8 编译，无需安装任何运行时或第三方依赖。

## 功能

| 功能 | 说明 |
| --- | --- |
| 启动服务 | 内置智能启动判断：遍历仓库依赖闭包，比较各 workspace 包的 src/ 与 lib/（前端 app 特判 dist/）新鲜度。产物新鲜 → 直接运行内置产物（快速）；源码较新 → 自动退回 tsx 源码模式。端口被占用时视为已在运行并打开页面；服务就绪后自动打开浏览器 |
| 停止服务 | 终止本启动器拉起的进程树；若端口仍被外部进程占用（如命令行里手动启动的实例），用 netstat 定位并弹窗确认后终止其进程树 |
| 检查/获取更新 | git fetch 源仓库（https://github.com/deepseek-ai/deepseek-harness）；直连失败时自动探测本地代理（Clash Verge 7897 等常见端口）并用只读 ls-remote 验证后重试；SSL 证书错误可选项忽略证书重试；落后时确认后拉取（ff-only）；无提交历史的 zip 副本用 reset --hard + checkout -B 对齐远端，保留不在远端中的本地文件 |
| 构建 | 在仓库目录运行一次 pnpm run build，实时输出日志，完成后弹窗提示成功/失败，并自动刷新启动模式状态；构建失败时直接询问是否清理重试 |
| 修复 | 在仓库目录运行一次 pnpm run clean，清理构建残留；完成后可一键重新构建。与"构建"互斥运行（同一时间只跑一个） |
| 打开页面 | 手动打开 http://127.0.0.1:3080 |
| 创建桌面快捷方式 | 在桌面创建指向本 EXE 的快捷方式（带图标） |

其他特性：

- 单实例（Mutex），重复启动只提示已在运行
- 仓库目录可改，记忆在 %APPDATA%\DSHLauncher\config.json（首次启动未配置时会弹出目录选择）
- 彩色日志（错误红 / 提示橙 / 成功绿），同时写入 %USERPROFILE%\.dsh\desktop-launcher\launcher.log
- 环境变量 DSH_HTTP_PROXY 可指定首选代理
- 全局异常处理：崩溃详情写入 exe 同目录 DSHLauncher-crash.log

## 依赖

- Windows 10/11（内置 .NET Framework 4.8 与 csc 编译器）
- node、git、pnpm 需在 PATH 上（运行服务 / 更新 / 构建用）
- 直连 GitHub 受限的网络环境需本地代理（应用会自动探测）

## 构建

```cmd
build.cmd
```

build.cmd 调用系统自带编译器：

```cmd
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /out:DSHLauncher.exe /win32icon:DSHLauncher.ico /r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Web.Extensions.dll" src\*.cs
```

## 自检

```cmd
DSHLauncher.exe --selftest
type DSHLauncher-selftest.log
```

## 源码结构（src/）

| 文件 | 职责 |
| --- | --- |
| Program.cs | 入口、单实例 Mutex、全局异常处理 |
| MainForm.cs | 界面、配置记忆、快捷方式、构建与清理流程 |
| SmartLaunch.cs | 智能启动：依赖闭包 + src/lib 新鲜度判断 |
| HarnessService.cs | 服务进程管理、端口探测、netstat 定位外部进程 |
| GitUpdater.cs | 更新流程：fetch / 代理探测 / SSL 重试 / 拉取 |
| AppLog.cs | 彩色日志（界面 + 文件双写） |
| Selftest.cs | 无界面自检 |

注意：源码为 C# 5 兼容语法（系统自带编译器仅支持到 C# 5，无字符串插值、无 ?. 运算符）。

## 安全说明

- 项目不含任何凭据；运行时数据（仓库路径、端口）只写本机 %APPDATA%\DSHLauncher\config.json
- 仓库目录路径为运行时配置，不写死在源码中
- 日志仅存本机（%USERPROFILE%\.dsh\desktop-launcher\launcher.log），不会上传

## 提示

- SmartScreen 可能提示"未知发布者"：EXE 未签名，点"仍要运行"即可
- 停止服务会按端口定位进程，弹窗确认后才终止外部进程，避免误杀
- 仓库更新（zip 副本场景）会执行 git reset --hard，二次确认后才执行
- 构建失败时的推荐流程：点"修复 (pnpm run clean)"清理残留 → 弹窗里选"是"直接重新构建
using System;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("SnapTransOCR")]
[assembly: AssemblyDescription("Windows 截图、贴图、OCR 与 API 翻译")]
[assembly: AssemblyVersion("0.2.21.0")]
[assembly: AssemblyFileVersion("0.2.21.0")]
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8")]

namespace SnapTransOCR
{
    internal static class Program
    {
        internal static Icon AppIcon;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string className, string name);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
        [STAThread]
        private static void Main(string[] args)
        {
            bool created;
            using (var mutex = new Mutex(true, @"Local\SnapTransOCR.Desktop.v1", out created))
            {
                if (!created)
                {
                    PostMessage(FindWindow(null, CommandWindow.Caption), args.Contains("--quit") ? 0x8008 : 0x8007, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                if (args.Contains("--quit")) return;
                try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
                catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e) { Ui.Error(null, e.Exception); };
                AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                try
                {
                    AppSettings settings = SettingsStore.Load();
                    string startupWarning = null;
                    try { AutoStart.Migrate(Application.ExecutablePath); }
                    catch (Exception ex) { startupWarning = "旧版开机启动项未更新，请在常规设置中重新启用。（" + ex.GetType().Name + "）"; }
                    using (var app = new DesktopApp(settings, !args.Contains("--show"), startupWarning, startClipboardHistory: true)) Application.Run(app);
                }
                catch (Exception ex) { Ui.Error(null, ex); }
                finally { if (AppIcon != null) AppIcon.Dispose(); }
                GC.KeepAlive(mutex);
            }
        }
    }
}

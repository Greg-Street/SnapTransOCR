using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

internal static class ScrollProbe
{
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr c);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);
    const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly Dictionary<string, int> layouts = new Dictionary<string, int>(), paints = new Dictionary<string, int>();
    static void Count(Dictionary<string, int> values, string name) { int value; values.TryGetValue(name, out value); values[name] = value + 1; }
    static IEnumerable<Control> All(Control c) { yield return c; foreach (Control child in c.Controls) foreach (var item in All(child)) yield return item; }
    [STAThread] static void Main(string[] args)
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4));
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        var app = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        app.GetType("SnapTransOCR.Program").GetField("AppIcon", BindingFlags.Static | BindingFlags.NonPublic).SetValue(null, SystemIcons.Application);
        var settings = Activator.CreateInstance(app.GetType("SnapTransOCR.AppSettings"));
        using (var form = (Form)Activator.CreateInstance(app.GetType("SnapTransOCR.SettingsForm"), new object[] { settings, null, null }))
        {
            form.Text = "SnapTransOCR scroll test — synthetic settings"; form.StartPosition = FormStartPosition.Manual;
            form.Location = new Point(40, 40); form.ShowInTaskbar = false; form.Show(); form.Activate(); Application.DoEvents();
            var body = (Panel)form.GetType().GetField("body", Fields).GetValue(form);
            foreach (var control in All(form))
            {
                string name = control.GetType().Name;
                control.Layout += (s,e) => Count(layouts, name);
                control.Paint += (s,e) => Count(paints, name);
            }
            var results = new List<object>();
            foreach (var page in new[] { "翻译设置", "识别设置" })
            foreach (int delta in new[] { 120, 30 })
            {
                form.GetType().GetMethod("SelectPage", Fields | BindingFlags.Public).Invoke(form, new object[] { page });
                body.AutoScrollPosition = Point.Empty; Application.DoEvents(); form.Update(); Thread.Sleep(100);
                layouts.Clear(); paints.Clear();
                var times = new List<double>(); var positions = new List<int>();
                int moving = 0; var cpu = Process.GetCurrentProcess().TotalProcessorTime;
                int previous = body.AutoScrollPosition.Y;
                for (int i = 0; i < 120; i++)
                {
                    int signed = i < 60 ? -delta : delta;
                    var watch = Stopwatch.StartNew();
                    SendMessage(body.Handle, 0x20a, new IntPtr(signed << 16), IntPtr.Zero);
                    Application.DoEvents(); form.Update();
                    times.Add(watch.Elapsed.TotalMilliseconds);
                    int y = body.AutoScrollPosition.Y; positions.Add(y); if (y != previous) moving++; previous = y;
                    Thread.Sleep(8);
                }
                double cpuMs = (Process.GetCurrentProcess().TotalProcessorTime - cpu).TotalMilliseconds;
                var sorted = times.OrderBy(x=>x).ToArray();
                results.Add(new { page, delta, moving, maxOffset = positions.Min(), finalOffset = positions.Last(), medianMs = sorted[60], p95Ms = sorted[114], maxMs = sorted[119], cpuMs,
                    layouts = new Dictionary<string,int>(layouts), paints = new Dictionary<string,int>(paints), positions });
            }
            File.WriteAllText(args[1], new JavaScriptSerializer().Serialize(new { version = app.GetName().Version.ToString(), dpi = form.DeviceDpi, wheelLines = SystemInformation.MouseWheelScrollLines,
                formExStyle = GetWindowLongPtr(form.Handle, -20).ToInt64(), bodyExStyle = GetWindowLongPtr(body.Handle, -20).ToInt64(),
                composited = All(form).Where(control => control.IsHandleCreated && (GetWindowLongPtr(control.Handle, -20).ToInt64() & 0x02000000) != 0).Select(control => control.GetType().Name).ToArray(), results }));
        }
        Console.WriteLine("Scroll samples completed: " + args[1]);
    }
}

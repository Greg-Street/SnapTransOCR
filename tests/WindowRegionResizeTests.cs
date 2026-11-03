using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class WindowRegionResizeTests
    {
        [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Native.RECT bounds);

        internal static void Run(Action<bool, string> check)
        {
            bool previous = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var form = new SettingsForm(new AppSettings(), delegate { }) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                {
                    form.Show(); form.SelectPage("识别设置");
                    var body = (Panel)typeof(SettingsForm).GetField("body", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(form);
                    bool sampling = false, current = true;
                    int resizeEvents = 0, bodyEvents = 0;
                    form.Resize += delegate { if (sampling) { resizeEvents++; current &= FullClientAvailable(form); } };
                    body.SizeChanged += delegate { if (sampling) { bodyEvents++; current &= FullClientAvailable(form); } };
                    foreach (int dpi in new[] { 96, 144, 192, 96 })
                    {
                        ChangeDpi(form, dpi);
                        form.ClientSize = new Size(Ui.Scale(form, 1000), Ui.Scale(form, 680));
                        foreach (bool top in new[] { false, true })
                        {
                            Rectangle original = form.Bounds;
                            resizeEvents = bodyEvents = 0; current = true; sampling = true;
                            foreach (int delta in new[] { 40, 80, 20, -40, 0 })
                            {
                                int pixels = Ui.Scale(form, delta);
                                form.SetBounds(original.Left, original.Top - (top ? pixels : 0), original.Width, original.Height + pixels);
                                current &= FullClientAvailable(form);
                            }
                            sampling = false;
                            check(resizeEvents > 0 && bodyEvents > 0 && current,
                                "unclipped native client covers the whole window throughout " + (top ? "top" : "bottom") + " edge resize and child layout at " + dpi + " DPI");
                            check(form.Bounds == original && !body.HorizontalScroll.Visible,
                                "vertical resize returns to its original bounds without horizontal scrolling at " + dpi + " DPI");
                        }
                    }
                }
            }
            finally { Ui.RenderOffscreen = previous; }
        }

        private static bool FullClientAvailable(Form form)
        {
            IntPtr region = CreateRectRgn(0, 0, 0, 0);
            try
            {
                Native.RECT bounds;
                return form.Region == null && GetWindowRgn(form.Handle, region) == 0 && GetClientRect(form.Handle, out bounds)
                    && bounds.Rectangle == new Rectangle(Point.Empty, form.Size);
            }
            finally { Native.DeleteObject(region); }
        }

        private static void ChangeDpi(Form form, int dpi)
        {
            if (form.DeviceDpi == dpi) return;
            double factor = dpi / (double)form.DeviceDpi;
            IntPtr rectangle = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.Copy(new[] { form.Left, form.Top, form.Left + (int)Math.Round(form.Width * factor), form.Top + (int)Math.Round(form.Height * factor) }, 0, rectangle, 4);
                Native.SendMessage(form.Handle, 0x02e0, new IntPtr(dpi | dpi << 16), rectangle);
            }
            finally { Marshal.FreeHGlobal(rectangle); }
        }
    }
}

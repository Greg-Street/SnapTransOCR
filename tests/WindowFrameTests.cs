using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class WindowFrameTests
    {
        [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr window, IntPtr region);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var form = new Frame())
                {
                    form.Show(); Application.DoEvents();
                    foreach (int dpi in new[] { 96, 144, 192, 240, 96 })
                    {
                        ChangeDpi(form, dpi);
                        bool rectangular = true, sizeStable = true;
                        foreach (int width in new[] { 320, 381 })
                        {
                            var requested = new Size(Ui.Scale(form, width), Ui.Scale(form, 221));
                            form.ClientSize = requested;
                            IntPtr region = CreateRectRgn(0, 0, 0, 0);
                            try
                            {
                                rectangular &= form.Region == null && GetWindowRgn(form.Handle, region) == 0;
                                var points = new[] { new Point(2, 2), new Point(form.Width - 3, 2), new Point(2, form.Height - 3), new Point(form.Width - 3, form.Height - 3) };
                                var expected = new[] { 13, 14, 16, 17 };
                                bool hit = true;
                                for (int i = 0; i < points.Length; i++)
                                {
                                    Point screen = form.PointToScreen(points[i]);
                                    hit &= Native.SendMessage(form.Handle, 0x84, IntPtr.Zero, new IntPtr(screen.Y << 16 | screen.X & 0xffff)).ToInt32() == expected[i];
                                }
                                check(hit, "all four native corner resize targets survive at " + dpi + " DPI and width " + width);
                            }
                            finally { DeleteObject(region); }
                            sizeStable &= form.Size == requested && form.ClientSize == requested;
                        }
                        check(rectangular, "window has no binary clipping region at " + dpi + " DPI");
                        check(sizeStable, "rounded frame changes preserve borderless client size at " + dpi + " DPI");
                        using (var bitmap = new Bitmap(form.Width, form.Height))
                        {
                            form.DrawToBitmap(bitmap, form.ClientRectangle);
                            int top = bitmap.GetPixel(form.Width / 2, 0).ToArgb();
                            check(top == bitmap.GetPixel(0, form.Height / 2).ToArgb()
                                && top == bitmap.GetPixel(form.Width - 1, form.Height / 2).ToArgb()
                                && top == bitmap.GetPixel(form.Width / 2, form.Height - 1).ToArgb(), "frame border has equal coverage on all four sides at " + dpi + " DPI");
                            int preference;
                            if (DwmGetWindowAttribute(form.Handle, 33, out preference, sizeof(int)) < 0)
                                check(bitmap.GetPixel(0, 0).ToArgb() == top && bitmap.GetPixel(form.Width - 1, 0).ToArgb() == top
                                    && bitmap.GetPixel(0, form.Height - 1).ToArgb() == top && bitmap.GetPixel(form.Width - 1, form.Height - 1).ToArgb() == top,
                                    "unsupported system corner API uses complete straight corners at " + dpi + " DPI");
                            else check(preference == 2, "supported system corner API retains the round preference at " + dpi + " DPI");
                        }
                    }
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static void ChangeDpi(Form form, int dpi)
        {
            if (form.DeviceDpi == dpi) return;
            double factor = (double)dpi / form.DeviceDpi;
            var rectangle = new Native.RECT { Left = form.Left, Top = form.Top, Right = form.Left + (int)Math.Round(form.Width * factor), Bottom = form.Top + (int)Math.Round(form.Height * factor) };
            IntPtr memory = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.RECT)));
            try
            {
                Marshal.StructureToPtr(rectangle, memory, false);
                Native.SendMessage(form.Handle, 0x2e0, new IntPtr(dpi | (dpi << 16)), memory);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        private sealed class Frame : RoundedForm
        {
            protected override void OnDpiChanged(DpiChangedEventArgs e)
            {
                base.OnDpiChanged(e);
                MinimumSize = new Size(Ui.Scale(this, 100), Ui.Scale(this, 100));
            }
            internal Frame()
            {
                MinimumSize = new Size(100, 100); ClientSize = new Size(320, 221); StartPosition = FormStartPosition.Manual;
                Location = new Point(-30000, -30000); ShowInTaskbar = false; ResumeLayout(true);
            }
        }
    }
}

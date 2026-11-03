using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class WindowSizeTests
    {
        internal static void Run(Action<bool, string> check)
        {
            using (var form = new MainForm())
                check(form.ClientSize.Width == Ui.Scale(form, 520), "translation default width is 520 logical pixels");

            var saved = new AppSettings { TranslateWindowWidth = 500, TranslateWindowHeight = 620 };
            var copy = saved.Copy();
            check(copy.TranslateWindowWidth == 500 && copy.TranslateWindowHeight == 620, "logical translation dimensions survive JSON serialization");
            var stale = saved.Copy();
            saved.TranslateWindowWidth = 490; saved.TranslateWindowHeight = 650;
            stale.PreserveLiveWindowSize(saved);
            check(stale.TranslateWindowWidth == 490 && stale.TranslateWindowHeight == 650, "saving stale settings retains dimensions resized while settings were open");
            saved.TranslateAutoHeight = false;
            stale.TranslateAutoHeight = true; stale.PreserveLiveWindowSize(saved);
            check(stale.TranslateWindowWidth == 490 && stale.TranslateWindowHeight == 0, "explicit auto-height enable retains width and clears the remembered manual height");
            foreach (int dpi in new[] { 96, 144, 192, 240 })
                check(MainForm.RestoredSize(500, 620, dpi, new Size(5000, 5000)) == new Size(500 * dpi / 96, 620 * dpi / 96), "remembered size converts from logical units at " + dpi + " DPI");
            check(MainForm.RestoredSize(Int32.MaxValue, Int32.MaxValue, 240, new Size(1280, 720)) == new Size(1280, 720), "oversized saved dimensions fit the current work area without overflow");
            check(MainForm.RestoredSize(1, 1, 96, new Size(1920, 1080)) == new Size(360, 460), "saved dimensions retain the minimum usable translation size");
            check(MainForm.RestoredSize(0, 0, 96, new Size(1920, 1080)) == new Size(520, 500), "older settings without dimensions use compact defaults");

            bool offscreen = Ui.RenderOffscreen;
            Ui.RenderOffscreen = true;
            try
            {
                using (var form = Open(new AppSettings { TranslateWindowWidth = Int32.MaxValue, TranslateWindowHeight = Int32.MaxValue }))
                {
                    Size available = Screen.FromControl(form).WorkingArea.Size;
                    check(form.ClientSize.Width <= available.Width && form.ClientSize.Height <= available.Height, "restored native window dimensions are clipped to the current working area");
                }
                using (var form = Open(new AppSettings()))
                {
                    int writes = 0;
                    var remembered = new AppSettings();
                    form.UserSizeChanged += delegate(int width, int height) { writes++; remembered.TranslateWindowWidth = width; remembered.TranslateWindowHeight = height; };
                    form.SetOutput(new string('x', 1000)); form.SetStatus("automatic height");
                    Native.SendMessage(form.Handle, 0x231, IntPtr.Zero, IntPtr.Zero);
                    form.Location = new Point(form.Left + 20, form.Top + 20);
                    Native.SendMessage(form.Handle, 0x232, IntPtr.Zero, IntPtr.Zero);
                    check(writes == 0, "content height and moving the window do not persist a manual size");
                    Resize(form, new Size(500, 620));
                    Native.SendMessage(form.Handle, 0x232, IntPtr.Zero, IntPtr.Zero);
                    check(writes == 1 && remembered.TranslateWindowWidth == 500 && remembered.TranslateWindowHeight == 620, "one completed user resize records the logical client size once");
                    form.SetSource("new source"); form.SetStatus(""); form.SetOutput("new output");
                    check(form.ClientSize == new Size(500, 620) && writes == 1, "translation updates keep the remembered manual size without new writes");
                    using (var restarted = Open(remembered.Copy()))
                    {
                        restarted.SetOutput(new string('y', 2000));
                        check(restarted.ClientSize == new Size(500, 620), "a recreated translation window restores manual width and height before results arrive");
                        remembered.TranslateAutoHeight = false; restarted.RefreshSettings(remembered);
                        var automatic = remembered.Copy(); automatic.TranslateAutoHeight = true; automatic.PreserveLiveWindowSize(remembered);
                        restarted.SetOutput(""); restarted.RefreshSettings(automatic);
                        check(restarted.Width == 500 && restarted.Height < 620, "explicit auto height resumes after restart without widening the window");
                    }
                    ChangeDpi(form, 144);
                    check(form.Width == 750 && writes == 1, "DPI changes preserve logical width without persisting a resize");
                    Resize(form, new Size(735, 900));
                    check(writes == 2 && remembered.TranslateWindowWidth == 490 && remembered.TranslateWindowHeight == 600, "manual resizing on a scaled display saves logical rather than physical pixels");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static MainForm Open(AppSettings settings)
        {
            var form = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false };
            form.RefreshSettings(settings); form.Show(); Application.DoEvents(); return form;
        }
        private static void Resize(MainForm form, Size size)
        {
            IntPtr memory = Marshal.AllocHGlobal(16);
            try
            {
                Native.SendMessage(form.Handle, 0x231, IntPtr.Zero, IntPtr.Zero);
                Marshal.Copy(new[] { form.Left, form.Top, form.Left + size.Width, form.Top + size.Height }, 0, memory, 4);
                Native.SendMessage(form.Handle, 0x214, new IntPtr(8), memory);
                form.ClientSize = size;
                Native.SendMessage(form.Handle, 0x232, IntPtr.Zero, IntPtr.Zero);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
        private static void ChangeDpi(Form form, int dpi)
        {
            double scale = dpi / (double)form.DeviceDpi;
            IntPtr memory = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.Copy(new[] { form.Left, form.Top, form.Left + (int)Math.Round(form.Width * scale), form.Top + (int)Math.Round(form.Height * scale) }, 0, memory, 4);
                Native.SendMessage(form.Handle, 0x2e0, new IntPtr(dpi | dpi << 16), memory);
            }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class CaptureRefinementTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll")] private static extern int GetGuiResources(IntPtr process, int flags);
        internal static void Run(Action<bool, string> check, string output)
        {
            Styles(check, output);
            Windows(check);
            FixedElements(check, output);
            var legacy = Json.Read<AppSettings>("{\"CaptureMagnifier\":true}");
            check(!legacy.CaptureCursor && legacy.CaptureMagnifier, "older settings default to screenshots without a cursor");
            var enabled = new AppSettings { CaptureCursor = true };
            check(Json.Read<AppSettings>(Json.Write(enabled)).CaptureCursor && enabled.Copy().CaptureCursor, "cursor preference survives serialization and capture settings snapshots");
        }
        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control nested in Descendants(child)) yield return nested;
            }
        }
        private static void Styles(Action<bool, string> check, string output)
        {
            bool offscreen = Ui.RenderOffscreen;
            Ui.RenderOffscreen = true;
            var fonts = new List<Font>();
            try
            {
                AppSettings saved = null;
                using (var form = new SettingsForm(new AppSettings(), value => saved = value.Copy()))
                {
                    form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000);
                    form.Show(); Application.DoEvents();
                    foreach (int dpi in new[] { 96, 144, 192, 240, 144 })
                    {
                        SettingsLayoutTests.SimulateDpi(form, dpi, fonts);
                        foreach (string page in new[] { "常规设置", "翻译设置", "识别设置", "截图设置" })
                        {
                            form.SelectPage(page); form.PerformLayout(); Application.DoEvents();
                            var toggles = Descendants(form).OfType<ToggleSwitch>().Where(control => control.Visible).ToArray();
                            check(toggles.Length > 0 && toggles.All(control => control.Size == new Size(Ui.Scale(control, 46), Ui.Scale(control, 28))), "visible switches share one size on " + page + " at " + dpi + " DPI");
                            if (page == "翻译设置")
                            {
                                var mode = (ComboBox)typeof(SettingsForm).GetField("baiduMode", Private).GetValue(form);
                                foreach (int selection in new[] { 1, 2, 0 })
                                {
                                    mode.SelectedIndex = selection; Application.DoEvents();
                                    check(Descendants(form).OfType<ToggleSwitch>().Where(control => control.Visible).All(control => control.Size == new Size(Ui.Scale(control, 46), Ui.Scale(control, 28))), "switch size survives changing Baidu mode at " + dpi + " DPI, mode " + selection);
                                }
                            }
                        }
                        var save = Descendants(form).OfType<Button>().Single(control => control.Text == "保存设置");
                        var cancel = Descendants(form).OfType<Button>().Single(control => control.Text == "取消");
                        check(save is RoundedButton && cancel is RoundedButton && save.Size == cancel.Size && save.Parent.ClientRectangle.Contains(save.Bounds) && cancel.Parent.ClientRectangle.Contains(cancel.Bounds), "rounded settings actions have equal sizes and fit the footer at " + dpi + " DPI, save=" + save.Bounds + ", cancel=" + cancel.Bounds + ", footer=" + save.Parent.ClientSize);
                        form.SelectPage("常规设置");
                        var retention = Descendants(form).OfType<RetentionInput>().Single();
                        var editor = retention.Controls.OfType<TextBox>().Single();
                        check(!Descendants(retention).OfType<NumericUpDown>().Any() && retention.ClientRectangle.Contains(editor.Bounds) && editor.TextAlign == HorizontalAlignment.Center, "retention is a direct editor with no native spinner or clipped input at " + dpi + " DPI");
                    }
                    var days = Descendants(form).OfType<RetentionInput>().Single();
                    var text = days.Controls.OfType<TextBox>().Single();
                    foreach (string invalid in new[] { "", "0", "3651", "ten" })
                    {
                        text.Text = invalid; bool rejected = false;
                        try { int value = days.Value; } catch (FormatException) { rejected = true; }
                        check(rejected && saved == null, "invalid retention is rejected before saving, value=" + invalid);
                    }
                    text.Text = "10";
                    form.SelectPage("截图设置");
                    var cursor = Descendants(form).OfType<ToggleSwitch>().Single(control => control.AccessibleName == "CaptureCursor");
                    cursor.Checked = true;
                    typeof(SettingsForm).GetMethod("Save", Private).Invoke(form, null);
                    check(saved != null && saved.CaptureCursor && saved.HistoryRetentionDays == 10, "saving settings applies cursor visibility and exactly the typed retention");
                }
                using (var fresh = new SettingsForm(new AppSettings { HistoryRetentionDays = 10 }, delegate { }))
                {
                    fresh.ShowInTaskbar = false; fresh.StartPosition = FormStartPosition.Manual; fresh.Location = new Point(-30000, -30000);
                    fresh.Show(); Application.DoEvents();
                    foreach (string page in new[] { "常规设置", "翻译设置", "截图设置" })
                    {
                        fresh.SelectPage(page); Application.DoEvents();
                        check(Descendants(fresh).OfType<ToggleSwitch>().Where(control => control.Visible).All(control => control.Size == new Size(Ui.Scale(control, 46), Ui.Scale(control, 28))), "switches have their standard size on a fresh actual-DPI launch, page=" + page);
                        Render(fresh, Path.Combine(output, page == "常规设置" ? "settings-general-0.2.26.png" : page == "翻译设置" ? "settings-translation-0.2.26.png" : "settings-capture-0.2.26.png"));
                    }
                    fresh.SelectPage("翻译设置");
                    var baidu = Descendants(fresh).OfType<ToggleSwitch>().Single(control => control.AccessibleName == "BaiduTranslateEnabled");
                    var body = (Panel)typeof(SettingsForm).GetField("body", Private).GetValue(fresh);
                    body.AutoScrollPosition = new Point(0, baidu.Parent.Parent.Top);
                    Render(fresh, Path.Combine(output, "settings-services-0.2.26.png"));
                }
                int clicks = 0;
                using (var parent = new Panel { BackColor = Ui.Background })
                using (var button = Ui.Button("保存设置", () => clicks++, true))
                {
                    button.AutoSize = false; button.Size = new Size(186, 60); parent.Controls.Add(button);
                    using (var image = new Bitmap(button.Width, button.Height))
                    {
                        button.DrawToBitmap(image, button.ClientRectangle);
                        check(image.GetPixel(0, 0).ToArgb() == Ui.Background.ToArgb() && image.GetPixel(button.Width / 2, 5).ToArgb() == Ui.Accent.ToArgb(), "rounded primary button corners expose the parent background without a rectangular border");
                    }
                    button.PerformClick(); button.Enabled = false; button.PerformClick();
                    check(clicks == 1, "shared rounded actions preserve enabled and disabled button behavior");
                }
                using (var card = new RoundedPanel { Fill = Color.White })
                using (var action = Ui.Button("操作", delegate { }, true))
                {
                    action.AutoSize = false; action.Size = new Size(120, 40); card.Controls.Add(action);
                    using (var image = new Bitmap(action.Width, action.Height))
                    {
                        action.DrawToBitmap(image, action.ClientRectangle);
                        check(image.GetPixel(0, 0).ToArgb() == Color.White.ToArgb(), "rounded actions on transparent card surfaces use the card fill for their corners");
                    }
                }
            }
            finally { Ui.RenderOffscreen = offscreen; foreach (Font font in fonts) font.Dispose(); }
        }
        private static void Render(Form form, string path)
        {
            using (var image = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            { form.DrawToBitmap(image, form.ClientRectangle); image.Save(path); }
        }
        private static void Mouse(CaptureForm form, string name, MouseButtons button, Point point)
        {
            typeof(CaptureForm).GetMethod(name, Private).Invoke(form, new object[] { new MouseEventArgs(button, 1, point.X, point.Y, 0) });
        }
        private static Rectangle Selection(CaptureForm form) { return (Rectangle)typeof(CaptureForm).GetField("selected", Private).GetValue(form); }
        private static void Windows(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            var bounds = new Rectangle(-250, -100, 640, 480);
            try
            {
                foreach (bool drag in new[] { false, true })
                using (var first = new Bitmap(bounds.Width, bounds.Height))
                using (var form = new CaptureForm("", new AppSettings(), 0, (Bitmap)first.Clone(), bounds))
                {
                    var windows = (List<Rectangle>)typeof(CaptureForm).GetField("windows", Private).GetValue(form);
                    windows.Add(new Rectangle(-220, -80, 300, 240)); windows.Add(bounds);
                    IntPtr handle = form.Handle;
                    Mouse(form, "OnMouseMove", MouseButtons.None, new Point(80, 80));
                    check(Selection(form) == new Rectangle(30, 20, 300, 240), "hover automatically snaps to the front window using physical negative-desktop coordinates");
                    var end = drag ? new Point(210, 190) : new Point(81, 80);
                    Mouse(form, "OnMouseDown", MouseButtons.Left, new Point(80, 80));
                    Mouse(form, "OnMouseMove", MouseButtons.Left, end);
                    Mouse(form, "OnMouseUp", MouseButtons.Left, end);
                    check(Selection(form) == (drag ? new Rectangle(80, 80, 130, 110) : new Rectangle(30, 20, 300, 240)), drag ? "dragging freely chooses an independent region while automatic window snapping is enabled" : "a click with tiny mouse jitter selects the snapped window");
                    check(form.Controls.OfType<ImageCanvas>().Count() == 1, "snapped and manually dragged regions both open the normal annotation toolbar");
                }
                using (var frozen = new Bitmap(bounds.Width, bounds.Height))
                using (var form = new CaptureForm("", new AppSettings(), 0, (Bitmap)frozen.Clone(), bounds))
                {
                    var windows = (List<Rectangle>)typeof(CaptureForm).GetField("windows", Private).GetValue(form);
                    windows.Add(new Rectangle(-300, -130, 180, 170));
                    Mouse(form, "OnMouseMove", MouseButtons.None, new Point(20, 20));
                    check(Selection(form) == new Rectangle(0, 0, 130, 140), "window snapping clips offscreen borders to the captured desktop");
                    typeof(CaptureForm).GetMethod("OnKeyDown", Private).Invoke(form, new object[] { new KeyEventArgs(Keys.Space) });
                    Mouse(form, "OnMouseMove", MouseButtons.None, new Point(20, 20));
                    check(Selection(form).IsEmpty, "Space disables automatic window snapping before selection");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        internal static Bitmap Page(int width, int height)
        {
            var image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(image))
            using (var font = new Font("Segoe UI", 11))
            {
                graphics.Clear(Color.White);
                for (int y = 0; y < height; y += 27)
                {
                    graphics.DrawString("Row " + (y / 27).ToString("000") + "   unique scrolling text", font, Brushes.Black, 16, y);
                    using (var brush = new SolidBrush(Color.FromArgb(80 + y % 130, 90 + y % 110, 140 + y % 90))) graphics.FillRectangle(brush, width - 42, y + 5, 20, 9);
                }
            }
            return image;
        }
        internal static Bitmap Viewport(Bitmap page, int offset, int height, int header, int footer, bool floating)
        {
            var frame = page.Clone(new Rectangle(0, offset, page.Width, height), PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(frame))
            {
                if (header > 0) { graphics.FillRectangle(Brushes.LightBlue, 0, 0, frame.Width, header); graphics.DrawString("Fixed header", Ui.Font, Brushes.Navy, 8, 8); }
                if (footer > 0) { graphics.FillRectangle(Brushes.Lavender, 0, height - footer, frame.Width, footer); graphics.DrawString("Fixed footer", Ui.Font, Brushes.DarkBlue, 8, height - footer + 8); }
                if (floating) { graphics.FillEllipse(Brushes.Magenta, frame.Width / 2 - 22, height - 67, 44, 44); graphics.DrawString("...", Ui.Font, Brushes.White, frame.Width / 2 - 12, height - 65); }
            }
            return frame;
        }
        private static void FixedElements(Action<bool, string> check, string output)
        {
            using (var page = Page(420, 2200))
            foreach (bool floating in new[] { false, true })
            {
                int header = floating ? 0 : 62, footer = floating ? 0 : 74;
                using (var first = Viewport(page, 0, 600, header, footer, floating))
                using (var stitcher = new Stitcher(first))
                {
                    foreach (int offset in new[] { 170, 340, 345, 346, 510, 680 })
                    using (var next = Viewport(page, offset, 600, header, footer, floating))
                    {
                        string message;
                        check(stitcher.Append(next, out message) && stitcher.TotalHeight == 600 + offset, "fixed and floating elements stitch without losing rows at scroll offset " + offset + ", floating=" + floating + ", " + message);
                    }
                    using (var result = stitcher.Finish())
                    {
                        bool equal = true;
                        for (int y = header; y < result.Height - Math.Max(footer, floating ? 70 : 0); y++)
                            for (int x = 0; x < result.Width; x++) if (result.GetPixel(x, y) != page.GetPixel(x, y)) { equal = false; break; }
                        check(equal, "stitched body matches the original page exactly with each fixed decoration retained only at its edge, floating=" + floating);
                        check(stitcher.FrameCount == 7, "scroll frame limit counts accepted frames even when overlapping pieces are replaced");
                        result.Save(Path.Combine(output, floating ? "scroll-floating-fixed-0.2.26.png" : "scroll-header-footer-fixed-0.2.26.png"));
                    }
                }
            }
        }
        internal static void RunNative(Action<bool, string> check, string output)
        {
            Point pointer = Cursor.Position;
            try
            {
                using (var fixture = new Form { Text = "SnapTransOCR cursor capture fixture", FormBorderStyle = FormBorderStyle.None, BackColor = Color.White, Size = new Size(360, 240), StartPosition = FormStartPosition.CenterScreen, TopMost = true, Cursor = Cursors.Arrow })
                {
                    fixture.Show(); Application.DoEvents(); Native.DwmFlush();
                    Rectangle area = fixture.RectangleToScreen(fixture.ClientRectangle);
                    Cursor.Position = new Point(area.Left + 72, area.Top + 64); Application.DoEvents();
                    Cursor.Current = Cursors.Arrow;
                    using (var clean = ScreenCapture.FrozenDesktop(area))
                    using (var marked = ScreenCapture.FrozenDesktop(area, true))
                    {
                        int differences = 0; Rectangle changed = Rectangle.Empty;
                        for (int y = 0; y < clean.Height; y++) for (int x = 0; x < clean.Width; x++)
                            if (clean.GetPixel(x, y) != marked.GetPixel(x, y)) { differences++; var pixel = new Rectangle(x, y, 1, 1); changed = changed.IsEmpty ? pixel : Rectangle.Union(changed, pixel); }
                        clean.Save(Path.Combine(output, "cursor-clean-0.2.26.png"));
                        marked.Save(Path.Combine(output, "cursor-capture-0.2.26.png"));
                        check(differences > 10 && new Rectangle(8, 0, 128, 128).Contains(changed) && Rectangle.Inflate(changed, 3, 3).Contains(new Point(72, 64)), "native frozen screenshot includes the original pointer at its physical hotspot only when enabled, changed=" + differences + ", bounds=" + changed);
                        Cursor.Position = new Point(area.Left + 72, area.Top + 64); Cursor.Current = Cursors.Arrow;
                        using (var raw = ScreenCapture.Desktop(area))
                        using (var rawMarked = ScreenCapture.Desktop(area, true))
                        using (var inserted = (Bitmap)clean.Clone())
                        {
                            check(raw.PixelFormat == PixelFormat.Format32bppRgb && CapturePerformanceTests.SamePixels(clean, raw), "shared DIB capture retains the RGB format and all source pixels used by scrolling and recording");
                            using (var display = ScreenCapture.ForDisplay(rawMarked))
                            {
                                display.Save(Path.Combine(output, "cursor-rgb-0.2.28.png"));
                                check(CapturePerformanceTests.SamePixels(marked, display), "GIF cursor capture matches the frozen desktop path including alpha-blended pointer edges, cursor=" + Cursor.Position + ", area=" + area);
                            }
                            CaptureSessionUi.DrawCursor(inserted, area);
                            check(CapturePerformanceTests.SamePixels(marked, inserted), "drawing a pointer onto an existing image uses its initialized background pixels");
                        }
                        using (var pointerPatch = CaptureSessionUi.FreezePointer(clean, area))
                        using (var target = (Bitmap)clean.Clone())
                        {
                            check(pointerPatch != null && pointerPatch.Bounds.Width < area.Width && pointerPatch.Bounds.Height < area.Height, "interactive cursor capture stores only the native cursor-sized patch");
                            pointerPatch.Apply(target, true);
                            target.Save(Path.Combine(output, "cursor-interactive-0.2.27.png"));
                            bool equal = true; int mismatch = 0; Rectangle mismatchBounds = Rectangle.Empty;
                            for (int y = 0; y < target.Height; y++) for (int x = 0; x < target.Width; x++)
                                if (target.GetPixel(x, y) != marked.GetPixel(x, y)) { equal = false; mismatch++; var pixel = new Rectangle(x, y, 1, 1); mismatchBounds = mismatchBounds.IsEmpty ? pixel : Rectangle.Union(mismatchBounds, pixel); }
                            check(equal, "interactive cursor insertion matches the native cursor-inclusive screenshot exactly, patch=" + pointerPatch.Bounds + ", differences=" + mismatch + ", bounds=" + mismatchBounds);
                            pointerPatch.Apply(target, false); equal = true;
                            for (int y = 0; y < target.Height; y++) for (int x = 0; x < target.Width; x++)
                                if (target.GetPixel(x, y) != clean.GetPixel(x, y)) equal = false;
                            check(equal, "interactive cursor removal restores every original background pixel");
                        }
                    }
                    using (var process = System.Diagnostics.Process.GetCurrentProcess())
                    {
                        int before = GetGuiResources(process.Handle, 0);
                        for (int i = 0; i < 32; i++) using (var frame = ScreenCapture.FrozenDesktop(area, true)) { }
                        int after = GetGuiResources(process.Handle, 0);
                        check(after <= before + 2, "repeated cursor-inclusive captures release cursor bitmaps and GDI handles, before=" + before + ", after=" + after);
                        using (var clean = ScreenCapture.FrozenDesktop(area))
                        {
                            before = GetGuiResources(process.Handle, 0);
                            for (int i = 0; i < 32; i++) using (var patch = CaptureSessionUi.FreezePointer(clean, area)) { }
                            after = GetGuiResources(process.Handle, 0);
                            check(after <= before + 2, "repeated interactive pointer snapshots release both image patches and native icon bitmaps, before=" + before + ", after=" + after);
                        }
                    }
                    using (var capture = new CaptureForm("window snap native", new AppSettings(), 0))
                    {
                        var windows = (List<Rectangle>)typeof(CaptureForm).GetField("windows", Private).GetValue(capture);
                        check(windows.Contains(fixture.Bounds), "native snapshot enumerates the visible fixture with DWM frame bounds");
                        Rectangle expected = fixture.Bounds; expected.Offset(-SystemInformation.VirtualScreen.Left, -SystemInformation.VirtualScreen.Top);
                        check(Selection(capture) == expected, "first screenshot preview automatically highlights the actual window under the cursor");
                    }
                    foreach (int style in new[] { 0x80020, 0x80000, 0x20 })
                    {
                        bool clickThrough = style == 0x80020;
                        using (var overlay = new OverlayFixture(style) { Bounds = clickThrough ? SystemInformation.VirtualScreen : new Rectangle(area.Left + 40, area.Top + 40, 160, 120) })
                        {
                            overlay.Show(); Application.DoEvents(); Native.DwmFlush();
                            check((Native.GetWindowLongPtr(overlay.Handle, -20).ToInt64() & 0x80020) == style,
                                "window snapping fixture exposes the expected native extended style " + style.ToString("X"));
                            using (var capture = new CaptureForm("overlay snap native", new AppSettings(), 0))
                            {
                                Rectangle expected = clickThrough ? fixture.Bounds : overlay.Bounds;
                                expected.Offset(-SystemInformation.VirtualScreen.Left, -SystemInformation.VirtualScreen.Top);
                                check(Selection(capture) == expected, clickThrough
                                    ? "a click-through layered overlay leaves the underlying window selectable at the first screenshot preview"
                                    : "an interactive window remains selectable with native extended style " + style.ToString("X"));
                            }
                        }
                    }
                    fixture.WindowState = FormWindowState.Minimized; Application.DoEvents();
                    using (var capture = new CaptureForm("minimized window test", new AppSettings(), 0))
                    {
                        var windows = (List<Rectangle>)typeof(CaptureForm).GetField("windows", Private).GetValue(capture);
                        check(!windows.Contains(area), "window snapping excludes minimized windows from the snapshot");
                    }
                }
            }
            finally { Cursor.Position = pointer; }
        }
        private sealed class OverlayFixture : Form
        {
            private readonly int extendedStyle;
            internal OverlayFixture(int extendedStyle)
            {
                this.extendedStyle = extendedStyle;
                FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual;
                ShowInTaskbar = false; TopMost = true; BackColor = Color.LightBlue;
                if ((extendedStyle & 0x80000) != 0) Opacity = 0.5;
            }
            protected override bool ShowWithoutActivation { get { return true; } }
            protected override CreateParams CreateParams
            {
                get { var value = base.CreateParams; value.ExStyle |= extendedStyle | 0x08000080; return value; }
            }
        }
    }
}

using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class CaptureInteractionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private static void Key(CaptureForm form, Keys key)
        {
            var args = new KeyEventArgs(key);
            typeof(CaptureForm).GetMethod("OnKeyDown", Private).Invoke(form, new object[] { args });
            if ((key == Keys.F1 || key == Keys.F2) && (!args.Handled || !args.SuppressKeyPress)) throw new Exception("Capture shortcut was not consumed.");
        }
        internal static void Run(Action<bool, string> check, string output)
        {
            var legacy = Json.Read<AppSettings>("{}");
            check(legacy.CaptureBorderWidth == 2 && legacy.CaptureShortcutHints, "legacy settings receive a two-pixel selection border and shortcut help");
            string settingsPath = Path.Combine(output, "capture-border-settings.json");
            try
            {
                foreach (int width in new[] { -1, 0, 1, 12, 13 })
                {
                    File.WriteAllText(settingsPath, Json.Write(new AppSettings { CaptureBorderWidth = width, CaptureShortcutHints = false }));
                    AppSettings loaded = SettingsStore.Load(settingsPath);
                    check(loaded.CaptureBorderWidth == (width < 1 || width > 12 ? 2 : width) && !loaded.CaptureShortcutHints, "loading selection width validates its range and preserves hidden help, width=" + width);
                }
            }
            finally { if (File.Exists(settingsPath)) File.Delete(settingsPath); }
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var plain = new Bitmap(500, 380))
                {
                    using (var graphics = Graphics.FromImage(plain)) graphics.Clear(Color.White);
                    var clean = new Bitmap(16, 20); using (var graphics = Graphics.FromImage(clean)) graphics.Clear(Color.White);
                    var marked = new Bitmap(16, 20); using (var graphics = Graphics.FromImage(marked)) graphics.Clear(Color.Magenta);
                    using (var form = new CaptureForm("", new AppSettings { CaptureMagnifier = false }, 0, (Bitmap)plain.Clone(), new Rectangle(-30000, -30000, 500, 380), new FrozenPointer(new Rectangle(70, 80, 16, 20), clean, marked)))
                    {
                        form.PreviewSelection(new Rectangle(30, 30, 350, 260));
                        var canvas = form.Controls.OfType<ImageCanvas>().Single();
                        canvas.AddMark(new Mark { Kind = Tool.Rectangle, Bounds = new RectangleF(110, 80, 60, 45), Color = Color.Blue, Width = 3 });
                        using (var before = canvas.Render())
                        {
                            Key(form, Keys.F2);
                            using (var visible = canvas.Render()) check(form.PointerVisible && visible.GetPixel(45, 55).ToArgb() == Color.Magenta.ToArgb(), "F2 inserts the frozen pointer into the current selected image");
                            Key(form, Keys.F2);
                            using (var restored = canvas.Render()) check(!form.PointerVisible && Equal(before, restored), "F2 restores original pixels while preserving every annotation pixel");
                        }
                        canvas.BeginText(new PointF(190, 120)); canvas.TextInput.Text = "Keep editing";
                        Key(form, Keys.F2);
                        check(canvas.EditingText && canvas.TextInput.Text == "Keep editing", "pointer switching works while inline text is being edited and retains its contents");
                        canvas.CancelText(); Key(form, Keys.F2);
                        check(form.ShortcutsVisible, "shortcut help starts enabled");
                        Key(form, Keys.F1); check(!form.ShortcutsVisible, "F1 hides shortcut help for the active capture");
                        Key(form, Keys.F1); check(form.ShortcutsVisible, "F1 restores shortcut help for the active capture");
                        var help = form.Controls.OfType<CaptureShortcuts>().Single();
                        var close = (Rectangle)typeof(CaptureShortcuts).GetProperty("CloseBounds", Private).GetValue(help);
                        typeof(CaptureShortcuts).GetMethod("OnMouseUp", Private).Invoke(help, new object[] { new MouseEventArgs(MouseButtons.Left, 1, close.Left + 2, close.Top + 2, 0) });
                        check(!form.ShortcutsVisible, "the help panel close icon hides it without closing or clearing the screenshot");
                        Key(form, Keys.F1);
                        check(form.ClientRectangle.Contains(help.Bounds), "shortcut help fits the lower-left of the captured screen");
                        using (var result = canvas.Render()) check(result.GetPixel(20, result.Height - 20).ToArgb() == Color.White.ToArgb(), "help and selection borders remain outside the exported image pixels");
                    }
                }
                foreach (int width in new[] { 1, 8, 12 })
                using (var form = new CaptureForm("", new AppSettings { CaptureMagnifier = false, CaptureShortcutHints = false, CaptureBorderWidth = width }, 0, new Bitmap(600, 420), new Rectangle(-30000, -30000, 600, 420)))
                {
                    form.PreviewSelection(new Rectangle(100, 90, 300, 210));
                    using (var frame = new Bitmap(600, 420))
                    using (var graphics = Graphics.FromImage(frame))
                    {
                        typeof(CaptureForm).GetMethod("OnPaint", Private).Invoke(form, new object[] { new PaintEventArgs(graphics, form.ClientRectangle) });
                        check((frame.GetPixel(94, 160).ToArgb() == Ui.Accent.ToArgb()) == (width >= 8), "the selected border width changes actual outside-outline pixels, width=" + width);
                    }
                }
                AppSettings saved = null;
                using (var settings = new SettingsForm(new AppSettings(), next => saved = next.Copy()))
                {
                    var border = (ComboBox)typeof(SettingsForm).GetField("captureBorder", Private).GetValue(settings);
                    check(border.Items.Count == 12 && border.SelectedIndex == 1, "selection border width offers one through twelve pixels with a two-pixel default");
                    border.SelectedIndex = 7;
                    typeof(SettingsForm).GetMethod("Save", Private).Invoke(settings, null);
                    check(saved != null && saved.CaptureBorderWidth == 8 && saved.CaptureStroke == 2, "selection border preference saves independently from annotation stroke width");
                }
                using (var preview = new CaptureForm("", new AppSettings { CaptureMagnifier = false, CaptureBorderWidth = 4 }, 0, CaptureRefinementTests.Page(980, 640), new Rectangle(-30000, -30000, 980, 640)))
                {
                    preview.Show(); preview.PreviewSelection(new Rectangle(100, 80, 650, 350));
                    using (var image = new Bitmap(preview.Width, preview.Height))
                    { preview.DrawToBitmap(image, preview.ClientRectangle); image.Save(Path.Combine(output, "capture-shortcuts-0.2.27.png")); }
                    var help = preview.Controls.OfType<CaptureShortcuts>().Single();
                    Point point = help.PointToScreen(new Point(12, 50));
                    check(Native.SendMessage(help.Handle, 0x84, IntPtr.Zero, new IntPtr(unchecked(point.Y << 16 | point.X & 0xffff))).ToInt32() == -1, "the shortcut-help body passes mouse hit testing through to the selection underneath");
                    preview.Close();
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static bool Equal(Bitmap first, Bitmap second)
        {
            if (first.Size != second.Size) return false;
            for (int y = 0; y < first.Height; y++) for (int x = 0; x < first.Width; x++) if (first.GetPixel(x, y) != second.GetPixel(x, y)) return false;
            return true;
        }
    }
}

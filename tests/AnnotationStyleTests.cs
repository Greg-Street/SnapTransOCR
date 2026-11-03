using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class AnnotationStyleTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var settings = new AppSettings { CaptureColorArgb = Color.Purple.ToArgb(), CaptureStroke = 6, CaptureTextSize = 42, CaptureTextBold = true };
            var initial = new AnnotationStyle(settings);
            using (var options = new AnnotationOptions(settings))
            using (var canvas = new ImageCanvas(new Bitmap(100, 80)))
            {
                options.Bind(canvas);
                check(Matches(options.Style, initial) && Matches(canvas, initial), "annotation controls restore all four saved values into the canvas");
                var snapshot = options.Style;
                Change(options);
                check(Matches(canvas, options.Style) && !Matches(snapshot, options.Style), "annotation controls apply live edits without mutating an earlier style snapshot");
                check(settings.CaptureColorArgb == initial.Color.ToArgb() && settings.CaptureStroke == 6 && settings.CaptureTextSize == 42 && settings.CaptureTextBold,
                    "style controls do not mutate settings while colors, widths and text styles change");
                options.SetScale(2); options.SetScale(1);
                check(Matches(canvas, options.Style), "DPI scaling preserves annotation style values");
            }
            using (var options = new AnnotationOptions(3))
            using (var canvas = new ImageCanvas(new Bitmap(100, 80)))
            {
                options.Bind(canvas);
                check(canvas.Stroke == 3 && canvas.TextSize == 24 && !canvas.TextBold && canvas.Color.ToArgb() == Color.FromArgb(239, 74, 64).ToArgb(), "legacy annotation constructor keeps default color and text style");
            }
            var small = new AnnotationStyle(Color.Red, -4, 0, false);
            var large = new AnnotationStyle(Color.Blue, 100, 1000, true);
            check(small.Stroke == 1 && small.TextSize == 8 && large.Stroke == 20 && large.TextSize == 144,
                "annotation style clamps malformed numeric settings to supported control ranges");
            using (var capture = new CaptureForm("", settings.Copy(), 0, new Bitmap(640, 480), new Rectangle(-30000, -30000, 640, 480)))
            {
                check(Matches(capture.Style, initial), "capture exposes saved style before its lazy toolbar exists");
                capture.PreviewSelection(new Rectangle(20, 30, 220, 130));
                var styles = Descendants(capture).OfType<AnnotationOptions>().Single();
                Change(styles);
                AnnotationStyle changed = capture.Style;
                capture.PreviewSelection(new Rectangle(60, 70, 230, 150));
                check(Matches(capture.Style, changed) && Matches(capture.Controls.OfType<ImageCanvas>().Single(), changed), "reselecting a screenshot retains the updated annotation style on its replacement canvas");
                capture.Complete(CaptureAction.Ocr);
                using (capture.Captured) check(Matches(capture.Style, changed), "completed capture exposes its final annotation style before disposal");
            }
            using (var editor = new EditorForm(new Bitmap(200, 120), settings.Copy()))
            {
                check(Matches(editor.Style, initial) && Matches(editor.Controls.OfType<ImageCanvas>().Single(), initial), "image editor restores the same style as screenshot annotations");
                check(!Descendants(editor).OfType<GlyphButton>().Any(button => button.AccessibleName == "返回历史记录"), "ordinary image editors do not show a history navigation button");
                Change(editor.Controls.OfType<AnnotationOptions>().Single());
                var changed = editor.Style;
                check(!Matches(initial, changed) && Matches(editor.Controls.OfType<ImageCanvas>().Single(), changed), "editor exposes its final live style for a single close-time save");
                int returned = 0; bool closed = false;
                editor.EnableHistoryNavigation(delegate { returned++; closed = editor.IsDisposed; });
                GlyphButton back = Descendants(editor).OfType<GlyphButton>().Single(button => button.AccessibleName == "返回历史记录");
                check(back.Parent.Controls.GetChildIndex(back) == 0, "history editor places its return action at the start of the toolbar");
                back.PerformClick();
                check(returned == 1 && closed, "history return closes the editor before invoking its return callback once");
            }
            using (var editor = new EditorForm(new Bitmap(100, 80), 4)) check(editor.Style.Stroke == 4 && editor.Style.TextSize == 24, "legacy editor constructor preserves its stroke argument");
            using (var editor = new EditorForm(new Bitmap(400, 300), new AppSettings { CaptureTextSize = 144 }))
            {
                editor.EnableHistoryNavigation(delegate { });
                editor.StartPosition = FormStartPosition.Manual; editor.Location = new Point(-30000, -30000); editor.Show();
                var styles = editor.Controls.OfType<AnnotationOptions>().Single();
                var canvas = editor.Controls.OfType<ImageCanvas>().Single();
                foreach (int scale in new[] { 1, 2 })
                {
                    editor.ClientSize = new Size(720 * scale, 420 * scale); styles.SetScale(scale); editor.PerformLayout();
                    var toolbar = editor.Controls.OfType<FlowLayoutPanel>().Single(control => !(control is AnnotationOptions));
                    check(canvas.Width > 0 && canvas.Height >= 100 * scale && canvas.Top >= styles.Bottom && styles.Top >= toolbar.Bottom,
                        "history editor toolbar wraps while retaining a separate usable canvas at style scale " + scale);
                    check(toolbar.Controls.Cast<Control>().All(control => control.Right <= toolbar.ClientSize.Width && control.Bottom <= toolbar.ClientSize.Height),
                        "history toolbar buttons remain reachable after wrapping at style scale " + scale);
                }
                canvas.BeginText(new PointF(30, 30)); canvas.TextInput.Text = "Sample";
                check(canvas.TextSize == 144 && canvas.EditingText && canvas.TextInput.Font.Size > 100, "maximum remembered text size is applied to the inline editor after layout scaling");
                canvas.CancelText(); editor.Close();
            }
            using (var capture = new CaptureForm("", settings.Copy(), 4, new Bitmap(200, 120), new Rectangle(-30000, -30000, 200, 120)))
            {
                capture.PreviewSelection(new Rectangle(10, 10, 100, 80));
                typeof(CaptureForm).GetMethod("AutoAction", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(capture, null);
                using (capture.Captured) check(capture.Action == CaptureAction.ImageTranslate && capture.Captured != null && Matches(capture.Style, initial), "direct image-translation capture mode preserves style and dispatches the image translation action");
            }
            InitialNavigationDpi(check);
        }
        private static void InitialNavigationDpi(Action<bool, string> check)
        {
            Type helper = typeof(Control).Assembly.GetType("System.Windows.Forms.DpiHelper");
            FieldInfo deviceDpi = helper.GetField("deviceDpi", BindingFlags.Static | BindingFlags.NonPublic);
            FieldInfo factor = helper.GetField("logicalToDeviceUnitsScalingFactor", BindingFlags.Static | BindingFlags.NonPublic);
            object previousDpi = deviceDpi.GetValue(null), previousFactor = factor.GetValue(null);
            try
            {
                foreach (int dpi in new[] { 96, 144, 192, 240 })
                {
                    deviceDpi.SetValue(null, (double)dpi); factor.SetValue(null, dpi / 96.0);
                    using (var editor = new EditorForm(new Bitmap(100, 80)))
                    {
                        editor.EnableHistoryNavigation(delegate { });
                        var toolbar = editor.Controls.OfType<FlowLayoutPanel>().Single(control => !(control is AnnotationOptions));
                        Control back = toolbar.Controls[0], next = toolbar.Controls[1];
                        check(editor.DeviceDpi == dpi && back.Size == next.Size && back.Margin == next.Margin,
                            "history navigation added after initial layout matches existing toolbar scale at " + dpi + " DPI");
                    }
                }
            }
            finally { deviceDpi.SetValue(null, previousDpi); factor.SetValue(null, previousFactor); }
        }
        private static void Change(AnnotationOptions options)
        {
            options.Controls.OfType<GlyphButton>().First(button => button.Swatch.ToArgb() == Color.Black.ToArgb()).PerformClick();
            options.Controls.OfType<NumericUpDown>().Single(control => control.AccessibleName == "线宽").Value = 9;
            options.Controls.OfType<NumericUpDown>().Single(control => control.AccessibleName == "字号").Value = 58;
            options.Controls.OfType<GlyphButton>().Single(button => button.Glyph == "bold").PerformClick();
        }
        private static bool Matches(AnnotationStyle a, AnnotationStyle b) { return a.Color.ToArgb() == b.Color.ToArgb() && a.Stroke == b.Stroke && a.TextSize == b.TextSize && a.TextBold == b.TextBold; }
        private static bool Matches(ImageCanvas canvas, AnnotationStyle style) { return canvas.Color.ToArgb() == style.Color.ToArgb() && canvas.Stroke == style.Stroke && canvas.TextSize == style.TextSize && canvas.TextBold == style.TextBold; }
        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control owner)
        {
            foreach (Control child in owner.Controls) { yield return child; foreach (Control descendant in Descendants(child)) yield return descendant; }
        }
    }
}

using System;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class OcrNarrowTests
    {
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var form = new OcrForm())
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.Show();
                    form.RefreshSettings(new AppSettings { OcrProvider = "tencent" });
                    form.SetSource("这是可以选择、复制和翻译的 OCR 文字。" + new string('x', 1000));
                    var content = form.Controls.OfType<RoundedPanel>().Single();
                    var translate = content.Controls.OfType<Button>().Single();
                    foreach (int dpi in new[] { 96, 144, 192, 240, 96 })
                    {
                        if (form.DeviceDpi != dpi) ChangeDpi(form, dpi);
                        form.ClientSize = new Size(Ui.Scale(form, 360), Ui.Scale(form, 420));
                        check(form.MinimumSize.Width == Ui.Scale(form, 360) && form.ClientSize.Width == Ui.Scale(form, 360), "OCR permits 360 logical pixels at " + dpi + " DPI");
                        foreach (bool busy in new[] { false, true })
                        {
                            form.Busy(busy);
                            var actions = content.Controls.Cast<Control>().Where(control => control.Visible && !(control is RichTextBox)).ToArray();
                            check(actions.All(control => content.ClientRectangle.Contains(control.Bounds)) && actions.All(control => actions.All(other => control == other || !control.Bounds.IntersectsWith(other.Bounds))), "narrow OCR actions remain visible without overlap (busy=" + busy + ") at " + dpi + " DPI");
                        }
                        form.Busy(false);
                        check(translate.Text == "翻译", "narrow OCR translation action uses a compact label at " + dpi + " DPI");
                        var text = content.Controls.OfType<RichTextBox>().Single();
                        Rectangle narrowContent = content.Bounds, narrowText = text.Bounds;
                        Font textFont = text.Font;
                        Size iconSize = content.Controls.OfType<GlyphButton>().First().Size;
                        form.ClientSize = new Size(Ui.Scale(form, 560), Ui.Scale(form, 500));
                        check(translate.Text.Contains("Ctrl+Enter"), "wide OCR action restores its shortcut hint at " + dpi + " DPI");
                        check(content.Left == narrowContent.Left && form.ClientSize.Width - content.Right == Ui.Scale(form, 16) &&
                            text.Left == narrowText.Left && text.Width > narrowText.Width && text.Height > narrowText.Height &&
                            text.Font == textFont && content.Controls.OfType<GlyphButton>().All(button => button.Size == iconSize),
                            "OCR resize expands the text area while preserving margins, font and icon sizes at " + dpi + " DPI");
                        form.ClientSize = new Size(Ui.Scale(form, 360), Ui.Scale(form, 420));
                        check(content.Bounds == narrowContent && text.Bounds == narrowText,
                            "OCR narrow-wide-narrow resizing returns to the same component geometry at " + dpi + " DPI");
                    }
                    string sent = null; form.TranslateRequested += text => sent = text;
                    translate.PerformClick();
                    check(sent != null && sent.StartsWith("这是"), "compact OCR translation action keeps its original behavior");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
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

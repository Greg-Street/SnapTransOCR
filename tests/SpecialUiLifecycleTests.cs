using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class SpecialUiLifecycleTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var form = new OcrForm())
                {
                    form.RefreshSettings(new AppSettings { OcrProvider = "baidu" });
                    var caption = Field<Label>(form, "caption");
                    var source = Field<RichTextBox>(form, "source");
                    foreach (RecognitionKind kind in new[] { RecognitionKind.QrCode, RecognitionKind.Handwriting, RecognitionKind.BaiduTable, RecognitionKind.Table })
                    {
                        byte[] workbook = kind == RecognitionKind.Table || kind == RecognitionKind.BaiduTable ? new byte[] { 1, 2 } : null;
                        form.SetRecognitionResult(new RecognitionResult("unchanged result", workbook), kind);
                        string specialCaption = caption.Text;
                        form.RefreshSettings(new AppSettings { OcrProvider = "tencent", VolcTranslateEnabled = false, BaiduTranslateEnabled = false });
                        check(caption.Text == specialCaption && source.Text == "unchanged result" && ReferenceEquals(Field<byte[]>(form, "spreadsheet"), workbook), "applying settings preserves the active special result, title and workbook: " + kind);
                        check(!Field<Button>(form, "translate").Enabled, "settings still update translation availability for a special result: " + kind);
                        form.SetSource("ordinary OCR");
                        check(caption.Text == "文字识别 · 腾讯 OCR" && Field<byte[]>(form, "spreadsheet") == null, "reusing the result window for ordinary OCR restores the current provider title and releases its workbook: " + kind);
                    }
                    form.RefreshSettings(new AppSettings { OcrProvider = "baidu" });
                    check(caption.Text == "文字识别 · 百度 OCR" && source.Text == "ordinary OCR", "ordinary OCR title follows a later provider change without changing its text");
                }
                var initial = new AppSettings { CaptureStroke = 3 };
                using (var editor = new EditorForm(new Bitmap(120, 60), initial))
                {
                    var options = Field<AnnotationOptions>(editor, "styles");
                    Field<NumericUpDown>(options, "stroke").Value = 7;
                    var button = Field<GlyphButton>(editor, "recognition");
                    var menu = button.ContextMenuStrip;
                    RecognitionKind selected = RecognitionKind.Text;
                    editor.SpecialRecognitionRequested += delegate(Bitmap image, RecognitionKind kind) { using (image) selected = kind; };
                    foreach (RecognitionKind kind in new[] { RecognitionKind.Handwriting, RecognitionKind.QrCode, RecognitionKind.BaiduTable, RecognitionKind.Table })
                    {
                        var updated = new AppSettings {
                            BaiduHandwritingEnabled = kind == RecognitionKind.Handwriting, BaiduQrEnabled = kind == RecognitionKind.QrCode,
                            BaiduTableEnabled = kind == RecognitionKind.BaiduTable, TencentTableEnabled = kind == RecognitionKind.Table,
                            CaptureStroke = 19
                        };
                        editor.RefreshSettings(updated);
                        var enabled = menu.Items.Cast<ToolStripMenuItem>().Where(item => item.Enabled).ToArray();
                        check(ReferenceEquals(menu, button.ContextMenuStrip) && menu.Items.Count == 4 && enabled.Length == 1 && (RecognitionKind)enabled[0].Tag == kind, "an existing editor immediately reflects the selected service without duplicating menu entries: " + kind);
                        enabled[0].PerformClick();
                        check(selected == kind, "an enabled refreshed menu item still dispatches its original recognition action: " + kind);
                        check(editor.Style.Stroke == 7 && updated.CaptureStroke == 19 && initial.CaptureStroke == 3, "refreshing services preserves unsaved annotation choices and does not mutate settings: " + kind);
                    }
                    editor.RefreshSettings(new AppSettings());
                    check(menu.Items.Cast<ToolStripMenuItem>().All(item => !item.Enabled && item.ToolTipText.Contains("启用")), "disabling services updates every existing menu entry and its explanation");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    }
}

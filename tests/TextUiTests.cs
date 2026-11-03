using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class TextUiTests
    {
        public static void Run(Action<bool, string> check)
        {
            OcrCharacterSelection(check);
            check(TextFormatting.MergeLines("first line\r\nsecond line\r\n\r\nnext paragraph") == "first line second line\r\n\r\nnext paragraph", "merge keeps paragraph boundaries and English word spaces");
            check(TextFormatting.MergeLines("中文识别\n结果，\n没有多余空格。") == "中文识别结果，没有多余空格。", "merge joins Chinese OCR lines without spaces");
            check(TextFormatting.MergeLines("using\nAPI\n接口\n识别") == "using API接口识别", "merge handles mixed Chinese and English");
            check(TextFormatting.MergeLines(" (sample\ntext\n)\n\nstate-\nof-the-art ") == "(sample text)\r\n\r\nstate-of-the-art", "merge keeps punctuation and existing hyphens");
            check(TextFormatting.MergeLines("\r\n \n") == "" && TextFormatting.MergeLines(null) == "", "merge handles empty OCR results");
            check(TextFormatting.CamelCase("hello_world sample-name", true) == "HelloWorldSampleName" && TextFormatting.CamelCase("hello_world sample-name", false) == "helloWorldSampleName", "camel formats combine spaces, underscores and hyphens");
            check(TextFormatting.CamelCase("XMLHttpRequest HTTPServer getURLValue", false) == "xmlHttpRequestHttpServerGetUrlValue", "camel formats preserve existing word and acronym boundaries");
            check(TextFormatting.CamelCase("version2Value", true) == "Version2Value" && TextFormatting.CamelCase("alreadyCamelCase", false) == "alreadyCamelCase", "camel formats preserve digit and existing camel boundaries");
            check(TextFormatting.CamelCase("école_中文_😀_e\u0301lan", true) == "École中文😀E\u0301lan", "camel formats preserve non-Latin text, emoji and combining accents");
            check(TextFormatting.CamelCase(null, false) == "" && TextFormatting.CamelCase(" _-\r\n ", true) == "", "camel formats handle empty and separator-only input");
            CultureInfo previousCulture = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = new CultureInfo("tr-TR"); check(TextFormatting.CamelCase("INPUT_ID", false) == "inputId", "camel casing is independent of the system language"); }
            finally { CultureInfo.CurrentCulture = previousCulture; }

            using (var main = new MainForm())
            using (var ocr = new OcrForm())
            {
                main.RefreshSettings(new AppSettings());
                check(Descendants(main).OfType<GlyphButton>().Count(button => button.AccessibleName == "文本格式") == 1
                    && Descendants(ocr).OfType<GlyphButton>().Count(button => button.AccessibleName == "文本格式") == 1,
                    "translation and OCR each expose one text-format icon");
                main.SetSource("Translate\nthis text");
                ocr.SetSource("OCR\nresult");
                var input = Descendants(main).OfType<RichTextBox>().Single(control => control.AccessibleName == "原文");
                var ocrText = Descendants(ocr).OfType<RichTextBox>().Single();
                check(input.Text.Contains("\n") && !ocrText.ReadOnly && ocrText.Text.Contains("OCR"), "OCR has independent editable multiline text");
                int requests = 0;
                main.TranslateRequested += delegate(string text) { requests++; };
                var enter = new KeyEventArgs(Keys.Enter);
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, new object[] { enter });
                check(!enter.SuppressKeyPress && requests == 0, "Enter remains a source line break");
                var translate = new KeyEventArgs(Keys.Control | Keys.Enter);
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(input, new object[] { translate });
                check(translate.SuppressKeyPress && requests == 1, "Ctrl Enter triggers translation once");
                check(input.WordWrap && input.ScrollBars == RichTextBoxScrollBars.Vertical && !Descendants(main).OfType<CheckBox>().Any(), "translation always soft-wraps and offers only vertical scrolling");
                check(input.Text.Contains("\n"), "soft wrapping preserves explicit source line breaks");
                int cancellations = 0;
                main.CancelRequested += delegate { cancellations++; };
                main.Busy(true);
                main.SetSource("programmatic source");
                check(cancellations == 0, "starting a request does not cancel its programmatic source update");
                input.AppendText(" edited");
                check(cancellations == 1, "editing source cancels an obsolete translation request");
                main.Busy(false);
                main.SetSource("Translate\nthis text");
                main.SetOutput("translation");
                MarkUserResize(main);
                Size resized = new Size(Ui.Scale(main, 860), Ui.Scale(main, 640));
                main.ClientSize = resized;
                main.SetStatus("status after resizing");
                main.SetOutput(new string('A', 1000));
                check(main.ClientSize == resized, "translation updates preserve the user resized window");
                main.RefreshSettings(new AppSettings { VolcId = "changed-credential" });
                check(Descendants(main).OfType<ServiceCard>().Any(card => card.Value == new string('A', 1000)), "refreshing unchanged languages retains valid translation results");
                main.RefreshSettings(new AppSettings { TargetLanguage = "fr" });
                check(Descendants(main).OfType<ServiceCard>().All(card => card.Value == "" && !card.Expanded), "changing target language in settings clears obsolete translations");
                main.SetOutput("French translation");
                main.RefreshSettings(new AppSettings { SourceLanguage = "en", TargetLanguage = "fr" });
                check(Descendants(main).OfType<ServiceCard>().All(card => card.Value == "" && !card.Expanded), "changing source language in settings clears obsolete translations");
                ocr.SetSource("updated OCR");
                check(input.Text.Contains("Translate"), "OCR edits do not replace translation source");
                main.SetOutput("short translation");
                main.RefreshSettings(new AppSettings { SourceLanguage = "en", TargetLanguage = "fr", TranslateAutoHeight = false });
                Size manualSize = main.ClientSize;
                main.RefreshSettings(new AppSettings { SourceLanguage = "en", TargetLanguage = "fr", TranslateAutoHeight = true });
                check(main.ClientSize.Width == manualSize.Width && main.ClientSize.Height < manualSize.Height, "explicitly re-enabling auto height resumes content sizing after a manual resize");
                IntPtr style = Native.SendMessage(main.Handle, 0x84, IntPtr.Zero, Pack(main.PointToScreen(new Point(2, main.ClientSize.Height / 2))));
                check(style.ToInt32() == 10, "translation left edge exposes native resize hit test");
                check(Native.SendMessage(main.Handle, 0x84, IntPtr.Zero, Pack(main.PointToScreen(new Point(main.ClientSize.Width - 3, main.ClientSize.Height - 3)))).ToInt32() == 17, "translation corner exposes native resize hit test");
                FormattingMenu(main, input, check);
                FormattingMenu(ocr, ocrText, check);
                ocr.Busy(true);
                var format = Descendants(ocr).OfType<GlyphButton>().Single(button => button.AccessibleName == "文本格式");
                format.ContextMenuStrip.Items[2].PerformClick();
                check(!format.Enabled && ocrText.Text == "already lower", "OCR formatting is disabled while recognition owns the text");
            }
        }
        private static void OcrCharacterSelection(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var form = new OcrForm { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
                {
                    form.Show();
                    var text = Descendants(form).OfType<RichTextBox>().Single();
                    long options = Native.SendMessage(text.Handle, 0x44e, IntPtr.Zero, IntPtr.Zero).ToInt64();
                    check((options & 1) == 0, "OCR initially disables native automatic word expansion");
                    typeof(Control).GetMethod("RecreateHandle", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(text, null);
                    long recreated = Native.SendMessage(text.Handle, 0x44e, IntPtr.Zero, IntPtr.Zero).ToInt64();
                    check((recreated & 1) == 0 && (recreated & ~1L) == (options & ~1L), "OCR handle recreation keeps automatic word expansion off and preserves other RichEdit options");
                    form.Exiting = true; form.Close();
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static void FormattingMenu(Form form, RichTextBox text, Action<bool, string> check)
        {
            var button = Descendants(form).OfType<GlyphButton>().Single(control => control.AccessibleName == "文本格式");
            IntPtr handle = button.Handle;
            var tip = (ToolTip)typeof(GlyphButton).GetField("hints", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(button);
            var menu = button.ContextMenuStrip;
            check(menu.Items.Count == 5 && tip.GetToolTip(button).Contains("驼峰") && !Descendants(form).OfType<Button>().Any(control => control.Text == "合并断行"), form.GetType().Name + " formatting icon has a tooltip and five named menu operations");
            text.Text = "before HTTP_server after"; text.ClearUndo(); text.Select(7, 11);
            menu.Items[4].PerformClick();
            check(text.Text == "before httpServer after" && text.SelectedText == "httpServer", form.GetType().Name + " formatting changes only selected text");
            text.Undo();
            check(text.Text == "before HTTP_server after", form.GetType().Name + " selected formatting is one undo step");
            text.Text = "hello\nworld"; text.Select(3, 0); menu.Items[2].PerformClick();
            check(text.Text == "HELLO\nWORLD", form.GetType().Name + " no selection formats the entire source and preserves line breaks");
            text.Text = "hello"; text.ClearUndo(); text.AppendText(" world"); text.Select(0, 0); menu.Items[3].PerformClick();
            text.Undo();
            check(text.Text == "hello world", form.GetType().Name + " format undo retains the preceding edit");
            text.Undo();
            check(text.Text == "hello", form.GetType().Name + " preceding edit remains in undo history");
            text.Clear(); text.ClearUndo(); menu.Items[3].PerformClick();
            check(text.TextLength == 0 && !text.CanUndo, form.GetType().Name + " formatting empty source does not create an edit");
            text.Text = "already lower"; text.ClearUndo(); text.Select(5, 0); menu.Items[1].PerformClick();
            check(!text.CanUndo && text.SelectionStart == 5 && text.SelectionLength == 0, form.GetType().Name + " unchanged formatting preserves cursor and undo history");
        }
        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control nested in Descendants(child)) yield return nested;
            }
        }
        private static IntPtr Pack(Point point) { return new IntPtr(unchecked((point.Y << 16) | (point.X & 0xffff))); }
        private static void MarkUserResize(Form form)
        {
            IntPtr rectangle = Marshal.AllocHGlobal(16);
            try
            {
                Marshal.Copy(new[] { form.Left, form.Top, form.Right, form.Bottom }, 0, rectangle, 4);
                Native.SendMessage(form.Handle, 0x214, new IntPtr(8), rectangle);
            }
            finally { Marshal.FreeHGlobal(rectangle); }
        }
    }
}

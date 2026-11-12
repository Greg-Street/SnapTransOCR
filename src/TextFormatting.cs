using System;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class TextFormatting
    {
        public static string CamelCase(string value, bool upperFirst)
        {
            string words = Regex.Replace(value ?? "", @"([\p{Ll}\p{Nd}])(\p{Lu})", "$1 $2");
            words = Regex.Replace(words, @"(\p{Lu})(\p{Lu}\p{Ll})", "$1 $2");
            var result = new StringBuilder();
            foreach (string word in Regex.Split(words, @"[\s_-]+"))
            {
                if (word.Length == 0) continue;
                string lower = word.ToLowerInvariant();
                string first = StringInfo.GetNextTextElement(lower);
                result.Append(result.Length == 0 && !upperFirst ? first : first.ToUpperInvariant());
                result.Append(lower, first.Length, lower.Length - first.Length);
            }
            return result.ToString();
        }
        public static string MergeLines(string value)
        {
            var result = new StringBuilder();
            string previous = "";
            bool paragraph = false;
            foreach (string raw in (value ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { paragraph = result.Length > 0; continue; }
                if (result.Length > 0)
                {
                    if (paragraph) result.Append("\r\n\r\n");
                    else if (NeedsSpace(previous, line)) result.Append(' ');
                }
                result.Append(line);
                previous = line;
                paragraph = false;
            }
            return result.ToString();
        }
        private static bool NeedsSpace(string previous, string next)
        {
            int last = previous.Length - 1;
            if (Char.IsLowSurrogate(previous[last]) && last > 0 && Char.IsHighSurrogate(previous[last - 1])) last--;
            int previousCode = last + 1 < previous.Length ? Char.ConvertToUtf32(previous, last) : previous[last];
            int nextCode = Char.IsSurrogatePair(next, 0) ? Char.ConvertToUtf32(next, 0) : next[0];
            if (IsCjk(previousCode) || IsCjk(nextCode)) return false;
            return "([{“‘-".IndexOf(previous[previous.Length - 1]) < 0 && ".,!?;:%)]}”’".IndexOf(next[0]) < 0;
        }
        private static bool IsCjk(int code)
        {
            return code >= 0x2E80 && code <= 0x9FFF || code >= 0xAC00 && code <= 0xD7AF || code >= 0xF900 && code <= 0xFAFF || code >= 0xFE30 && code <= 0xFE4F || code >= 0x20000 && code <= 0x323AF || code <= Char.MaxValue && "，。！？：；、（）【】《》".IndexOf((char)code) >= 0;
        }
    }

    internal static class TextFormatMenu
    {
        public static GlyphButton CreateButton(RichTextBox text)
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false, Font = Ui.Font };
            Add(menu, "合并断行", text, TextFormatting.MergeLines);
            Add(menu, "全部小写  hello world", text, value => value.ToLowerInvariant());
            Add(menu, "全部大写  HELLO WORLD", text, value => value.ToUpperInvariant());
            Add(menu, "大驼峰  HelloWorld", text, value => TextFormatting.CamelCase(value, true));
            Add(menu, "小驼峰  helloWorld", text, value => TextFormatting.CamelCase(value, false));
            GlyphButton button = null;
            button = new GlyphButton("text", "文本格式：合并断行、大小写、驼峰转换", delegate { menu.Show(button, new Point(0, button.Height)); })
                { BackColor = Color.White, AccessibleName = "文本格式", ContextMenuStrip = menu };
            button.Disposed += delegate { menu.Dispose(); };
            return button;
        }
        private static void Add(ContextMenuStrip menu, string title, RichTextBox text, Func<string, string> transform)
        {
            menu.Items.Add(title, null, delegate { Apply(text, transform); });
        }
        internal static void Apply(RichTextBox text, Func<string, string> transform)
        {
            if (text.ReadOnly || text.TextLength == 0) return;
            bool selected = text.SelectionLength > 0;
            int start = selected ? text.SelectionStart : 0;
            string original = selected ? text.SelectedText : text.Text;
            string changed = transform(original);
            if (changed == original) return;
            Native.SendMessage(text.Handle, 0x458, IntPtr.Zero, IntPtr.Zero); // EM_STOPGROUPTYPING
            text.Select(start, original.Length);
            text.SelectedText = changed;
            Native.SendMessage(text.Handle, 0x458, IntPtr.Zero, IntPtr.Zero);
            text.Select(start, changed.Length);
            text.Focus();
        }
    }
}

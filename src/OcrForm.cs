using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class OcrForm : RoundedForm
    {
        public event Action SettingsRequested, CancelRequested;
        public event Action<string> TranslateRequested;
        public bool Exiting;
        private readonly Label caption, status;
        private readonly RoundedPanel content;
        private readonly RichTextBox source;
        private readonly GlyphButton pin, settingsButton, close, copy, cancel, format;
        private readonly Button translate;
        private readonly CheckBox wrap;
        private bool translationEnabled = true;
        private string defaultCaption = "文字识别";
        private RecognitionKind resultKind = RecognitionKind.Text;
        private byte[] spreadsheet;
        private readonly GlyphButton saveSpreadsheet;

        protected override CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                value.ExStyle |= 0x02000000; // Keep the native editor and its toolbar in the same presentation.
                return value;
            }
        }

        public OcrForm()
        {
            Text = "SnapTransOCR · OCR"; ClientSize = new Size(560, 500); MinimumSize = new Size(360, 420); KeyPreview = true;
            caption = new Label { Text = "文字识别", AutoSize = false, AutoEllipsis = true, ForeColor = Ui.Muted, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9F) };
            caption.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Shapes.Drag(this); };
            pin = new GlyphButton("pin", "置顶识别窗口", delegate { TopMost = !TopMost; pin.Selected = TopMost; pin.Invalidate(); });
            settingsButton = new GlyphButton("settings", "设置", delegate { if (SettingsRequested != null) SettingsRequested(); });
            close = new GlyphButton("close", "关闭识别窗口", Close);
            content = new RoundedPanel { Fill = Color.White, BorderColor = Color.FromArgb(169, 170, 174), Radius = 14 };
            source = new RichTextBox { Multiline = true, AutoWordSelection = false, DetectUrls = false, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 11F), BackColor = Color.White, ForeColor = Ui.Ink, ScrollBars = RichTextBoxScrollBars.Vertical, MaxLength = 100000, AccessibleName = "识别文字" };
            source.HandleCreated += delegate { Native.SendMessage(source.Handle, 0x44d, new IntPtr(3), new IntPtr(~1)); }; // Preserve RichEdit options while clearing ECO_AUTOWORDSELECTION.
            copy = new GlyphButton("copy", "复制识别文字", async delegate { try { await Clip.SetText(source.Text); } catch (Exception ex) { Ui.Error(this, ex); } }) { BackColor = Color.White };
            format = TextFormatMenu.CreateButton(source);
            saveSpreadsheet = new GlyphButton("save", "保存识别结果为 Excel", SaveSpreadsheet) { BackColor = Color.White, Visible = false };
            source.TextChanged += delegate { ClearSpreadsheet(); };
            wrap = new CheckBox { Text = "自动换行", Checked = true, BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 9F) };
            wrap.CheckedChanged += delegate { Ui.SetWordWrap(source, wrap.Checked); };
            translate = Ui.TextAction("翻译  Ctrl+Enter", delegate { if (TranslateRequested != null && !String.IsNullOrWhiteSpace(source.Text)) TranslateRequested(source.Text); });
            translate.ForeColor = Ui.Accent;
            cancel = new GlyphButton("stop", "停止识别", delegate { if (CancelRequested != null) CancelRequested(); }) { BackColor = Color.White, Visible = false };
            status = new Label { AutoSize = false, ForeColor = Ui.Muted, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 8F) };
            content.Controls.AddRange(new Control[] { source, copy, format, saveSpreadsheet, wrap, translate, cancel });
            Controls.AddRange(new Control[] { caption, pin, settingsButton, close, content, status });
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && e.Y < S(44)) Shapes.Drag(this); };
            source.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; translate.PerformClick(); } };
            Resize += delegate { LayoutContent(); };
            ResumeLayout(true);
            LayoutContent();
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            MinimumSize = new Size(S(360), S(420));
            LayoutContent();
        }
        private int S(int value) { return Ui.Scale(this, value); }
        private void LayoutContent()
        {
            if (status == null || WindowState == FormWindowState.Minimized) return;
            int width = ClientSize.Width, statusHeight = String.IsNullOrWhiteSpace(status.Text) ? 0 : S(30);
            pin.SetBounds(S(14), S(6), S(38), S(38)); caption.SetBounds(S(62), S(17), Math.Max(0, width - S(160)), S(24));
            settingsButton.SetBounds(width - S(92), S(6), S(38), S(38)); close.SetBounds(width - S(48), S(6), S(38), S(38));
            content.SetBounds(S(16), S(54), width - S(32), ClientSize.Height - S(70) - statusHeight);
            source.SetBounds(S(16), S(16), content.Width - S(32), Math.Max(S(80), content.Height - S(78)));
            int y = content.Height - S(50);
            copy.SetBounds(S(12), y, S(38), S(38));
            format.SetBounds(S(56), y, S(38), S(38)); wrap.SetBounds(S(112), y + S(1), S(105), S(36));
            saveSpreadsheet.Bounds = format.Bounds;
            int actionWidth = width < S(440) ? S(88) : S(158);
            translate.Text = width < S(440) ? "翻译" : "翻译  Ctrl+Enter";
            translate.SetBounds(content.Width - actionWidth - S(12), y, actionWidth, S(38));
            cancel.SetBounds(content.Width - S(50), y, S(38), S(38));
            status.SetBounds(S(18), ClientSize.Height - statusHeight - S(8), width - S(36), statusHeight);
        }
        public void RefreshSettings(AppSettings value)
        {
            defaultCaption = "文字识别 · " + (value.OcrProvider == "tencent" ? "腾讯 OCR" : "百度 OCR");
            UpdateCaption();
            translationEnabled = value.IsTranslationEnabled(value.TranslateProvider);
            translate.Enabled = translationEnabled && !source.ReadOnly;
        }
        public void Present(string text, bool nearCursor = false)
        {
            if (text != null) SetSource(text);
            if (nearCursor && !Visible) Ui.NearCursor(this);
            if (!Visible) Show();
            Native.ShowWindow(Handle, 9); Native.SetForegroundWindow(Handle); Activate();
        }
        public void SetSource(string value) { ClearSpreadsheet(); resultKind = RecognitionKind.Text; UpdateCaption(); source.Text = value ?? ""; }
        internal void SetRecognitionResult(RecognitionResult result, RecognitionKind kind)
        {
            SetSource(result.Text);
            resultKind = kind; UpdateCaption();
            // The bounded server workbook lives only while its unchanged result is open.
            spreadsheet = result.Spreadsheet;
            saveSpreadsheet.Visible = spreadsheet != null && spreadsheet.Length > 0;
            format.Visible = !saveSpreadsheet.Visible;
        }
        private void UpdateCaption()
        {
            caption.Text = resultKind == RecognitionKind.Text ? defaultCaption :
                (resultKind == RecognitionKind.Table || resultKind == RecognitionKind.BaiduTable) ? "表格识别 · 可复制到 Excel" : RecognitionMenu.Name(resultKind);
        }
        private void ClearSpreadsheet()
        {
            spreadsheet = null;
            if (saveSpreadsheet != null) saveSpreadsheet.Visible = false;
            if (format != null) format.Visible = true;
        }
        private async void SaveSpreadsheet()
        {
            byte[] data = spreadsheet;
            if (data == null) return;
            using (var dialog = new SaveFileDialog { Filter = "Excel 工作簿|*.xlsx", DefaultExt = "xlsx", AddExtension = true, FileName = "表格识别.xlsx" })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    await SaveWorkbookAsync(dialog.FileName, data);
                }
                catch (Exception ex) { if (!IsDisposed) Ui.Error(this, ex); }
            }
        }
        internal static async Task SaveWorkbookAsync(string destination, byte[] data)
        {
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, true))
                    await stream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
                if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public void SetStatus(string value) { status.Text = value; LayoutContent(); }
        public void Busy(bool value)
        {
            source.ReadOnly = value;
            copy.Enabled = format.Enabled = !value;
            saveSpreadsheet.Enabled = !value;
            translate.Enabled = !value && translationEnabled;
            translate.Visible = !value;
            cancel.Visible = value;
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; if (CancelRequested != null) CancelRequested(); ClearSpreadsheet(); Hide(); }
            base.OnFormClosing(e);
        }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); base.OnKeyDown(e); }
    }
}

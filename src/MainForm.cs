using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class ResultTextBox : RichTextBox
    {
        public event Action<int> DocumentScrolled;
        public event Action CaretNavigated;
        public Control WheelTarget;
        private bool positioning;
        private int knownOffset;
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr value, ref Point point);
        public int DocumentOffset { get { return IsHandleCreated ? -GetPositionFromCharIndex(0).Y : 0; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.SendMessage(Handle, 0x44d, new IntPtr(3), new IntPtr(~1)); // EM_SETOPTIONS: clear ECO_AUTOWORDSELECTION only.
        }
        public void PositionDocument(int offset)
        {
            offset = Math.Max(0, offset);
            if (IsHandleCreated && knownOffset == offset && DocumentOffset == offset) return;
            positioning = true;
            try
            {
                var point = new Point(0, offset);
                SendMessage(Handle, 0x4de, IntPtr.Zero, ref point); // EM_SETSCROLLPOS accepts full pixel coordinates.
                knownOffset = DocumentOffset; // EM_GETSCROLLPOS scales documents taller than 65535 pixels.
            }
            finally { positioning = false; }
        }
        protected override void OnVScroll(EventArgs e)
        {
            base.OnVScroll(e);
            ReportScroll();
        }
        private void ReportScroll()
        {
            if (positioning || !IsHandleCreated) return;
            int offset = DocumentOffset, delta = offset - knownOffset;
            knownOffset = offset;
            if (delta != 0 && DocumentScrolled != null) DocumentScrolled(delta);
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x20a && WheelTarget != null)
            {
                Native.SendMessage(WheelTarget.Handle, m.Msg, m.WParam, m.LParam);
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x100 || m.Msg == 0xb7 || m.Msg == 0x200 || m.Msg == 0x113)
            {
                ReportScroll();
                int key = m.WParam.ToInt32();
                if (!positioning && CaretNavigated != null && (m.Msg == 0xb7 || (m.Msg == 0x100 && key >= 0x21 && key <= 0x28))) CaretNavigated();
            }
        }
    }

    internal sealed class ResultPanel : Panel
    {
        public event Action ViewportChanged;
        protected override Point ScrollToControl(Control activeControl) { return DisplayRectangle.Location; }
        protected override void OnScroll(ScrollEventArgs e) { base.OnScroll(e); Changed(); }
        protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Changed(); }
        private void Changed() { if (ViewportChanged != null) ViewportChanged(); }
        public void ScrollBy(int delta)
        {
            AutoScrollPosition = new Point(0, Math.Max(0, -AutoScrollPosition.Y + delta));
            Changed();
        }
    }

    internal sealed class ServiceCard : RoundedPanel
    {
        private readonly Label title, badge;
        private readonly GlyphButton expand, copy;
        private readonly ResultTextBox result;
        private string measuredValue;
        private Font measuredFont;
        private int measuredWidth, measuredDpi, measuredHeight, documentHeight;
        private int viewportOffset, logicalHeight;
        private bool mapping;
        [DllImport("user32.dll")] private static extern bool GetCaretPos(out Point point);
        public int LogicalTop { get; private set; }
        public string Value = "";
        public bool IsNotice { get; private set; }
        public bool Expanded;
        public event Action HeaderClick, SizeChangedByContent;
        public event Action<int> ScrollRequested;
        public ServiceCard(string caption, string letter)
        {
            Radius = 13; Fill = Color.FromArgb(232, 232, 234);
            title = new Label { Text = caption, Font = new Font("Microsoft YaHei UI", 10.5F), BackColor = Color.Transparent, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Cursor = Cursors.Hand };
            badge = new Label { Text = letter, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.White, ForeColor = Ui.Accent, Font = new Font("Segoe UI", 12, FontStyle.Bold) };
            result = new ResultTextBox { Multiline = true, ReadOnly = true, DetectUrls = false, AutoWordSelection = false, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Ui.Ink, ScrollBars = RichTextBoxScrollBars.None, Font = new Font("Microsoft YaHei UI", 10.5F), AccessibleName = caption + "译文" };
            result.ContentsResized += delegate(object sender, ContentsResizedEventArgs e) { documentHeight = e.NewRectangle.Height; };
            result.DocumentScrolled += delegate(int delta) { if (!mapping && ScrollRequested != null) ScrollRequested(delta); };
            result.CaretNavigated += EnsureCaretVisible;
            expand = new GlyphButton("chevron", "请求翻译 / 展开空结果", delegate { if (HeaderClick != null) HeaderClick(); });
            copy = new GlyphButton("copy", "复制这项译文", async delegate { try { await Clip.SetText(Value); } catch (Exception ex) { Ui.Error(FindForm(), ex); } });
            title.Click += delegate { if (HeaderClick != null) HeaderClick(); };
            Controls.Add(title); Controls.Add(badge); Controls.Add(expand); Controls.Add(result); Controls.Add(copy);
            SetValue("", false);
        }
        public void SetValue(string value, bool expanded)
        {
            value = value ?? "";
            IsNotice = false; copy.Enabled = !String.IsNullOrWhiteSpace(value);
            mapping = true;
            try
            {
                if (Value != value) { measuredValue = null; Value = value; result.Text = Value; }
                Expanded = expanded || Value.Length > 0;
                Fill = Expanded ? Color.White : Color.FromArgb(232, 232, 234);
                title.Cursor = Value.Length > 0 ? Cursors.Default : Cursors.Hand;
                logicalHeight = DesiredHeight;
            }
            finally { mapping = false; }
            Invalidate(); PerformLayout();
            if (SizeChangedByContent != null) SizeChangedByContent();
        }
        public void SetNotice(string value) { SetValue(value, true); IsNotice = true; copy.Enabled = false; title.Cursor = Cursors.Hand; }
        public void SetCaption(string value) { title.Text = value; result.AccessibleName = value + "译文"; }
        public int DesiredHeight { get { return MeasureHeight(Width); } }
        public int MeasureHeight(int width)
        {
            if (!Expanded) return S(40);
            int dpi = ((Control)FindForm() ?? this).DeviceDpi;
            int available = Math.Max(1, width - S(32));
            if (measuredValue == Value && measuredFont == result.Font && measuredWidth == available && measuredDpi == dpi) return measuredHeight;
            bool wasMapping = mapping; mapping = true;
            SuspendLayout();
            try
            {
                result.Width = available;
                Native.SendMessage(result.Handle, 0x448, IntPtr.Zero, IntPtr.Zero); // Reflow hidden cards at their new viewport width.
                Native.SendMessage(result.Handle, 0x441, IntPtr.Zero, IntPtr.Zero); // EM_REQUESTRESIZE measures the entire native wrapped document.
                measuredValue = Value; measuredFont = result.Font; measuredWidth = available; measuredDpi = dpi;
                return measuredHeight = S(84) + Math.Max(S(60), documentHeight);
            }
            finally { ResumeLayout(false); mapping = wasMapping; }
        }
        public void SetViewport(int top, int width, int height, ResultPanel parent)
        {
            int y = top + parent.AutoScrollPosition.Y;
            int visibleHeight = Math.Max(0, Math.Min(parent.ClientSize.Height, y + height) - Math.Max(0, y));
            mapping = true;
            try
            {
                SuspendLayout();
                try
                {
                    LogicalTop = top; logicalHeight = height;
                    viewportOffset = Math.Max(0, -y);
                    result.WheelTarget = parent;
                    // Native child windows cannot exceed 65535 pixels. Keep one complete document in a viewport-sized editor.
                    SetBounds(0, Math.Max(-30000, Math.Min(30000, Math.Max(0, y))), width, visibleHeight);
                    Visible = visibleHeight > 0;
                }
                finally { ResumeLayout(false); }
                if (visibleHeight > 0) PerformLayout();
            }
            finally { mapping = false; }
        }
        private void EnsureCaretVisible()
        {
            if (mapping || ScrollRequested == null || result.Parent == null) return;
            int index = Math.Min(result.TextLength, result.SelectionStart + result.SelectionLength);
            Point caret;
            if (!result.Focused || !GetCaretPos(out caret)) caret = result.GetPositionFromCharIndex(index);
            int top = Top + result.Top + caret.Y;
            int bottom = top + result.Font.Height;
            if (top < 0) ScrollRequested(top);
            else if (Parent != null && bottom > Parent.ClientSize.Height) ScrollRequested(bottom - Parent.ClientSize.Height);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (title == null) return;
            bool wasMapping = mapping; mapping = true;
            SuspendLayout();
            try
            {
                int headerY = -Math.Min(S(40), viewportOffset);
                badge.SetBounds(S(7), headerY + S(7), S(27), S(27)); title.SetBounds(S(43), headerY, Math.Max(S(80), Width - S(85)), S(40));
                expand.SetBounds(Width - S(36), headerY + S(3), S(32), S(32)); expand.BackColor = Fill;
                bool headerVisible = viewportOffset < S(40);
                badge.Visible = title.Visible = headerVisible; expand.Visible = headerVisible && Value.Length == 0;
                int bodyTop = S(46), start = Math.Max(bodyTop, viewportOffset), end = Math.Min(bodyTop + documentHeight, viewportOffset + Height);
                bool bodyVisible = Expanded && end > start;
                result.Visible = bodyVisible;
                if (bodyVisible)
                {
                    result.SetBounds(S(16), start - viewportOffset, Math.Max(1, Width - S(32)), end - start);
                    result.PositionDocument(start - bodyTop);
                }
                int copyTop = logicalHeight - S(36) - viewportOffset;
                copy.Visible = Expanded && copyTop < Height && copyTop + S(30) > 0;
                copy.SetBounds(Width - S(41), Math.Max(-S(30), Math.Min(Height, copyTop)), S(30), S(30)); copy.BackColor = Color.White;
            }
            finally { ResumeLayout(false); mapping = wasMapping; }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var brush = new SolidBrush(Fill))
            {
                if (viewportOffset > 0) e.Graphics.FillRectangle(brush, 0, 0, Width, Math.Min(Height, Radius));
                if (viewportOffset + Height < logicalHeight) e.Graphics.FillRectangle(brush, 0, Math.Max(0, Height - Radius), Width, Radius);
            }
        }
        private int S(int value) { return Ui.Scale((Control)FindForm() ?? this, value); }
    }
    internal sealed class MainForm : RoundedForm
    {
        public event Action SettingsRequested, HistoryRequested, CancelRequested;
        public event Action<string> TranslateRequested;
        public event Action<string, string> ServiceTranslateRequested, LanguagesChanged;
        public event Action<int, int> UserSizeChanged;
        public bool Exiting;
        internal bool CaptureInProgress { get; set; }
        private readonly RichTextBox source;
        private readonly Label status, caption;
        private readonly RoundedPanel input, language;
        private readonly GlyphButton translate, pin, settingsButton, historyButton, close, swap, format;
        private readonly ComboBox from, to;
        private readonly ResultPanel results;
        private readonly Dictionary<string, ServiceCard> cards = new Dictionary<string, ServiceCard>();
        private readonly HashSet<string> enabledProviders = new HashSet<string> { "volc", "baidu" };
        private string activeProvider = "volc";
        private string arkModel;
        private string[] providerOrder = (string[])AppSettings.TranslationProviders.Clone();
        private bool busy, refreshing, autoHeight = true, hideOnBlur, userSized, layingOut, settingSource, clearingResults;
        private bool settingsLoaded, resizing;
        private readonly string[] sources = { "auto", "zh", "en", "ja", "ko", "fr", "de", "ru", "es" };
        private readonly string[] targets = { "smart", "zh", "en", "ja", "ko", "fr", "de", "ru", "es" };
        [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
        [DllImport("user32.dll")] private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);

        protected override CreateParams CreateParams
        {
            get
            {
                var value = base.CreateParams;
                value.ExStyle |= 0x02000000; // Compose the native editors and other child controls in one presentation.
                return value;
            }
        }

        public MainForm()
        {
            Text = "SnapTransOCR · 翻译"; ClientSize = new Size(520, 500); MinimumSize = new Size(360, 460); KeyPreview = true;
            pin = MakeIcon("pin", "置顶翻译窗口", delegate { TopMost = !TopMost; pin.Selected = TopMost; pin.Invalidate(); });
            settingsButton = MakeIcon("settings", "设置", delegate { if (SettingsRequested != null) SettingsRequested(); });
            historyButton = MakeIcon("history", "历史记录", delegate { if (HistoryRequested != null) HistoryRequested(); });
            close = MakeIcon("close", "关闭翻译窗口", Close);
            caption = new Label { Text = "翻译", ForeColor = Ui.Muted, AutoSize = true, BackColor = Color.Transparent, Font = new Font("Microsoft YaHei UI", 9F) };
            caption.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Shapes.Drag(this); };
            input = new RoundedPanel { BorderColor = Color.FromArgb(169, 170, 174), Fill = Color.White, Radius = 14 };
            source = new RichTextBox { Multiline = true, AutoWordSelection = false, DetectUrls = false, BorderStyle = BorderStyle.None, Font = new Font("Microsoft YaHei UI", 11F), BackColor = Color.White, ForeColor = Ui.Ink, ScrollBars = RichTextBoxScrollBars.Vertical, MaxLength = 100000, AccessibleName = "原文" };
            // Clear the native flag after every handle creation without toggling unrelated RichEdit options.
            source.HandleCreated += delegate { Native.SendMessage(source.Handle, 0x44d, new IntPtr(3), new IntPtr(~1)); }; // EM_SETOPTIONS, ECOOP_AND, ~ECO_AUTOWORDSELECTION
            input.Controls.Add(source);
            var copy = MakeIcon("copy", "复制原文", async delegate { try { await Clip.SetText(source.Text); } catch (Exception ex) { Ui.Error(this, ex); } });
            var paste = MakeIcon("paste", "粘贴文本", delegate { try { if (Clipboard.ContainsText()) source.Text = Clipboard.GetText(); } catch (Exception ex) { Ui.Error(this, ex); } });
            var clear = MakeIcon("close", "清空", delegate { if (CancelRequested != null) CancelRequested(); SetSource(""); SetStatus(""); });
            translate = MakeIcon("translate", "翻译 / Ctrl+Enter", delegate { if (busy) { if (CancelRequested != null) CancelRequested(); } else if (TranslateRequested != null) TranslateRequested(source.Text); });
            translate.Primary = true; translate.BackColor = Color.White;
            input.Controls.Add(copy); input.Controls.Add(paste); input.Controls.Add(clear); input.Controls.Add(translate);
            input.Tag = new[] { copy, paste, clear };
            format = TextFormatMenu.CreateButton(source);
            input.Controls.Add(format);
            language = new RoundedPanel { Fill = Color.White, Radius = 13 };
            from = Ui.Combo(new[] { "自动检测", "简体中文", "英语", "日语", "韩语", "法语", "德语", "俄语", "西班牙语" }, 0);
            to = Ui.Combo(new[] { "智能中英互译", "简体中文", "英语", "日语", "韩语", "法语", "德语", "俄语", "西班牙语" }, 0);
            from.FlatStyle = to.FlatStyle = FlatStyle.Flat;
            swap = MakeIcon("swap", "交换源语言与目标语言", delegate
            {
                int oldFrom = from.SelectedIndex, oldTo = to.SelectedIndex;
                refreshing = true; from.SelectedIndex = oldTo == 0 ? 1 : oldTo; to.SelectedIndex = oldFrom == 0 ? 2 : oldFrom; refreshing = false; RaiseLanguage();
            });
            language.Controls.Add(from); language.Controls.Add(to); language.Controls.Add(swap);
            from.SelectionChangeCommitted += delegate { RaiseLanguage(); };
            to.SelectionChangeCommitted += delegate { RaiseLanguage(); };
            results = new ResultPanel { AutoScroll = true, BackColor = Ui.Background };
            results.ViewportChanged += LayoutResults;
            foreach (string provider in AppSettings.TranslationProviders)
            {
                AddServiceCard(provider, AppSettings.TranslationName(provider), provider == "volc" ? "V" : provider == "bing" ? "M" : provider == "ark" ? "A" : "B");
            }
            source.TextChanged += delegate
            {
                if (!settingSource && busy && CancelRequested != null) CancelRequested();
                ClearResults();
            };
            status = new Label { Text = "", AutoSize = false, ForeColor = Ui.Muted, Font = new Font("Microsoft YaHei UI", 8F), BackColor = Color.Transparent };
            Controls.Add(input); Controls.Add(language); Controls.Add(results); Controls.Add(pin); Controls.Add(historyButton); Controls.Add(settingsButton); Controls.Add(close); Controls.Add(caption); Controls.Add(status);
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && e.Y < S(44)) Shapes.Drag(this); };
            source.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Control && e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; translate.PerformClick(); } };
            Deactivate += delegate { if (hideOnBlur && !CaptureInProgress && !busy && !Exiting) Hide(); };
            Resize += delegate { LayoutContent(); };
            ResumeLayout(true);
            LayoutContent();
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            base.OnDpiChanged(e);
            MinimumSize = new Size(S(360), S(460));
            LayoutContent();
        }
        private GlyphButton MakeIcon(string name, string title, Action action)
        {
            var button = new GlyphButton(name, title, action) { BackColor = Ui.Background };
            return button;
        }
        private void AddServiceCard(string provider, string title, string badge)
        {
            var card = new ServiceCard(title, badge) { Visible = enabledProviders.Contains(provider) };
            card.HeaderClick += delegate
            {
                if (!enabledProviders.Contains(provider)) return;
                if ((!card.IsNotice && card.Value.Length > 0) || String.IsNullOrWhiteSpace(source.Text)) card.SetValue(card.Value, !card.Expanded);
                else if (ServiceTranslateRequested != null) ServiceTranslateRequested(source.Text, provider);
            };
            card.SizeChangedByContent += delegate { if (!clearingResults) LayoutContent(); };
            card.ScrollRequested += results.ScrollBy;
            cards.Add(provider, card); results.Controls.Add(card);
        }
        private void RaiseLanguage()
        {
            if (refreshing) return;
            if (busy && CancelRequested != null) CancelRequested();
            ClearResults();
            if (LanguagesChanged != null) LanguagesChanged(sources[from.SelectedIndex], targets[to.SelectedIndex]);
        }
        private void ClearResults()
        {
            bool changed = false;
            clearingResults = true;
            try
            {
                foreach (var card in cards.Values)
                    if (card.Value.Length > 0 || card.Expanded) { card.SetValue("", false); changed = true; }
            }
            finally { clearingResults = false; }
            if (changed) LayoutContent();
        }
        private void LayoutContent()
        {
            if (input == null || status == null || layingOut || WindowState == FormWindowState.Minimized) return;
            layingOut = true;
            SuspendLayout(); input.SuspendLayout(); language.SuspendLayout();
            try
            {
            int width = ClientSize.Width;
            int statusHeight = String.IsNullOrWhiteSpace(status.Text) ? 0 : S(30);
            // Reserve the vertical gutter before measuring, so its appearance cannot widen or rewrap cards.
            int cardWidth = Math.Max(1, width - S(32) - Math.Max(GetSystemMetricsForDpi(2, (uint)DeviceDpi), SystemInformation.VerticalScrollBarWidth));
            int natural = 0;
            foreach (var pair in cards)
            {
                if (!enabledProviders.Contains(pair.Key)) continue;
                natural += pair.Value.MeasureHeight(cardWidth) + S(9);
            }
            if (autoHeight && !userSized)
            {
                int maximum = Screen.FromControl(this).WorkingArea.Height - S(32);
                int desired = Math.Max(MinimumSize.Height, Math.Min(maximum, S(360) + natural + statusHeight + S(16)));
                if (ClientSize.Height != desired) ClientSize = new Size(width, desired);
            }
            pin.SetBounds(S(14), S(6), S(38), S(38)); caption.Location = new Point(S(62), S(17));
            settingsButton.SetBounds(width - S(92), S(6), S(38), S(38)); close.SetBounds(width - S(48), S(6), S(38), S(38));
            historyButton.SetBounds(width - S(136), S(6), S(38), S(38));
            int inputHeight = autoHeight && !userSized ? S(226) : Math.Max(S(176), Math.Min(S(350), (ClientSize.Height - S(100)) * 43 / 100));
            input.SetBounds(S(16), S(54), cardWidth, inputHeight);
            source.SetBounds(S(16), S(15), input.Width - S(32), inputHeight - S(76));
            var controls = (GlyphButton[])input.Tag;
            for (int i = 0; i < controls.Length; i++) { controls[i].SetBounds(S(12 + i * 44), inputHeight - S(49), S(38), S(38)); controls[i].BackColor = Color.White; }
            format.SetBounds(S(144), inputHeight - S(49), S(38), S(38));
            translate.SetBounds(input.Width - S(68), inputHeight - S(63), S(52), S(52));
            int languageHeight = Math.Max(S(52), Math.Max(from.GetPreferredSize(Size.Empty).Height, to.GetPreferredSize(Size.Empty).Height) + S(22));
            language.SetBounds(S(16), input.Bottom + S(14), cardWidth, languageHeight);
            from.SetBounds(S(12), S(11), language.Width / 2 - S(42), S(28)); to.SetBounds(language.Width / 2 + S(32), S(11), language.Width / 2 - S(44), S(28));
            swap.SetBounds(language.Width / 2 - S(16), S(7), S(32), S(32)); swap.BackColor = Color.White;
            int resultTop = language.Bottom + S(14);
            results.SuspendLayout();
            results.SetBounds(S(16), resultTop, width - S(32), Math.Max(S(50), ClientSize.Height - resultTop - statusHeight - S(16)));
            foreach (var card in cards.Values) card.Width = cardWidth;
            results.AutoScrollMinSize = new Size(0, Math.Max(natural, results.ClientSize.Height));
            results.ResumeLayout(true);
            LayoutResults();
            if (results.IsHandleCreated && !results.HorizontalScroll.Visible) ShowScrollBar(results.Handle, 0, false);
            status.SetBounds(S(18), ClientSize.Height - statusHeight - S(10), width - S(36), statusHeight);
            }
            finally { language.ResumeLayout(false); input.ResumeLayout(false); ResumeLayout(false); layingOut = false; }
        }
        private void LayoutResults()
        {
            if (results == null || results.IsDisposed) return;
            int width = input.Width, natural = 0, expanded = 0;
            foreach (string provider in providerOrder)
            {
                if (!enabledProviders.Contains(provider)) continue;
                natural += cards[provider].MeasureHeight(width) + S(9);
                if (cards[provider].Expanded) expanded++;
            }
            int extra = expanded > 0 ? Math.Max(0, results.ClientSize.Height - natural) / expanded : 0;
            int top = 0;
            results.SuspendLayout();
            try
            {
                foreach (string provider in providerOrder)
                {
                    var card = cards[provider];
                    if (!enabledProviders.Contains(provider)) { card.Visible = false; continue; }
                    int height = card.MeasureHeight(width) + (card.Expanded ? extra : 0);
                    card.SetViewport(top, width, height, results);
                    top += height + S(9);
                }
            }
            finally { results.ResumeLayout(false); }
        }
        private int S(int value) { return Ui.Scale(this, value); }
        internal static Size RestoredSize(int width, int height, int dpi, Size workingArea)
        {
            double scale = dpi / 96.0;
            return new Size(
                (int)Math.Min(workingArea.Width, Math.Round(Math.Max(360, width > 0 ? width : 520) * scale)),
                (int)Math.Min(workingArea.Height, Math.Round(Math.Max(460, height > 0 ? height : 500) * scale)));
        }
        private void FitWorkingArea()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(S(360), area.Width), Math.Min(S(460), area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            if (!Ui.RenderOffscreen) Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }
        public void RefreshSettings(AppSettings value)
        {
            if (!settingsLoaded)
            {
                settingsLoaded = true;
                userSized = value.TranslateWindowHeight > 0;
                autoHeight = value.TranslateAutoHeight;
                Rectangle area = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(S(360), area.Width), Math.Min(S(460), area.Height));
                ClientSize = RestoredSize(value.TranslateWindowWidth, value.TranslateWindowHeight, DeviceDpi, area.Size);
            }
            int previousFrom = from.SelectedIndex, previousTo = to.SelectedIndex;
            refreshing = true;
            from.SelectedIndex = Math.Max(0, Array.IndexOf(sources, value.SourceLanguage ?? "auto"));
            to.SelectedIndex = Math.Max(0, Array.IndexOf(targets, value.TargetLanguage));
            refreshing = false;
            if (previousFrom != from.SelectedIndex || previousTo != to.SelectedIndex)
                ClearResults();
            activeProvider = value.TranslateProvider;
            string[] extra = value.ExtraArkModels();
            foreach (string model in extra)
                if (!cards.ContainsKey("ark:" + model)) AddServiceCard("ark:" + model, "Ark · " + TranslationModels.Caption(model), "A");
            if (arkModel != value.ArkModel) cards["ark"].SetValue("", false);
            arkModel = value.ArkModel;
            cards["ark"].SetCaption(extra.Length == 0 ? AppSettings.TranslationName("ark") : "Ark · " + TranslationModels.Caption(value.ArkModel));
            var order = new List<string>();
            foreach (string provider in AppSettings.NormalizeOrder(value.TranslateServiceOrder, AppSettings.TranslationProviders))
            {
                order.Add(provider);
                if (provider == "ark") order.AddRange(extra.Select(model => "ark:" + model));
            }
            providerOrder = order.ToArray();
            foreach (string key in cards.Keys.Where(key => key.StartsWith("ark:", StringComparison.Ordinal) && !extra.Contains(key.Substring(4))).ToArray())
            { var card = cards[key]; cards.Remove(key); results.Controls.Remove(card); card.Dispose(); }
            for (int i = 0; i < providerOrder.Length; i++) results.Controls.SetChildIndex(cards[providerOrder[i]], i);
            enabledProviders.Clear();
            foreach (var pair in cards)
            {
                bool enabled = pair.Key.StartsWith("ark:", StringComparison.Ordinal) ? value.ArkEnabled : value.IsTranslationEnabled(pair.Key);
                if (enabled) enabledProviders.Add(pair.Key);
                pair.Value.Visible = enabled;
            }
            translate.Enabled = busy || enabledProviders.Count > 0;
            if (!autoHeight && value.TranslateAutoHeight) userSized = false;
            autoHeight = value.TranslateAutoHeight; hideOnBlur = value.TranslateHideOnBlur;
            TopMost = value.TranslateTopmost; pin.Selected = TopMost; pin.Invalidate(); LayoutContent();
        }
        public void Present(string text) { Present(text, false); }
        public void Present(string text, bool nearCursor)
        {
            if (text != null) SetSource(text);
            if (nearCursor && !Visible) Ui.NearCursor(this);
            if (!Visible) Show();
            Native.ShowWindow(Handle, 9); FitWorkingArea(); Native.SetForegroundWindow(Handle); Activate();
        }
        public void SetSource(string value)
        {
            if (source.Text == (value ?? "")) return;
            settingSource = true;
            try { source.Text = value ?? ""; }
            finally { settingSource = false; }
        }
        public void SetActiveProvider(string provider) { activeProvider = provider; }
        public void SetOutput(string value, string provider = null) { string key = provider ?? activeProvider; if (enabledProviders.Contains(key)) cards[key].SetValue(value, true); }
        public void SetNotice(string provider, string value) { if (enabledProviders.Contains(provider)) cards[provider].SetNotice(value); }
        public void SetStatus(string value) { status.Text = value; status.Visible = !String.IsNullOrWhiteSpace(value); LayoutContent(); }
        public void Busy(bool value) { busy = value; translate.Enabled = value || enabledProviders.Count > 0; translate.Glyph = value ? "stop" : "translate"; translate.Invalidate(); }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x214) userSized = resizing = true; // WM_SIZING, not moving or automatic height.
            base.WndProc(ref message);
            if (message.Msg == 0x2e0) { FitWorkingArea(); LayoutContent(); }
            if (message.Msg == 0x232 && resizing)
            {
                resizing = false;
                if (WindowState == FormWindowState.Normal && UserSizeChanged != null)
                    UserSizeChanged((int)Math.Round(ClientSize.Width * 96.0 / DeviceDpi), (int)Math.Round(ClientSize.Height * 96.0 / DeviceDpi));
            }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!Exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; if (CancelRequested != null) CancelRequested(); Hide(); }
            base.OnFormClosing(e);
        }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape && !from.DroppedDown && !to.DroppedDown) Close(); base.OnKeyDown(e); }
    }
}

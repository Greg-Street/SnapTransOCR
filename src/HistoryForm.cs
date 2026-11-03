using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class HistoryButton : Button
    {
        internal bool Selected;
        private readonly string glyph;
        private bool hover;
        internal HistoryButton(string text, string glyph, Action action)
        {
            Text = text; this.glyph = glyph; AccessibleName = text; AutoSize = false;
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Cursor = Cursors.Hand;
            BackColor = Ui.Background; UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Click += delegate { action(); };
            MouseEnter += delegate { hover = true; Invalidate(); };
            MouseLeave += delegate { hover = false; Invalidate(); };
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color fill = Selected ? Color.FromArgb(232, 237, 255) : hover && Enabled ? Color.FromArgb(234, 235, 238) : Color.White;
            Color ink = !Enabled ? Ui.Muted : Selected ? Ui.Accent : Ui.Ink;
            using (var path = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Ui.Scale(this, 8)))
            using (var brush = new SolidBrush(fill)) e.Graphics.FillPath(brush, path);
            int size = Ui.Scale(this, 16), gap = Ui.Scale(this, 4);
            int textWidth = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int inset = Math.Max(Ui.Scale(this, 4), (Width - textWidth - size - gap) / 2);
            Glyphs.Draw(e.Graphics, glyph, new RectangleF(inset, (Height - size) / 2, size, size), ink);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(inset + size + gap, 0, Width - inset * 2 - size - gap, Height), ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
            if (Focused && ShowFocusCues)
                using (var path = Shapes.Round(new RectangleF(2, 2, Width - 5, Height - 5), Ui.Scale(this, 6)))
                using (var pen = new Pen(Color.FromArgb(149, 171, 242))) e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class HistoryForm : RoundedForm
    {
        internal readonly HistoryView View;
        internal event Action SettingsRequested;
        internal event Action<bool> PinChanged;
        private readonly Label title, shortcut;
        private readonly GlyphButton pin, settings, close;
        private bool presented;
        internal HistoryForm(HistoryStore store, bool recordingEnabled, bool pinned, string hotkey)
        {
            Text = "SnapTransOCR · 历史与剪贴板"; ClientSize = new Size(900, 620); MinimumSize = new Size(740, 480);
            title = new Label { Text = "历史与剪贴板", AutoSize = true, Font = new Font("Microsoft YaHei UI", 12F), BackColor = Ui.Background };
            shortcut = new Label { Text = hotkey ?? "", AutoSize = true, ForeColor = Ui.Muted, Font = new Font("Segoe UI", 9F), BackColor = Ui.Background };
            pin = new GlyphButton("pin", "置顶历史窗口", delegate
            {
                TopMost = !TopMost; RefreshPin();
                if (PinChanged != null) PinChanged(TopMost);
            });
            settings = new GlyphButton("settings", "打开设置", delegate { if (SettingsRequested != null) SettingsRequested(); });
            close = new GlyphButton("close", "关闭历史窗口（Esc）", Close);
            View = new HistoryView(store, recordingEnabled);
            Controls.AddRange(new Control[] { title, shortcut, pin, settings, close, View });
            MouseDown += DragHeader; title.MouseDown += DragHeader; shortcut.MouseDown += DragHeader;
            TopMost = pinned; RefreshPin(); ResumeLayout(true);
        }
        private void DragHeader(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left && e.Y < Ui.Scale(this, 54)) Shapes.Drag(this); }
        private void RefreshPin() { pin.Selected = TopMost; pin.SetHint(TopMost ? "取消置顶历史窗口" : "置顶历史窗口"); pin.Invalidate(); }
        internal void RefreshShortcut(string hotkey) { shortcut.Text = hotkey ?? ""; PerformLayout(); }
        internal void Present()
        {
            if (!presented)
            {
                presented = true;
                if (Ui.RenderOffscreen) { StartPosition = FormStartPosition.Manual; Location = new Point(-30000, -30000); }
                else
                {
                    Rectangle work = Screen.FromPoint(Cursor.Position).WorkingArea;
                    MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
                    Size size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
                    StartPosition = FormStartPosition.Manual;
                    Bounds = new Rectangle(work.Left + (work.Width - size.Width) / 2, work.Top + (work.Height - size.Height) / 2, size.Width, size.Height);
                }
            }
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Show(); Activate(); View.RefreshEntries(); View.FocusSearch();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (View == null) return;
            int margin = Ui.Scale(this, 18), button = Ui.Scale(this, 32), top = Ui.Scale(this, 12);
            title.Location = new Point(Ui.Scale(this, 48), top + Ui.Scale(this, 3));
            shortcut.Location = new Point(title.Right + Ui.Scale(this, 16), top + Ui.Scale(this, 7));
            close.SetBounds(ClientSize.Width - margin - button, top, button, button);
            settings.SetBounds(close.Left - button - Ui.Scale(this, 6), top, button, button);
            pin.SetBounds(settings.Left - button - Ui.Scale(this, 6), top, button, button);
            View.SetBounds(margin, Ui.Scale(this, 62), Math.Max(1, ClientSize.Width - margin * 2), Math.Max(1, ClientSize.Height - Ui.Scale(this, 76)));
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int size = Ui.Scale(this, 22);
            Glyphs.Draw(e.Graphics, "history", new RectangleF(Ui.Scale(this, 18), Ui.Scale(this, 18), size, size), Ui.Accent);
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { View.FocusSearch(); return true; }
            return base.ProcessCmdKey(ref message, keyData);
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); PerformLayout(); }
    }

    internal sealed class HistoryView : UserControl
    {
        public event Action<bool> RecordingChanged;
        public event Action<Bitmap> ImageRequested;
        private readonly HistoryStore store;
        private readonly ListBox list;
        private readonly TextBox search;
        private readonly TextInputField searchField;
        private readonly HistoryButton[] filterButtons;
        private int filterIndex;
        private readonly RichTextBox source, translation;
        private readonly Label sourceTitle, translationTitle, captureSummary, previewTitle, status, storagePath, recordingLabel, empty;
        private readonly RoundedPanel listPanel, sourcePanel, translationPanel, previewPanel;
        private readonly PictureBox preview;
        private readonly Panel actions;
        private readonly ToggleSwitch recording;
        private readonly GlyphButton copySource, copyTranslation;
        private readonly Button copySelected, remove, clear, openImage;
        private readonly Font metadataFont = new Font("Microsoft YaHei UI", 8.5F);
        private readonly System.Windows.Forms.Timer refreshTimer;
        private readonly ToolTip storageHint;
        private volatile bool storeDirty;
        private bool refreshing, busy, refreshPending, updatingEntries;
        private bool closing;
        private string previewId;
        private string selectedId;
        private HistoryEntry[] entries = new HistoryEntry[0];
        private CancellationTokenSource previewRequest;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr parameter, string text);

        internal HistoryView(HistoryStore store, bool recordingEnabled)
        {
            this.store = store;
            SuspendLayout(); AutoScaleMode = AutoScaleMode.Inherit; BackColor = Ui.Background; Font = Ui.Font; Margin = Padding.Empty;
            Size = new Size(700, 500);
            recordingLabel = new Label { Text = "保存记录", AutoSize = true, BackColor = Color.Transparent, ForeColor = Ui.Muted };
            recording = new ToggleSwitch { Checked = recordingEnabled, AccessibleName = "保存历史记录" };
            recording.CheckedChanged += delegate { if (!refreshing && RecordingChanged != null) RecordingChanged(recording.Checked); };
            search = new TextBox { Font = Ui.Font, ForeColor = Ui.Ink, MaxLength = 512, AccessibleName = "搜索历史原文或译文" };
            search.HandleCreated += delegate { SendMessage(search.Handle, 0x1501, new IntPtr(1), "搜索原文或译文"); }; // EM_SETCUEBANNER
            searchField = new TextInputField(search, "search", true);
            string[] types = { "全部", "截图", "OCR", "翻译", "剪贴板", "文字", "图片" };
            string[] icons = { "general", "crop", "ocr", "translate", "paste", "text", "image" };
            filterButtons = new HistoryButton[types.Length];
            for (int i = 0; i < types.Length; i++)
            {
                int index = i;
                filterButtons[i] = new HistoryButton(types[i], icons[i], delegate { SelectFilter(index); }) { Selected = i == 0, AccessibleName = "筛选" + types[i] };
                Controls.Add(filterButtons[i]);
            }
            listPanel = new RoundedPanel { Radius = 12, Fill = Color.White };
            sourcePanel = new RoundedPanel { Radius = 12, Fill = Color.White, BorderColor = Color.FromArgb(226, 226, 229) };
            translationPanel = new RoundedPanel { Radius = 12, Fill = Color.White, BorderColor = Color.FromArgb(226, 226, 229) };
            previewPanel = new RoundedPanel { Radius = 12, Fill = Color.White, BorderColor = Color.FromArgb(226, 226, 229), Visible = false };
            actions = new Panel { BackColor = Ui.Background };
            list = new ListBox { BorderStyle = BorderStyle.None, BackColor = Color.White, IntegralHeight = false, HorizontalScrollbar = false, DrawMode = DrawMode.OwnerDrawFixed, AccessibleName = "历史与剪贴板" };
            list.DrawItem += DrawEntry;
            list.DoubleClick += async delegate { await CopySelected(); };
            list.SelectedIndexChanged += delegate
            {
                if (updatingEntries) return;
                if (Selected != null) selectedId = Selected.Id;
                ShowSelected();
            };
            list.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.C) { e.Handled = e.SuppressKeyPress = true; await CopySelected(); }
                else if (e.KeyData == Keys.Delete && Selected != null) { e.Handled = e.SuppressKeyPress = true; string id = Selected.Id; await Change(delegate { return store.DeleteAsync(id); }); }
            };
            empty = new Label { Text = "暂无历史记录", ForeColor = Ui.Muted, BackColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
            sourceTitle = Ui.Label("原文"); translationTitle = Ui.Label("译文");
            sourceTitle.ForeColor = Ui.Ink; translationTitle.ForeColor = Ui.Accent;
            captureSummary = new Label { ForeColor = Ui.Muted, BackColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
            previewTitle = Ui.Label("截图 · 单击放大"); previewTitle.ForeColor = Ui.Ink;
            preview = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.FromArgb(247, 247, 248), Cursor = Cursors.Hand, AccessibleName = "截图缩略图，单击放大" };
            preview.Click += async delegate { await ShowImage(); };
            source = TextArea("历史原文"); translation = TextArea("历史译文");
            copySource = new GlyphButton("copy", "复制全部原文", async delegate { await Copy(source.Text); }) { BackColor = Color.White };
            copyTranslation = new GlyphButton("copy", "复制全部译文", async delegate { await Copy(translation.Text); }) { BackColor = Color.White };
            copySelected = new HistoryButton("复制", "copy", async delegate { await CopySelected(); });
            remove = new HistoryButton("删除", "trash", async delegate { HistoryEntry entry = Selected; if (entry != null) await Change(delegate { return store.DeleteAsync(entry.Id); }); });
            clear = new HistoryButton("清空全部", "trash", async delegate { await Change(store.ClearAsync); });
            openImage = new HistoryButton("编辑图片", "image", async delegate { await ShowImage(); });
            status = new Label { Text = "保留 " + store.ActiveRetentionDays + " 天；过期自动清理", ForeColor = Ui.Muted, BackColor = Color.Transparent, AutoEllipsis = true };
            storagePath = new Label { Text = "存储：" + store.DirectoryPath, AccessibleName = "历史存储目录：" + store.DirectoryPath, ForeColor = Ui.Muted, BackColor = Color.Transparent, AutoEllipsis = true };
            storageHint = new ToolTip(); storageHint.SetToolTip(storagePath, store.DirectoryPath);
            listPanel.Controls.Add(list); listPanel.Controls.Add(empty);
            sourcePanel.Controls.AddRange(new Control[] { sourceTitle, source, copySource });
            translationPanel.Controls.AddRange(new Control[] { translationTitle, translation, copyTranslation });
            previewPanel.Controls.AddRange(new Control[] { previewTitle, preview, captureSummary });
            actions.Controls.Add(copySelected); actions.Controls.Add(remove); actions.Controls.Add(openImage);
            Controls.AddRange(new Control[] { recordingLabel, recording, searchField, listPanel, sourcePanel, translationPanel, previewPanel, actions, clear, status, storagePath });
            search.TextChanged += delegate { ApplyFilter(); };
            refreshTimer = new System.Windows.Forms.Timer { Interval = 180 };
            refreshTimer.Tick += delegate { refreshTimer.Stop(); if (storeDirty && !closing && Visible) RefreshEntries(); };
            store.Changed += StoreChanged;
            ResumeLayout(true); LayoutContent(); ShowSelected();
        }
        internal void FocusSearch() { search.Focus(); search.SelectAll(); }
        internal int FilterIndex { get { return filterIndex; } }
        private void SelectFilter(int index)
        {
            filterIndex = index;
            for (int i = 0; i < filterButtons.Length; i++) { filterButtons[i].Selected = i == index; filterButtons[i].Invalidate(); }
            ApplyFilter();
        }
        internal void RefreshRecording(bool enabled) { refreshing = true; recording.Checked = enabled; refreshing = false; }
        internal async void RefreshEntries() { await RefreshAsync(); }
        internal async Task RefreshAsync()
        {
            if (IsDisposed || closing) return;
            refreshTimer.Stop(); storeDirty = false;
            if (busy) { refreshPending = true; return; }
            SetBusy(true);
            try { await LoadEntries(); }
            catch (Exception ex) { if (!IsDisposed) status.Text = ex is InvalidOperationException ? ex.Message : "历史记录暂时无法读取。"; }
            finally { if (!IsDisposed) SetBusy(false); }
        }
        private void StoreChanged()
        {
            storeDirty = true;
            if (closing || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (closing || !Visible) return;
                    refreshTimer.Stop(); refreshTimer.Start();
                }));
            }
            catch (InvalidOperationException) { }
        }
        private async Task LoadEntries()
        {
            HistoryEntry[] loaded = await store.LoadAsync();
            if (IsDisposed) return;
            entries = loaded;
            ApplyFilter();
        }
        private void ApplyFilter()
        {
            if (closing) return;
            if (Selected != null) selectedId = Selected.Id;
            string query = search.Text.Trim();
            int type = filterIndex;
            HistoryEntry[] matches = Array.FindAll(entries, item => MatchesFilter(item, type, query));
            list.BeginUpdate(); updatingEntries = true;
            try
            {
                list.Items.Clear(); list.Items.AddRange(matches);
                int index = Array.FindIndex(matches, item => item.Id == selectedId);
                if (matches.Length > 0) { list.SelectedIndex = index < 0 ? 0 : index; selectedId = Selected.Id; }
            }
            finally { updatingEntries = false; list.EndUpdate(); }
            empty.Visible = matches.Length == 0;
            empty.Text = entries.Length == 0 ? "暂无历史记录" : "没有匹配记录";
            ShowSelected();
            status.Text = entries.Length == 0 ? "暂无历史记录" : query.Length == 0 && type <= 0
                ? "最近 " + store.ActiveRetentionDays + " 天 · " + entries.Length + " 条"
                : "显示 " + matches.Length + " / " + entries.Length + " 条 · 最近 " + store.ActiveRetentionDays + " 天";
        }
        internal static bool MatchesFilter(HistoryEntry item, int type, string query)
        {
            if (item == null) return false;
            bool matches = type <= 0 || (type == 1 && item.Kind == "capture") || (type == 2 && item.Kind == "ocr")
                || (type == 3 && (item.Kind == "translation" || item.Kind == "image_translation"))
                || (type == 4 && (item.Kind == "clipboard_text" || item.Kind == "clipboard_image"))
                || (type == 5 && (!String.IsNullOrWhiteSpace(item.Source) || !String.IsNullOrWhiteSpace(item.Translation)))
                || (type == 6 && !String.IsNullOrEmpty(item.ImageFile));
            return matches && (String.IsNullOrEmpty(query) || (item.Source ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (item.Translation ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }
        private async Task Change(Func<Task> operation)
        {
            if (busy) return;
            SetBusy(true);
            try { await operation(); await LoadEntries(); }
            catch (Exception ex) { if (!IsDisposed) Ui.Error(FindForm(), ex); }
            finally { if (!IsDisposed) SetBusy(false); }
        }
        private async Task Copy(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            try { await Clip.SetText(text); }
            catch (Exception ex) { if (!IsDisposed) Ui.Error(FindForm(), ex); }
        }
        private async Task CopySelected()
        {
            HistoryEntry entry = Selected;
            if (busy || entry == null) return;
            SetBusy(true);
            try
            {
                if (!String.IsNullOrEmpty(entry.ImageFile))
                {
                    using (Bitmap image = await store.LoadImageAsync(entry.Id))
                    {
                        if (closing) return;
                        if (image == null) { status.Text = "这条记录的图片已不存在。"; return; }
                        await Clip.SetImage(image);
                    }
                }
                else await Clip.SetText(String.IsNullOrEmpty(entry.Source) ? entry.Translation : entry.Source);
                if (!closing) status.Text = "已复制";
            }
            catch (Exception ex) { if (!closing) Ui.Error(FindForm(), ex); }
            finally { if (!closing) SetBusy(false); }
        }
        private async Task ShowImage()
        {
            HistoryEntry entry = Selected;
            if (busy || entry == null) return;
            SetBusy(true);
            Bitmap image = null;
            try
            {
                image = await store.LoadImageAsync(entry.Id);
                if (IsDisposed) return;
                if (image == null) status.Text = "这条记录的截图已不存在。";
                else if (ImageRequested != null) { ImageRequested(image); image = null; }
            }
            catch (Exception ex) { if (!IsDisposed) Ui.Error(FindForm(), ex); }
            finally { if (image != null) image.Dispose(); if (!IsDisposed) SetBusy(false); }
        }
        private HistoryEntry Selected { get { return list.SelectedItem as HistoryEntry; } }
        private async void RefreshPreview()
        {
            if (closing) return;
            HistoryEntry entry = Selected;
            string id = Visible && IsHandleCreated && entry != null && !String.IsNullOrEmpty(entry.ImageFile) ? entry.Id : null;
            if (id == previewId) return;
            previewId = id;
            if (previewRequest != null) { previewRequest.Cancel(); previewRequest = null; }
            SetPreview(null);
            if (id == null) return;
            captureSummary.Text = "正在加载截图…";
            var request = new CancellationTokenSource(); previewRequest = request;
            Bitmap thumbnail = null;
            try
            {
                thumbnail = await store.LoadThumbnailAsync(id, new Size(800, 600), request.Token);
                if (closing || request.IsCancellationRequested || previewId != id) return;
                SetPreview(thumbnail); thumbnail = null;
                captureSummary.Text = preview.Image == null ? "这条记录的截图已不存在。" : "";
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (!closing && previewId == id && !request.IsCancellationRequested) captureSummary.Text = "截图暂时无法读取。"; }
            finally
            {
                if (thumbnail != null) thumbnail.Dispose();
                if (previewRequest == request) previewRequest = null;
                request.Dispose();
            }
        }
        private void SetPreview(Bitmap image)
        {
            Image previous = preview.Image; preview.Image = image;
            preview.Visible = image != null; captureSummary.Visible = image == null;
            if (previous != null) previous.Dispose();
        }
        private void ShowSelected()
        {
            HistoryEntry entry = Selected;
            bool capture = ImageOnly(entry);
            bool ocr = entry != null && entry.Kind == "ocr" && String.IsNullOrEmpty(entry.Translation);
            bool clipboardText = entry != null && entry.Kind == "clipboard_text";
            sourceTitle.Text = capture ? (entry.Kind == "capture" ? "截图" : "图片") : clipboardText ? "剪贴板文字" : ocr ? "OCR 识别文字" : "原文";
            sourcePanel.Visible = !capture;
            translationPanel.Visible = !capture && !ocr && !clipboardText;
            previewPanel.Visible = entry != null && !String.IsNullOrEmpty(entry.ImageFile);
            string sourceText = entry == null ? "" : entry.Source ?? "", translatedText = entry == null ? "" : entry.Translation ?? "";
            if (source.Text != sourceText) source.Text = sourceText;
            if (translation.Text != translatedText) translation.Text = translatedText;
            copySource.Enabled = source.TextLength > 0; copyTranslation.Enabled = translation.TextLength > 0;
            copySelected.Text = entry != null && !String.IsNullOrEmpty(entry.ImageFile) ? "复制图片" : "复制文字";
            copySelected.Enabled = !busy && entry != null;
            remove.Enabled = !busy && entry != null;
            openImage.Visible = entry != null && !String.IsNullOrEmpty(entry.ImageFile);
            openImage.Enabled = !busy && entry != null;
            LayoutContent();
            RefreshPreview();
        }
        private void SetBusy(bool value)
        {
            busy = value; list.Enabled = clear.Enabled = search.Enabled = !value;
            foreach (var button in filterButtons) button.Enabled = !value;
            copySelected.Enabled = !value && Selected != null;
            remove.Enabled = !value && Selected != null;
            openImage.Enabled = !value && Selected != null;
            if (!value && refreshPending && IsHandleCreated) { refreshPending = false; BeginInvoke(new Action(RefreshEntries)); }
        }
        private static RichTextBox TextArea(string name)
        {
            var box = new RichTextBox { ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = Color.White, ForeColor = Ui.Ink,
                DetectUrls = false, AutoWordSelection = false, WordWrap = true, ScrollBars = RichTextBoxScrollBars.Vertical,
                Font = new Font("Microsoft YaHei UI", 10.5F), AccessibleName = name };
            box.HandleCreated += delegate { Native.SendMessage(box.Handle, 0x44d, new IntPtr(3), new IntPtr(~1)); };
            return box;
        }
        private void DrawEntry(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var entry = (HistoryEntry)list.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;
            e.Graphics.FillRectangle(Brushes.White, e.Bounds);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            if (selected)
                using (var path = Shapes.Round(new RectangleF(e.Bounds.X + S(2), e.Bounds.Y + S(2), e.Bounds.Width - S(4), e.Bounds.Height - S(4)), S(8)))
                using (var brush = new SolidBrush(Color.FromArgb(232, 237, 255))) e.Graphics.FillPath(brush, path);
            string kind = KindLabel(entry);
            string glyph = entry.Kind == "capture" ? "crop" : entry.Kind == "clipboard_image" ? "image" : entry.Kind == "clipboard_text" ? "paste" : entry.Kind == "ocr" ? "ocr" : "translate";
            string text = String.IsNullOrWhiteSpace(entry.Source) ? entry.Translation ?? "" : entry.Source;
            if (entry.Kind == "capture") text = "PNG 截图";
            else if (entry.Kind == "clipboard_image") text = "剪贴板图片";
            if (text.Length > 150) text = text.Substring(0, 150);
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            Glyphs.Draw(e.Graphics, glyph, new RectangleF(e.Bounds.X + S(10), e.Bounds.Y + S(10), S(16), S(16)), selected ? Ui.Accent : Ui.Muted);
            TextRenderer.DrawText(e.Graphics, kind, metadataFont, new Rectangle(e.Bounds.X + S(31), e.Bounds.Y + S(8), e.Bounds.Width - S(119), S(22)), selected ? Ui.Accent : Ui.Muted, flags);
            TextRenderer.DrawText(e.Graphics, entry.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm"), metadataFont,
                new Rectangle(e.Bounds.Right - S(87), e.Bounds.Y + S(8), S(79), S(22)), Ui.Muted, flags | TextFormatFlags.Right);
            TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(e.Bounds.X + S(10), e.Bounds.Y + S(34), e.Bounds.Width - S(20), S(26)), Ui.Ink, flags);
        }
        internal static string KindLabel(HistoryEntry entry)
        {
            if (entry.Kind == "clipboard_text") return "剪贴板文字";
            if (entry.Kind == "clipboard_image") return "剪贴板图片";
            if (entry.Kind == "ocr")
            {
                if (entry.OcrProvider == "baidu_handwriting") return "手写识别";
                if (entry.OcrProvider == "baidu_qrcode") return "二维码";
                if (entry.OcrProvider == "tencent_table" || entry.OcrProvider == "baidu_table") return "表格识别";
                return "OCR";
            }
            return entry.Kind == "capture" ? "截图" : entry.Kind == "image_translation" ? "图片翻译" : !String.IsNullOrEmpty(entry.ImageFile) ? "截图翻译" : "翻译";
        }
        private static bool ImageOnly(HistoryEntry entry) { return entry != null && (entry.Kind == "capture" || entry.Kind == "clipboard_image"); }
        private int S(int value) { return Ui.Scale(this, value); }
        private void LayoutContent()
        {
            if (status == null) return;
            int width = ClientSize.Width, height = ClientSize.Height, side = Math.Min(S(260), width / 3), details = width - side - S(12);
            clear.SetBounds(width - S(110), 0, S(110), S(34));
            recording.SetBounds(clear.Left - S(58), S(4), S(44), S(26));
            recordingLabel.Location = new Point(recording.Left - S(70), S(7));
            searchField.SetBounds(0, 0, Math.Max(1, recordingLabel.Left - S(16)), S(34));
            int x = 0, filterTop = S(46), filterHeight = S(34), gap = S(6);
            int textWidth = 0;
            foreach (var button in filterButtons) textWidth += TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPadding).Width;
            int buttonPadding = Math.Max(S(30), Math.Min(S(42), (width - gap * (filterButtons.Length - 1) - textWidth) / filterButtons.Length));
            foreach (var button in filterButtons)
            {
                int buttonWidth = TextRenderer.MeasureText(button.Text, button.Font, Size.Empty, TextFormatFlags.NoPadding).Width + buttonPadding;
                if (x > 0 && x + buttonWidth > width) { x = 0; filterTop += filterHeight + gap; }
                button.SetBounds(x, filterTop, buttonWidth, filterHeight); x += buttonWidth + gap;
            }
            int contentTop = filterTop + filterHeight + S(12);
            bool capture = ImageOnly(Selected);
            bool thumbnail = Selected != null && !String.IsNullOrEmpty(Selected.ImageFile);
            bool single = capture || Selected != null && (Selected.Kind == "clipboard_text" || (Selected.Kind == "ocr" && String.IsNullOrEmpty(Selected.Translation)));
            int available = Math.Max(S(80), height - contentTop - S(36)), previewHeight = !capture && thumbnail ? Math.Min(S(160), Math.Max(S(84), available / 4)) : 0;
            if (previewHeight > 0)
            {
                int cardMinimum = S(64) + Math.Max(source.Font.Height, translation.Font.Height);
                int textMinimum = single ? cardMinimum : cardMinimum * 2 + S(14);
                previewHeight = Math.Max(0, Math.Min(previewHeight, available - S(54) - textMinimum));
            }
            listPanel.SetBounds(0, contentTop, side, available);
            list.SetBounds(S(6), S(6), side - S(12), listPanel.Height - S(12)); list.ItemHeight = S(72);
            empty.Bounds = list.Bounds;
            int detailHeight = available - S(42), top = contentTop + (previewHeight > 0 ? previewHeight + S(12) : 0);
            int textHeight = detailHeight - (top - contentTop), part = (textHeight - S(14)) / 2;
            sourcePanel.SetBounds(side + S(12), top, details, single ? textHeight : part);
            translationPanel.SetBounds(sourcePanel.Left, sourcePanel.Bottom + S(14), details, textHeight - part - S(14));
            actions.SetBounds(sourcePanel.Left, (single ? sourcePanel.Bottom : translationPanel.Bottom) + S(8), details, S(34));
            sourceTitle.Location = new Point(S(14), S(10)); copySource.SetBounds(details - S(44), S(4), S(32), S(32));
            source.SetBounds(S(14), S(42), details - S(28), Math.Max(1, sourcePanel.Height - S(56)));
            previewPanel.Bounds = capture ? sourcePanel.Bounds : new Rectangle(sourcePanel.Left, contentTop, details, previewHeight);
            previewTitle.Text = capture ? (Selected.Kind == "clipboard_image" ? "图片 · 单击放大" : "截图 · 单击放大") : "截图预览\n单击放大";
            previewTitle.Location = new Point(S(14), capture ? S(10) : Math.Max(S(10), (previewHeight - previewTitle.Height) / 2));
            preview.SetBounds(capture ? S(10) : S(100), capture ? S(36) : S(10), Math.Max(1, previewPanel.Width - (capture ? S(20) : S(110))), Math.Max(1, previewPanel.Height - (capture ? S(46) : S(20))));
            captureSummary.Bounds = preview.Bounds;
            translationTitle.Location = new Point(S(14), S(10)); copyTranslation.SetBounds(details - S(44), S(4), S(32), S(32));
            translation.SetBounds(S(14), S(42), details - S(28), Math.Max(1, translationPanel.Height - S(56)));
            remove.SetBounds(details - S(80), 0, S(80), S(34));
            copySelected.SetBounds(remove.Left - S(120), 0, S(112), S(34));
            openImage.SetBounds(0, 0, S(112), S(34));
            status.SetBounds(0, height - S(28), width / 2, S(28));
            storagePath.SetBounds(width / 2, height - S(28), width - width / 2, S(28));
        }
        protected override void OnLayout(LayoutEventArgs e) { base.OnLayout(e); LayoutContent(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); LayoutContent(); }
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (preview != null) RefreshPreview();
            if (refreshTimer == null) return;
            if (Visible && storeDirty) RefreshEntries();
            else if (!Visible) refreshTimer.Stop();
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); if (preview != null) RefreshPreview(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing && list != null)
            {
                closing = true;
                store.Changed -= StoreChanged; refreshTimer.Dispose(); storageHint.Dispose(); metadataFont.Dispose();
                if (previewRequest != null) { previewRequest.Cancel(); previewRequest = null; }
                SetPreview(null); entries = new HistoryEntry[0]; list.Items.Clear(); source.Clear(); translation.Clear();
            }
            base.Dispose(disposing);
        }
    }
}

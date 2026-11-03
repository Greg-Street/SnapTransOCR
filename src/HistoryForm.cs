using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class HistoryView : UserControl
    {
        public event Action<bool> RecordingChanged;
        public event Action<Bitmap> ImageRequested;
        private readonly HistoryStore store;
        private readonly ListBox list;
        private readonly TextBox search;
        private readonly TextInputField searchField;
        private readonly ComboBox typeFilter;
        private readonly RichTextBox source, translation;
        private readonly Label sourceTitle, translationTitle, captureSummary, previewTitle, status, storagePath, recordingLabel, empty;
        private readonly RoundedPanel listPanel, sourcePanel, translationPanel, previewPanel;
        private readonly PictureBox preview;
        private readonly Panel actions;
        private readonly ToggleSwitch recording;
        private readonly GlyphButton copySource, copyTranslation;
        private readonly Button copySelected, remove, clear;
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
            searchField = new TextInputField(search);
            typeFilter = Ui.Combo(new[] { "全部", "截图", "OCR", "翻译", "剪贴板", "文字", "图片" }, 0);
            typeFilter.AccessibleName = "历史类型筛选";
            listPanel = new RoundedPanel { Radius = 12, Fill = Color.White };
            sourcePanel = new RoundedPanel { Radius = 12, Fill = Color.White, BorderColor = Color.FromArgb(226, 226, 229) };
            translationPanel = new RoundedPanel { Radius = 12, Fill = Color.White, BorderColor = Color.FromArgb(226, 226, 229) };
            previewPanel = new RoundedPanel { Radius = 12, Fill = Color.White, BorderColor = Color.FromArgb(226, 226, 229), Visible = false };
            actions = new Panel { BackColor = Ui.Background };
            list = new ListBox { BorderStyle = BorderStyle.None, BackColor = Color.White, IntegralHeight = false, HorizontalScrollbar = false, DrawMode = DrawMode.OwnerDrawFixed, AccessibleName = "历史与剪贴板" };
            list.DrawItem += DrawEntry;
            list.SelectedIndexChanged += delegate
            {
                if (updatingEntries) return;
                if (Selected != null) selectedId = Selected.Id;
                ShowSelected();
            };
            list.KeyDown += async delegate(object sender, KeyEventArgs e)
            {
                if (e.Control && e.KeyCode == Keys.C) { e.Handled = e.SuppressKeyPress = true; await CopySelected(); }
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
            copySelected = Ui.TextAction("复制", async delegate { await CopySelected(); });
            remove = Ui.TextAction("删除", async delegate { HistoryEntry entry = Selected; if (entry != null) await Change(delegate { return store.DeleteAsync(entry.Id); }); });
            clear = Ui.TextAction("清空全部", async delegate { await Change(store.ClearAsync); });
            status = new Label { Text = "保留 " + store.ActiveRetentionDays + " 天；过期自动清理", ForeColor = Ui.Muted, BackColor = Color.Transparent, AutoEllipsis = true };
            storagePath = new Label { Text = "存储：" + store.DirectoryPath, AccessibleName = "历史存储目录：" + store.DirectoryPath, ForeColor = Ui.Muted, BackColor = Color.Transparent, AutoEllipsis = true };
            storageHint = new ToolTip(); storageHint.SetToolTip(storagePath, store.DirectoryPath);
            listPanel.Controls.Add(list); listPanel.Controls.Add(empty);
            sourcePanel.Controls.AddRange(new Control[] { sourceTitle, source, copySource });
            translationPanel.Controls.AddRange(new Control[] { translationTitle, translation, copyTranslation });
            previewPanel.Controls.AddRange(new Control[] { previewTitle, preview, captureSummary });
            actions.Controls.Add(copySelected); actions.Controls.Add(remove);
            Controls.AddRange(new Control[] { recordingLabel, recording, searchField, typeFilter, listPanel, sourcePanel, translationPanel, previewPanel, actions, clear, status, storagePath });
            search.TextChanged += delegate { ApplyFilter(); };
            typeFilter.SelectionChangeCommitted += delegate { ApplyFilter(); };
            refreshTimer = new System.Windows.Forms.Timer { Interval = 180 };
            refreshTimer.Tick += delegate { refreshTimer.Stop(); if (storeDirty && !closing && Visible) RefreshEntries(); };
            store.Changed += StoreChanged;
            ResumeLayout(true); LayoutContent(); ShowSelected();
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
            int type = typeFilter.SelectedIndex;
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
            LayoutContent();
            RefreshPreview();
        }
        private void SetBusy(bool value)
        {
            busy = value; list.Enabled = clear.Enabled = search.Enabled = typeFilter.Enabled = !value;
            copySelected.Enabled = !value && Selected != null;
            remove.Enabled = !value && Selected != null;
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
            Color background = (e.State & DrawItemState.Selected) != 0 ? Color.FromArgb(232, 237, 255) : Color.White;
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            string kind = KindLabel(entry);
            string title = entry.CreatedUtc.ToLocalTime().ToString("MM-dd HH:mm") + " · " + kind;
            string text = String.IsNullOrWhiteSpace(entry.Source) ? entry.Translation ?? "" : entry.Source;
            if (entry.Kind == "capture") text = "PNG 截图";
            else if (entry.Kind == "clipboard_image") text = "剪贴板图片";
            if (text.Length > 150) text = text.Substring(0, 150);
            text = text.Replace('\r', ' ').Replace('\n', ' ');
            const TextFormatFlags flags = TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(e.Graphics, title, Font, new Rectangle(e.Bounds.X + S(10), e.Bounds.Y + S(6), e.Bounds.Width - S(20), S(24)), Ui.Muted, flags);
            TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(e.Bounds.X + S(10), e.Bounds.Y + S(31), e.Bounds.Width - S(20), S(25)), Ui.Ink, flags);
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
            int width = ClientSize.Width, height = ClientSize.Height, side = Math.Min(S(235), width / 3), details = width - side - S(12);
            recordingLabel.Location = new Point(0, S(10)); recording.SetBounds(S(90), S(5), S(44), S(26));
            clear.SetBounds(width - S(92), 0, S(92), S(32));
            typeFilter.SetBounds(clear.Left - S(112), 0, S(100), S(34));
            searchField.SetBounds(S(146), 0, Math.Max(1, typeFilter.Left - S(158)), S(34));
            bool capture = ImageOnly(Selected);
            bool thumbnail = Selected != null && !String.IsNullOrEmpty(Selected.ImageFile);
            int available = height - S(80), previewHeight = !capture && thumbnail ? Math.Min(S(160), Math.Max(S(84), available / 4)) : 0;
            listPanel.SetBounds(0, S(44), side, available);
            list.SetBounds(S(6), S(6), side - S(12), listPanel.Height - S(12)); list.ItemHeight = S(64);
            empty.Bounds = list.Bounds;
            bool single = capture || Selected != null && (Selected.Kind == "clipboard_text" || (Selected.Kind == "ocr" && String.IsNullOrEmpty(Selected.Translation)));
            int detailHeight = available - S(42), top = S(44) + (previewHeight > 0 ? previewHeight + S(12) : 0);
            int textHeight = detailHeight - (top - S(44)), part = (textHeight - S(14)) / 2;
            sourcePanel.SetBounds(side + S(12), top, details, single ? textHeight : part);
            translationPanel.SetBounds(sourcePanel.Left, sourcePanel.Bottom + S(14), details, textHeight - part - S(14));
            actions.SetBounds(sourcePanel.Left, (single ? sourcePanel.Bottom : translationPanel.Bottom) + S(8), details, S(34));
            sourceTitle.Location = new Point(S(14), S(10)); copySource.SetBounds(details - S(44), S(4), S(32), S(32));
            source.SetBounds(S(14), S(42), details - S(28), Math.Max(1, sourcePanel.Height - S(56)));
            previewPanel.Bounds = capture ? sourcePanel.Bounds : new Rectangle(sourcePanel.Left, S(44), details, previewHeight);
            previewTitle.Text = capture ? (Selected.Kind == "clipboard_image" ? "图片 · 单击放大" : "截图 · 单击放大") : "截图预览\n单击放大";
            previewTitle.Location = new Point(S(14), capture ? S(10) : Math.Max(S(10), (previewHeight - previewTitle.Height) / 2));
            preview.SetBounds(capture ? S(10) : S(100), capture ? S(36) : S(10), Math.Max(1, previewPanel.Width - (capture ? S(20) : S(110))), Math.Max(1, previewPanel.Height - (capture ? S(46) : S(20))));
            captureSummary.Bounds = preview.Bounds;
            translationTitle.Location = new Point(S(14), S(10)); copyTranslation.SetBounds(details - S(44), S(4), S(32), S(32));
            translation.SetBounds(S(14), S(42), details - S(28), Math.Max(1, translationPanel.Height - S(56)));
            remove.SetBounds(details - S(62), 0, S(62), S(34));
            copySelected.SetBounds(details - S(160), 0, S(88), S(34));
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
                store.Changed -= StoreChanged; refreshTimer.Dispose(); storageHint.Dispose();
                if (previewRequest != null) { previewRequest.Cancel(); previewRequest = null; }
                SetPreview(null); entries = new HistoryEntry[0]; list.Items.Clear(); source.Clear(); translation.Clear();
            }
            base.Dispose(disposing);
        }
    }
}

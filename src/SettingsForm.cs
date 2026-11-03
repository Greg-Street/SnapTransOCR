using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class RetentionInput : RoundedPanel
    {
        private readonly NumericUpDown input;
        private readonly GlyphButton decrease, increase;
        private readonly Label unit;
        internal RetentionInput(NumericUpDown value)
        {
            input = value;
            Fill = Color.White; BorderColor = Color.FromArgb(216, 220, 227); Radius = 8;
            Size = new Size(240, 38); TabStop = false;
            input.BorderStyle = BorderStyle.None; input.TextAlign = HorizontalAlignment.Center; input.BackColor = Color.White;
            decrease = new GlyphButton("line", "减少一天", delegate { input.Value = Math.Max(input.Minimum, input.Value - 1); input.Focus(); }) { BackColor = Color.White, TabStop = false };
            increase = new GlyphButton("plus", "增加一天", delegate { input.Value = Math.Min(input.Maximum, input.Value + 1); input.Focus(); }) { BackColor = Color.White, TabStop = false };
            unit = new Label { Text = "天", ForeColor = Ui.Muted, BackColor = Color.White, TextAlign = ContentAlignment.MiddleCenter };
            Controls.AddRange(new Control[] { input, decrease, unit, increase });
            input.Layout += delegate { HideNativeButtons(); };
            HideNativeButtons();
            MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) input.Focus(); };
        }
        private void HideNativeButtons()
        {
            foreach (Control child in input.Controls) if (!(child is TextBoxBase)) child.Visible = false;
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (input == null || unit == null) return;
            int button = Ui.Scale(this, 28), gap = Ui.Scale(this, 6);
            Radius = Ui.Scale(this, 8); Height = Math.Max(Ui.Scale(this, 38), input.PreferredHeight + Ui.Scale(this, 14));
            decrease.SetBounds(Ui.Scale(this, 40), (Height - button) / 2, button, button);
            increase.SetBounds(Width - button - gap, (Height - button) / 2, button, button);
            unit.SetBounds(increase.Left - Ui.Scale(this, 30), Ui.Scale(this, 4), Ui.Scale(this, 24), Height - Ui.Scale(this, 8));
            int left = decrease.Right + gap;
            input.SetBounds(left, (Height - input.PreferredHeight) / 2, Math.Max(Ui.Scale(this, 40), unit.Left - left - gap), input.PreferredHeight);
            HideNativeButtons();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            int size = Ui.Scale(this, 20);
            Glyphs.Draw(e.Graphics, "history", new RectangleF(Ui.Scale(this, 13), (Height - size) / 2, size, size), Ui.Accent);
        }
        protected override void OnEnter(EventArgs e) { base.OnEnter(e); BorderColor = Ui.Accent; Invalidate(); }
        protected override void OnLeave(EventArgs e) { base.OnLeave(e); BorderColor = Color.FromArgb(216, 220, 227); Invalidate(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); PerformLayout(); }
    }

    internal sealed class NavButton : Button
    {
        public string Glyph;
        public bool Selected;
        public NavButton(string text, string glyph)
        {
            Text = text; Glyph = glyph; Height = 47; FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            Cursor = Cursors.Hand; BackColor = Color.FromArgb(250, 250, 251);
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(BackColor);
            int inset = Ui.Scale(this, 2);
            if (Selected) using (var path = Shapes.Round(new RectangleF(0, inset, Width - 1, Height - inset * 2), Ui.Scale(this, 10))) using (var brush = new SolidBrush(Color.FromArgb(232, 237, 255))) e.Graphics.FillPath(brush, path);
            Color ink = Selected ? Ui.Accent : Ui.Ink;
            Glyphs.Draw(e.Graphics, Glyph, new RectangleF(Ui.Scale(this, 20), Ui.Scale(this, 13), Ui.Scale(this, 21), Ui.Scale(this, 21)), ink);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(Ui.Scale(this, 62), 0, Width - Ui.Scale(this, 68), Height), ink, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -inset * 2, -inset * 2));
        }
    }

    internal sealed class SettingsForm : RoundedForm
    {
        private sealed class BufferedPanel : Panel { internal BufferedPanel() { DoubleBuffered = true; } }
        private sealed class BufferedFlowPanel : FlowLayoutPanel { internal BufferedFlowPanel() { DoubleBuffered = true; } }
        private sealed class BufferedTablePanel : TableLayoutPanel { internal BufferedTablePanel() { DoubleBuffered = true; } }
        public AppSettings Value { get; private set; }
        public event Action<Bitmap> HistoryImageRequested;
        public event Action<bool> HistoryRecordingChanged;
        private readonly Dictionary<string, TextBox> fields = new Dictionary<string, TextBox>();
        private readonly Dictionary<string, ToggleSwitch> toggles = new Dictionary<string, ToggleSwitch>();
        private readonly Dictionary<string, ToggleSwitch> serviceChecks = new Dictionary<string, ToggleSwitch>();
        private readonly Dictionary<string, FlowLayoutPanel> pages = new Dictionary<string, FlowLayoutPanel>();
        private readonly Dictionary<string, NavButton> navigation = new Dictionary<string, NavButton>();
        private readonly Panel sidebar, header, body, footer;
        private readonly Label pageTitle, brand, version, saveStatus;
        private readonly ComboBox ocr, translator, target, source, stroke, arkModel, arkApiMode, baiduMode, baiduDomain, imageTranslator, imageTarget;
        private static readonly string[] arkApiModes = { "auto", "seed", "chat" };
        private static readonly string[] imageTargets = { "zh", "en", "ja", "ko", "fr", "de", "ru", "es" };
        private readonly ServiceOrderControl translationOrder, recognitionOrder;
        private readonly Action<AppSettings> save;
        private readonly GlyphButton close, minimize;
        private readonly Button saveButton, cancelButton;
        private readonly HistoryView historyView;
        private readonly NumericUpDown historyRetention;
        private bool ready, arranging, refreshingHistory, historyLoaded;
        private FlowLayoutPanel arrangedPage;
        private int arrangedWidth, arrangedDpi;
        [DllImport("user32.dll")] private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);

        public SettingsForm(AppSettings settings, Action<AppSettings> onSave, HistoryStore historyStore = null)
        {
            Value = settings.Copy(); Value.NormalizeProviders(); Value.NormalizeHotkeys(); settings = Value; save = onSave;
            Text = "SnapTransOCR · 设置"; ClientSize = new Size(1000, 730); MinimumSize = new Size(860, 580);
            sidebar = new BufferedPanel { BackColor = Color.FromArgb(250, 250, 251), Dock = DockStyle.Left, Width = 208, AutoScroll = true };
            header = new BufferedPanel { BackColor = Ui.Background, Dock = DockStyle.Top, Height = 54, Width = 790 };
            body = new BufferedPanel { AutoScroll = true, BackColor = Ui.Background, Dock = DockStyle.Fill };
            footer = new BufferedFlowPanel { BackColor = Ui.Background, Dock = DockStyle.Bottom, Height = 70, Width = 790, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Padding = new Padding(26, 16, 26, 16) };
            pageTitle = new Label { Text = "常规设置", Font = new Font("Microsoft YaHei UI", 13F), AutoSize = true, Location = new Point(27, 16) };
            header.Controls.Add(pageTitle);
            header.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Shapes.Drag(this); };
            pageTitle.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Shapes.Drag(this); };
            close = new GlyphButton("close", "关闭设置", Close) { BackColor = Ui.Background };
            minimize = new GlyphButton("min", "最小化", delegate { WindowState = FormWindowState.Minimized; }) { BackColor = Ui.Background };
            close.SetBounds(748, 9, 32, 32); minimize.SetBounds(711, 9, 32, 32);
            close.Anchor = minimize.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(close); header.Controls.Add(minimize);
            brand = new Label { Text = "SnapTransOCR", AutoSize = true, Font = new Font("Segoe UI", 17F, FontStyle.Bold), ForeColor = Ui.Accent, Location = new Point(18, 38) };
            version = new Label { Text = "SnapTransOCR  0.2.20", ForeColor = Ui.Muted, AutoSize = true, Location = new Point(44, 86), Font = new Font("Segoe UI", 9F) };
            sidebar.Controls.Add(brand); sidebar.Controls.Add(version);
            string[] titles = { "常规设置", "热键设置", "截图设置", "翻译设置", "识别设置", "历史记录", "关于" };
            string[] icons = { "general", "keyboard", "crop", "translate", "ocr", "history", "info" };
            for (int i = 0; i < titles.Length; i++)
            {
                string title = titles[i];
                var button = new NavButton(title, icons[i]);
                button.SetBounds(10, 136 + i * 48, 188, 47);
                button.Anchor = AnchorStyles.Top | AnchorStyles.Left;
                button.Click += delegate { SelectPage(title); };
                sidebar.Controls.Add(button); navigation.Add(title, button);
                var page = new BufferedFlowPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Ui.Background, Visible = false, Padding = new Padding(26, 12, 26, 20), Margin = Padding.Empty };
                pages.Add(title, page); body.Controls.Add(page);
            }
            var general = pages["常规设置"];
            Section(general, "日常使用");
            var card = Card(general);
            Row(card, "运行方式", "关闭翻译窗口后留在托盘；右键托盘图标打开截图、贴图和设置。", Ui.Label("托盘运行"));
            Row(card, "开机自启动", "登录 Windows 后自动在托盘启动。", Toggle("StartWithWindows", settings.StartWithWindows));
            Row(card, "历史与剪贴板", "开关立即生效，记录截图、翻译及其他软件复制的文字和图片。关闭仅停止新增，过期内容仍自动清理。", Toggle("HistoryEnabled", settings.HistoryEnabled));
            historyRetention = new NumericUpDown { Minimum = 1, Maximum = 3650, Value = Math.Max(1, Math.Min(3650, settings.HistoryRetentionDays)), Width = 120, Font = Ui.Font, AccessibleName = "历史保存天数" };
            Row(card, "历史保存天数", "直接输入 1–3650 天，默认 7 天；保存设置后生效，缩短时自动清理超期记录。", new RetentionInput(historyRetention));
            ocr = Ui.Combo(new[] { "百度 OCR", "腾讯 OCR" }, settings.OcrProvider == "tencent" ? 1 : 0);
            translator = Ui.Combo(Array.ConvertAll(AppSettings.TranslationProviders, AppSettings.TranslationName), Math.Max(0, Array.IndexOf(AppSettings.TranslationProviders, settings.TranslateProvider)));

            var hotkeys = pages["热键设置"]; Section(hotkeys, "全局热键"); card = Card(hotkeys);
            string[] keyLabels = { "截图 / OCR 入口", "直接截图识别", "直接截图翻译", "划词翻译", "截图原位翻译", "剪贴板 OCR", "剪贴板贴图", "历史 / 剪贴板", "隐藏 / 恢复全部贴图" };
            for (int i = 0; i < AppSettings.HotkeyCount; i++)
            {
                if (i == 4) { Section(hotkeys, "可选快捷命令"); card = Card(hotkeys); }
                var field = Input("hotkey" + i, settings.Hotkeys[i], false);
                Row(card, keyLabels[i], i == 0 ? "框选后显示截图悬浮工具条。" : i == 3 ? "选中文字后按热键查看译文。" : "留空则不注册此热键。", field);
                field.KeyDown += CaptureKey;
            }
            Note(hotkeys, "点击输入框后直接按组合键，Backspace 清除。被其他软件占用的热键会在保存时提示。");

            Note(hotkeys, "划词翻译只在按下快捷键后取词；未选中文字时不操作。优先使用系统辅助功能接口，必要时临时复制，并恢复剪贴板。");

            var capture = pages["截图设置"]; Section(capture, "截图与标注"); card = Card(capture);
            Row(card, "完成时复制图片", "保存或置顶贴图时，同时将完成的截图复制到剪贴板。", Toggle("ScreenshotCopyOnConfirm", settings.ScreenshotCopyOnConfirm));
            Row(card, "显示选区放大镜", "框选时放大鼠标附近的像素，便于对齐边缘。", Toggle("CaptureMagnifier", settings.CaptureMagnifier));
            var widths = new string[20];
            for (int index = 0; index < widths.Length; index++) widths[index] = (index + 1) + " 像素";
            stroke = Ui.Combo(widths, Math.Max(0, Math.Min(19, settings.CaptureStroke - 1)));
            Row(card, "默认线条粗细", "截图工具条中可随时调整颜色和粗细。", stroke);
            Section(capture, "贴图"); card = Card(capture);
            Row(card, "贴图默认置顶", "将截图或剪贴板图片固定在其他窗口上方。", Toggle("PinTopmost", settings.PinTopmost));

            var translation = pages["翻译设置"]; Section(translation, "语言"); card = Card(translation);
            Row(card, "默认翻译服务", "仅列出下方已启用的服务。", translator);
            string[] languages = { "smart", "zh", "en", "ja", "ko", "fr", "de", "ru", "es" };
            string[] names = { "智能中英互译", "简体中文", "英语", "日语", "韩语", "法语", "德语", "俄语", "西班牙语" };
            source = Ui.Combo(new[] { "自动检测", "简体中文", "英语", "日语", "韩语", "法语", "德语", "俄语", "西班牙语" }, Math.Max(0, Array.IndexOf(new[] { "auto", "zh", "en", "ja", "ko", "fr", "de", "ru", "es" }, settings.SourceLanguage ?? "auto")));
            target = Ui.Combo(names, Math.Max(0, Array.IndexOf(languages, settings.TargetLanguage)));
            Row(card, "源语言", "自动检测适用于大多数日常文本。", source); Row(card, "目标语言", "智能中英互译：含中文时译为英文，否则译为中文。", target);
            Section(translation, "窗口行为"); card = Card(translation);
            Row(card, "在鼠标旁显示翻译结果", "划词与截图翻译从当前鼠标所在位置打开。", Toggle("TranslateNearCursor", settings.TranslateNearCursor));
            Row(card, "自动调整翻译窗口高度", "根据展开的译文内容调整高度。", Toggle("TranslateAutoHeight", settings.TranslateAutoHeight));
            Row(card, "默认置顶翻译窗口", "也可点击浮窗左上角图钉切换。", Toggle("TranslateTopmost", settings.TranslateTopmost));
            Row(card, "失去焦点时隐藏翻译窗口", "鼠标切换到其他窗口后隐藏。", Toggle("TranslateHideOnBlur", settings.TranslateHideOnBlur));

            var services = pages["翻译设置"]; Section(services, "服务排序"); card = Card(services);
            translationOrder = new ServiceOrderControl(settings.TranslateServiceOrder, AppSettings.TranslationName) { AccessibleName = "翻译服务顺序" };
            Row(card, "显示顺序", "拖动左侧把手，或选中后按 Alt+↑ / ↓；默认服务单独选择。", translationOrder);
            int groupStart = services.Controls.Count;
            Section(services, "火山引擎翻译"); card = Card(services);
            Row(card, "启用服务", "关闭后不显示此服务，也不向其发送翻译请求。", ServiceCheck("VolcTranslateEnabled", settings.VolcTranslateEnabled));
            Row(card, "Access Key ID", "机器翻译服务的访问密钥 ID。", Input("VolcId", settings.VolcId, true));
            Row(card, "Secret Access Key", "与 Access Key ID 配对的密钥。", Input("VolcSecret", settings.VolcSecret, true));
            Note(services, "使用机器翻译 TranslateText 接口。此处不是豆包 Ark API Key。");
            MarkService(services, groupStart, "volc"); groupStart = services.Controls.Count;
            Section(services, "百度翻译"); card = Card(services);
            baiduMode = Ui.Combo(new[] { "通用翻译", "大模型翻译", "领域翻译" }, settings.TranslateProvider == "baidu_llm" ? 1 : settings.TranslateProvider == "baidu_domain" ? 2 : 0);
            baiduMode.AccessibleName = "BaiduTranslationMode";
            Row(card, "翻译功能", "切换下方配置；各功能可独立启用，默认服务在页面顶部选择。", baiduMode);
            Row(card, "APP ID", "百度翻译开放平台的应用 ID。", Input("BaiduTranslateId", settings.BaiduTranslateId, true));
            Row(card, "共享密钥", "通用、领域和图片翻译共用；大模型可另填 API Key。", Input("BaiduTranslateSecret", settings.BaiduTranslateSecret, true));
            Row(card, "启用通用翻译", "关闭后不显示此功能，也不发送对应翻译请求。", ServiceCheck("BaiduTranslateEnabled", settings.BaiduTranslateEnabled));
            Row(card, "启用大模型翻译", "使用共享 APP ID，可单独设置翻译要求。", ServiceCheck("BaiduLlmEnabled", settings.BaiduLlmEnabled));
            Row(card, "API Key（可选）", "优先使用此密钥；留空时使用共享密钥签名。", Input("BaiduLlmApiKey", settings.BaiduLlmApiKey, true));
            Row(card, "翻译要求（可选）", "例如保留公式、术语或指定行文风格。", Input("BaiduLlmInstruction", settings.BaiduLlmInstruction, false));
            Row(card, "启用领域翻译", "使用共享 APP ID / 密钥，需开通对应服务。", ServiceCheck("BaiduDomainEnabled", settings.BaiduDomainEnabled));
            baiduDomain = Ui.Combo(new[] { "学术论文", "信息技术", "金融财经", "机械制造", "生物医药", "网络文学", "航空航天", "人文社科", "新闻资讯", "法律法规", "合同" }, Math.Max(0, Array.IndexOf(AppSettings.BaiduDomains, settings.BaiduDomain)));
            baiduDomain.AccessibleName = "BaiduDomain";
            Row(card, "翻译领域", "支持中英互译；网络文学和人文社科仅支持中文译英文。", baiduDomain);
            TableLayoutPanel baiduCard = card;
            Action refreshBaiduRows = delegate
            {
                if (baiduMode.SelectedIndex < 0) return;
                ShowRows(baiduCard, 3, 1, baiduMode.SelectedIndex == 0);
                ShowRows(baiduCard, 4, 3, baiduMode.SelectedIndex == 1);
                ShowRows(baiduCard, 7, 2, baiduMode.SelectedIndex == 2);
                Arrange(true);
            };
            baiduMode.SelectedIndexChanged += delegate { refreshBaiduRows(); }; refreshBaiduRows();
            Note(services, "此 APP ID 和密钥也供百度领域翻译、图片翻译使用。大模型翻译可单独填写 API Key。");
            MarkService(services, groupStart, "baidu");
            groupStart = services.Controls.Count;
            Section(services, "必应翻译"); card = Card(services);
            Row(card, "启用服务", "免填写密钥，联网使用微软翻译。服务可能限流；失败时可手动切换其他服务。", ServiceCheck("BingTranslateEnabled", settings.BingTranslateEnabled));
            MarkService(services, groupStart, "bing");

            groupStart = services.Controls.Count;
            Section(services, "豆包 / Ark 翻译"); card = Card(services);
            Row(card, "启用服务", "使用所选模型翻译文字；关闭后不发送请求。", ServiceCheck("ArkEnabled", settings.ArkEnabled));
            Row(card, "API Key", "火山方舟 API Key，与机器翻译访问密钥独立。", Input("ArkApiKey", settings.ArkApiKey, true));
            arkModel = new StyledComboBox { Width = 250, AccessibleName = "ArkModel" };
            foreach (var model in TranslationModels.Ark)
            {
                string name = model.Name;
                int category = name.IndexOf(" · ", StringComparison.Ordinal);
                string title = category < 0 ? name : name.Substring(category + 3);
                arkModel.Items.Add(name.StartsWith("即将下线", StringComparison.Ordinal) ? title + "（即将下线）" : title);
            }
            arkModel.Items.Add("自定义 Model ID / 接入点…");
            arkModel.Tag = Array.ConvertAll(TranslationModels.Ark, model => model.Id);
            var savedModel = TranslationModels.Find(settings.ArkModel);
            arkModel.SelectedIndex = savedModel == null ? TranslationModels.Ark.Length : Array.IndexOf((string[])arkModel.Tag, savedModel.Id);
            arkModel.DropDownWidth = 360;
            Row(card, "文本模型", "选择文本模型；列表支持键盘前缀查找，悬停可查看完整模型 ID。", arkModel);
            var customModel = Input("ArkModel", settings.ArkModel, false);
            Row(card, "自定义 Model ID", "填写已开通的模型 ID 或 ep-* 推理接入点。", customModel);
            TableLayoutPanel arkCard = card;
            var modelHint = new ToolTip();
            Action updateModelHint = delegate
            {
                string id = ArkModelId(); var known = TranslationModels.Find(id);
                modelHint.SetToolTip(arkModel, known == null ? id : known.DisplayName); arkModel.AccessibleDescription = id;
            };
            Action refreshModelChoice = delegate { ShowRows(arkCard, 3, 1, arkModel.SelectedIndex == TranslationModels.Ark.Length); updateModelHint(); Arrange(true); };
            arkModel.SelectedIndexChanged += delegate { refreshModelChoice(); };
            customModel.TextChanged += delegate { updateModelHint(); }; refreshModelChoice();
            Disposed += delegate { modelHint.Dispose(); };
            arkApiMode = Ui.Combo(new[] { "自动识别", "专用翻译（Seed）", "通用文本（Chat）" }, Math.Max(0, Array.IndexOf(arkApiModes, settings.ArkApiMode)));
            arkApiMode.AccessibleName = "ArkApiMode";
            Row(card, "调用协议", "使用 ep-* 接入点时，按其实际模型选择协议。自动模式仅从公开模型 ID 判断。", arkApiMode);
            Note(services, "Seed Translation 使用专用翻译协议；其他文本模型使用翻译指令。模型可用性和用量以方舟控制台为准。");
            MarkService(services, groupStart, "ark");

            Section(translation, "图片翻译"); card = Card(translation);
            imageTranslator = Ui.Combo(new[] { "OCR + 所选文本翻译服务", "百度图片翻译" }, settings.BaiduImageTranslateEnabled && settings.ImageTranslateProvider == "baidu_image" ? 1 : 0);
            imageTranslator.AccessibleName = "ImageTranslateProvider"; imageTranslator.DropDownWidth = 280;
            Row(card, "图片翻译方式", "OCR 流程使用默认识别和文本翻译；百度方式直接调用图片接口，使用共享 APP ID / 密钥。", imageTranslator);
            imageTarget = Ui.Combo(new[] { "简体中文", "英语", "日语", "韩语", "法语", "德语", "俄语", "西班牙语" }, Math.Max(0, Array.IndexOf(imageTargets, settings.BaiduImageTargetLanguage)));
            imageTarget.AccessibleName = "BaiduImageTargetLanguage";
            Row(card, "百度图片目标语言", "仅用于百度图片直译，独立于文本智能中英互译。", imageTarget);
            TableLayoutPanel imageCard = card;
            Action refreshImageChoices = delegate { ShowRows(imageCard, 1, 1, imageTranslator.SelectedIndex == 1); Arrange(true); };
            imageTranslator.SelectedIndexChanged += delegate { refreshImageChoices(); };
            refreshImageChoices();

            var recognition = pages["识别设置"]; Section(recognition, "识别模式"); card = Card(recognition);
            Row(card, "默认识别服务", "截图识别和截图翻译使用此服务。", ocr);
            Row(card, "高精度识别", "使用所选服务的高精度 OCR 接口；用量按对应服务规则计算。", Toggle("AccurateOcr", settings.AccurateOcr));
            Note(recognition, "自动识别图中文字语言。长截图按段识别，保留原图文字分辨率；每次仅调用你选中的 OCR 服务。");
            Note(recognition, "百度纯文字 OCR 使用 accurate_basic / general_basic；图片文字定位使用 accurate / general，以保留文字坐标。");
            var ocrServices = pages["识别设置"]; Section(ocrServices, "服务排序"); card = Card(ocrServices);
            recognitionOrder = new ServiceOrderControl(settings.OcrServiceOrder, code => code == "baidu" ? "百度 OCR" : "腾讯 OCR") { AccessibleName = "识别服务顺序" };
            Row(card, "显示顺序", "拖动左侧把手，或选中后按 Alt+↑ / ↓；默认服务单独选择。", recognitionOrder);
            groupStart = ocrServices.Controls.Count;
            Section(ocrServices, "百度 OCR"); card = Card(ocrServices);
            Row(card, "启用服务", "关闭后不能选为默认 OCR；已填凭据保留。", ServiceCheck("BaiduOcrEnabled", settings.BaiduOcrEnabled));
            Row(card, "API Key", "百度智能云 OCR 应用凭据。", Input("BaiduOcrKey", settings.BaiduOcrKey, true));
            Row(card, "Secret Key", "与 API Key 配对，与百度翻译密钥独立。", Input("BaiduOcrSecret", settings.BaiduOcrSecret, true));
            Row(card, "二维码 / 条码识别", "共用百度 OCR 凭据，仅在选择此操作时请求。", Toggle("BaiduQrEnabled", settings.BaiduQrEnabled));
            Row(card, "手写文字识别", "共用百度 OCR 凭据，需开通手写文字识别。", Toggle("BaiduHandwritingEnabled", settings.BaiduHandwritingEnabled));
            Row(card, "表格识别 V2", "共用百度 OCR 凭据，识别后可复制表格或导出 Excel。", Toggle("BaiduTableEnabled", settings.BaiduTableEnabled));
            MarkService(ocrServices, groupStart, "baidu"); groupStart = ocrServices.Controls.Count;
            Section(ocrServices, "腾讯 OCR"); card = Card(ocrServices);
            Row(card, "启用服务", "关闭后不能选为默认 OCR；已填凭据保留。", ServiceCheck("TencentOcrEnabled", settings.TencentOcrEnabled));
            Row(card, "SecretId", "腾讯云访问密钥 ID。", Input("TencentId", settings.TencentId, true));
            Row(card, "SecretKey", "与 SecretId 配对，需开通 OCR 服务。", Input("TencentSecret", settings.TencentSecret, true));
            Row(card, "表格识别 V3", "共用腾讯云凭据，识别后可复制表格或导出 Excel。", Toggle("TencentTableEnabled", settings.TencentTableEnabled));
            MarkService(ocrServices, groupStart, "tencent");
            translationOrder.OrderChanged += delegate { ReorderServices(services, translationOrder.Order); RefreshServices(); Arrange(true); };
            recognitionOrder.OrderChanged += delegate { ReorderServices(ocrServices, recognitionOrder.Order); RefreshServices(); Arrange(true); };
            ReorderServices(services, translationOrder.Order); ReorderServices(ocrServices, recognitionOrder.Order);
            Section(pages["关于"], "SnapTransOCR"); card = Card(pages["关于"]);
            Row(card, "截图、贴图、OCR 与翻译", "按需截图与云端识别，原生界面，无浏览器内核和本地识别模型。", Ui.Label("0.2.20"));
            Note(pages["关于"], "仅在你点击识别或翻译时请求对应 API 服务。历史记录保存在本机，可关闭记录或清空，不会上传历史。");

            historyView = new HistoryView(historyStore ?? new HistoryStore(retentionDays: () => Value.HistoryRetentionDays), settings.HistoryEnabled);
            pages["历史记录"].Controls.Add(historyView);
            historyView.ImageRequested += delegate(Bitmap image) { if (HistoryImageRequested != null) HistoryImageRequested(image); else image.Dispose(); };
            historyView.RecordingChanged += ChangeHistoryRecording;
            toggles["HistoryEnabled"].CheckedChanged += delegate { if (!refreshingHistory) ChangeHistoryRecording(toggles["HistoryEnabled"].Checked); };

            saveButton = Ui.Button("保存设置", Save, true); cancelButton = Ui.Button("取消", Close);
            saveButton.Margin = new Padding(10, 0, 0, 0); cancelButton.Margin = Padding.Empty;
            saveStatus = new Label { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(8, 9, 8, 0) };
            footer.Controls.Add(saveButton); footer.Controls.Add(cancelButton); footer.Controls.Add(saveStatus);
            EventHandler clearSaved = delegate { saveStatus.Text = ""; };
            historyRetention.ValueChanged += clearSaved;
            foreach (TextBox field in fields.Values) field.TextChanged += clearSaved;
            foreach (ToggleSwitch item in toggles.Values) item.CheckedChanged += clearSaved;
            foreach (ComboBox item in new[] { source, target, ocr, translator, stroke, arkModel, arkApiMode, baiduDomain, imageTranslator, imageTarget }) item.SelectedIndexChanged += clearSaved;
            arkModel.TextChanged += clearSaved;
            translationOrder.OrderChanged += delegate { saveStatus.Text = ""; };
            recognitionOrder.OrderChanged += delegate { saveStatus.Text = ""; };
            Controls.Add(body); Controls.Add(header); Controls.Add(footer); Controls.Add(sidebar);
            body.SizeChanged += delegate { Arrange(); };
            RefreshServices();
            ResumeLayout(true);
            ready = true; SelectPage("常规设置");
        }
        private ToggleSwitch ServiceCheck(string name, bool enabled)
        {
            var item = Toggle(name, enabled);
            serviceChecks.Add(name, item);
            item.CheckedChanged += delegate { if (ready) RefreshServices(); };
            return item;
        }
        private void RefreshServices()
        {
            RefreshChoices(ocr, recognitionOrder.Order, new[] { "baidu", "tencent" }, new[] { "百度 OCR", "腾讯 OCR" }, new[] { "BaiduOcrEnabled", "TencentOcrEnabled" }, Value.OcrProvider);
            RefreshChoices(translator, translationOrder.Order, AppSettings.TranslationProviders, Array.ConvertAll(AppSettings.TranslationProviders, AppSettings.TranslationName), AppSettings.TranslationFlags, Value.TranslateProvider);
            string[] modes = { "通用翻译", "大模型翻译", "领域翻译" };
            string[] flags = { "BaiduTranslateEnabled", "BaiduLlmEnabled", "BaiduDomainEnabled" };
            for (int index = 0; index < modes.Length; index++) baiduMode.Items[index] = modes[index] + (serviceChecks[flags[index]].Checked ? "（已启用）" : "（未启用）");
        }
        private void RefreshChoices(ComboBox combo, string[] order, string[] providers, string[] names, string[] flags, string preferred)
        {
            string[] previous = combo.Tag as string[];
            if (previous != null && combo.SelectedIndex >= 0 && combo.SelectedIndex < previous.Length) preferred = previous[combo.SelectedIndex];
            var codes = new List<string>();
            combo.Items.Clear();
            foreach (string provider in order)
            {
                int i = Array.IndexOf(providers, provider);
                if (serviceChecks[flags[i]].Checked) { codes.Add(provider); combo.Items.Add(names[i]); }
            }
            combo.Tag = codes.ToArray(); combo.Enabled = codes.Count > 0;
            if (codes.Count == 0) combo.Items.Add("未启用服务");
            combo.SelectedIndex = Math.Max(0, codes.IndexOf(preferred));
        }
        private static void MarkService(FlowLayoutPanel page, int start, string provider)
        {
            for (int i = start; i < page.Controls.Count; i++) page.Controls[i].Tag = provider;
        }
        private static void ReorderServices(FlowLayoutPanel page, string[] order)
        {
            var controls = new List<Control>();
            bool baiduAdded = false;
            foreach (string provider in order)
            {
                bool baidu = provider == "baidu" || provider == "baidu_llm" || provider == "baidu_domain";
                if (baidu && baiduAdded) continue;
                string group = baidu ? "baidu" : provider;
                foreach (Control control in page.Controls) if ((string)control.Tag == group) controls.Add(control);
                if (baidu) baiduAdded = true;
            }
            page.SuspendLayout();
            foreach (Control control in controls) page.Controls.SetChildIndex(control, page.Controls.Count - 1);
            page.ResumeLayout(false);
        }
        private static string Provider(ComboBox combo)
        {
            var codes = (string[])combo.Tag;
            return combo.SelectedIndex >= 0 && combo.SelectedIndex < codes.Length ? codes[combo.SelectedIndex] : "";
        }
        private string ArkModelId()
        {
            var ids = (string[])arkModel.Tag;
            return arkModel.SelectedIndex >= 0 && arkModel.SelectedIndex < ids.Length ? ids[arkModel.SelectedIndex] : fields["ArkModel"].Text.Trim();
        }
        private static void CaptureKey(object sender, KeyEventArgs e)
        {
            var box = (TextBox)sender; e.SuppressKeyPress = true;
            if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Delete) { box.Clear(); return; }
            if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey || e.KeyCode == Keys.Menu) return;
            string key = e.KeyCode.ToString(); if (key.Length == 2 && key[0] == 'D' && Char.IsDigit(key[1])) key = key.Substring(1);
            box.Text = (e.Control ? "Ctrl+" : "") + (e.Alt ? "Alt+" : "") + (e.Shift ? "Shift+" : "") + key;
        }
        private ToggleSwitch Toggle(string name, bool value) { var item = new ToggleSwitch { Checked = value, AccessibleName = name }; toggles.Add(name, item); return item; }
        private TextBox Input(string name, string value, bool secret)
        {
            var item = new TextBox { Text = value ?? "", UseSystemPasswordChar = secret, Width = 250, MaxLength = 512, Font = new Font("Segoe UI", 10F), AccessibleName = name };
            fields.Add(name, item); return item;
        }
        private static void Section(FlowLayoutPanel page, string title)
        {
            page.Controls.Add(new Label { Text = title, AutoSize = true, Font = new Font("Microsoft YaHei UI", 11F), Margin = new Padding(0, 14, 0, 12) });
        }
        private static void Note(FlowLayoutPanel page, string text)
        {
            page.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Ui.Muted, MaximumSize = new Size(660, 0), Margin = new Padding(4, 10, 4, 12), Font = new Font("Microsoft YaHei UI", 9F) });
        }
        private static TableLayoutPanel Card(FlowLayoutPanel page)
        {
            var rounded = new RoundedPanel { Fill = Color.White, BorderColor = Color.FromArgb(226, 226, 229), Padding = new Padding(17, 5, 17, 5), Width = 660, Height = 100, Margin = new Padding(0, 0, 0, 4) };
            var table = new BufferedTablePanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, BackColor = Color.White };
            table.Width = rounded.Width - rounded.Padding.Horizontal;
            table.SizeChanged += delegate { rounded.Height = table.Height + rounded.Padding.Vertical; };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 56)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            rounded.Controls.Add(table); page.Controls.Add(rounded); return table;
        }
        private static void Row(TableLayoutPanel card, string title, string detail, Control value)
        {
            if (value is TextBox) value = new TextInputField((TextBox)value);
            int row = card.RowCount++;
            card.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var text = new BufferedFlowPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, Margin = new Padding(0, 15, 14, 16), Anchor = AnchorStyles.Top | AnchorStyles.Left };
            text.Controls.Add(new Label { Text = title, AutoSize = false, Font = new Font("Microsoft YaHei UI", 10.5F), Margin = new Padding(0, 0, 0, 4) });
            text.Controls.Add(new Label { Text = detail, AutoSize = false, ForeColor = Ui.Muted, Font = new Font("Microsoft YaHei UI", 8.5F), Margin = Padding.Empty });
            value.Anchor = AnchorStyles.Right; value.Margin = new Padding(0, 12, 3, 12);
            card.Controls.Add(text, 0, row); card.Controls.Add(value, 1, row);
        }
        private static void ShowRows(TableLayoutPanel card, int start, int count, bool visible)
        {
            card.SuspendLayout();
            for (int row = start; row < start + count; row++)
            {
                card.RowStyles[row].SizeType = visible ? SizeType.AutoSize : SizeType.Absolute;
                card.RowStyles[row].Height = 0;
                card.GetControlFromPosition(0, row).Visible = visible;
                Control value = card.GetControlFromPosition(1, row);
                if (visible && value is ToggleSwitch) value.Size = new Size(Ui.Scale(card, 46), Ui.Scale(card, 28));
                value.Visible = visible;
            }
            card.ResumeLayout(true);
        }
        internal void SelectPage(string title)
        {
            body.AutoScroll = title != "历史记录";
            historyView.Visible = title == "历史记录";
            foreach (var pair in pages) pair.Value.Visible = pair.Key == title;
            foreach (var pair in navigation) { pair.Value.Selected = pair.Key == title; pair.Value.Invalidate(); }
            pageTitle.Text = title; body.AutoScrollPosition = Point.Empty; Arrange(true);
            if (title == "历史记录" && Visible) LoadHistory();
        }
        private void ChangeHistoryRecording(bool enabled)
        {
            RefreshHistory(enabled);
            if (HistoryRecordingChanged != null) HistoryRecordingChanged(enabled);
        }
        internal void RefreshHistory(bool enabled)
        {
            refreshingHistory = true;
            try { Value.HistoryEnabled = enabled; toggles["HistoryEnabled"].Checked = enabled; historyView.RefreshRecording(enabled); }
            finally { refreshingHistory = false; }
        }
        internal void RefreshHistoryEntries() { if (historyLoaded && !IsDisposed) historyView.RefreshEntries(); }
        private void LoadHistory() { if (!historyLoaded) { historyLoaded = true; historyView.RefreshEntries(); } }
        private void Arrange(bool contentChanged = false)
        {
            if (!ready || arranging || body.Width < 1) return;
            arranging = true;
            SuspendLayout();
            sidebar.Width = Ui.Scale(this, 208);
            header.Height = Ui.Scale(this, 54); footer.Height = Ui.Scale(this, 70);
            ResumeLayout(true);
            sidebar.AutoScrollMinSize = new Size(0, Ui.Scale(this, 136 + navigation.Count * 48));
            brand.Location = new Point(Ui.Scale(this, 18), Ui.Scale(this, 38) + sidebar.AutoScrollPosition.Y);
            version.Location = new Point(Ui.Scale(this, 44), Ui.Scale(this, 86) + sidebar.AutoScrollPosition.Y);
            int index = 0;
            foreach (var button in navigation.Values)
                button.SetBounds(Ui.Scale(this, 10), Ui.Scale(this, 136 + index++ * 48) + sidebar.AutoScrollPosition.Y, sidebar.ClientSize.Width - Ui.Scale(this, 20), Ui.Scale(this, 47));
            pageTitle.Location = new Point(Ui.Scale(this, 27), Ui.Scale(this, 16));
            close.SetBounds(header.ClientSize.Width - Ui.Scale(this, 42), Ui.Scale(this, 9), Ui.Scale(this, 32), Ui.Scale(this, 32));
            minimize.SetBounds(header.ClientSize.Width - Ui.Scale(this, 79), Ui.Scale(this, 9), Ui.Scale(this, 32), Ui.Scale(this, 32));
            body.SuspendLayout();
            var page = pages[pageTitle.Text];
            page.SuspendLayout();
            if (page == pages["历史记录"])
            {
                page.SetBounds(0, 0, body.ClientSize.Width, body.ClientSize.Height);
                historyView.Size = new Size(Math.Max(1, page.Width - page.Padding.Horizontal), Math.Max(1, page.Height - page.Padding.Vertical));
                page.ResumeLayout(true); historyView.PerformLayout();
            }
            else if (contentChanged || arrangedPage != page || arrangedWidth != body.Width || arrangedDpi != DeviceDpi)
            {
                page.Width = Math.Max(1, body.Width - Ui.Scale(this, 20));
                int width = Math.Max(1, page.Width - page.Padding.Horizontal);
                foreach (Control control in page.Controls)
                {
                    var rounded = control as RoundedPanel;
                    if (rounded != null)
                    {
                        rounded.SuspendLayout();
                        var table = (TableLayoutPanel)rounded.Controls[0];
                        table.SuspendLayout();
                        rounded.Width = width;
                        table.Width = width - rounded.Padding.Horizontal;
                        int textWidth = Math.Max(1, (int)(table.Width * .56) - Ui.Scale(this, 14));
                        int valueWidth = Math.Max(1, (int)(table.Width * .44) - Ui.Scale(this, 3));
                        for (int row = 0; row < table.RowCount; row++)
                        {
                            var text = (FlowLayoutPanel)table.GetControlFromPosition(0, row);
                            text.SuspendLayout();
                            foreach (Control label in text.Controls) label.Size = label.GetPreferredSize(new Size(textWidth, 0));
                            text.ResumeLayout(true);
                            Control value = table.GetControlFromPosition(1, row);
                            if (value is TextInputField || value is ComboBox || value is ServiceOrderControl) value.Width = Math.Min(Ui.Scale(this, value is ComboBox && value != arkModel && value != baiduMode ? 170 : 250), valueWidth);
                            else if (value is Label) value.MaximumSize = new Size(valueWidth, 0);
                        }
                        table.ResumeLayout(true);
                        rounded.Height = table.PreferredSize.Height + rounded.Padding.Vertical;
                        rounded.ResumeLayout(true);
                    }
                    else if (control is Label) control.MaximumSize = new Size(Math.Max(1, width - control.Margin.Horizontal), 0);
                }
                page.ResumeLayout(true);
                page.Height = page.GetPreferredSize(new Size(page.Width, 0)).Height;
            }
            else page.ResumeLayout(false);
            arrangedPage = page; arrangedWidth = body.Width; arrangedDpi = DeviceDpi;
            body.ResumeLayout(true);
            // .NET 4.8 can leave WS_HSCROLL set after DPI scaling even when HorizontalScroll.Visible is false.
            if (body.IsHandleCreated && !body.HorizontalScroll.Visible) ShowScrollBar(body.Handle, 0, false);
            arranging = false;
        }
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitWorkingArea(); Arrange();
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); if (pageTitle.Text == "历史记录") LoadHistory(); }
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            // Recalculate scroll extents after docked bounds and the active page have both settled.
            if (body != null) body.PerformLayout();
            // The resized compositing surface needs its descendants repainted, including unchanged labels.
            Invalidate(true);
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            bool previous = ready;
            ready = false;
            try { base.OnDpiChanged(e); }
            finally { ready = previous; }
        }
        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg == 0x02e0) { FitWorkingArea(); Arrange(); }
        }
        private void FitWorkingArea()
        {
            Rectangle area = Screen.FromControl(this).WorkingArea;
            MinimumSize = new Size(Math.Min(Ui.Scale(this, 860), area.Width), Math.Min(Ui.Scale(this, 580), area.Height));
            Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
            if (!Ui.RenderOffscreen) Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
        }
        private void Save()
        {
            try
            {
                var next = Value.Copy();
                string[] sources = { "auto", "zh", "en", "ja", "ko", "fr", "de", "ru", "es" };
                string[] targets = { "smart", "zh", "en", "ja", "ko", "fr", "de", "ru", "es" };
                next.OcrProvider = Provider(ocr);
                next.TranslateProvider = Provider(translator);
                next.TranslateServiceOrder = translationOrder.Order;
                next.OcrServiceOrder = recognitionOrder.Order;
                next.SourceLanguage = sources[source.SelectedIndex];
                next.TargetLanguage = targets[target.SelectedIndex];
                next.CaptureStroke = stroke.SelectedIndex + 1;
                next.HistoryRetentionDays = (int)historyRetention.Value;
                next.ArkModel = ArkModelId();
                next.ArkApiMode = arkApiModes[arkApiMode.SelectedIndex];
                next.BaiduDomain = AppSettings.BaiduDomains[baiduDomain.SelectedIndex];
                next.ImageTranslateProvider = imageTranslator.SelectedIndex == 1 ? "baidu_image" : "pipeline";
                next.BaiduImageTranslateEnabled = imageTranslator.SelectedIndex == 1;
                next.BaiduImageTargetLanguage = imageTargets[imageTarget.SelectedIndex];
                for (int i = 0; i < AppSettings.HotkeyCount; i++) next.Hotkeys[i] = fields["hotkey" + i].Text.Trim();
                Hotkey.Validate(next.Hotkeys);
                foreach (var field in fields) if (!field.Key.StartsWith("hotkey", StringComparison.Ordinal) && field.Key != "ArkModel") typeof(AppSettings).GetProperty(field.Key).SetValue(next, field.Value.Text.Trim(), null);
                foreach (var item in toggles) typeof(AppSettings).GetProperty(item.Key).SetValue(next, item.Value.Checked, null);
                next.NormalizeProviders();
                save(next);
                Value = next.Copy();
                source.SelectedIndex = Math.Max(0, Array.IndexOf(sources, Value.SourceLanguage));
                target.SelectedIndex = Math.Max(0, Array.IndexOf(targets, Value.TargetLanguage));
                stroke.SelectedIndex = Math.Max(0, Math.Min(19, Value.CaptureStroke - 1));
                saveStatus.Text = "设置已保存";
            }
            catch (Exception ex) { saveStatus.Text = "保存未完成"; Ui.Error(this, ex); }
        }
    }
}

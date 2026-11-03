using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal enum CaptureAction { Copy, Save, Pin, Ocr, Translate, Scroll, ImageTranslate, Handwriting, QrCode, Table, BaiduTable }
    internal sealed class CaptureForm : Form
    {
        private readonly Bitmap desktop;
        private readonly Rectangle desktopBounds;
        private readonly AppSettings options;
        private readonly AnnotationStyle initialStyle;
        private readonly int initialAction;
        private readonly List<Rectangle> windows = new List<Rectangle>();
        private readonly List<Panel> grips = new List<Panel>();
        private readonly List<GlyphButton> toolButtons = new List<GlyphButton>();
        private readonly List<GlyphButton> toolbarButtons = new List<GlyphButton>();
        private readonly Font tagFont = new Font("Microsoft YaHei UI", 9F);
        private AnnotationOptions styles;
        private RoundedPanel toolbar;
        private ImageCanvas canvas;
        private Rectangle selected, origin, transformStart;
        private Point start, mouse, transformPoint;
        private bool dragging, transforming, windowMode;
        private int transformHandle;
        public Bitmap Captured { get; private set; }
        public Rectangle ScreenRegion { get; private set; }
        public CaptureAction Action { get; private set; }
        internal AnnotationStyle Style { get { return styles == null ? initialStyle : styles.Style; } }
        protected override bool ShowWithoutActivation { get { return Ui.RenderOffscreen; } }

        public CaptureForm(string title) : this(title, new AppSettings(), 0) { }
        public CaptureForm(string title, AppSettings settings, int action) : this(title, settings, action, SystemInformation.VirtualScreen)
        {
            Native.EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (Native.IsWindowVisible(window))
                {
                    Native.RECT rectangle;
                    if (Native.DwmGetWindowAttribute(window, 9, out rectangle, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.RECT))) != 0) Native.GetWindowRect(window, out rectangle);
                    if (rectangle.Right - rectangle.Left > 20 && rectangle.Bottom - rectangle.Top > 20) windows.Add(rectangle.Rectangle);
                }
                return true;
            }, IntPtr.Zero);
        }
        private CaptureForm(string title, AppSettings settings, int action, Rectangle bounds) : this(title, settings, action, ScreenCapture.FrozenDesktop(bounds), bounds) { }
        internal CaptureForm(string title, AppSettings settings, int action, Bitmap frozen, Rectangle bounds)
        {
            options = settings; initialStyle = new AnnotationStyle(settings); desktop = frozen; desktopBounds = bounds; initialAction = action;
            AutoScaleMode = AutoScaleMode.None; Font = Ui.Font; FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual; Bounds = bounds; TopMost = true; ShowInTaskbar = true;
            DoubleBuffered = true; KeyPreview = true; Cursor = Cursors.Cross; Text = "SnapTransOCR · 截图";
            // Every paint copies opaque frozen pixels over its full clip; no separate background clear is needed.
            SetStyle(ControlStyles.Opaque, true);
            for (int i = 0; i < 8; i++)
            {
                int handle = i;
                var grip = new Panel { Size = new Size(8, 8), BackColor = Color.White, BorderStyle = BorderStyle.FixedSingle, Visible = false };
                grip.Cursor = i == 0 || i == 4 ? Cursors.SizeNWSE : i == 2 || i == 6 ? Cursors.SizeNESW : i == 1 || i == 5 ? Cursors.SizeNS : Cursors.SizeWE;
                grip.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) BeginTransform(handle, grip.PointToScreen(e.Location)); };
                grips.Add(grip); Controls.Add(grip);
            }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // A hotkey can open the overlay without any MouseMove event before its first paint.
            mouse = Cursor.Position;
            mouse.Offset(-desktopBounds.Left, -desktopBounds.Top);
        }
        protected override void OnShown(EventArgs e) { base.OnShown(e); if (!Ui.RenderOffscreen) { Bounds = desktopBounds; Activate(); } }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            // The frozen desktop and the overlay must keep one physical pixel per client pixel.
            e.Cancel = true;
            base.OnDpiChanged(e);
            PositionToolbar();
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x007e) { Close(); return; }
            base.WndProc(ref message);
        }
        private GlyphButton Button(string icon, string title, Action action)
        {
            var button = new GlyphButton(icon, title, action) { BackColor = Color.White };
            toolbar.Controls.Add(button); toolbarButtons.Add(button); return button;
        }
        private void BuildToolbar()
        {
            toolbar = new RoundedPanel { Fill = Color.White, BorderColor = Color.FromArgb(214, 215, 219), Radius = 14, Visible = false };
            var tools = new[] { Tool.Pan, Tool.Rectangle, Tool.Ellipse, Tool.Line, Tool.Arrow, Tool.Pen, Tool.Text, Tool.Mosaic, Tool.Crop };
            var icons = new[] { "move", "rect", "ellipse", "line", "arrow", "brush", "text", "mosaic", "crop" };
            var labels = new[] { "移动 / 调整选区", "矩形", "椭圆", "直线", "箭头", "画笔", "文字", "马赛克", "裁剪" };
            for (int i = 0; i < tools.Length; i++)
            {
                Tool tool = tools[i];
                var item = Button(icons[i], labels[i], delegate
                {
                    if (canvas == null) return;
                    canvas.Tool = tool; canvas.Cursor = tool == Tool.Pan ? Cursors.SizeAll : Cursors.Cross;
                    foreach (var button in toolButtons) { button.Selected = (Tool)button.Tag == tool; button.Invalidate(); }
                });
                item.Tag = tool; item.Selected = i == 0; toolButtons.Add(item);
            }
            Button("undo", "撤销 Ctrl+Z", delegate { if (canvas != null) canvas.Undo(); });
            Button("redo", "重做 Ctrl+Y", delegate { if (canvas != null) canvas.Redo(); });
            Button("ocr", "OCR 文字识别", delegate { Complete(CaptureAction.Ocr); });
            var recognition = Button("general", "专项识别：手写、二维码、表格", delegate { });
            RecognitionMenu.Attach(recognition, options, delegate(RecognitionKind kind)
            {
                Complete(kind == RecognitionKind.Handwriting ? CaptureAction.Handwriting : kind == RecognitionKind.QrCode ? CaptureAction.QrCode : kind == RecognitionKind.BaiduTable ? CaptureAction.BaiduTable : CaptureAction.Table);
            });
            Button("translate", "截图翻译", delegate { Complete(CaptureAction.Translate); });
            Button("image-translate", "图片原位翻译 / 悬浮译图", delegate { Complete(CaptureAction.ImageTranslate); });
            Button("scroll", "滚动长截图", delegate { Complete(CaptureAction.Scroll); });
            Button("pin", "置顶贴图", delegate { Complete(CaptureAction.Pin); });
            Button("save", "保存 Ctrl+S", delegate { Complete(CaptureAction.Save); });
            Button("copy", "复制并完成 Enter / Ctrl+C", delegate { Complete(CaptureAction.Copy); });
            Button("close", "取消 Esc", Close);
            styles = new AnnotationOptions(initialStyle);
            toolbar.Controls.Add(styles);
            Controls.Add(toolbar);
        }
        internal void Complete(CaptureAction action)
        {
            if (canvas == null) return;
            Action = action;
            Captured = action == CaptureAction.Scroll ? null : canvas.Render();
            ScreenRegion = new Rectangle(desktopBounds.Left + selected.X, desktopBounds.Top + selected.Y, selected.Width, selected.Height);
            DialogResult = DialogResult.OK; Close();
        }
        internal void PreviewSelection(Rectangle rectangle) { Select(rectangle); }
        private void Select(Rectangle rectangle)
        {
            rectangle = Rectangle.Intersect(rectangle, new Rectangle(Point.Empty, desktop.Size));
            if (rectangle.Width < 3 || rectangle.Height < 3) return;
            if (toolbar == null) BuildToolbar();
            if (canvas != null) { Controls.Remove(canvas); canvas.Dispose(); }
            origin = selected = rectangle;
            canvas = new ImageCanvas(desktop.Clone(rectangle, PixelFormat.Format32bppPArgb)) { PixelView = true, Bounds = rectangle, Dock = DockStyle.None, Cursor = Cursors.SizeAll };
            canvas.MoveSelectionRequested += delegate(Point point) { BeginTransform(-1, point); };
            canvas.Changed += delegate
            {
                Rectangle crop = canvas.Crop;
                selected = new Rectangle(origin.X + crop.X, origin.Y + crop.Y, crop.Width, crop.Height);
                canvas.Bounds = selected; PositionToolbar(); Invalidate();
            };
            Controls.Add(canvas); canvas.Fit();
            styles.Bind(canvas);
            foreach (var button in toolButtons) { button.Selected = (Tool)button.Tag == Tool.Pan; button.Invalidate(); }
            toolbar.Visible = true; PositionToolbar(); toolbar.BringToFront();
            foreach (var grip in grips) { grip.Visible = true; grip.BringToFront(); }
            Invalidate();
        }
        private void PositionToolbar()
        {
            if (toolbar == null || canvas == null) return;
            Point center = new Point(desktopBounds.Left + selected.X + selected.Width / 2, desktopBounds.Top + selected.Y + selected.Height / 2);
            float scale = Native.MonitorScale(center);
            Rectangle screen = Screen.FromPoint(center).WorkingArea;
            screen.Offset(-desktopBounds.Left, -desktopBounds.Top);
            int cell = (int)(44 * scale), inset = (int)(8 * scale);
            int columns = Math.Max(5, Math.Min(toolbarButtons.Count, (screen.Width - 24 - inset * 2) / cell));
            for (int i = 0; i < toolbarButtons.Count; i++) toolbarButtons[i].SetBounds(inset + i % columns * cell, inset + i / columns * cell, cell, cell);
            int rows = (toolbarButtons.Count + columns - 1) / columns;
            int toolbarWidth = columns * cell + inset * 2;
            styles.SetScale(scale);
            styles.MaximumSize = new Size(toolbarWidth - inset * 2, 0);
            styles.SetBounds(inset, inset + rows * cell + 4, toolbarWidth - inset * 2, (int)(46 * scale));
            styles.PerformLayout();
            toolbar.Size = new Size(toolbarWidth, inset + rows * cell + 4 + styles.PreferredSize.Height + inset);
            int x = Math.Max(screen.Left + 6, Math.Min(selected.Right - toolbar.Width, screen.Right - toolbar.Width - 6));
            int y = selected.Bottom + (int)(10 * scale);
            if (y + toolbar.Height > screen.Bottom - 6) y = selected.Top - toolbar.Height - (int)(10 * scale);
            if (y < screen.Top + 6) y = Math.Max(screen.Top + 6, screen.Bottom - toolbar.Height - 8);
            toolbar.Location = new Point(x, y);
            Point[] points = { new Point(selected.Left, selected.Top), new Point(selected.Left + selected.Width / 2, selected.Top), new Point(selected.Right, selected.Top), new Point(selected.Right, selected.Top + selected.Height / 2), new Point(selected.Right, selected.Bottom), new Point(selected.Left + selected.Width / 2, selected.Bottom), new Point(selected.Left, selected.Bottom), new Point(selected.Left, selected.Top + selected.Height / 2) };
            int gripSize = Math.Max(8, (int)(8 * scale));
            for (int i = 0; i < grips.Count; i++) grips[i].SetBounds(points[i].X - gripSize / 2, points[i].Y - gripSize / 2, gripSize, gripSize);
        }
        private void BeginTransform(int handle, Point screen)
        {
            transforming = true; transformHandle = handle; transformStart = selected; transformPoint = PointToClient(screen);
            canvas.Hide(); toolbar.Hide(); foreach (var grip in grips) grip.Hide(); Capture = true;
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right) { Close(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (windowMode) { Select(selected); AutoAction(); return; }
            start = mouse = e.Location; dragging = true; selected = Rectangle.Empty; Capture = true;
            if (canvas != null) { Controls.Remove(canvas); canvas.Dispose(); canvas = null; }
            if (toolbar != null) toolbar.Hide(); foreach (var grip in grips) grip.Hide();
            Invalidate();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            Point oldMouse = mouse;
            Rectangle oldSelection = selected;
            mouse = e.Location;
            if (transforming)
            {
                int dx = e.X - transformPoint.X, dy = e.Y - transformPoint.Y;
                Rectangle box = transformStart;
                if (transformHandle == -1) box.Location = new Point(Math.Max(0, Math.Min(ClientSize.Width - box.Width, box.X + dx)), Math.Max(0, Math.Min(ClientSize.Height - box.Height, box.Y + dy)));
                else
                {
                    int left = box.Left, right = box.Right, top = box.Top, bottom = box.Bottom;
                    if (transformHandle == 0 || transformHandle == 6 || transformHandle == 7) left = Math.Max(0, Math.Min(right - 3, left + dx));
                    if (transformHandle == 2 || transformHandle == 3 || transformHandle == 4) right = Math.Min(ClientSize.Width, Math.Max(left + 3, right + dx));
                    if (transformHandle == 0 || transformHandle == 1 || transformHandle == 2) top = Math.Max(0, Math.Min(bottom - 3, top + dy));
                    if (transformHandle == 4 || transformHandle == 5 || transformHandle == 6) bottom = Math.Min(ClientSize.Height, Math.Max(top + 3, bottom + dy));
                    box = Rectangle.FromLTRB(left, top, right, bottom);
                }
                selected = box;
            }
            else if (dragging) selected = ScreenCapture.Selection(start, mouse, ClientSize);
            else if (windowMode && canvas == null)
            {
                selected = Rectangle.Empty; Point screen = PointToScreen(mouse);
                foreach (Rectangle rectangle in windows) if (rectangle.Contains(screen)) { var relative = rectangle; relative.Offset(-desktopBounds.Left, -desktopBounds.Top); selected = Rectangle.Intersect(relative, ClientRectangle); break; }
            }
            using (var dirty = new Region(oldSelection))
            {
                // The unchanged selection interior and frozen background need no repaint.
                dirty.Xor(selected);
                if (oldSelection != selected)
                {
                    AddSelectionOutline(dirty, oldSelection);
                    AddSelectionOutline(dirty, selected);
                }
                if (options.CaptureMagnifier && canvas == null && !transforming)
                {
                    dirty.Union(LoupeBounds(oldMouse));
                    dirty.Union(LoupeBounds(mouse));
                }
                Invalidate(dirty);
            }
        }
        private void AddSelectionOutline(Region dirty, Rectangle rectangle)
        {
            if (rectangle.IsEmpty) return;
            Rectangle outer = rectangle, inner = rectangle;
            outer.Inflate(2, 2); inner.Inflate(-2, -2);
            using (var border = new Region(outer))
            {
                if (inner.Width > 0 && inner.Height > 0) border.Exclude(inner);
                dirty.Union(border);
            }
            dirty.Union(TagBounds(rectangle));
        }
        private Rectangle LoupeBounds(Point point)
        {
            return new Rectangle(Math.Min(ClientSize.Width - 130, point.X + 25) - 4, Math.Min(ClientSize.Height - 145, point.Y + 25) - 4, 132, 132);
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || Disposing) return;
            if (dragging) { dragging = false; selected = Rectangle.Empty; Invalidate(); }
            if (transforming)
            {
                transforming = false; selected = transformStart;
                canvas.Show(); toolbar.Show();
                PositionToolbar(); foreach (var grip in grips) grip.Show();
                Invalidate();
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (transforming) { transforming = false; Capture = false; Select(selected); return; }
            if (!dragging) return;
            dragging = false; Capture = false;
            Select(ScreenCapture.Selection(start, e.Location, ClientSize)); AutoAction();
        }
        private void AutoAction()
        {
            if (initialAction == 1) Complete(CaptureAction.Ocr);
            else if (initialAction == 2) Complete(CaptureAction.Translate);
            else if (initialAction == 3) Complete(CaptureAction.Scroll);
            else if (initialAction == 4) Complete(CaptureAction.ImageTranslate);
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (canvas != null && canvas.EditingText)
            {
                if (e.KeyCode == Keys.Escape) { canvas.CancelText(); e.Handled = e.SuppressKeyPress = true; }
                else if (e.Control && e.KeyCode == Keys.Enter) { canvas.CommitText(); e.Handled = e.SuppressKeyPress = true; }
                base.OnKeyDown(e); return;
            }
            if (e.KeyCode == Keys.Escape) Close();
            else if (e.Control && e.KeyCode == Keys.Z && canvas != null) { canvas.Undo(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.Y && canvas != null) { canvas.Redo(); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.S) { Complete(CaptureAction.Save); e.SuppressKeyPress = true; }
            else if (e.Control && e.KeyCode == Keys.C) { Complete(CaptureAction.Copy); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Space && canvas == null) { windowMode = !windowMode; selected = Rectangle.Empty; Invalidate(); }
            else if (e.KeyCode == Keys.Enter)
            {
                if (canvas != null) Complete(e.Control ? CaptureAction.Ocr : CaptureAction.Copy);
                else
                {
                    Rectangle rectangle = e.Shift ? desktopBounds : Screen.FromPoint(Cursor.Position).Bounds; rectangle.Offset(-desktopBounds.Left, -desktopBounds.Top); Select(rectangle); AutoAction();
                }
            }
            base.OnKeyDown(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            CompositingMode previous = e.Graphics.CompositingMode;
            e.Graphics.CompositingMode = CompositingMode.SourceCopy;
            e.Graphics.DrawImageUnscaled(desktop, 0, 0);
            e.Graphics.CompositingMode = previous;
            using (var shade = new SolidBrush(Color.FromArgb(115, 10, 13, 20)))
            using (var outside = new Region(ClientRectangle)) { if (!selected.IsEmpty) outside.Exclude(selected); e.Graphics.FillRegion(shade, outside); }
            if (!selected.IsEmpty)
            {
                using (var border = new Pen(Ui.Accent, 1)) e.Graphics.DrawRectangle(border, selected);
                DrawTag(e.Graphics);
            }
            if (canvas == null && !transforming)
            {
                if (options.CaptureMagnifier && ClientRectangle.Contains(mouse)) DrawLoupe(e.Graphics);
            }
        }
        private void DrawLoupe(Graphics graphics)
        {
            int pixelX = Math.Max(6, Math.Min(desktop.Width - 7, mouse.X)), pixelY = Math.Max(6, Math.Min(desktop.Height - 7, mouse.Y));
            if (desktop.Width < 14 || desktop.Height < 14) return;
            int x = Math.Min(ClientSize.Width - 130, mouse.X + 25), y = Math.Min(ClientSize.Height - 145, mouse.Y + 25);
            graphics.FillRectangle(Brushes.White, x - 3, y - 3, 126, 126);
            GraphicsState state = graphics.Save();
            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = PixelOffsetMode.Half;
            graphics.DrawImage(desktop, new Rectangle(x, y, 120, 120), pixelX - 6, pixelY - 6, 12, 12, GraphicsUnit.Pixel);
            using (var pen = new Pen(Ui.Accent)) { graphics.DrawLine(pen, x + 60, y, x + 60, y + 120); graphics.DrawLine(pen, x, y + 60, x + 120, y + 60); }
            graphics.Restore(state);
        }
        private Rectangle TagBounds(Rectangle rectangle)
        {
            Size size = TextRenderer.MeasureText(rectangle.Width + " × " + rectangle.Height, tagFont);
            return new Rectangle(rectangle.X, Math.Max(4, rectangle.Y - 29), size.Width + 14, size.Height + 8);
        }
        private void DrawTag(Graphics graphics)
        {
            Rectangle bounds = TagBounds(selected);
            using (var brush = new SolidBrush(Color.FromArgb(225, 36, 38, 43))) graphics.FillRectangle(brush, bounds);
            TextRenderer.DrawText(graphics, selected.Width + " × " + selected.Height, tagFont, new Point(bounds.X + 7, bounds.Y + 4), Color.White, TextFormatFlags.PreserveGraphicsClipping);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { desktop.Dispose(); tagFont.Dispose(); }
            base.Dispose(disposing);
        }
    }
}

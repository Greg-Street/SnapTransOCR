using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal enum Tool { Pan, Rectangle, Ellipse, Arrow, Pen, Text, Mosaic, Crop, Line }
    internal sealed class Mark : IDisposable
    {
        public Tool Kind;
        public RectangleF Bounds;
        public PointF First, Last;
        public Color Color;
        public float Width;
        public string Text;
        public float FontSize = 24;
        public bool Bold;
        public List<PointF> Points = new List<PointF>();
        public Bitmap Pixels;
        public void Draw(Graphics graphics)
        {
            using (var pen = new Pen(Color, Width))
            using (var brush = new SolidBrush(Color))
            {
                pen.LineJoin = LineJoin.Round;
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                switch (Kind)
                {
                    case Tool.Rectangle: graphics.DrawRectangle(pen, Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height); break;
                    case Tool.Ellipse: graphics.DrawEllipse(pen, Bounds); break;
                    case Tool.Arrow:
                        using (var cap = new AdjustableArrowCap(4, 6, true)) { pen.CustomEndCap = cap; graphics.DrawLine(pen, First, Last); }
                        break;
                    case Tool.Pen: if (Points.Count > 1) graphics.DrawLines(pen, Points.ToArray()); break;
                    case Tool.Line: graphics.DrawLine(pen, First, Last); break;
                    case Tool.Text:
                        using (var font = new Font("Microsoft YaHei UI", FontSize, Bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
                        {
                            if (Bounds.Width > 0) graphics.DrawString(Text, font, brush, Bounds);
                            else graphics.DrawString(Text, font, brush, First);
                        }
                        break;
                    case Tool.Mosaic:
                        if (Pixels != null)
                        {
                            InterpolationMode previous = graphics.InterpolationMode;
                            PixelOffsetMode offset = graphics.PixelOffsetMode;
                            graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
                            graphics.PixelOffsetMode = PixelOffsetMode.Half;
                            graphics.DrawImage(Pixels, Bounds, new RectangleF(0, 0, Pixels.Width, Pixels.Height), GraphicsUnit.Pixel);
                            graphics.InterpolationMode = previous;
                            graphics.PixelOffsetMode = offset;
                        }
                        break;
                }
            }
        }
        public void Dispose() { if (Pixels != null) Pixels.Dispose(); }
    }

    internal sealed class ImageCanvas : Control
    {
        private readonly Bitmap image;
        private readonly List<Mark> marks = new List<Mark>();
        private int count;
        private Mark current;
        private float zoom = 1;
        private PointF pan;
        private Point dragStart;
        private PointF panStart;
        private bool panning;
        private Tool tool = Tool.Pan;
        private Color color = Color.FromArgb(239, 74, 64);
        private float textSize = 24;
        private bool textBold;
        private TextBox textEditor;
        private Panel textFrame;
        private Font editorFont;
        private PointF textOrigin;
        public Tool Tool { get { return tool; } set { if (tool != value) CommitText(); tool = value; } }
        public Color Color { get { return color; } set { color = value; UpdateTextStyle(); } }
        public float TextSize { get { return textSize; } set { textSize = Math.Max(8, Math.Min(144, value)); UpdateTextStyle(); } }
        public bool TextBold { get { return textBold; } set { textBold = value; UpdateTextStyle(); } }
        public bool EditingText { get { return textEditor != null; } }
        internal TextBox TextInput { get { return textEditor; } }
        public float Stroke = 3;
        public bool PixelView;
        public event Action<Point> MoveSelectionRequested;
        public event Action Changed;
        public ImageCanvas(Bitmap source)
        {
            image = source;
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            BackColor = System.Drawing.Color.FromArgb(229, 234, 238);
            SetStyle(ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
            TabStop = true;
        }
        public Rectangle Crop
        {
            get
            {
                for (int i = count - 1; i >= 0; i--) if (marks[i].Kind == Tool.Crop) return Rectangle.Round(marks[i].Bounds);
                return new Rectangle(Point.Empty, image.Size);
            }
        }
        public float Zoom { get { return zoom; } }
        public void Fit()
        {
            if (PixelView) { zoom = 1; pan = PointF.Empty; UpdateView(); return; }
            Rectangle crop = Crop;
            zoom = Math.Max(0.01f, Math.Min(1, Math.Min((ClientSize.Width - 32f) / crop.Width, (ClientSize.Height - 32f) / crop.Height)));
            Center();
        }
        public void Actual() { zoom = 1; Center(); }
        private void Center()
        {
            Rectangle crop = Crop;
            pan = new PointF((ClientSize.Width - crop.Width * zoom) / 2, (ClientSize.Height - crop.Height * zoom) / 2);
            UpdateView();
        }
        public void Undo() { CommitText(); if (count > 0) { count--; Fit(); } }
        public void Redo() { CommitText(); if (count < marks.Count) { count++; Fit(); } }
        public Bitmap Render()
        {
            CommitText();
            return Render(Crop);
        }
        internal void UpdateBackground(Image patch, Point location)
        {
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(patch, location);
            }
            InvalidateImageRectangle(new RectangleF(location, patch.Size));
        }
        private void InvalidateImageRectangle(RectangleF bounds)
        {
            Rectangle crop = Crop;
            bounds = new RectangleF(pan.X + (bounds.X - crop.X) * zoom, pan.Y + (bounds.Y - crop.Y) * zoom, bounds.Width * zoom, bounds.Height * zoom);
            bounds.Inflate(2, 2);
            Rectangle dirty = Rectangle.Intersect(ClientRectangle, Rectangle.Ceiling(bounds));
            if (dirty.Width > 0 && dirty.Height > 0) Invalidate(dirty);
        }
        private Bitmap Render(Rectangle region)
        {
            var result = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(result))
                {
                    graphics.Clear(System.Drawing.Color.White);
                    graphics.TranslateTransform(-region.X, -region.Y);
                    Draw(graphics);
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
        private void Draw(Graphics graphics)
        {
            graphics.DrawImageUnscaled(image, 0, 0);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < count; i++) marks[i].Draw(graphics);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle crop = Crop;
            e.Graphics.TranslateTransform(pan.X, pan.Y);
            e.Graphics.ScaleTransform(zoom, zoom);
            e.Graphics.TranslateTransform(-crop.X, -crop.Y);
            e.Graphics.SetClip(crop, CombineMode.Intersect);
            Draw(e.Graphics);
            if (current != null)
            {
                if (Tool == Tool.Crop || Tool == Tool.Mosaic)
                {
                    using (var pen = new Pen(System.Drawing.Color.FromArgb(0, 140, 120), 1 / zoom)) { pen.DashStyle = DashStyle.Dash; e.Graphics.DrawRectangle(pen, current.Bounds.X, current.Bounds.Y, current.Bounds.Width, current.Bounds.Height); }
                }
                else current.Draw(e.Graphics);
            }
        }
        private PointF ImagePoint(Point point)
        {
            Rectangle crop = Crop;
            return new PointF(Math.Max(crop.Left, Math.Min(crop.Right - 1, (point.X - pan.X) / zoom + crop.X)), Math.Max(crop.Top, Math.Min(crop.Bottom - 1, (point.Y - pan.Y) / zoom + crop.Y)));
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            if (e.Button == MouseButtons.Left && Tool == Tool.Pan && MoveSelectionRequested != null) { MoveSelectionRequested(PointToScreen(e.Location)); return; }
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && Tool == Tool.Pan))
            {
                panning = true;
                dragStart = e.Location;
                panStart = pan;
                Capture = true;
                Cursor = Cursors.Hand;
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            PointF point = ImagePoint(e.Location);
            if (Tool == Tool.Text)
            {
                BeginText(point);
                return;
            }
            current = new Mark { Kind = Tool, First = point, Last = point, Bounds = new RectangleF(point, SizeF.Empty), Color = Color, Width = Stroke };
            current.Points.Add(point);
            Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (panning) { pan = new PointF(panStart.X + e.X - dragStart.X, panStart.Y + e.Y - dragStart.Y); Invalidate(); return; }
            if (current == null) return;
            RectangleF previous = current.Kind == Tool.Pen ? new RectangleF(current.Last, SizeF.Empty) : current.Bounds;
            current.Last = ImagePoint(e.Location);
            current.Bounds = RectangleF.FromLTRB(Math.Min(current.First.X, current.Last.X), Math.Min(current.First.Y, current.Last.Y), Math.Max(current.First.X, current.Last.X), Math.Max(current.First.Y, current.Last.Y));
            if (current.Kind == Tool.Pen) current.Points.Add(current.Last);
            RectangleF dirty = RectangleF.Union(previous, current.Kind == Tool.Pen ? new RectangleF(current.Last, SizeF.Empty) : current.Bounds);
            float padding = Math.Max(3 / zoom, current.Width * (current.Kind == Tool.Arrow ? 7 : 1));
            dirty.Inflate(padding, padding);
            InvalidateImageRectangle(dirty);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (panning) { panning = false; Capture = false; Cursor = Cursors.Default; return; }
            if (current == null) return;
            Mark completed = current;
            current = null;
            Capture = false;
            if (completed.Kind == Tool.Pen && completed.Points.Count < 2) { completed.Dispose(); return; }
            if (completed.Kind != Tool.Pen && completed.Kind != Tool.Arrow && completed.Kind != Tool.Line && (completed.Bounds.Width < 2 || completed.Bounds.Height < 2)) { completed.Dispose(); return; }
            if (completed.Kind == Tool.Mosaic)
            {
                Rectangle rectangle = Rectangle.Intersect(Rectangle.Round(completed.Bounds), Crop);
                completed.Bounds = rectangle;
                using (var patch = Render(rectangle))
                {
                    completed.Pixels = new Bitmap(Math.Max(1, rectangle.Width / 12), Math.Max(1, rectangle.Height / 12));
                    using (var graphics = Graphics.FromImage(completed.Pixels)) graphics.DrawImage(patch, new Rectangle(Point.Empty, completed.Pixels.Size));
                }
            }
            AddMark(completed);
            if (completed.Kind == Tool.Crop) Fit();
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            if (!Capture)
            {
                panning = false; Cursor = Cursors.Default;
                if (current != null) { current.Dispose(); current = null; Invalidate(); }
            }
            base.OnMouseCaptureChanged(e);
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (PixelView) return;
            CommitText();
            Rectangle crop = Crop;
            float x = (e.X - pan.X) / zoom, y = (e.Y - pan.Y) / zoom;
            zoom = Math.Max(0.01f, Math.Min(8, zoom * (e.Delta > 0 ? 1.2f : 1 / 1.2f)));
            pan = new PointF(e.X - x * zoom, e.Y - y * zoom);
            UpdateView();
        }
        internal void AddMark(Mark mark)
        {
            while (marks.Count > count) { marks[marks.Count - 1].Dispose(); marks.RemoveAt(marks.Count - 1); }
            marks.Add(mark);
            count++;
            UpdateView();
        }
        private void UpdateView() { Invalidate(); if (Changed != null) Changed(); }
        internal void BeginText(PointF point)
        {
            CommitText();
            Rectangle crop = Crop;
            int minimum = Math.Min(120, Math.Max(20, crop.Width));
            textOrigin = new PointF(Math.Max(crop.Left, Math.Min(point.X, crop.Right - minimum)), Math.Max(crop.Top, Math.Min(point.Y, crop.Bottom - Math.Min(crop.Height, TextSize + 8))));
            textFrame = new Panel { BackColor = System.Drawing.Color.White, Padding = new Padding(3), BorderStyle = BorderStyle.FixedSingle };
            textEditor = new TextBox { Multiline = true, AcceptsReturn = true, BorderStyle = BorderStyle.None, MaxLength = 10000, BackColor = System.Drawing.Color.White, WordWrap = true, AccessibleName = "截图文字" };
            textFrame.Controls.Add(textEditor); Controls.Add(textFrame);
            textEditor.TextChanged += delegate { LayoutTextInput(); };
            textEditor.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Escape) { e.Handled = e.SuppressKeyPress = true; CancelText(); }
                else if (e.Control && e.KeyCode == Keys.Enter) { e.Handled = e.SuppressKeyPress = true; CommitText(); }
            };
            UpdateTextStyle(); textFrame.BringToFront(); textEditor.Focus();
        }
        public void FocusText() { if (textEditor != null) textEditor.Focus(); }
        private void UpdateTextStyle()
        {
            if (textEditor == null) return;
            float pixels = Math.Max(6, TextSize * zoom);
            if (editorFont == null || Math.Abs(editorFont.Size - pixels) > .01f || editorFont.Bold != TextBold)
            {
                Font previous = editorFont;
                editorFont = new Font("Microsoft YaHei UI", pixels, TextBold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                textEditor.Font = editorFont;
                if (previous != null) previous.Dispose();
            }
            textEditor.ForeColor = Color;
            LayoutTextInput();
        }
        private void LayoutTextInput()
        {
            if (textEditor == null) return;
            Rectangle crop = Crop;
            int left = (int)(pan.X + (textOrigin.X - crop.X) * zoom), top = (int)(pan.Y + (textOrigin.Y - crop.Y) * zoom);
            int available = Math.Max(30, ClientSize.Width - left);
            int width = Math.Min(available, Math.Max(120, (int)(300 * zoom)));
            int measured = TextRenderer.MeasureText(textEditor.Text + " ", textEditor.Font, new Size(Math.Max(20, width - 12), 3000), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding).Height;
            int height = Math.Min(Math.Max(30, ClientSize.Height - top), Math.Max(textEditor.Font.Height + 10, measured + 12));
            textFrame.SetBounds(left, top, width, height); textEditor.SetBounds(4, 3, width - 10, height - 8);
        }
        public void CommitText()
        {
            if (textEditor == null) return;
            string value = textEditor.Text;
            var point = new PointF(textOrigin.X + textEditor.Left / zoom, textOrigin.Y + textEditor.Top / zoom);
            var mark = new Mark { Kind = Tool.Text, First = point, Text = value, Color = Color, Width = Stroke, FontSize = TextSize, Bold = TextBold, Bounds = new RectangleF(point.X, point.Y, Math.Max(10, (textEditor.Width + 1) / zoom), Math.Max(TextSize + 8, (textEditor.Height + 10) / zoom)) };
            CancelText();
            if (!String.IsNullOrWhiteSpace(value)) AddMark(mark);
            else mark.Dispose();
        }
        public void CancelText()
        {
            if (textEditor == null) return;
            var frame = textFrame; var font = editorFont;
            textEditor = null; textFrame = null; editorFont = null; Controls.Remove(frame); frame.Dispose(); if (font != null) font.Dispose(); Invalidate();
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (EditingText && keys == Keys.Escape) { CancelText(); return true; }
            if (EditingText && keys == (Keys.Control | Keys.Enter)) { CommitText(); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { CancelText(); image.Dispose(); foreach (var mark in marks) mark.Dispose(); if (current != null) current.Dispose(); }
            base.Dispose(disposing);
        }
    }

}

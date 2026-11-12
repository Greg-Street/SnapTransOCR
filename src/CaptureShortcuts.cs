using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class CaptureShortcuts : Control
    {
        private readonly Action hide;
        private Font ownedFont;
        private float scale;
        private bool pointerVisible;
        internal CaptureShortcuts(Action hide)
        {
            this.hide = hide; TabStop = false;
            AccessibleName = "截图快捷键提示，F1显示或隐藏，F2切换截图指针";
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            BackColor = Color.Transparent;
        }
        internal void Place(Rectangle area, float nextScale)
        {
            if (scale != nextScale)
            {
                scale = nextScale;
                Font previous = ownedFont;
                Font = ownedFont = new Font("Microsoft YaHei UI", 12 * scale, FontStyle.Regular, GraphicsUnit.Pixel);
                if (previous != null) previous.Dispose();
            }
            int gap = (int)(14 * scale), width = Math.Min((int)(490 * scale), Math.Max(1, area.Width - gap * 2));
            SetBounds(area.Left + gap, area.Bottom - gap - (int)(86 * scale), width, (int)(86 * scale));
        }
        internal void SetPointer(bool visible) { pointerVisible = visible; Invalidate(); }
        private Rectangle CloseBounds { get { int size = (int)(24 * scale); return new Rectangle(Width - size - (int)(6 * scale), (int)(5 * scale), size, size); } }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x84)
            {
                long packed = message.LParam.ToInt64();
                Point point = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
                message.Result = new IntPtr(CloseBounds.Contains(point) ? 1 : -1);
                return;
            }
            base.WndProc(ref message);
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left && CloseBounds.Contains(e.Location)) hide(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var path = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), 10 * scale))
            using (var fill = new SolidBrush(Color.FromArgb(239, 32, 35, 42))) e.Graphics.FillPath(fill, path);
            int left = (int)(12 * scale), line = (int)(23 * scale), top = (int)(8 * scale);
            var capture = Parent as CaptureForm;
            bool editing = capture != null && capture.EditingText, selected = capture != null && capture.HasSelection;
            string first = editing ? "文字编辑 · Enter 换行 · Ctrl+Enter 完成" : selected ? "拖动边缘调整 · 工具栏标注 / 翻译 / 贴图" : "拖动框选 · 单击选窗口 · Space 切换吸附";
            string second = editing ? "Esc 取消当前文字，其他操作完成文字后使用" : selected ? "Enter 复制 · Ctrl+Enter OCR · Ctrl+S 保存 · Esc 取消" : "Enter 当前屏幕 · Shift+Enter 全部屏幕 · Esc 取消";
            string[] lines = { first, second, "F2 截图指针 " + (pointerVisible ? "开" : "关") + " · F1 显示/隐藏提示" };
            for (int i = 0; i < lines.Length; i++)
                TextRenderer.DrawText(e.Graphics, lines[i], Font, new Rectangle(left, top + line * i, Width - left * 2 - (i == 0 ? CloseBounds.Width : 0), line), i == 2 ? Color.FromArgb(166, 195, 255) : Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            Glyphs.Draw(e.Graphics, "close", Rectangle.Inflate(CloseBounds, -(int)(5 * scale), -(int)(5 * scale)), Color.White);
        }
        protected override void Dispose(bool disposing) { if (disposing && ownedFont != null) ownedFont.Dispose(); base.Dispose(disposing); }
    }
}

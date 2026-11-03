using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class ServiceOrderControl : ListBox
    {
        private sealed class Service
        {
            internal string Code, Caption;
            public override string ToString() { return Caption; }
        }
        private int dragIndex = -1, dropIndex = -1, dragTop;
        private Point dragStart;
        private bool dragging, handlingMouseUp;
        public event Action OrderChanged;
        internal string[] Order
        {
            get
            {
                var result = new string[Items.Count];
                for (int i = 0; i < result.Length; i++) result[i] = ((Service)Items[i]).Code;
                return result;
            }
        }
        internal ServiceOrderControl(string[] order, Func<string, string> caption)
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BorderStyle = BorderStyle.None; BackColor = Color.White; IntegralHeight = false;
            DrawMode = DrawMode.OwnerDrawFixed; Width = 250;
            AccessibleDescription = "拖动左侧把手排序，或选中服务后按 Alt 加上、下方向键。";
            foreach (string provider in order) Items.Add(new Service { Code = provider, Caption = caption(provider) });
            SelectedIndex = 0; UpdateMetrics();
        }
        private void UpdateMetrics() { ItemHeight = Math.Max(Ui.Scale(this, 40), Font.Height + Ui.Scale(this, 16)); Height = ItemHeight * Items.Count; }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateMetrics(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateMetrics(); }
        protected override void ScaleControl(SizeF factor, BoundsSpecified specified) { base.ScaleControl(factor, specified); UpdateMetrics(); }
        internal void MoveSelected(int offset)
        {
            if (dragIndex >= 0) CancelDrag();
            int target = SelectedIndex + offset;
            if (SelectedIndex < 0 || target < 0 || target >= Items.Count || offset == 0) return;
            BeginUpdate();
            try { object item = SelectedItem; Items.RemoveAt(SelectedIndex); Items.Insert(target, item); SelectedIndex = target; }
            finally { EndUpdate(); }
            if (OrderChanged != null) OrderChanged();
        }
        protected override bool ProcessCmdKey(ref Message message, Keys keys)
        {
            if (keys == (Keys.Alt | Keys.Up) || keys == (Keys.Alt | Keys.Down)) { MoveSelected(keys == (Keys.Alt | Keys.Up) ? -1 : 1); return true; }
            if (keys == Keys.Escape && dragIndex >= 0) { CancelDrag(); return true; }
            return base.ProcessCmdKey(ref message, keys);
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left || e.X < 0 || e.X >= Ui.Scale(this, 36)) return;
            dragIndex = IndexFromPoint(e.Location); dragStart = e.Location;
            if (dragIndex >= 0) { dropIndex = dragIndex; dragTop = GetItemRectangle(dragIndex).Top; Capture = true; }
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = e.X < Ui.Scale(this, 36) || dragging ? Cursors.SizeAll : Cursors.Default;
            if (dragIndex < 0 || e.Button != MouseButtons.Left) return;
            if (!ClientRectangle.Contains(e.Location)) { CancelDrag(); return; }
            if (!dragging && Math.Abs(e.Y - dragStart.Y) < SystemInformation.DragSize.Height / 2) return;
            int top = Math.Max(0, Math.Min(ClientSize.Height - ItemHeight, GetItemRectangle(dragIndex).Top + e.Y - dragStart.Y));
            if (dragging && top == dragTop) return;
            dragging = true; dragTop = top;
            dropIndex = Math.Min(Items.Count - 1, (dragTop + ItemHeight / 2) / ItemHeight);
            Invalidate();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            int from = dragIndex, to = dragging && ClientRectangle.Contains(e.Location) ? dropIndex : -1;
            CancelDrag(); base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && from >= 0 && to >= 0) { SelectedIndex = from; MoveSelected(to - from); }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg != 0x202) { base.WndProc(ref message); return; }
            // Native ListBox releases capture before WinForms raises MouseUp.
            handlingMouseUp = true;
            try { base.WndProc(ref message); }
            finally { handlingMouseUp = false; }
        }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && !handlingMouseUp) CancelDrag(); }
        private void CancelDrag()
        {
            if (dragIndex < 0) return;
            int source = dragIndex;
            dragIndex = dropIndex = -1; dragging = false; Capture = false; Cursor = Cursors.Default;
            SelectedIndex = source; Invalidate();
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); if (!dragging) Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); CancelDrag(); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            for (int row = 0; row < Items.Count; row++)
            {
                int item = row;
                if (dragging)
                {
                    if (row == dropIndex) continue;
                    if (row >= dropIndex) item--;
                    if (item >= dragIndex) item++;
                }
                DrawService(e.Graphics, GetItemRectangle(row), item, false);
            }
            if (dragging) DrawService(e.Graphics, new Rectangle(0, dragTop, ClientSize.Width, ItemHeight), dragIndex, true);
            base.OnPaint(e);
        }
        private void DrawService(Graphics graphics, Rectangle bounds, int index, bool source)
        {
            Rectangle row = Rectangle.Inflate(bounds, -Ui.Scale(this, 2), -Ui.Scale(this, 3));
            using (var path = Shapes.Round(row, Ui.Scale(this, 7)))
            using (var fill = new SolidBrush(source ? Ui.Accent : index == SelectedIndex ? Color.FromArgb(232, 237, 255) : Ui.Background))
            {
                graphics.FillPath(fill, path);
                using (var pen = new Pen(source ? Ui.Accent : Color.FromArgb(219, 226, 231))) graphics.DrawPath(pen, path);
            }
            using (var brush = new SolidBrush(source ? Color.White : Ui.Ink))
                for (int x = 0; x < 2; x++) for (int y = 0; y < 3; y++)
                    graphics.FillEllipse(brush, Ui.Scale(this, 13 + x * 5), bounds.Top + ItemHeight / 2 + Ui.Scale(this, y * 5 - 6), Ui.Scale(this, 3), Ui.Scale(this, 3));
            TextRenderer.DrawText(graphics, GetItemText(Items[index]), Font,
                new Rectangle(Ui.Scale(this, 36), bounds.Top, Width - Ui.Scale(this, 44), ItemHeight), source ? Color.White : Ui.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            if (!dragging && Focused && ShowFocusCues && index == SelectedIndex) ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(row, -2, -2));
        }
    }
}

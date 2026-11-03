using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class StyledComboBox : ComboBox
    {
        private bool hover;
        private object originalSelection;
        private string originalText;
        private bool restoreOnClose;
        public override string Text
        {
            get { return base.Text; }
            set
            {
                if (DropDownStyle == ComboBoxStyle.DropDown && SelectedIndex >= 0 && value != GetItemText(SelectedItem)) SelectedIndex = -1;
                base.Text = value;
            }
        }
        public StyledComboBox(bool editable = false)
        {
            // Match PreferredHeight to the owner-drawn native item height during table layout.
            FormattingEnabled = true;
            // Paint the closed field once; the popup list keeps its native owner-drawn behavior.
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, !editable);
            DropDownStyle = editable ? ComboBoxStyle.DropDown : ComboBoxStyle.DropDownList;
            DrawMode = DrawMode.OwnerDrawFixed;
            FlatStyle = FlatStyle.Flat;
            BackColor = Color.White; ForeColor = Ui.Ink;
            IntegralHeight = false; MaxDropDownItems = 8;
            UpdateMetrics();
        }
        private void UpdateMetrics()
        {
            // OwnerDraw ItemHeight can create an HWND before the control receives its parent's DPI.
            bool pending = !IsHandleCreated && DrawMode != DrawMode.Normal;
            if (pending) DrawMode = DrawMode.Normal;
            ItemHeight = Math.Max(Ui.Scale(this, 28), Font.Height + Ui.Scale(this, 8));
            if (pending) DrawMode = DrawMode.OwnerDrawFixed;
            DropDownHeight = ItemHeight * Math.Min(MaxDropDownItems, Math.Max(1, Items.Count)) + Ui.Scale(this, 2);
            Invalidate();
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateMetrics(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); UpdateMetrics(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateMetrics(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnDropDownClosed(EventArgs e)
        {
            bool restore = restoreOnClose;
            restoreOnClose = false;
            if (restore && (originalSelection == null || Items.Contains(originalSelection)))
            {
                SelectedItem = originalSelection;
                if (DropDownStyle == ComboBoxStyle.DropDown && originalSelection == null) Text = originalText;
            }
            base.OnDropDownClosed(e); Invalidate();
        }
        protected override void OnSelectionChangeCommitted(EventArgs e)
        {
            restoreOnClose = false;
            base.OnSelectionChangeCommitted(e);
        }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnDropDown(EventArgs e)
        {
            originalSelection = SelectedItem;
            originalText = Text;
            restoreOnClose = true;
            UpdateMetrics();
            int width = Width;
            foreach (object item in Items) width = Math.Max(width, TextRenderer.MeasureText(GetItemText(item), Font).Width + Ui.Scale(this, 32));
            DropDownWidth = width;
            base.OnDropDown(e); Invalidate();
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool disabled = !Enabled || (e.State & DrawItemState.Disabled) != 0;
            Color background = selected && !disabled ? Color.FromArgb(232, 238, 255) : Color.White;
            using (var brush = new SolidBrush(background)) e.Graphics.FillRectangle(brush, e.Bounds);
            var text = new Rectangle(e.Bounds.Left + Ui.Scale(this, 10), e.Bounds.Top, Math.Max(0, e.Bounds.Width - Ui.Scale(this, 20)), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, e.Index < 0 ? "" : GetItemText(Items[e.Index]), Font, text, disabled ? Ui.Muted : Ui.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            base.OnDrawItem(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            PaintField(e.Graphics);
            base.OnPaint(e);
        }
        private void PaintField(Graphics graphics)
        {
            if (Width < 2 || Height < 2) return;
            using (var background = new SolidBrush(Parent is RoundedPanel ? ((RoundedPanel)Parent).Fill : Parent == null ? Ui.Background : Parent.BackColor))
                graphics.FillRectangle(background, ClientRectangle);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            bool active = Enabled && (hover || Focused || DroppedDown);
            using (var path = Shapes.Round(new RectangleF(.5f, .5f, Width - 1, Height - 1), Ui.Scale(this, 6)))
            using (var fill = new SolidBrush(Enabled ? Color.White : Color.FromArgb(247, 248, 250)))
            using (var border = new Pen(active ? Color.FromArgb(133, 157, 235) : Color.FromArgb(216, 220, 227)))
            {
                graphics.FillPath(fill, path); graphics.DrawPath(border, path);
            }
            var text = new Rectangle(Ui.Scale(this, 10), 0, Math.Max(0, Width - Ui.Scale(this, 38)), Height);
            TextRenderer.DrawText(graphics, SelectedIndex < 0 ? "" : GetItemText(SelectedItem), Font, text, Enabled ? Ui.Ink : Ui.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            float x = Width - Ui.Scale(this, 18), y = Height / 2f;
            using (var pen = new Pen(Enabled ? Color.FromArgb(103, 111, 125) : Ui.Muted, Math.Max(1, Ui.Scale(this, 1))))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                graphics.DrawLines(pen, new[] { new PointF(x - Ui.Scale(this, 4), y - Ui.Scale(this, 2)), new PointF(x, y + Ui.Scale(this, 2)), new PointF(x + Ui.Scale(this, 4), y - Ui.Scale(this, 2)) });
            }
        }
    }
}

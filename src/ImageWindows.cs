using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ImageFiles
    {
        public static bool Save(IWin32Window owner, Bitmap image)
        {
            using (var dialog = new SaveFileDialog { Title = "保存截图", Filter = "PNG 图片|*.png|JPEG 图片|*.jpg|BMP 图片|*.bmp", FileName = "SnapTransOCR_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png", AddExtension = true })
            {
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                string extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
                ImageFormat format = extension == ".jpg" || extension == ".jpeg" ? ImageFormat.Jpeg : extension == ".bmp" ? ImageFormat.Bmp : ImageFormat.Png;
                string temporary = dialog.FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    image.Save(temporary, format);
                    if (File.Exists(dialog.FileName)) File.Replace(temporary, dialog.FileName, null);
                    else File.Move(temporary, dialog.FileName);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return true;
            }
        }
    }

    internal sealed partial class PinForm : RoundedForm
    {
        private Bitmap image, original;
        private readonly ContextMenuStrip menu;
        private GlyphButton toggle;
        private PinToolbar toolbar;
        private ToolStripMenuItem toggleItem;
        private Label status;
        private ToolTip statusHint;
        private bool showingOriginal;
        private readonly bool preserveLocation;
        private Bitmap CurrentImage { get { return showingOriginal ? original : image; } }
        protected override int CornerRadius { get { return 0; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x80; return value; } // WS_EX_TOOLWINDOW, without a caption.
        }
        public PinForm(Bitmap bitmap, bool topmost = true, Bitmap original = null, Point? sourceLocation = null)
        {
            if (original != null && (original.Width != bitmap.Width || original.Height > bitmap.Height || ReferenceEquals(original, bitmap))) throw new ArgumentException("译图必须是同宽且不小于原图高度的独立图片。", "original");
            image = bitmap;
            this.original = original;
            preserveLocation = sourceLocation.HasValue;
            Text = "SnapTransOCR · 贴图";
            AutoScaleMode = AutoScaleMode.None;
            TopMost = topmost;
            ShowInTaskbar = false;
            KeyPreview = true;
            Padding = Padding.Empty;
            BackColor = Color.White;
            MinimumSize = ImageSize(Math.Min(1, 80d / Math.Max(image.Width, image.Height)));
            menu = new ContextMenuStrip();
            var top = new ToolStripMenuItem("置顶") { Checked = topmost, CheckOnClick = true };
            top.CheckedChanged += delegate { TopMost = top.Checked; LayoutToolbar(); };
            menu.Items.Add(top);
            menu.Items.Add("原始大小", null, delegate { OriginalSize(); });
            var rotate = new ToolStripMenuItem("旋转");
            rotate.DropDownItems.Add("顺时针 90°", null, delegate { RotateImage(1); });
            rotate.DropDownItems.Add("逆时针 90°", null, delegate { RotateImage(-1); });
            rotate.DropDownItems.Add("旋转 180°", null, delegate { RotateImage(2); });
            menu.Items.Add(rotate);
            menu.Items.Add("复制图片", null, async delegate { try { using (var copy = ExportImage()) await Clip.SetImage(copy); } catch (Exception ex) { Ui.Error(this, ex); } });
            menu.Items.Add("保存图片", null, delegate { try { using (var copy = ExportImage()) ImageFiles.Save(this, copy); } catch (Exception ex) { Ui.Error(this, ex); } });
            menu.Items.Add("关闭贴图", null, delegate { Close(); });
            ContextMenuStrip = menu;
            if (original != null) AddToggle();
            ResumeLayout(true);
            if (sourceLocation.HasValue)
            {
                StartPosition = FormStartPosition.Manual;
                Location = sourceLocation.Value;
                ClientSize = bitmap.Size;
            }
            else { OriginalSize(); Ui.NearCursor(this); }
        }
        public void SetTranslation(Bitmap translated)
        {
            Bitmap source = original ?? image;
            if (translated == null || translated.Width != source.Width || translated.Height < source.Height) throw new ArgumentException("译图必须与原图同宽且不能减少高度。", "translated");
            if (ReferenceEquals(translated, image) || ReferenceEquals(translated, original)) throw new ArgumentException("译图必须是独立图片。", "translated");
            float scale = ImageScale;
            bool resized = translated.Size != CurrentImage.Size;
            HideTextSelection();
            if (original == null) original = image;
            else image.Dispose();
            image = translated;
            showingOriginal = false;
            if (toggle == null) AddToggle();
            UpdateToggle();
            SetStatus("");
            if (resized) ResizeImage(scale); else LayoutViewport();
            Invalidate();
        }
        public void SetStatus(string text)
        {
            if (status == null && !String.IsNullOrEmpty(text))
            {
                status = new Label { AutoEllipsis = true, BackColor = Color.White, ForeColor = Ui.Ink, TextAlign = ContentAlignment.MiddleLeft, ContextMenuStrip = menu };
                status.MouseDown += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) Shapes.Drag(this); };
                status.MouseDoubleClick += delegate(object sender, MouseEventArgs e) { if (e.Button == MouseButtons.Left) OriginalSize(); };
                statusHint = new ToolTip { ShowAlways = true };
                Controls.Add(status);
                LayoutOverlays();
            }
            if (status == null) return;
            status.Text = text ?? "";
            statusHint.SetToolTip(status, status.Text);
            status.Visible = status.Text.Length != 0;
        }
        private void AddToggle()
        {
            toggle = new GlyphButton("image-translate", "查看原图", ToggleOriginal, false) { BackColor = Color.White, ContextMenuStrip = menu };
            toolbar = new PinToolbar(toggle);
            toggleItem = new ToolStripMenuItem("查看原图", null, delegate { ToggleOriginal(); });
            menu.Items.Insert(2, toggleItem);
            LayoutOverlays();
        }
        private void ToggleOriginal()
        {
            float scale = ImageScale;
            HideTextSelection();
            showingOriginal = !showingOriginal;
            UpdateToggle();
            if (original.Size != image.Size) ResizeImage(scale); else LayoutViewport();
            Invalidate();
        }
        private void UpdateToggle()
        {
            string title = showingOriginal ? "查看译文" : "查看原图";
            toggle.SetHint(title); toggleItem.Text = title;
        }
        private void LayoutOverlays()
        {
            int margin = Ui.Scale(this, 7);
            LayoutToolbar();
            if (status != null) status.SetBounds(margin, Math.Max(0, ClientSize.Height - Ui.Scale(this, 28) - margin), Math.Max(1, ClientSize.Width - margin * 2), Ui.Scale(this, 28));
        }
        private void LayoutToolbar()
        {
            if (toolbar == null || toolbar.IsDisposed) return;
            if (!Visible || WindowState == FormWindowState.Minimized) { toolbar.Hide(); return; }
            int side = Ui.Scale(this, 34), gap = Ui.Scale(this, 4);
            Rectangle area = Screen.FromControl(this).WorkingArea;
            if (Ui.RenderOffscreen) area = Rectangle.Inflate(Bounds, side + gap, side + gap);
            Rectangle placement = PinToolbar.Place(Bounds, area, side, gap);
            if (placement.IsEmpty) { toolbar.Hide(); return; }
            toolbar.Bounds = placement;
            // Ownership carries native topmost state; Form.TopMost would activate the toolbar on handle creation.
            if (!toolbar.Visible) toolbar.Show(this);
        }
        protected override void OnLocationChanged(EventArgs e) { base.OnLocationChanged(e); LayoutToolbar(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); LayoutToolbar(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); HideTextSelection(); LayoutViewport(); LayoutOverlays(); }
        private void OriginalSize()
        {
            ResizeImage(1);
        }
        private Size ImageSize(double scale)
        {
            return new Size(Math.Max(1, (int)Math.Round(DisplaySize.Width * scale)), Math.Max(1, (int)Math.Round(DisplaySize.Height * scale)));
        }
        private Size ConstrainedSize(double scale)
        {
            Rectangle area = Visible ? Screen.FromControl(this).WorkingArea : Screen.FromPoint(Cursor.Position).WorkingArea;
            Size displayed = DisplaySize;
            if (preserveLocation)
            {
                area = Screen.FromPoint(Location).WorkingArea;
                double maximumZoom = Math.Max(1, ExpandedCanvas ? (double)area.Width / displayed.Width : Math.Min((double)area.Width / displayed.Width, (double)area.Height / displayed.Height));
                if (quarterTurns != 0)
                {
                    int availableWidth = area.Contains(Location) ? area.Right - Left : area.Width;
                    int availableHeight = area.Contains(Location) ? area.Bottom - Top : area.Height;
                    maximumZoom = ExpandedCanvas ? (double)availableWidth / displayed.Width : Math.Min((double)availableWidth / displayed.Width, (double)availableHeight / displayed.Height);
                }
                Size size = ImageSize(Math.Min(maximumZoom, Math.Max(Math.Min(1, 80d / Math.Max(displayed.Width, displayed.Height)), scale)));
                if (ExpandedCanvas)
                {
                    // Keep the source anchor; scroll extra translated lines below it instead of moving the pin.
                    int height = Math.Min(size.Height, Math.Max(MinimumSize.Height, area.Bottom - Top));
                    if (height < size.Height) size.Width += SystemInformation.VerticalScrollBarWidth;
                    size.Height = height;
                }
                return size;
            }
            if (ExpandedCanvas)
            {
                double zoom = Math.Min((double)area.Width / displayed.Width, Math.Max(Math.Min(1, 80d / displayed.Width), scale));
                int height = Math.Min(area.Height, (int)Math.Round(displayed.Height * zoom));
                int bar = displayed.Height * zoom > height ? SystemInformation.VerticalScrollBarWidth : 0;
                return new Size(Math.Min(area.Width, Math.Max(1, (int)Math.Round(displayed.Width * zoom) + bar)), Math.Max(1, height));
            }
            double maximum = Math.Min((double)area.Width / displayed.Width, (double)area.Height / displayed.Height);
            double minimum = Math.Min(1, 80d / Math.Max(displayed.Width, displayed.Height));
            return ImageSize(Math.Min(maximum, Math.Max(minimum, scale)));
        }
        private void ResizeImage(double scale, bool keepLocation = false)
        {
            MinimumSize = ImageSize(Math.Min(1, 80d / Math.Max(DisplaySize.Width, DisplaySize.Height)));
            ClientSize = ConstrainedSize(scale);
            LayoutViewport(); LayoutOverlays();
            if (!Ui.RenderOffscreen && !preserveLocation && !keepLocation)
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
            }
            Invalidate();
        }
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (scrollVisible && (ModifierKeys & Keys.Control) == 0) ScrollImage(-e.Delta / 3);
            else if (e.Delta != 0) ResizeImage(ImageScale * (e.Delta > 0 ? 1.12 : 1 / 1.12));
        }
        protected override void OnDpiChanged(DpiChangedEventArgs e)
        {
            HideTextSelection();
            e.Cancel = true; // A 100% pin keeps one screenshot pixel per physical screen pixel.
            base.OnDpiChanged(e);
            Padding = Padding.Empty;
            LayoutViewport(); LayoutOverlays();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Bitmap displayed = CurrentImage;
            bool redrawText = ImageScale > 1 && !OriginalTextVisible && textLayouts != null;
            if (!redrawText && quarterTurns == 0 && ImageScale == 1) e.Graphics.DrawImageUnscaled(displayed, 0, (int)ImageDestination.Top);
            else
            {
                var state = e.Graphics.Save();
                using (var transform = ImageTransform()) e.Graphics.Transform = transform;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
                e.Graphics.InterpolationMode = ImageScale == 1 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                if (redrawText) ImageTranslation.Draw(e.Graphics, original, translatedText, textLayouts);
                else using (var attributes = new ImageAttributes())
                {
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    e.Graphics.DrawImage(displayed, new Rectangle(0, 0, displayed.Width, displayed.Height), 0, 0, displayed.Width, displayed.Height, GraphicsUnit.Pixel, attributes);
                }
                e.Graphics.Restore(state);
            }
            PaintTextSelection(e.Graphics);
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x31e) ApplyShadow(); // Reapply when desktop composition changes.
            if (message.Msg == 0xa1) HideTextSelection();
            if (message.Msg == 0xa5)
            {
                long packed = message.LParam.ToInt64();
                menu.Show(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
                return;
            }
            if (message.Msg == 0xa3 && message.WParam.ToInt32() == 2) { OriginalSize(); return; }
            if (message.Msg == 0x214 && image != null)
            {
                var rectangle = (Native.RECT)Marshal.PtrToStructure(message.LParam, typeof(Native.RECT));
                int edge = message.WParam.ToInt32();
                Size displayed = DisplaySize;
                Size size;
                if (ExpandedCanvas)
                {
                    Rectangle area = Screen.FromControl(this).WorkingArea;
                    int maximumWidth = preserveLocation ? Math.Max(area.Width, Width) : area.Width;
                    size = new Size(Math.Max(MinimumSize.Width, Math.Min(maximumWidth, rectangle.Right - rectangle.Left)),
                        Math.Max(MinimumSize.Height, Math.Min(area.Height, rectangle.Bottom - rectangle.Top)));
                }
                else size = ConstrainedSize(edge == 3 || edge == 6 ? (double)(rectangle.Bottom - rectangle.Top) / displayed.Height : (double)(rectangle.Right - rectangle.Left) / displayed.Width);
                if (edge == 1 || edge == 4 || edge == 7) rectangle.Left = rectangle.Right - size.Width;
                else rectangle.Right = rectangle.Left + size.Width;
                if (edge == 3 || edge == 4 || edge == 5) rectangle.Top = rectangle.Bottom - size.Height;
                else rectangle.Bottom = rectangle.Top + size.Height;
                Marshal.StructureToPtr(rectangle, message.LParam, false);
                message.Result = new IntPtr(1);
                return;
            }
            base.WndProc(ref message);
            if (message.Msg == 0x84 && message.Result.ToInt32() == 1)
            {
                long packed = message.LParam.ToInt64();
                Point point = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
                if ((!scrollVisible || !imageScroll.Bounds.Contains(point)) && TextBlockAt(point) < 0)
                    message.Result = new IntPtr(2);
            }
        }
        protected override async void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.C) { e.Handled = e.SuppressKeyPress = true; await CopySelectedText(); }
            if (e.Control && e.KeyCode == Keys.A && ActiveLayouts != null)
            { selectionAnchor = 0; selectionEnd = AllText(OriginalTextVisible).Length; e.Handled = e.SuppressKeyPress = true; Invalidate(); }
            if (e.KeyCode == Keys.Escape)
            {
                if (selectionAnchor >= 0) HideTextSelection(); else Close();
                e.Handled = e.SuppressKeyPress = true;
            }
            if (e.KeyCode == Keys.Space && !e.Control && !e.Alt && original != null) { ToggleOriginal(); e.Handled = e.SuppressKeyPress = true; }
            base.OnKeyDown(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing && toolbar != null) toolbar.Dispose();
            base.Dispose(disposing);
            if (disposing) { menu.Dispose(); image.Dispose(); if (original != null && !ReferenceEquals(image, original)) original.Dispose(); if (statusHint != null) statusHint.Dispose(); }
        }
    }
}

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class CaptureSessionUi
    {
        [StructLayout(LayoutKind.Sequential)] private struct CursorInfo { internal int Size, Flags; internal IntPtr Handle; internal Point Position; }
        [StructLayout(LayoutKind.Sequential)] private struct IconInfo { internal bool Icon; internal int HotX, HotY; internal IntPtr Mask, Color; }
        [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
        [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CursorInfo info);
        [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out IconInfo info);
        [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr target, int x, int y, IntPtr icon, int width, int height, int step, IntPtr brush, int flags);
        internal static bool Exclude(Form form)
        {
            return Environment.OSVersion.Version.Build >= 19041 && SetWindowDisplayAffinity(form.Handle, 0x11);
        }
        internal static void Place(Form form, Rectangle region)
        {
            form.StartPosition = FormStartPosition.Manual;
            if (Ui.RenderOffscreen) { form.Location = new Point(-30000, -30000); return; }
            Rectangle work = Screen.FromRectangle(region).WorkingArea;
            form.Location = work.Location;
            IntPtr handle = form.Handle;
            int gap = Ui.Scale(form, 8), x = region.Right - form.Width, y;
            if (region.Bottom + gap + form.Height <= work.Bottom) y = region.Bottom + gap;
            else if (region.Top - gap - form.Height >= work.Top) y = region.Top - gap - form.Height;
            else if (region.Right + gap + form.Width <= work.Right) { x = region.Right + gap; y = region.Top; }
            else if (region.Left - gap - form.Width >= work.Left) { x = region.Left - gap - form.Width; y = region.Top; }
            else { x = work.Right - form.Width - gap; y = work.Bottom - form.Height - gap; }
            form.Location = new Point(Math.Max(work.Left, Math.Min(x, work.Right - form.Width)), Math.Max(work.Top, Math.Min(y, work.Bottom - form.Height)));
        }
        internal static void DrawCursor(Bitmap image, Rectangle region)
        {
            using (var pointer = FreezePointer(image, region))
                if (pointer != null) pointer.Apply(image, true);
        }
        internal static void DrawCursor(IntPtr target, Rectangle region)
        {
            CursorInfo cursor;
            if (VisibleCursor(region, out cursor)) DrawCursor(target, region, cursor);
        }
        private static bool VisibleCursor(Rectangle region, out CursorInfo cursor)
        {
            cursor = new CursorInfo { Size = Marshal.SizeOf(typeof(CursorInfo)) };
            return GetCursorInfo(ref cursor) && (cursor.Flags & 1) != 0 && region.Contains(cursor.Position);
        }
        internal static FrozenPointer FreezePointer(Bitmap desktop, Rectangle region)
        {
            CursorInfo cursor; IconInfo icon;
            if (!VisibleCursor(region, out cursor) || !GetIconInfo(cursor.Handle, out icon)) return null;
            Bitmap plain = null, marked = null;
            try
            {
                Size size;
                using (var bitmap = Image.FromHbitmap(icon.Color != IntPtr.Zero ? icon.Color : icon.Mask))
                    size = new Size(bitmap.Width, icon.Color != IntPtr.Zero ? bitmap.Height : bitmap.Height / 2);
                Rectangle screen = Rectangle.Intersect(region, new Rectangle(cursor.Position.X - icon.HotX, cursor.Position.Y - icon.HotY, size.Width, size.Height));
                if (screen.Width == 0 || screen.Height == 0) return null;
                var relative = screen; relative.Offset(-region.Left, -region.Top);
                plain = desktop.Clone(relative, PixelFormat.Format32bppPArgb);
                IntPtr bitmapHandle = IntPtr.Zero, target = IntPtr.Zero, previous = IntPtr.Zero;
                try
                {
                    // Native alpha cursors must blend against initialized pixels, not a GDI+ scratch DC.
                    bitmapHandle = plain.GetHbitmap(); target = Native.CreateCompatibleDC(IntPtr.Zero);
                    if (target == IntPtr.Zero) throw new InvalidOperationException("无法创建截图指针画布。");
                    previous = Native.SelectObject(target, bitmapHandle);
                    if (previous == IntPtr.Zero || previous == new IntPtr(-1)) throw new InvalidOperationException("无法准备截图指针画布。");
                    DrawIconEx(target, cursor.Position.X - screen.Left - icon.HotX, cursor.Position.Y - screen.Top - icon.HotY, cursor.Handle, 0, 0, 0, IntPtr.Zero, 3);
                    Native.GdiFlush();
                    using (var source = Image.FromHbitmap(bitmapHandle)) marked = ScreenCapture.ForDisplay(source);
                }
                finally
                {
                    if (previous != IntPtr.Zero && previous != new IntPtr(-1)) Native.SelectObject(target, previous);
                    if (bitmapHandle != IntPtr.Zero) Native.DeleteObject(bitmapHandle);
                    if (target != IntPtr.Zero) Native.DeleteDC(target);
                }
                return new FrozenPointer(relative, plain, marked);
            }
            catch { if (plain != null) plain.Dispose(); if (marked != null) marked.Dispose(); throw; }
            finally { if (icon.Mask != IntPtr.Zero) Native.DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) Native.DeleteObject(icon.Color); }
        }
        private static void DrawCursor(IntPtr target, Rectangle region, CursorInfo cursor)
        {
            IconInfo icon;
            if (!GetIconInfo(cursor.Handle, out icon)) return;
            try
            {
                DrawIconEx(target, cursor.Position.X - region.Left - icon.HotX, cursor.Position.Y - region.Top - icon.HotY, cursor.Handle, 0, 0, 0, IntPtr.Zero, 3);
            }
            finally { if (icon.Mask != IntPtr.Zero) Native.DeleteObject(icon.Mask); if (icon.Color != IntPtr.Zero) Native.DeleteObject(icon.Color); }
        }
    }

    internal sealed class FrozenPointer : IDisposable
    {
        internal readonly Rectangle Bounds;
        private readonly Bitmap plain, marked;
        internal FrozenPointer(Rectangle bounds, Bitmap plain, Bitmap marked) { Bounds = bounds; this.plain = plain; this.marked = marked; }
        internal Bitmap Pixels(bool visible) { return visible ? marked : plain; }
        internal void Apply(Bitmap target, bool visible)
        {
            using (var graphics = Graphics.FromImage(target))
            {
                graphics.CompositingMode = CompositingMode.SourceCopy;
                graphics.DrawImageUnscaled(Pixels(visible), Bounds.Location);
            }
        }
        public void Dispose() { plain.Dispose(); marked.Dispose(); }
    }

    internal sealed class CaptureOutline : Form
    {
        internal Color OutlineColor = Ui.Accent;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get { var value = base.CreateParams; value.ExStyle |= 0x08000000 | 0x80 | 0x20; return value; }
        }
        internal CaptureOutline(Rectangle region)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            AutoScaleMode = AutoScaleMode.None; StartPosition = FormStartPosition.Manual;
            BackColor = TransparencyKey = Color.Fuchsia;
            Bounds = Rectangle.Inflate(region, 2, 2);
            if (Ui.RenderOffscreen) Location = new Point(-30000, -30000);
        }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); CaptureSessionUi.Exclude(this); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (var pen = new Pen(OutlineColor))
            { e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3); }
        }
    }
}

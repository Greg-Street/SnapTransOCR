using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SnapTransOCR
{
    internal sealed partial class PinForm
    {
        private int quarterTurns;
        private Size DisplaySize { get { return (quarterTurns & 1) == 0 ? CurrentImage.Size : new Size(CurrentImage.Height, CurrentImage.Width); } }

        private void RotateImage(int turns)
        {
            float scale = ImageScale;
            HideTextSelection();
            quarterTurns = (quarterTurns + turns + 4) % 4;
            ResizeImage(scale, true);
        }
        private Bitmap ExportImage()
        {
            var copy = (Bitmap)CurrentImage.Clone();
            try { if (quarterTurns != 0) copy.RotateFlip((RotateFlipType)quarterTurns); return copy; }
            catch { copy.Dispose(); throw; }
        }
        // Source pixels stay immutable. Painting and hit testing share the same quarter-turn mapping.
        private Matrix ImageTransform()
        {
            float scale = ImageScale, top = ImageDestination.Top;
            switch (quarterTurns)
            {
                case 1: return new Matrix(0, scale, -scale, 0, CurrentImage.Height * scale, top);
                case 2: return new Matrix(-scale, 0, 0, -scale, CurrentImage.Width * scale, CurrentImage.Height * scale + top);
                case 3: return new Matrix(0, -scale, scale, 0, 0, CurrentImage.Width * scale + top);
                default: return new Matrix(scale, 0, 0, scale, 0, top);
            }
        }
        private RectangleF DisplayTextRectangle(RectangleF bounds)
        {
            switch (quarterTurns)
            {
                case 1: bounds = new RectangleF(CurrentImage.Height - bounds.Bottom, bounds.X, bounds.Height, bounds.Width); break;
                case 2: bounds = new RectangleF(CurrentImage.Width - bounds.Right, CurrentImage.Height - bounds.Bottom, bounds.Width, bounds.Height); break;
                case 3: bounds = new RectangleF(bounds.Y, CurrentImage.Width - bounds.Right, bounds.Height, bounds.Width); break;
            }
            float scale = ImageScale;
            return new RectangleF(bounds.X * scale, ImageDestination.Top + bounds.Y * scale, bounds.Width * scale, bounds.Height * scale);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ShadowMargins { public int Left, Right, Top, Bottom; }
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref ShadowMargins margins);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyShadow();
        }
        private void ApplyShadow()
        {
            int enabled = 2, square = 1;
            DwmSetWindowAttribute(Handle, 2, ref enabled, sizeof(int)); // DWMNCRP_ENABLED
            DwmSetWindowAttribute(Handle, 33, ref square, sizeof(int)); // DWMWCP_DONOTROUND (Windows 11)
            var margins = new ShadowMargins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            DwmExtendFrameIntoClientArea(Handle, ref margins);
        }
    }
}

using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed partial class PinForm
    {
        private VScrollBar imageScroll;
        private bool viewportLayout, scrollVisible;
        private bool ExpandedCanvas { get { return original != null && !showingOriginal && image.Height > original.Height; } }
        private float ImageScale { get { return Math.Max(1, ClientSize.Width - (scrollVisible ? imageScroll.Width : 0)) / (float)DisplaySize.Width; } }
        private RectangleF ImageDestination
        {
            get { return new RectangleF(0, scrollVisible ? -imageScroll.Value : 0, DisplaySize.Width * ImageScale, DisplaySize.Height * ImageScale); }
        }
        private void LayoutViewport()
        {
            if (image == null || viewportLayout) return;
            viewportLayout = true;
            try
            {
                scrollVisible = ExpandedCanvas && DisplaySize.Height * (double)ClientSize.Width / DisplaySize.Width > ClientSize.Height + 1;
                if (scrollVisible && imageScroll == null)
                {
                    imageScroll = new VScrollBar { SmallChange = 40, AccessibleName = "译图上下滚动" };
                    imageScroll.ValueChanged += delegate { Invalidate(); };
                    Controls.Add(imageScroll);
                }
                if (imageScroll == null) return;
                imageScroll.Visible = scrollVisible;
                if (scrollVisible)
                {
                    imageScroll.SetBounds(ClientSize.Width - SystemInformation.VerticalScrollBarWidth, 0, SystemInformation.VerticalScrollBarWidth, ClientSize.Height);
                    imageScroll.LargeChange = Math.Max(1, ClientSize.Height);
                    imageScroll.Maximum = Math.Max(imageScroll.LargeChange, (int)Math.Ceiling(DisplaySize.Height * ImageScale)) - 1;
                    imageScroll.Value = Math.Min(imageScroll.Value, Math.Max(0, imageScroll.Maximum - imageScroll.LargeChange + 1));
                    imageScroll.BringToFront();
                }
                else imageScroll.Value = 0;
            }
            finally { viewportLayout = false; }
        }
        private void ScrollImage(int amount)
        {
            if (!scrollVisible) return;
            imageScroll.Value = Math.Max(0, Math.Min(imageScroll.Maximum - imageScroll.LargeChange + 1, imageScroll.Value + amount));
        }
    }
}

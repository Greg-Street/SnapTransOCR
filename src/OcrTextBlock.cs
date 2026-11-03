using System.Drawing;

namespace SnapTransOCR
{
    internal sealed class OcrTextBlock
    {
        public string Text { get; private set; }
        public Rectangle Bounds { get; private set; }
        public OcrTextBlock(string text, Rectangle bounds) { Text = text; Bounds = bounds; }
    }
}

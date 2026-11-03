using System.Drawing;

namespace SnapTransOCR
{
    internal sealed class ImageTextLine
    {
        internal readonly int Start, Length;
        internal readonly RectangleF Bounds;
        internal ImageTextLine(int start, int length, RectangleF bounds)
        { Start = start; Length = length; Bounds = bounds; }
    }

    internal sealed class ImageTextLayout
    {
        internal readonly Rectangle Bounds;
        internal readonly float FontPixels;
        internal readonly Color Background, Foreground;
        internal readonly ImageTextLine[] Lines;
        internal readonly Rectangle SourceBounds;
        internal readonly int InsertionY, InsertionHeight;
        internal ImageTextLayout(Rectangle bounds, float fontPixels, Color background, Color foreground)
            : this(bounds, fontPixels, background, foreground, new ImageTextLine[0], bounds, 0, 0) { }
        internal ImageTextLayout(Rectangle bounds, float fontPixels, Color background, Color foreground,
            ImageTextLine[] lines, Rectangle sourceBounds, int insertionY, int insertionHeight)
        {
            Bounds = bounds; FontPixels = fontPixels; Background = background; Foreground = foreground;
            Lines = lines; SourceBounds = sourceBounds; InsertionY = insertionY; InsertionHeight = insertionHeight;
        }
    }
}

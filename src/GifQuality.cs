using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SnapTransOCR
{
    internal sealed class GifQuality
    {
        internal static readonly GifQuality[] Presets = {
            new GifQuality("compact", "省空间 · 6 帧/秒 · 长边 960", 6, 960),
            new GifQuality("balanced", "均衡 · 10 帧/秒 · 长边 1280", 10, 1280),
            new GifQuality("clear", "清晰 · 15 帧/秒 · 长边 1920", 15, 1920),
            new GifQuality("original", "原尺寸 · 10 帧/秒 · 不缩放", 10, 0)
        };
        internal readonly string Id, Caption;
        internal readonly int FramesPerSecond, MaximumEdge;
        private GifQuality(string id, string caption, int framesPerSecond, int maximumEdge)
        { Id = id; Caption = caption; FramesPerSecond = framesPerSecond; MaximumEdge = maximumEdge; }
        internal static GifQuality Find(string id) { return Array.Find(Presets, value => value.Id == id) ?? Presets[0]; }
        internal Size OutputSize(Size source)
        {
            double scale = MaximumEdge == 0 ? 1 : Math.Min(1, (double)MaximumEdge / Math.Max(source.Width, source.Height));
            return new Size(Math.Max(1, (int)Math.Round(source.Width * scale)), Math.Max(1, (int)Math.Round(source.Height * scale)));
        }
        internal Bitmap Resize(Bitmap source)
        {
            Size size = OutputSize(source.Size);
            var result = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (var graphics = Graphics.FromImage(result))
                using (var attributes = new ImageAttributes())
                {
                    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    attributes.SetWrapMode(WrapMode.TileFlipXY);
                    graphics.DrawImage(source, new Rectangle(Point.Empty, size), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }
    }
}

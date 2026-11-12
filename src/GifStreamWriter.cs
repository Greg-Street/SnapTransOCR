// GIF container layout follows ShareX AnimatedGifCreator (GPL-2.0-or-later).
// Copyright (c) 2007-2026 ShareX Team. Adapted to use the Windows GDI+ frame encoder.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace SnapTransOCR
{
    internal sealed class GifStreamWriter : IDisposable
    {
        private readonly BinaryWriter writer;
        private readonly Size size;
        private byte[] pending;
        private byte[] previous, working;
        private Point pendingOffset;
        private long origin, lastTimestamp, writtenCentiseconds;
        private bool completed;
        internal int FrameCount { get; private set; }
        internal long Length { get { return writer.BaseStream.Length + (pending == null ? 0 : pending.Length); } }
        internal GifStreamWriter(string path, Size size)
        {
            if (size.Width < 1 || size.Height < 1 || size.Width > UInt16.MaxValue || size.Height > UInt16.MaxValue) throw new ArgumentOutOfRangeException("size");
            this.size = size;
            writer = new BinaryWriter(new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read), Encoding.ASCII);
            try
            {
                writer.Write(Encoding.ASCII.GetBytes("GIF89a"));
                writer.Write((ushort)size.Width); writer.Write((ushort)size.Height);
                writer.Write(new byte[] { 0x70, 0, 0 }); // Each frame carries its own native palette.
                writer.Write(new byte[] { 0x21, 0xff, 11 }); writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                writer.Write(new byte[] { 3, 1, 0, 0, 0 }); // Repeat indefinitely.
            }
            catch { writer.Dispose(); throw; }
        }
        internal void AddFrame(Bitmap image, long timestamp)
        {
            if (completed) throw new InvalidOperationException("GIF 已经完成。");
            if (image.Size != size) throw new InvalidOperationException("录制期间选区尺寸发生变化。");
            if (timestamp < lastTimestamp) throw new ArgumentOutOfRangeException("timestamp");
            lastTimestamp = timestamp;
            if (working == null) working = new byte[checked(size.Width * size.Height * 3)];
            ReadPixels(image, working);
            Rectangle changed = ChangedBounds(working);
            if (changed.IsEmpty) return;
            byte[] encoded;
            using (var memory = new MemoryStream())
            {
                if (changed.Size == size) image.Save(memory, ImageFormat.Gif);
                else using (var patch = image.Clone(changed, PixelFormat.Format24bppRgb)) patch.Save(memory, ImageFormat.Gif);
                encoded = memory.ToArray();
            }
            if (pending == null) origin = timestamp;
            else WritePending(timestamp);
            pending = encoded; pendingOffset = changed.Location;
            byte[] reuse = previous; previous = working; working = reuse;
        }
        private static void ReadPixels(Bitmap image, byte[] pixels)
        {
            PixelFormat format = image.PixelFormat;
            if (format != PixelFormat.Format24bppRgb && format != PixelFormat.Format32bppRgb)
            {
                using (var rgb = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
                {
                    using (var graphics = Graphics.FromImage(rgb)) graphics.DrawImageUnscaled(image, 0, 0);
                    ReadPixels(rgb, pixels); return;
                }
            }
            BitmapData data = image.LockBits(new Rectangle(Point.Empty, image.Size), ImageLockMode.ReadOnly, format);
            try
            {
                var row = new byte[Math.Abs(data.Stride)];
                int channels = Image.GetPixelFormatSize(format) / 8;
                for (int y = 0; y < image.Height; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, row.Length);
                    int destination = y * image.Width * 3;
                    if (channels == 3) Buffer.BlockCopy(row, 0, pixels, destination, image.Width * 3);
                    else for (int x = 0; x < image.Width; x++)
                        for (int color = 0; color < 3; color++) pixels[destination + x * 3 + color] = row[x * channels + color];
                }
            }
            finally { image.UnlockBits(data); }
        }
        private Rectangle ChangedBounds(byte[] current)
        {
            if (previous == null) return new Rectangle(Point.Empty, size);
            int left = size.Width, right = -1, top = size.Height, bottom = -1;
            for (int y = 0; y < size.Height; y++)
            {
                int row = y * size.Width * 3, first = 0, last = size.Width - 1;
                while (first < size.Width && SamePixel(current, previous, row + first * 3)) first++;
                if (first == size.Width) continue;
                while (last > first && SamePixel(current, previous, row + last * 3)) last--;
                left = Math.Min(left, first); right = Math.Max(right, last);
                top = Math.Min(top, y); bottom = y;
            }
            return right < left ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }
        private static bool SamePixel(byte[] first, byte[] second, int pixel)
        { return first[pixel] == second[pixel] && first[pixel + 1] == second[pixel + 1] && first[pixel + 2] == second[pixel + 2]; }
        internal void Complete(long duration)
        {
            if (completed) return;
            if (pending == null) throw new InvalidOperationException("尚未录到画面。");
            if (duration < lastTimestamp) throw new ArgumentOutOfRangeException("duration");
            WritePending(duration); pending = null;
            writer.Write((byte)0x3b); writer.Flush(); completed = true;
        }
        private void WritePending(long end)
        {
            long target = Math.Max(0, (end - origin + 5) / 10);
            int delay = (int)Math.Min(UInt16.MaxValue, Math.Max(2, target - writtenCentiseconds));
            writtenCentiseconds += delay;
            byte[] data = pending;
            if (data.Length < 14 || data[0] != 'G' || data[1] != 'I' || data[2] != 'F') throw new InvalidOperationException("系统未返回有效 GIF 帧。");
            int colors = (data[10] & 0x80) == 0 ? 0 : 2 << (data[10] & 7);
            int palette = 13, bits = data[10] & 7, position = palette + colors * 3;
            while (position < data.Length && data[position] == 0x21)
            {
                position += 2;
                SkipBlocks(data, ref position);
            }
            if (position + 10 >= data.Length || data[position++] != 0x2c) throw new InvalidOperationException("GIF 帧缺少图像数据。");
            int descriptor = position, packed = data[position + 8]; position += 9;
            if ((packed & 0x80) != 0) { palette = position; bits = packed & 7; colors = 2 << bits; position += colors * 3; }
            if (colors == 0 || position >= data.Length) throw new InvalidOperationException("GIF 帧缺少调色板。");
            int start = position++; // Keep native LZW code size and sub-blocks unchanged.
            SkipBlocks(data, ref position);
            writer.Write(new byte[] { 0x21, 0xf9, 4, 4 });
            writer.Write((ushort)delay); writer.Write(new byte[] { 0, 0 });
            // Disposal 1 retains the canvas; each opaque patch overwrites its changed rectangle.
            writer.Write((byte)0x2c); writer.Write((ushort)pendingOffset.X); writer.Write((ushort)pendingOffset.Y);
            writer.Write(data, descriptor + 4, 4);
            writer.Write((byte)(0x80 | (packed & 0x40) | bits));
            writer.Write(data, palette, colors * 3);
            writer.Write(data, start, position - start);
            FrameCount++;
        }
        private static void SkipBlocks(byte[] data, ref int position)
        {
            while (position < data.Length)
            {
                int count = data[position++];
                if (count == 0) return;
                if (position + count > data.Length) break;
                position += count;
            }
            throw new InvalidOperationException("GIF 帧数据不完整。");
        }
        public void Dispose() { pending = previous = working = null; writer.Dispose(); }
    }
}

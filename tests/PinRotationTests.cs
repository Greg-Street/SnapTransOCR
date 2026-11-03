using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class PinRotationTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                using (var pin = new PinForm(Pattern(), false, sourceLocation: new Point(-30000, -30000)))
                using (var source = Export(pin))
                {
                    pin.Show();
                    var rotation = pin.ContextMenuStrip.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "旋转");
                    check(rotation.DropDownItems.Count == 3, "pin offers clockwise, counterclockwise and half-turn rotation");
                    for (int turns = 1; turns <= 4; turns++)
                    {
                        rotation.DropDownItems[0].PerformClick();
                        using (var expected = (Bitmap)source.Clone())
                        using (var exported = Export(pin))
                        using (var painted = new Bitmap(pin.Width, pin.Height))
                        {
                            expected.RotateFlip((RotateFlipType)(turns % 4));
                            pin.DrawToBitmap(painted, pin.ClientRectangle);
                            check(pin.Location == new Point(-30000, -30000) && pin.ClientSize == expected.Size, "quarter-turn swaps dimensions without moving the captured image: " + turns);
                            check(EqualPixels(expected, exported), "rotation exports exact source pixels without shadow or selection: " + turns);
                            check(EqualPixels(expected, painted), "rotation painting matches lossless exported pixels: " + turns);
                        }
                    }
                    rotation.DropDownItems[1].PerformClick();
                    rotation.DropDownItems[0].PerformClick();
                    using (var exported = Export(pin)) check(EqualPixels(source, exported), "opposite rotations restore original pixels");
                    rotation.DropDownItems[2].PerformClick();
                    rotation.DropDownItems[2].PerformClick();
                    using (var exported = Export(pin)) check(EqualPixels(source, exported), "two half-turns restore original pixels");
                }
                SelectRotatedText(check);
                Rectangle area = Screen.PrimaryScreen.WorkingArea;
                Point anchor = new Point(area.Left + 100, area.Top + 100);
                using (var pin = new PinForm(new Bitmap(1600, 600), false, sourceLocation: anchor))
                {
                    Rotate(pin, 1);
                    check(pin.Location == anchor && pin.Right <= area.Right && pin.Bottom <= area.Bottom, "rotated ordinary screenshots fit both screen dimensions without moving their anchor");
                }
                using (var pin = new PinForm(new Bitmap(300, 120), false, sourceLocation: new Point(-30000, -30000)))
                {
                    Rotate(pin, 1);
                    pin.SetTranslation(new Bitmap(300, 6000));
                    using (var exported = Export(pin)) check(exported.Size == new Size(6000, 300), "a late translation inherits the current rotation and retains its full canvas");
                    check(pin.Width <= Screen.FromPoint(pin.Location).WorkingArea.Width, "a late rotated long translation fits within the screen width");
                    pin.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "查看原图").PerformClick();
                    using (var exported = Export(pin)) check(exported.Size == new Size(120, 300), "original toggle preserves orientation after an expanded translation");
                    check(pin.Location == new Point(-30000, -30000), "late translation and original toggle preserve the rotated pin anchor");
                    pin.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "查看译文").PerformClick();
                    Rotate(pin, -1); Rotate(pin, 1);
                    check(pin.Width <= Screen.FromPoint(pin.Location).WorkingArea.Width, "rotating an existing long translation keeps its entire width reachable");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static void SelectRotatedText(Action<bool, string> check)
        {
            using (var pin = new PinForm(new Bitmap(600, 180), false, sourceLocation: new Point(-30000, -30000)))
            {
                pin.Show();
                var blocks = new[] { new OcrTextBlock("Original alphabet text", new Rectangle(30, 50, 500, 30)) };
                pin.SetTextBlocks(blocks);
                foreach (bool translated in new[] { false, true })
                {
                    if (translated)
                    {
                        using (var source = Export(pin))
                        {
                            string[] text = { "Translated alphabet text" };
                            var layouts = ImageTranslation.Layout(source, blocks, text);
                            pin.SetTranslation(ImageTranslation.Render(source, blocks, text, layouts));
                            pin.SetTextBlocks(blocks, text, layouts);
                        }
                    }
                    int firstIndex = translated ? 13 : 11;
                    Point first = PinTextTests.GlyphPoint(pin, 0, firstIndex, false);
                    Point last = PinTextTests.GlyphPoint(pin, 0, firstIndex + 2, true);
                    Size sourceSize = pin.ClientSize;
                    for (int turns = 1; turns <= 4; turns++)
                    {
                        Rotate(pin, 1);
                        Point start = Turn(first, sourceSize, turns % 4), end = Turn(last, sourceSize, turns % 4);
                        Native.SendMessage(pin.Handle, 0x201, new IntPtr(1), Packed(start));
                        Native.SendMessage(pin.Handle, 0x200, new IntPtr(1), Packed(end));
                        Native.SendMessage(pin.Handle, 0x202, IntPtr.Zero, Packed(end));
                        check(pin.SelectedImageText == "pha", "direct character selection remains aligned after rotation (translated=" + translated + ", turns=" + turns + ")");
                        var area = (Rectangle)typeof(PinForm).GetMethod("SelectionArea", Private).Invoke(pin, null);
                        check(area.Contains(start) && area.Contains(end), "selection highlight follows rotated characters");
                    }
                }
            }
        }
        private static Point Turn(Point point, Size source, int turns)
        {
            switch (turns)
            {
                case 1: return new Point(source.Height - point.Y, point.X);
                case 2: return new Point(source.Width - point.X, source.Height - point.Y);
                case 3: return new Point(point.Y, source.Width - point.X);
                default: return point;
            }
        }
        private static Bitmap Pattern()
        {
            var image = new Bitmap(240, 120);
            using (var graphics = Graphics.FromImage(image))
            {
                graphics.Clear(Color.CornflowerBlue);
                graphics.FillRectangle(Brushes.Red, 0, 0, 120, 60);
                graphics.FillRectangle(Brushes.LimeGreen, 120, 0, 120, 60);
                graphics.FillRectangle(Brushes.Gold, 0, 60, 120, 60);
            }
            return image;
        }
        private static bool EqualPixels(Bitmap a, Bitmap b)
        {
            if (a.Size != b.Size) return false;
            for (int y = 0; y < a.Height; y++) for (int x = 0; x < a.Width; x++) if (a.GetPixel(x, y) != b.GetPixel(x, y)) return false;
            return true;
        }
        private static void Rotate(PinForm pin, int turns) { typeof(PinForm).GetMethod("RotateImage", Private).Invoke(pin, new object[] { turns }); }
        private static Bitmap Export(PinForm pin) { return (Bitmap)typeof(PinForm).GetMethod("ExportImage", Private).Invoke(pin, null); }
        private static IntPtr Packed(Point point) { return new IntPtr(unchecked(point.Y << 16 | point.X & 0xffff)); }
    }
}

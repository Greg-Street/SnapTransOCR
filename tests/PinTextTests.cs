using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class PinTextTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                var bitmap = new Bitmap(520, 240);
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.White);
                using (var pin = new PinForm(bitmap, false))
                {
                    pin.StartPosition = FormStartPosition.Manual; pin.Location = new Point(-30000, -30000); pin.Show();
                    var blocks = new[] { new OcrTextBlock("Original alphabet text", new Rectangle(20, 30, 400, 30)), new OcrTextBlock("Second line", new Rectangle(20, 130, 250, 30)) };
                    pin.SetTextBlocks(blocks);
                    Size initialSize = pin.ClientSize;
                    pin.Capture = true;
                    pin.ClientSize = new Size(initialSize.Width + 10, initialSize.Height);
                    check(pin.Capture, "pin resizing preserves mouse capture unrelated to text selection");
                    pin.Capture = false; pin.ClientSize = initialSize;
                    Native.SendMessage(pin.Handle, 0x201, new IntPtr(1), Packed(GlyphPoint(pin, 0, 1, false)));
                    bool selectionStarted = pin.Capture && Field<bool>(pin, "selectingText");
                    pin.ClientSize = new Size(initialSize.Width + 10, initialSize.Height);
                    check(selectionStarted && !pin.Capture && !Field<bool>(pin, "selectingText") && pin.SelectedImageText == "", "pin resizing cancels and releases an active text-selection drag");
                    pin.ClientSize = initialSize;
                    Point first = GlyphPoint(pin, 0, 11, false), last = GlyphPoint(pin, 0, 13, true);
                    check(Hit(pin, first) == 1 && Hit(pin, new Point(480, 210)) == 2, "text glyphs are selectable while blank image space remains draggable");
                    Drag(pin, first, last);
                    check(pin.SelectedImageText == "pha", "dragging directly on the image selects an arbitrary substring without expanding words");
                    check(!pin.Controls.OfType<RichTextBox>().Any() && !pin.Controls.OfType<RoundedPanel>().Any(), "image selection adds no popup editor or text child window");
                    Drag(pin, last, first);
                    check(pin.SelectedImageText == "pha", "reverse image dragging selects the same character range");
                    Native.SendMessage(pin.Handle, 0x201, new IntPtr(1), Packed(first));
                    pin.Capture = false;
                    Native.SendMessage(pin.Handle, 0x200, IntPtr.Zero, Packed(last));
                    check(pin.SelectedImageText == "", "losing mouse capture stops selection before subsequent hover messages");
                    Drag(pin, GlyphPoint(pin, 0, 9, false), GlyphPoint(pin, 1, 5, true));
                    check(pin.SelectedImageText == "alphabet text" + Environment.NewLine + "Second", "one image selection can span OCR blocks and preserve reading-order line breaks");
                    var copyOriginal = pin.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "复制全部原文");
                    var copyTranslation = pin.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "复制全部译文");
                    check(copyOriginal.Enabled && !copyTranslation.Enabled, "source text is available for copying while translation is pending");
                    blocks[0] = new OcrTextBlock("changed externally", Rectangle.Empty);
                    check((string)typeof(PinForm).GetMethod("AllText", Private).Invoke(pin, new object[] { true }) == "Original alphabet text" + Environment.NewLine + "Second line", "pins retain a stable text snapshot");
                    blocks[0] = new OcrTextBlock("Original alphabet text", new Rectangle(20, 30, 400, 30));
                    // Font fitting now reduces ordinary overflow; keep this fixture tall even at 8 pixels.
                    string[] translated = { "译文可以直接拖动选择复制。", String.Concat(Enumerable.Repeat("完整的长译文不需要点击展开。", 1000)) };
                    var layouts = ImageTranslation.Layout(bitmap, blocks, translated);
                    Bitmap rendered = ImageTranslation.Render(bitmap, blocks, translated, layouts);
                    pin.SetTranslation(rendered); pin.SetTextBlocks(blocks, translated, layouts);
                    Drag(pin, GlyphPoint(pin, 0, 2, false), GlyphPoint(pin, 0, 5, true));
                    check(pin.SelectedImageText == "可以直接", "translated text is selectable on its displayed glyphs without opening another control");
                    Key(pin, Keys.Control | Keys.A);
                    check(pin.SelectedImageText == String.Join(Environment.NewLine, translated), "Ctrl+A selects the complete translation including text beyond the current viewport");
                    Bitmap displayed = (Bitmap)typeof(PinForm).GetProperty("CurrentImage", Private).GetValue(pin, null);
                    check(ReferenceEquals(displayed, rendered) && displayed.Width == bitmap.Width && displayed.Height >= bitmap.Height, "image export uses the complete translated bitmap without the visual selection");
                    bool hasScroll = Field<bool>(pin, "scrollVisible");
                    check(hasScroll && Field<VScrollBar>(pin, "imageScroll").Visible, "long translated images keep readable scale and provide vertical scrolling");
                    Size beforeResize = pin.Size;
                    Size taller = Sizing(pin, 6, new Size(beforeResize.Width, beforeResize.Height - 30));
                    Size wider = Sizing(pin, 2, new Size(beforeResize.Width + 30, beforeResize.Height));
                    check(taller.Width == beforeResize.Width && taller.Height == beforeResize.Height - 30, "resizing a translated viewport vertically does not change its width");
                    check(wider.Width == beforeResize.Width + 30 && wider.Height == beforeResize.Height, "horizontal translated viewport resizing does not repeatedly add scrollbar width");
                    typeof(PinForm).GetMethod("ScrollImage", Private).Invoke(pin, new object[] { 180 });
                    check(Field<VScrollBar>(pin, "imageScroll").Value > 0, "the expanded image viewport scrolls without resizing the text");
                    typeof(PinForm).GetMethod("ScrollImage", Private).Invoke(pin, new object[] { 100000 });
                    int tail = translated[1].Length - 5;
                    Drag(pin, GlyphPoint(pin, 1, tail, false), GlyphPoint(pin, 1, translated[1].Length - 1, true));
                    check(pin.SelectedImageText == translated[1].Substring(tail), "drag selection at the bottom of a scrolled translation copies the visible final characters");
                    string output = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output"));
                    Directory.CreateDirectory(output);
                    using (var preview = new Bitmap(pin.Width, pin.Height)) { pin.DrawToBitmap(preview, pin.ClientRectangle); preview.Save(Path.Combine(output, "image-text-selection.png")); }
                    pin.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "查看原图").PerformClick();
                    check(pin.SelectedImageText == "" && !Field<bool>(pin, "scrollVisible"), "switching to the source clears stale selection and restores source geometry");
                    pin.ClientSize = new Size(260, 120);
                    Drag(pin, GlyphPoint(pin, 0, 11, false), GlyphPoint(pin, 0, 13, true));
                    check(pin.SelectedImageText == "pha", "source character selection follows half-size image zoom");
                    Key(pin, Keys.Escape);
                    check(!pin.IsDisposed && pin.SelectedImageText == "", "Escape clears the direct selection before closing the image");
                    pin.ClientSize = bitmap.Size;
                    pin.SetTextBlocks(new[] { new OcrTextBlock("你😀e\u0301好", new Rectangle(20, 30, 250, 32)) });
                    Drag(pin, GlyphPoint(pin, 0, 1, false), GlyphPoint(pin, 0, 3, true));
                    check(pin.SelectedImageText == "😀e\u0301", "selection preserves surrogate pairs and combining character clusters");
                    pin.SetTextBlocks(blocks);
                    Drag(pin, GlyphPoint(pin, 0, 9, false), GlyphPoint(pin, 0, 16, true));
                    check(pin.Controls.Count <= 2, "text selection uses a bounded number of controls independent of OCR block count");
                    Key(pin, Keys.Escape); Key(pin, Keys.Escape);
                    check(pin.IsDisposed, "a second Escape closes the pin and its viewport");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        internal static void RunClipboard(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            using (var previous = ClipboardSnapshot.Capture())
            {
                uint written = 0;
                try
                {
                    using (var pin = new PinForm(new Bitmap(420, 180), false))
                    {
                        pin.Show();
                        pin.SetTextBlocks(new[] { new OcrTextBlock("Original alphabet text", new Rectangle(20, 30, 350, 30)) });
                        Drag(pin, GlyphPoint(pin, 0, 11, false), GlyphPoint(pin, 0, 13, true));
                        Await(pin.CopySelectedText()); written = Native.GetClipboardSequenceNumber();
                        Await(ClipboardContentionTests.CheckReadable(check, written, delegate { return Clipboard.GetText() == "pha"; }, "direct image selection copies exactly its selected characters to the clipboard"));
                        Key(pin, Keys.Control | Keys.A);
                        Await(pin.CopySelectedText()); written = Native.GetClipboardSequenceNumber();
                        Await(ClipboardContentionTests.CheckReadable(check, written, delegate { return Clipboard.GetText() == "Original alphabet text"; }, "direct image select-all copies the complete original block"));
                    }
                }
                finally
                {
                    ClipboardRetry(delegate
                    {
                        if (written != 0 && Native.GetClipboardSequenceNumber() == written)
                        { if (previous.Data != null) Clipboard.SetDataObject(previous.Data, true); else Clipboard.Clear(); }
                        return true;
                    });
                    Ui.RenderOffscreen = offscreen;
                }
            }
        }
        private static void Await(Task task) { while (!task.IsCompleted) { Application.DoEvents(); Thread.Sleep(10); } task.GetAwaiter().GetResult(); }
        private static T ClipboardRetry<T>(Func<T> action)
        {
            for (int attempt = 0; ; attempt++)
            { try { return action(); } catch (ExternalException) { if (attempt == 5) throw; Thread.Sleep(40); } }
        }
        internal static Point GlyphPoint(PinForm pin, int block, int character, bool after)
        {
            Array glyphs = (Array)typeof(PinForm).GetMethod("Glyphs", Private).Invoke(pin, new object[] { block });
            object glyph = glyphs.Cast<object>().First(item => (int)item.GetType().GetField("Start", Private).GetValue(item) == character);
            RectangleF bounds = (RectangleF)glyph.GetType().GetField("Bounds", Private).GetValue(glyph);
            RectangleF destination = (RectangleF)typeof(PinForm).GetProperty("ImageDestination", Private).GetValue(pin, null);
            float scale = (float)typeof(PinForm).GetProperty("ImageScale", Private).GetValue(pin, null);
            return Point.Round(new PointF((bounds.Left + bounds.Width * (after ? .85f : .15f)) * scale, destination.Top + (bounds.Top + bounds.Height / 2) * scale));
        }
        private static void Drag(PinForm pin, Point first, Point last)
        {
            Native.SendMessage(pin.Handle, 0x201, new IntPtr(1), Packed(first));
            Native.SendMessage(pin.Handle, 0x200, new IntPtr(1), Packed(last));
            Native.SendMessage(pin.Handle, 0x202, IntPtr.Zero, Packed(last));
        }
        private static void Key(PinForm pin, Keys key) { typeof(PinForm).GetMethod("OnKeyDown", Private).Invoke(pin, new object[] { new KeyEventArgs(key) }); }
        private static Size Sizing(PinForm pin, int edge, Size size)
        {
            var rectangle = new Native.RECT { Left = pin.Left, Top = pin.Top, Right = pin.Left + size.Width, Bottom = pin.Top + size.Height };
            IntPtr buffer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.RECT)));
            try
            {
                Marshal.StructureToPtr(rectangle, buffer, false);
                Native.SendMessage(pin.Handle, 0x214, new IntPtr(edge), buffer);
                rectangle = (Native.RECT)Marshal.PtrToStructure(buffer, typeof(Native.RECT));
                return new Size(rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static IntPtr Packed(Point point) { return new IntPtr(unchecked(point.Y << 16 | point.X & 0xffff)); }
        private static int Hit(Form form, Point point) { return Native.SendMessage(form.Handle, 0x84, IntPtr.Zero, Packed(form.PointToScreen(point))).ToInt32(); }
    }
}

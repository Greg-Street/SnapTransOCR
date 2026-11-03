using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class EfficiencyTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            SettingsCopies(check);
            CardMeasurements(check);
            ScrollSampling(check);
            ScrollClosing(check);
        }
        private static void SettingsCopies(Action<bool, string> check)
        {
            var original = new AppSettings { VolcId = "synthetic-id", VolcSecret = "synthetic-secret", TranslateWindowWidth = 611 };
            var copy = original.Copy();
            var originalValues = Json.Object(Json.Write(original)); var copiedValues = Json.Object(Json.Write(copy));
            // Reflection cache population can change JSON property order without changing values.
            check(!ReferenceEquals(original, copy) && originalValues.Count == copiedValues.Count
                && originalValues.All(pair => copiedValues.ContainsKey(pair.Key) && Json.Write(pair.Value) == Json.Write(copiedValues[pair.Key])),
                "settings copy preserves every serialized value without a serialization round trip");
            copy.Hotkeys[0] = "Ctrl+2"; copy.OcrServiceOrder[0] = "tencent"; copy.TranslateServiceOrder[0] = "baidu";
            check(original.Hotkeys[0] == "Alt+1" && original.OcrServiceOrder[0] == "baidu" && original.TranslateServiceOrder[0] == "volc", "settings copies own all three mutable arrays");
            original.Hotkeys = original.OcrServiceOrder = original.TranslateServiceOrder = null;
            copy = original.Copy();
            check(copy.Hotkeys == null && copy.OcrServiceOrder == null && copy.TranslateServiceOrder == null, "settings copy preserves absent arrays for the existing normalization path");
        }
        private static void CardMeasurements(Action<bool, string> check)
        {
            using (var card = new ServiceCard("Test", "T"))
            {
                var text = card.Controls.OfType<RichTextBox>().Single();
                foreach (string value in new[] { "Short", new string('W', 6000), String.Join(" ", Enumerable.Repeat("synthetic translation", 1800)), new string('\u4e2d', 5000), new string('a', 4095) + "\ud83d\ude00\nending", "first\r\nsecond\r\nthird" })
                {
                    card.SetValue(value, true);
                    foreach (int width in new[] { 220, 480, 6000 })
                    {
                        int expected = ExpectedHeight(card, text, value, width);
                        check(card.MeasureHeight(width) == expected && card.MeasureHeight(width) == expected, "translation card cached and fresh heights match full measurement at width " + width + ", length " + value.Length + ", actual=" + card.MeasureHeight(width) + ", expected=" + expected);
                    }
                }
                card.SetValue("Line one\nLine two\nLine three", true);
                using (var font = new Font("Microsoft YaHei UI", 21F))
                {
                    text.Font = font;
                    check(card.MeasureHeight(480) == ExpectedHeight(card, text, card.Value, 480), "translation card height invalidates when the result font changes");
                    string previous = card.Value;
                    card.SetValue("", false);
                    check(card.MeasureHeight(480) == Ui.Scale(card, 40), "collapsed translation card retains its compact height");
                    card.SetValue(previous, true);
                    check(card.MeasureHeight(480) == ExpectedHeight(card, text, card.Value, 480), "reopened translation card restores its measured height");
                }
            }
        }
        private static int ExpectedHeight(Control card, RichTextBox text, string value, int width)
        {
            using (var reference = new RichTextBox { BorderStyle = BorderStyle.None, ScrollBars = RichTextBoxScrollBars.None, Font = text.Font, Width = Math.Max(1, width - Ui.Scale(card, 32)), Text = value })
            {
                int height = 0;
                reference.ContentsResized += delegate(object sender, ContentsResizedEventArgs e) { height = e.NewRectangle.Height; };
                Native.SendMessage(reference.Handle, 0x441, IntPtr.Zero, IntPtr.Zero);
                return Ui.Scale(card, 84) + Math.Max(Ui.Scale(card, 60), height);
            }
        }
        private static void ScrollSampling(Action<bool, string> check)
        {
            var sample = typeof(Stitcher).GetMethod("Sample", BindingFlags.Static | BindingFlags.NonPublic);
            foreach (PixelFormat format in new[] { PixelFormat.Format24bppRgb, PixelFormat.Format32bppRgb, PixelFormat.Format32bppArgb })
            using (var image = new Bitmap(321, 240, format))
            {
                using (var graphics = Graphics.FromImage(image))
                {
                    graphics.Clear(Color.White);
                    graphics.FillRectangle(Brushes.Navy, 13, 25, 200, 73);
                    using (var brush = new SolidBrush(Color.FromArgb(120, 240, 30, 60))) graphics.FillRectangle(brush, 87, 111, 150, 60);
                }
                byte[] actual = (byte[])sample.Invoke(null, new object[] { image });
                using (var converted = new Bitmap(image.Width, image.Height, PixelFormat.Format24bppRgb))
                {
                    using (var graphics = Graphics.FromImage(converted)) graphics.DrawImageUnscaled(image, 0, 0);
                    byte[] expected = (byte[])sample.Invoke(null, new object[] { converted });
                    check(actual.SequenceEqual(expected), "scroll sampling matches the previous RGB conversion for " + format);
                }
            }
        }
        private static void ScrollClosing(Action<bool, string> check)
        {
            using (var host = new Form { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
            {
                Exception failure = null;
                IntPtr unused = host.Handle;
                host.BeginInvoke((Action)(async delegate
                {
                    try
                    {
                        using (var first = new Bitmap(800, 600, PixelFormat.Format32bppRgb))
                        using (var form = new ScrollForm(new Rectangle(0, 0, 800, 600), first))
                        {
                            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false; form.Show();
                            bool delivered = false;
                            form.Completed += delegate(Bitmap image) { delivered = true; image.Dispose(); };
                            Task finish = (Task)typeof(ScrollForm).GetMethod("Finish", Private).Invoke(form, null);
                            check(!finish.IsCompleted, "scroll composition yields to the message loop while its worker owns the images");
                            form.Close();
                            check(!form.IsDisposed && !form.Visible, "closing a composing scroll hides the window and defers image disposal");
                            await finish;
                            check(form.IsDisposed && !delivered, "closing a composing scroll disposes after the worker returns without opening an editor");
                        }
                        using (var first = new Bitmap(800, 600, PixelFormat.Format32bppRgb))
                        using (var form = new ScrollForm(new Rectangle(0, 0, 800, 600), first))
                        {
                            typeof(ScrollForm).GetField("capturing", Private).SetValue(form, true);
                            int delivered = 0;
                            form.Completed += delegate(Bitmap image) { delivered++; image.Dispose(); };
                            Task firstFinish = (Task)typeof(ScrollForm).GetMethod("Finish", Private).Invoke(form, null);
                            Task repeated = (Task)typeof(ScrollForm).GetMethod("Finish", Private).Invoke(form, null);
                            check(repeated.IsCompleted && !firstFinish.IsCompleted, "repeated finish is ignored while the first request waits for the active capture");
                            typeof(ScrollForm).GetField("capturing", Private).SetValue(form, false);
                            await firstFinish;
                            check(delivered == 1 && form.IsDisposed, "only one composed image is delivered after the capture finishes");
                        }
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { host.Close(); }
                }));
                Application.Run(host);
                if (failure != null) throw failure;
            }
        }
    }
}

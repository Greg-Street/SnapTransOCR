using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class PinnedWindowTests
    {
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Native.RECT rectangle);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private sealed class FrameFixture : RoundedForm
        {
            public int NativeActivations, NativePaints;
            public FrameFixture() { BackColor = Color.LimeGreen; ClientSize = new Size(300, 180); ResumeLayout(true); }
            protected override void DefWndProc(ref Message message)
            {
                if (message.Msg == 0x86) NativeActivations++;
                if (message.Msg == 0x85) NativePaints++;
                base.DefWndProc(ref message);
            }
        }
        internal static void Run(Action<bool, string> check)
        {
            bool previous = Ui.RenderOffscreen;
            Ui.RenderOffscreen = true;
            try
            {
                using (var form = new FrameFixture())
                {
                    ShowOffscreen(form);
                    bool accepted = true;
                    foreach (int active in new[] { 1, 0, 1, 0 })
                    {
                        accepted &= Native.SendMessage(form.Handle, 0x86, new IntPtr(active), IntPtr.Zero) != IntPtr.Zero;
                        Native.SendMessage(form.Handle, 0x85, new IntPtr(1), IntPtr.Zero);
                    }
                    check(accepted, "custom frame accepts activation and deactivation changes");
                    check(form.NativeActivations == 0 && form.NativePaints == 0, "custom active frames bypass native activation and nonclient painting");
                    check(Hit(form, new Point(2, 80)) == 10 && Hit(form, new Point(297, 177)) == 17, "suppressing native frame paint retains native resize hit targets");
                }
                foreach (Size imageSize in new[] { new Size(600, 100), new Size(100, 600), new Size(37, 19), new Size(1400, 900) })
                using (var image = new Bitmap(imageSize.Width, imageSize.Height))
                {
                    using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.CornflowerBlue);
                    using (var pin = new PinForm((Bitmap)image.Clone()))
                    {
                        ShowOffscreen(pin);
                        long style = GetWindowLongPtr(pin.Handle, -16).ToInt64();
                        check(pin.FormBorderStyle == FormBorderStyle.None && (style & 0x00c00000) == 0 && NativeClientMatches(pin), "pin has no native caption or extra frame for " + imageSize);
                        check(pin.Controls.Count == 0 && pin.Padding == Padding.Empty && pin.Region == null, "pin displays rectangular image corners without padding or a shadow-blocking custom region");
                        bool ratio = RatioMatches(pin.Size, imageSize);
                        var wheel = typeof(PinForm).GetMethod("OnMouseWheel", Private);
                        for (int i = 0; i < 30; i++)
                        {
                            wheel.Invoke(pin, new object[] { new MouseEventArgs(MouseButtons.None, 0, 20, 20, i < 15 ? -120 : 120) });
                            ratio &= RatioMatches(pin.Size, imageSize) && NativeClientMatches(pin);
                        }
                        check(ratio, "pin wheel zoom retains aspect ratio and native client geometry for " + imageSize);
                        if (imageSize.Width >= 100 && imageSize.Height >= 100)
                        {
                            bool sizing = true;
                            foreach (int edge in Enumerable.Range(1, 8))
                            {
                                var requested = new Native.RECT { Left = -500, Top = -300, Right = 120, Bottom = 170 };
                                var resized = Sizing(pin, edge, requested);
                                sizing &= RatioMatches(resized.Rectangle.Size, imageSize);
                                if (edge == 1 || edge == 4 || edge == 7) sizing &= resized.Right == requested.Right;
                                else sizing &= resized.Left == requested.Left;
                                if (edge == 3 || edge == 4 || edge == 5) sizing &= resized.Bottom == requested.Bottom;
                                else sizing &= resized.Top == requested.Top;
                            }
                            check(sizing, "pin edge resizing preserves aspect ratio and the opposite anchor for " + imageSize);
                        }
                        Native.SendMessage(pin.Handle, 0xa3, new IntPtr(2), IntPtr.Zero);
                        check(pin.ClientSize == imageSize, "pin double click returns a fitting image to its physical original size");
                        using (var rendered = new Bitmap(pin.Width, pin.Height))
                        {
                            pin.DrawToBitmap(rendered, pin.ClientRectangle);
                            check(rendered.GetPixel(0, 0).ToArgb() == Color.CornflowerBlue.ToArgb() && rendered.GetPixel(pin.Width - 1, pin.Height - 1).ToArgb() == Color.CornflowerBlue.ToArgb(), "pin image reaches both outer corners without a painted frame");
                        }
                        var menu = pin.ContextMenuStrip;
                        check(menu != null && menu.Items.Count == 6 && menu.Items.Cast<ToolStripItem>().Any(item => item.Text == "旋转"), "pin retains right-click actions and offers image rotation");
                        bool opened = false;
                        menu.Opening += delegate(object sender, System.ComponentModel.CancelEventArgs e) { opened = true; e.Cancel = true; };
                        Point location = pin.PointToScreen(new Point(pin.Width / 2, pin.Height / 2));
                        Native.SendMessage(pin.Handle, 0xa5, new IntPtr(2), new IntPtr(unchecked(location.Y << 16 | location.X & 0xffff)));
                        check(opened, "right-clicking the draggable image invokes its context menu");
                        typeof(PinForm).GetMethod("OnKeyDown", Private).Invoke(pin, new object[] { new KeyEventArgs(Keys.Escape) });
                        check(pin.IsDisposed, "Escape closes the image pin");
                    }
                }
                foreach (Size stripSize in new[] { new Size(600, 3), new Size(3, 600) })
                using (var pin = new PinForm(new Bitmap(stripSize.Width, stripSize.Height)))
                {
                    ShowOffscreen(pin);
                    check(Hit(pin, new Point(pin.Width / 2, pin.Height / 2)) == 2, "thin image pins retain a draggable center for " + stripSize);
                }
                TranslationPin(check);
                CapturedPinPosition(check);
            }
            finally { Ui.RenderOffscreen = previous; }
        }
        private static void TranslationPin(Action<bool, string> check)
        {
            var original = SolidImage(Color.OrangeRed);
            var translated = SolidImage(Color.RoyalBlue);
            var replacement = SolidImage(Color.ForestGreen);
            PinToolbar toolbar;
            using (var pin = new PinForm(original))
            {
                ShowOffscreen(pin);
                pin.SetStatus("正在识别图片，长提示可悬停查看完整内容。");
                var label = pin.Controls.OfType<Label>().Single();
                check(label.Visible && label.AutoEllipsis && pin.ClientRectangle.Contains(label.Bounds), "translation progress stays inside the image and ellipsizes long status");
                var size = pin.Size;
                Point offscreenLocation = pin.Location;
                pin.Location = new Point(Screen.PrimaryScreen.WorkingArea.Left + 40, Screen.PrimaryScreen.WorkingArea.Top + 70);
                Native.SetForegroundWindow(pin.Handle);
                IntPtr beforeToolbar = Native.GetForegroundWindow();
                check(beforeToolbar == pin.Handle, "the visible image pin establishes the foreground baseline for toolbar activation tests");
                pin.SetTranslation(translated);
                toolbar = pin.OwnedForms.OfType<PinToolbar>().Single();
                var toggle = toolbar.Controls.OfType<GlyphButton>().Single();
                check(!toolbar.ContainsFocus && Native.GetForegroundWindow() == beforeToolbar, "creating the topmost pin's external switch preserves foreground and focus");
                check((GetWindowLongPtr(toolbar.Handle, -20).ToInt64() & 8) != 0, "the owned toolbar inherits its image pin's native topmost state");
                check(Pixel(pin) == Color.RoyalBlue.ToArgb() && pin.Size == size && !label.Visible, "setting a translation displays it without changing pin geometry and clears progress");
                check(toggle.Visible && toggle.AccessibleName == "查看原图" && !toolbar.Bounds.IntersectsWith(pin.Bounds), "translation switch is visible outside every screenshot pixel");
                check((GetWindowLongPtr(toolbar.Handle, -20).ToInt64() & 0x08000080) == 0x08000080 && Native.SendMessage(toolbar.Handle, 0x21, pin.Handle, IntPtr.Zero).ToInt32() == 3, "external toolbar is a no-activate tool window");
                check(!toggle.CanSelect && !toggle.TabStop, "external switch cannot take keyboard focus from image selection");
                IntPtr foreground = Native.GetForegroundWindow();
                Native.SendMessage(toggle.Handle, 0x201, new IntPtr(1), new IntPtr(8 << 16 | 8));
                Native.SendMessage(toggle.Handle, 0x202, IntPtr.Zero, new IntPtr(8 << 16 | 8));
                check(!toolbar.ContainsFocus && Native.GetForegroundWindow() == foreground, "native mouse switch click does not activate its tool window or change the foreground app");
                var topmost = (ToolStripMenuItem)pin.ContextMenuStrip.Items[0];
                topmost.Checked = false;
                check((GetWindowLongPtr(toolbar.Handle, -20).ToInt64() & 8) == 0 && !toolbar.ContainsFocus && Native.GetForegroundWindow() == foreground,
                    "disabling the image pin's topmost state also lowers its owned toolbar without activating it");
                topmost.Checked = true;
                check((GetWindowLongPtr(toolbar.Handle, -20).ToInt64() & 8) != 0 && !toolbar.ContainsFocus && Native.GetForegroundWindow() == foreground,
                    "enabling the image pin's topmost state also raises its owned toolbar without activating it");
                pin.Location = offscreenLocation;
                check(Pixel(pin) == Color.OrangeRed.ToArgb() && toggle.AccessibleName == "查看译文" && pin.Size == size, "switching to original preserves geometry and updates the tooltip");
                var item = pin.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(entry => entry.Text == "查看译文");
                item.PerformClick();
                check(Pixel(pin) == Color.RoyalBlue.ToArgb(), "the context menu switches back to the translation");
                var key = new KeyEventArgs(Keys.Space);
                typeof(PinForm).GetMethod("OnKeyDown", Private).Invoke(pin, new object[] { key });
                check(Pixel(pin) == Color.OrangeRed.ToArgb() && key.SuppressKeyPress, "Space switches the displayed image without a default key action");
                pin.SetTranslation(replacement);
                check(Pixel(pin) == Color.ForestGreen.ToArgb() && Disposed(translated) && !Disposed(original) && pin.ContextMenuStrip.Items.Count == 7, "replacing a translation releases the previous image and retains one original and switch");
                using (var incorrect = new Bitmap(40, 40))
                {
                    bool rejected = false;
                    try { pin.SetTranslation(incorrect); } catch (ArgumentException) { rejected = true; }
                    check(rejected && !Disposed(incorrect) && Pixel(pin) == Color.ForestGreen.ToArgb(), "wrong-sized translation is rejected without taking ownership or changing the display");
                }
                pin.Location = new Point(pin.Left + 25, pin.Top + 40);
                check(toolbar.Visible && !toolbar.Bounds.IntersectsWith(pin.Bounds) && toolbar.Right == pin.Right, "external switch follows pin movement without covering image text");
                pin.Hide();
                check(!toolbar.Visible, "hiding a translation pin hides its external switch");
                pin.Show();
                check(toolbar.Visible, "showing a translation pin restores its external switch");
                bool dpiStable = true;
                foreach (int dpi in new[] { 144, 192, 240, 96 })
                {
                    IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.RECT)));
                    try
                    {
                        Marshal.StructureToPtr(new Native.RECT { Left = pin.Left, Top = pin.Top, Right = pin.Left + size.Width * dpi / 96, Bottom = pin.Top + size.Height * dpi / 96 }, pointer, false);
                        Native.SendMessage(pin.Handle, 0x2e0, new IntPtr(dpi << 16 | dpi), pointer);
                        Rectangle toolbarBounds = toolbar.Bounds;
                        Marshal.StructureToPtr(new Native.RECT { Left = pin.Left, Top = pin.Top, Right = pin.Left + 120, Bottom = pin.Top + 120 }, pointer, false);
                        Native.SendMessage(toolbar.Handle, 0x2e0, new IntPtr(dpi << 16 | dpi), pointer);
                        dpiStable &= toolbar.Bounds == toolbarBounds;
                        dpiStable &= pin.Size == size && NativeClientMatches(pin) && !pin.Bounds.IntersectsWith(toolbar.Bounds) && toolbar.ClientRectangle.Contains(toggle.Bounds) && pin.Padding == Padding.Empty;
                    }
                    finally { Marshal.FreeHGlobal(pointer); }
                }
                check(dpiStable, "pin DPI transitions keep physical image dimensions and the switch outside the image");
            }
            check(Disposed(original) && Disposed(replacement), "closing the translation pin releases both owned images");
            check(toolbar.IsDisposed && !toolbar.IsHandleCreated, "closing a translation pin also destroys the external toolbar and its native handle");
            using (var pin = new PinForm(SolidImage(Color.RoyalBlue), false, SolidImage(Color.OrangeRed)))
            {
                ShowOffscreen(pin);
                check(Pixel(pin) == Color.RoyalBlue.ToArgb() && !pin.TopMost, "optional original constructor initially displays the translation and respects topmost setting");
                pin.OwnedForms.OfType<PinToolbar>().Single().Controls.OfType<GlyphButton>().Single().PerformClick();
                check(Pixel(pin) == Color.OrangeRed.ToArgb(), "optional original constructor enables the same original toggle");
            }
            var work = new Rectangle(-1920, -200, 1920, 1080);
            foreach (var bounds in new[] { new Rectangle(-1700, 0, 200, 100), new Rectangle(-1700, -200, 200, 100), new Rectangle(-200, -200, 200, 100), new Rectangle(-1920, -200, 1880, 1080), new Rectangle(-1920, -160, 1920, 1040) })
            {
                Rectangle placement = PinToolbar.Place(bounds, work, 34, 4);
                check(!placement.IsEmpty && work.Contains(placement) && !bounds.IntersectsWith(placement), "switch placement fits outside edge-pinned images on a negative-coordinate monitor");
            }
            check(PinToolbar.Place(work, work, 34, 4).IsEmpty, "screen-filling images use Space or the context menu instead of an obstructing switch");
        }
        private static Bitmap SolidImage(Color color)
        {
            var image = new Bitmap(200, 100);
            using (var graphics = Graphics.FromImage(image)) graphics.Clear(color);
            return image;
        }
        private static void CapturedPinPosition(Action<bool, string> check)
        {
            Rectangle work = Screen.PrimaryScreen.WorkingArea;
            foreach (Point location in new[] { new Point(70, 80), new Point(-800, -450), new Point(work.Left + 40, work.Bottom - 90) })
            using (var pin = new PinForm(new Bitmap(320, 120), false, sourceLocation: location))
            {
                check(pin.Location == location && pin.ClientSize == new Size(320, 120), "capture-origin pin retains its physical location and original dimensions at " + location);
                Point moved = new Point(location.X + 7, location.Y - 5);
                pin.Location = moved;
                pin.SetTranslation(new Bitmap(320, 1800));
                check(pin.Location == moved, "translation completion preserves the current pin anchor including a user's move");
                int available = Math.Max(pin.MinimumSize.Height, Screen.FromPoint(moved).WorkingArea.Bottom - moved.Y);
                check(pin.Height <= available, "expanded translation scrolls within the space below its current anchor");
                pin.ContextMenuStrip.Items.Cast<ToolStripItem>().Single(item => item.Text == "查看原图").PerformClick();
                check(pin.Location == moved && pin.ClientSize == new Size(320, 120), "switching back restores original pixel dimensions without moving the pin");
                Size zoomed = (Size)typeof(PinForm).GetMethod("ConstrainedSize", Private).Invoke(pin, new object[] { 1000d });
                Rectangle monitor = Screen.FromPoint(moved).WorkingArea;
                check(zoomed.Width <= Math.Max(320, monitor.Width) && zoomed.Height <= Math.Max(120, monitor.Height), "capture-origin zoom remains bounded while preserving one-to-one source size");
            }
            Rectangle desktop = SystemInformation.VirtualScreen;
            using (var pin = new PinForm(new Bitmap(desktop.Width, desktop.Height), false, sourceLocation: desktop.Location))
                check(pin.Bounds == desktop, "a full virtual-desktop pin includes taskbar pixels without work-area clamping or downscaling");
        }
        private static int Pixel(Form pin)
        {
            using (var rendered = new Bitmap(pin.Width, pin.Height)) { pin.DrawToBitmap(rendered, pin.ClientRectangle); return rendered.GetPixel(1, 1).ToArgb(); }
        }
        private static bool Disposed(Bitmap image)
        {
            try { image.GetPixel(0, 0); return false; } catch (ArgumentException) { return true; }
        }
        private static bool RatioMatches(Size size, Size image)
        {
            return Math.Abs((double)size.Width * image.Height - (double)size.Height * image.Width) <= Math.Max(image.Width, image.Height);
        }
        private static bool NativeClientMatches(Form form)
        {
            Native.RECT rectangle;
            GetClientRect(form.Handle, out rectangle);
            return form.Size == form.ClientSize && rectangle.Rectangle.Size == form.Size;
        }
        private static Native.RECT Sizing(Form form, int edge, Native.RECT rectangle)
        {
            IntPtr pointer = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Native.RECT)));
            try
            {
                Marshal.StructureToPtr(rectangle, pointer, false);
                Native.SendMessage(form.Handle, 0x214, new IntPtr(edge), pointer);
                return (Native.RECT)Marshal.PtrToStructure(pointer, typeof(Native.RECT));
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
        private static int Hit(Form form, Point point)
        {
            point = form.PointToScreen(point);
            return Native.SendMessage(form.Handle, 0x84, IntPtr.Zero, new IntPtr(unchecked(point.Y << 16 | point.X & 0xffff))).ToInt32();
        }
        private static void ShowOffscreen(Form form)
        {
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-10000, -10000); form.ShowInTaskbar = false; form.Show(); form.Invalidate(true); form.Update();
        }
    }
}

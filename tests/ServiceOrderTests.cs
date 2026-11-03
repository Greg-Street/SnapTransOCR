using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ServiceOrderTests
    {
        [DllImport("user32.dll")] private static extern bool GetKeyboardState(byte[] state);
        [DllImport("user32.dll")] private static extern bool SetKeyboardState(byte[] state);
        internal static void Run(Action<bool, string> check)
        {
            var legacy = Json.Read<AppSettings>("{}"); legacy.NormalizeProviders();
            check(legacy.AccurateOcr, "new and unspecified OCR settings default to accurate recognition");
            check(!Json.Read<AppSettings>(Json.Write(new AppSettings { AccurateOcr = false })).AccurateOcr, "an explicitly disabled accurate OCR setting survives serialization");
            check(legacy.TranslateServiceOrder.SequenceEqual(new[] { "volc", "baidu", "bing", "ark", "baidu_llm", "baidu_domain" }) && legacy.OcrServiceOrder.SequenceEqual(new[] { "baidu", "tencent" }), "legacy configuration keeps existing services before newly added services");
            var settings = new AppSettings { TranslateServiceOrder = new[] { "baidu", "unknown", "baidu", null }, OcrServiceOrder = null };
            settings.NormalizeProviders();
            check(settings.TranslateServiceOrder.SequenceEqual(new[] { "baidu", "volc", "bing", "ark", "baidu_llm", "baidu_domain" }) && settings.OcrServiceOrder.SequenceEqual(new[] { "baidu", "tencent" }), "service order discards unknown and duplicate IDs and restores missing services");
            check(settings.TranslateProvider == "volc" && settings.OcrProvider == "baidu", "normalizing order does not change valid default providers");
            string file = Path.Combine(Path.GetTempPath(), "SnapTransOCR-order-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                SettingsStore.Save(settings, file); var loaded = SettingsStore.Load(file);
                check(loaded.TranslateServiceOrder.SequenceEqual(settings.TranslateServiceOrder) && loaded.OcrServiceOrder.SequenceEqual(settings.OcrServiceOrder), "service order persists through the settings file");
            }
            finally { if (File.Exists(file)) File.Delete(file); }
            AppSettings saved = null;
            using (var form = new SettingsForm(new AppSettings(), value => saved = value))
            {
                var translation = Field<ServiceOrderControl>(form, "translationOrder");
                var recognition = Field<ServiceOrderControl>(form, "recognitionOrder");
                var translator = Field<ComboBox>(form, "translator"); var ocr = Field<ComboBox>(form, "ocr");
                translation.MoveSelected(1); recognition.MoveSelected(1);
                check(((string[])translator.Tag).SequenceEqual(new[] { "baidu", "volc" }) && translator.SelectedItem.ToString() == "火山引擎翻译", "translation dropdown follows order without changing its selected provider");
                check(((string[])ocr.Tag).SequenceEqual(new[] { "tencent", "baidu" }) && ocr.SelectedItem.ToString() == "百度 OCR", "OCR dropdown follows order without changing its selected provider");
                var pages = Field<System.Collections.Generic.Dictionary<string, FlowLayoutPanel>>(form, "pages");
                check(pages["翻译设置"].Controls.Cast<Control>().Where(control => control.Tag is string).Select(control => (string)control.Tag).SequenceEqual(new[] { "baidu", "baidu", "baidu", "volc", "volc", "volc", "bing", "bing", "ark", "ark", "ark" }), "translation credential sections follow provider order with one shared Baidu group");
                check(pages["识别设置"].Controls.Cast<Control>().Where(control => control.Tag is string).Select(control => (string)control.Tag).SequenceEqual(new[] { "tencent", "tencent", "baidu", "baidu" }), "OCR credential sections follow the chosen service order");
                var switches = Field<System.Collections.Generic.Dictionary<string, ToggleSwitch>>(form, "serviceChecks");
                switches["BaiduTranslateEnabled"].Checked = false; switches["BaiduTranslateEnabled"].Checked = true;
                check(translation.Order.SequenceEqual(new[] { "baidu", "volc", "bing", "ark", "baidu_llm", "baidu_domain" }) && ((string[])translator.Tag).SequenceEqual(new[] { "baidu", "volc" }), "disable and enable preserve the chosen order while unavailable services stay out of the selector");
                Invoke(form, "Save", null);
                check(saved.TranslateServiceOrder.SequenceEqual(new[] { "baidu", "volc", "bing", "ark", "baidu_llm", "baidu_domain" }) && saved.OcrServiceOrder.SequenceEqual(new[] { "tencent", "baidu" }) && saved.TranslateProvider == "volc" && saved.OcrProvider == "baidu", "saving service order preserves independent default providers");
            }
            using (var reopened = new SettingsForm(saved, delegate { }))
                check(Field<ServiceOrderControl>(reopened, "translationOrder").Order.SequenceEqual(saved.TranslateServiceOrder), "saved service order is restored when settings reopen");
            using (var control = new ServiceOrderControl(new[] { "volc", "baidu" }, code => code))
            {
                int changed = 0; control.OrderChanged += delegate { changed++; };
                control.MoveSelected(-1); check(changed == 0, "moving beyond the first service is a no-op");
                var message = new Message();
                Invoke(control, "ProcessCmdKey", new object[] { message, Keys.Alt | Keys.Down });
                check(control.Order.SequenceEqual(new[] { "baidu", "volc" }) && changed == 1, "Alt plus Down moves the selected service");
                Invoke(control, "ProcessCmdKey", new object[] { message, Keys.Alt | Keys.Up });
                check(control.Order.SequenceEqual(new[] { "volc", "baidu" }) && changed == 2, "Alt plus Up restores the selected service");
                var handle = control.Handle;
                Invoke(control, "OnMouseDown", new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, 15, 0) });
                Invoke(control, "OnMouseMove", new object[] { new MouseEventArgs(MouseButtons.Left, 0, 12, control.ItemHeight + 15, 0) });
                check(control.Order.SequenceEqual(new[] { "volc", "baidu" }) && changed == 2, "drag preview leaves the committed service order unchanged");
                Invoke(control, "OnMouseUp", new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, control.ItemHeight + 15, 0) });
                check(control.Order.SequenceEqual(new[] { "baidu", "volc" }) && changed == 3, "dragging the handle moves its service to the target row");
                Invoke(control, "OnMouseDown", new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, 15, 0) });
                Invoke(control, "OnMouseMove", new object[] { new MouseEventArgs(MouseButtons.Left, 0, 12, control.Height + 40, 0) });
                Invoke(control, "OnMouseUp", new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, control.Height + 40, 0) });
                check(changed == 3, "dropping outside the service list leaves order unchanged");
            }
            using (var form = new MainForm())
            {
                form.RefreshSettings(saved);
                var cards = Field<System.Collections.Generic.Dictionary<string, ServiceCard>>(form, "cards");
                check(cards["baidu"].Top < cards["volc"].Top, "translation result cards use saved order");
                saved.BaiduTranslateEnabled = false; form.RefreshSettings(saved);
                saved.BaiduTranslateEnabled = true; form.RefreshSettings(saved);
                check(cards["baidu"].Top < cards["volc"].Top, "reenabling a translation card restores its place");
                var text = Field<RichTextBox>(form, "source");
                check(!text.AutoWordSelection && (Native.SendMessage(text.Handle, 0x44e, IntPtr.Zero, IntPtr.Zero).ToInt64() & 1) == 0, "source rich edit disables native automatic whole-word selection");
                Invoke(text, "RecreateHandle", null);
                check((Native.SendMessage(text.Handle, 0x44e, IntPtr.Zero, IntPtr.Zero).ToInt64() & 1) == 0, "source whole-word selection stays disabled after handle recreation");
            }
            DragPresentation(check);
            DragCancellation(check);
            NativeDrag(check);
            InitialDpi(check);
        }
        private static void DragPresentation(Action<bool, string> check)
        {
            using (var control = new ServiceOrderControl(new[] { "volc", "baidu" }, code => code))
            {
                var handle = control.Handle;
                bool sourceInvalidated = false;
                control.Invalidated += delegate(object sender, InvalidateEventArgs e) { sourceInvalidated |= e.InvalidRect.IntersectsWith(control.GetItemRectangle(0)); };
                Invoke(control, "OnMouseDown", new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, 15, 0) });
                sourceInvalidated = false;
                Invoke(control, "OnMouseMove", new object[] { new MouseEventArgs(MouseButtons.Left, 0, 12, control.ItemHeight + 15, 0) });
                check(sourceInvalidated, "starting a service drag repaints the source row");
                using (var bitmap = DrawRows(control))
                {
                    check(bitmap.GetPixel(control.Width - 12, control.ItemHeight * 3 / 2).ToArgb() == Ui.Accent.ToArgb(), "dragged service has an opaque blue surface at its pointer position");
                    var textBounds = new Rectangle(Ui.Scale(control, 36), control.ItemHeight, Ui.Scale(control, 80), control.ItemHeight);
                    int whitePixels = 0;
                    for (int y = textBounds.Top + 8; y < textBounds.Bottom - 8; y++)
                        for (int x = textBounds.Left; x < textBounds.Right; x++)
                            if (bitmap.GetPixel(x, y).ToArgb() == Color.White.ToArgb()) whitePixels++;
                    check(whitePixels > 10, "dragged service caption stays visible as white text on blue");
                    check(bitmap.GetPixel(control.Width - 12, control.ItemHeight / 2).ToArgb() == Ui.Background.ToArgb(), "the other service immediately occupies the vacated row during drag");
                }
                sourceInvalidated = false;
                Invoke(control, "ProcessCmdKey", new object[] { new Message(), Keys.Escape });
                check(sourceInvalidated && !control.Capture, "canceling a service drag repaints its source and releases capture");
                using (var bitmap = DrawRows(control))
                    check(bitmap.GetPixel(control.Width - 12, control.ItemHeight / 2).ToArgb() != Ui.Accent.ToArgb(), "canceling a service drag removes the active blue surface");
            }
        }
        private static Bitmap DrawRows(ServiceOrderControl control)
        {
            var bitmap = new Bitmap(control.Width, control.Height);
            control.DrawToBitmap(bitmap, control.ClientRectangle);
            return bitmap;
        }
        private static void DragCancellation(Action<bool, string> check)
        {
            using (var control = new ServiceOrderControl(new[] { "first", "second", "third" }, code => code))
            {
                var handle = control.Handle; int changed = 0;
                control.OrderChanged += delegate { changed++; };
                foreach (string cancel in new[] { "Escape", "Capture", "Outside" })
                {
                    Invoke(control, "OnMouseDown", new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, 15, 0) });
                    Invoke(control, "OnMouseMove", new object[] { new MouseEventArgs(MouseButtons.Left, 0, 12, control.ItemHeight * 2 + 15, 0) });
                    if (cancel == "Escape") Invoke(control, "ProcessCmdKey", new object[] { new Message(), Keys.Escape });
                    else if (cancel == "Capture") control.Capture = false;
                    else Invoke(control, "OnMouseMove", new object[] { new MouseEventArgs(MouseButtons.Left, 0, control.Width + 1, control.ItemHeight + 15, 0) });
                    Invoke(control, "OnMouseMove", new object[] { new MouseEventArgs(MouseButtons.Left, 0, 12, control.ItemHeight * 2 + 15, 0) });
                    Invoke(control, "OnMouseUp", new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, control.ItemHeight * 2 + 15, 0) });
                    check(control.Order.SequenceEqual(new[] { "first", "second", "third" }) && changed == 0 && !control.Capture,
                        cancel + " cancels a preview without committing when the pointer returns");
                }
                Invoke(control, "OnMouseDown", new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, 15, 0) });
                Invoke(control, "OnMouseMove", new object[] { new MouseEventArgs(MouseButtons.Left, 0, 12, control.ItemHeight * 2 + 15, 0) });
                Invoke(control, "OnMouseMove", new object[] { new MouseEventArgs(MouseButtons.Left, 0, 12, control.ItemHeight + 15, 0) });
                Invoke(control, "OnMouseUp", new object[] { new MouseEventArgs(MouseButtons.Left, 1, 12, control.ItemHeight + 15, 0) });
                check(control.Order.SequenceEqual(new[] { "second", "first", "third" }) && changed == 1, "reversing a three-row preview commits only its final destination");
            }
        }
        private static void NativeDrag(Action<bool, string> check)
        {
            var originalKeys = new byte[256]; GetKeyboardState(originalKeys);
            try
            {
                using (var control = new ServiceOrderControl(new[] { "volc", "baidu" }, code => code))
                {
                    int changed = 0; control.OrderChanged += delegate { changed++; };
                    int second = control.ItemHeight + 15;
                    MouseMessage(control, 0x201, 1, 12, 15);
                    MouseMessage(control, 0x200, 1, 12, second);
                    MouseMessage(control, 0x202, 0, 12, second);
                    check(control.Order.SequenceEqual(new[] { "baidu", "volc" }) && changed == 1 && !control.Capture, "native mouse messages reorder one service and raise exactly one change");
                    MouseMessage(control, 0x201, 1, 12, second);
                    MouseMessage(control, 0x200, 1, 12, 15);
                    MouseMessage(control, 0x202, 0, 12, 15);
                    check(control.Order.SequenceEqual(new[] { "volc", "baidu" }) && changed == 2, "native dragging also moves a service upwards");
                    MouseMessage(control, 0x201, 1, 12, 15);
                    MouseMessage(control, 0x200, 1, 12, control.Height + 40);
                    MouseMessage(control, 0x202, 0, 12, control.Height + 40);
                    check(changed == 2 && !control.Capture, "native drop outside cancels without an order event");
                    MouseMessage(control, 0x201, 1, 12, 15);
                    MouseMessage(control, 0x200, 1, 12, second);
                    control.Capture = false;
                    MouseMessage(control, 0x202, 0, 12, second);
                    check(changed == 2, "losing capture before mouse up still cancels a native drag");
                    MouseMessage(control, 0x201, 1, 12, 15);
                    MouseMessage(control, 0x200, 1, 12, second);
                    Invoke(control, "ProcessCmdKey", new object[] { new Message(), Keys.Escape });
                    MouseMessage(control, 0x202, 0, 12, second);
                    check(changed == 2 && !control.Capture, "Escape cancels a native drag without an order event");
                    MouseMessage(control, 0x201, 1, 12, 15);
                    MouseMessage(control, 0x202, 0, 12, 15);
                    check(changed == 2, "clicking the handle without dragging does not reorder services");
                    MouseMessage(control, 0x201, 1, Ui.Scale(control, 60), 15);
                    MouseMessage(control, 0x200, 1, Ui.Scale(control, 60), second);
                    MouseMessage(control, 0x202, 0, Ui.Scale(control, 60), second);
                    check(changed == 2 && control.Order.SequenceEqual(new[] { "volc", "baidu" }), "dragging outside the handle does not reorder services");
                }
            }
            finally { SetKeyboardState(originalKeys); }
        }
        private static void MouseMessage(Control control, int message, int buttons, int x, int y)
        {
            // WinForms reads the calling thread's key state for MouseMove.Button.
            var keys = new byte[256]; GetKeyboardState(keys); keys[1] = (byte)(buttons == 1 ? 0x80 : 0); SetKeyboardState(keys);
            Native.SendMessage(control.Handle, message, new IntPtr(buttons), new IntPtr((y << 16) | (x & 0xffff)));
        }
        private static void InitialDpi(Action<bool, string> check)
        {
            Type helper = typeof(Control).Assembly.GetType("System.Windows.Forms.DpiHelper");
            var dpiField = helper.GetField("deviceDpi", BindingFlags.Static | BindingFlags.NonPublic);
            var factorField = helper.GetField("logicalToDeviceUnitsScalingFactor", BindingFlags.Static | BindingFlags.NonPublic);
            object dpi = dpiField.GetValue(null), factor = factorField.GetValue(null);
            try
            {
                foreach (int value in new[] { 96, 144, 192, 240 })
                {
                    dpiField.SetValue(null, (double)value); factorField.SetValue(null, value / 96.0);
                    using (var form = new SettingsForm(new AppSettings(), delegate { }))
                    {
                        form.ClientSize = new Size(Ui.Scale(form, 860), Ui.Scale(form, 580));
                        foreach (string field in new[] { "translationOrder", "recognitionOrder" })
                        {
                            var order = Field<ServiceOrderControl>(form, field);
                            check(order.Height == order.ItemHeight * order.Items.Count && order.ItemHeight >= Ui.Scale(form, 40) && order.Width <= Ui.Scale(form, 250) && order.Right <= order.Parent.ClientSize.Width, "service order rows and drag handles fit initial " + value + " DPI: " + field);
                        }
                    }
                }
            }
            finally { dpiField.SetValue(null, dpi); factorField.SetValue(null, factor); }
        }
        private static T Field<T>(object instance, string name) { return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
        private static object Invoke(object instance, string method, object[] arguments) { return instance.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, arguments); }
    }
}

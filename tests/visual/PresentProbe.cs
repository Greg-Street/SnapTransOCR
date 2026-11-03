using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class PresentProbe
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        sealed class FakeTranslation : HttpMessageHandler
        {
            public int Calls;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Interlocked.Increment(ref Calls);
                await Task.Delay(500, token);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[{\"translations\":[{\"text\":\"这是快捷键首帧测试的合成译文。\\n后台结果更新后，窗口背景仍应完整。\"}]}]") };
            }
        }
        static T Field<T>(object item, string name) { return (T)item.GetType().GetField(name, Fields).GetValue(item); }
        static int failure;
        static void Check(bool value, string text) { Console.WriteLine((value ? "ok " : "FAIL ") + text); if (!value) failure++; }
        static void Frame(Form form, string path)
        {
            Native.DwmFlush();
            using (var image = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppRgb))
            {
                using (var graphics = Graphics.FromImage(image)) graphics.CopyFromScreen(form.Location, Point.Empty, image.Size);
                int holes = 0, total = 0;
                for (int y = 24; y < image.Height - 24; y += 8)
                    for (int x = 24; x < image.Width - 24; x += 8)
                    { Color pixel = image.GetPixel(x, y); total++; if (pixel.G > 180 && pixel.R < 80 && pixel.B < 80) holes++; }
                image.Save(path, ImageFormat.Png);
                Check(holes == 0, Path.GetFileNameWithoutExtension(path) + " interior holes=" + holes + "/" + total);
            }
        }
        [STAThread] static int Main(string[] args)
        {
            string output = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, args.Length == 0 ? "frames" : args[0]);
            Directory.CreateDirectory(output);
            Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Program.AppIcon = SystemIcons.Application;
            Point cursor = Cursor.Position;
            var settings = new AppSettings { Hotkeys = new[] { "", "", "", "" }, HistoryEnabled = false, TranslateProvider = "bing", BingTranslateEnabled = true, VolcTranslateEnabled = false, BaiduTranslateEnabled = false, TranslateNearCursor = true, TranslateTopmost = false, TranslateHideOnBlur = false, TranslateAutoHeight = false, TranslateWindowHeight = 600 };
            using (var handler = new FakeTranslation())
            using (var api = new CloudApi(handler))
            using (var app = new DesktopApp(settings, true, persistSettings: delegate { }))
            using (var fixture = new Form { FormBorderStyle = FormBorderStyle.None, BackColor = Color.Lime, StartPosition = FormStartPosition.Manual, Bounds = Screen.PrimaryScreen.Bounds, ShowInTaskbar = false })
            {
                Field<CloudApi>(app, "api").Dispose(); typeof(DesktopApp).GetField("api", Fields).SetValue(app, api);
                var main = Field<MainForm>(app, "main");
                var input = new RichTextBox { Bounds = new Rectangle(30, 30, 450, 100), Text = "Synthetic selected text for hotkey presentation." };
                fixture.Controls.Add(input);
                fixture.Shown += async delegate
                {
                    try
                    {
                        for (int pass = 0; pass < 8; pass++)
                        {
                            if (pass == 4) { settings.TranslateAutoHeight = true; main.RefreshSettings(settings); }
                            main.Hide(); Native.SetForegroundWindow(fixture.Handle); fixture.Activate(); input.Focus();
                            input.Text = "Synthetic selected text " + pass; input.SelectAll(); Cursor.Position = new Point(100 + pass * 50, 180);
                            await Task.Delay(80);
                            if (Native.GetForegroundWindow() != fixture.Handle) throw new InvalidOperationException("Self-owned fixture does not own foreground; no input sent.");
                            Check(!main.Visible && main.IsHandleCreated, "pre-created translator is hidden before CommandWindow hotkey " + pass);
                            Native.PostMessage(Field<CommandWindow>(app, "commands").Handle, 0x312, new IntPtr(3), IntPtr.Zero);
                            while (!main.Visible && Field<bool>(app, "reading")) await Task.Delay(5);
                            await Task.Delay(60);
                            while (!main.Visible) { await Task.Delay(10); if (!Field<bool>(app, "reading")) throw new InvalidOperationException("Selection route returned without presentation."); }
                            Frame(main, Path.Combine(output, "hotkey-" + pass + "-first.png"));
                            await Task.Delay(90); Frame(main, Path.Combine(output, "hotkey-" + pass + "-pending.png"));
                            while (Field<bool>(app, "reading")) await Task.Delay(10);
                            await Task.Delay(60);
                            Frame(main, Path.Combine(output, "hotkey-" + pass + "-result.png"));
                            await Task.Delay(180); Frame(main, Path.Combine(output, "hotkey-" + pass + "-settled.png"));
                            Check(Field<RichTextBox>(main, "source").Text == input.Text, "actual selection route preserved selected text " + pass);
                        }
                        Check(handler.Calls == 8, "eight fake CloudApi requests; no network provider used");
                        main.Hide();
                        using (var ocr = new OcrForm { TopMost = true })
                        {
                            IntPtr unused = ocr.Handle; ocr.Present("Synthetic OCR text", true); await Task.Delay(20);
                            Frame(ocr, Path.Combine(output, "ocr-first.png")); ocr.Hide(); ocr.Present("Updated OCR text", true); await Task.Delay(20);
                            Frame(ocr, Path.Combine(output, "ocr-reshow.png"));
                        }
                        using (var options = new SettingsForm(new AppSettings { HistoryEnabled = false }, delegate { }, new HistoryStore(Path.Combine(output, "synthetic-history"))))
                            await OtherWindow(options, output, "settings");
                        using (var editor = new EditorForm(new Bitmap(240, 180)))
                            await OtherWindow(editor, output, "editor");
                        using (var pin = new PinForm(new Bitmap(240, 180), true, null, new Point(180, 220)))
                            await OtherWindow(pin, output, "pin");
                    }
                    catch (Exception ex) { Console.WriteLine(ex); failure++; }
                    finally { Cursor.Position = cursor; fixture.Close(); }
                };
                Application.Run(fixture);
            }
            return failure == 0 ? 0 : 1;
        }
        static async Task OtherWindow(RoundedForm form, string output, string label)
        {
            form.StartPosition = FormStartPosition.Manual; form.Location = new Point(80, 80);
            form.Show(); await Task.Delay(60); Frame(form, Path.Combine(output, label + "-first.png"));
            form.Hide(); form.Location = new Point(110, 110); form.Show(); await Task.Delay(60);
            Frame(form, Path.Combine(output, label + "-reshow.png"));
            int preference; int result = DwmGetWindowAttribute(form.Handle, 33, out preference, 4);
            Console.WriteLine(label + " DWM corner result=0x" + result.ToString("X8") + " preference=" + preference);
            Check(form.Region == null, label + " has no hard-edged managed window region");
        }
    }
}

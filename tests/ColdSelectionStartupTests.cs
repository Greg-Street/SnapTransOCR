using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ColdSelectionStartupTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowText(IntPtr window, string text);
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Control.CheckForIllegalCrossThreadCalls = true;
                Program.AppIcon = SystemIcons.Application;
                Action<bool, string> check = (passed, name) => { if (!passed) throw new Exception(name); Console.WriteLine("ok " + name); };
                if (Array.IndexOf(args, "document") >= 0) RunDocumentAncestor(check);
                else Run(check, Array.IndexOf(args, "copy") >= 0, Array.IndexOf(args, "blur") >= 0);
                return 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); return 1; }
        }

        internal static void Run(Action<bool, string> check, bool copy = false, bool hideOnBlur = false)
        {
            int key = AvailableKey();
            using (var handler = new TranslationHandler())
            using (var app = new DesktopApp(new AppSettings { Hotkeys = new[] { "", "", "", "Ctrl+Shift+" + (Keys)key }, HistoryEnabled = false,
                TranslateProvider = "baidu_llm", BaiduTranslateId = "synthetic", BaiduTranslateSecret = "synthetic",
                VolcTranslateEnabled = false, BaiduTranslateEnabled = false, BaiduLlmEnabled = true,
                TranslateNearCursor = true, TranslateHideOnBlur = hideOnBlur }, true, persistSettings: delegate { }))
            using (var ready = new ManualResetEvent(false))
            using (var timer = new System.Windows.Forms.Timer { Interval = 20 })
            {
                Field<CloudApi>(app, "api").Dispose();
                typeof(DesktopApp).GetField("api", Private).SetValue(app, new CloudApi(handler));
                var main = Field<MainForm>(app, "main");
                var commands = Field<CommandWindow>(app, "commands");
                SetWindowText(commands.Handle, "SnapTransOCR.ColdSelection.Commands." + Guid.NewGuid().ToString("N"));
                var reader = Field<SelectionReader>(app, "reader");
                var stalled = new TaskCompletionSource<string>();
                if (copy) typeof(SelectionReader).GetField("automation", Private).SetValue(reader, stalled.Task);
                Form fixture = null;
                IntPtr foreground = IntPtr.Zero;
                Exception failure = null;
                int received = 0;
                var watch = Stopwatch.StartNew();
                commands.Command += delegate(int id)
                {
                    if (id != 3) return;
                    received++;
                    Console.WriteLine("command: context=" + SynchronizationContext.Current + " apartment=" + Thread.CurrentThread.GetApartmentState()
                        + " reading=" + Field<bool>(app, "reading") + " visible=" + main.Visible);
                };
                main.VisibleChanged += delegate { Console.WriteLine("translator visible=" + main.Visible); };
                var fixtureThread = new Thread(delegate()
                {
                    try
                    {
                        using (fixture = new Form { Text = "SnapTransOCR cold selection fixture", ClientSize = new Size(450, 100) })
                        using (var input = new RichTextBox { Dock = DockStyle.Fill, Text = "cold startup selected text" })
                        {
                            fixture.Controls.Add(input);
                            fixture.Shown += delegate
                            {
                                Native.SetForegroundWindow(fixture.Handle); input.Focus(); input.SelectAll();
                                foreground = fixture.Handle; ready.Set();
                            };
                            Application.Run(fixture);
                        }
                    }
                    catch (Exception error) { failure = error; ready.Set(); }
                });
                fixtureThread.SetApartmentState(ApartmentState.STA);
                fixtureThread.Start();
                bool posted = false;
                timer.Tick += delegate
                {
                    try
                    {
                        if (failure != null) throw failure;
                        if (!posted && ready.WaitOne(0))
                        {
                            check(!main.Visible, "translator remains hidden before its first shortcut");
                            check(Native.GetForegroundWindow() == foreground, "independent selected-text fixture owns the foreground");
                            posted = true;
                            var inputs = new[] { Key(0x11, false), Key(0x10, false), Key((ushort)key, false), Key((ushort)key, true), Key(0x10, true), Key(0x11, true) };
                            check(Native.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(Native.INPUT))) == inputs.Length,
                                "the reserved synthetic shortcut is sent to the independent selection fixture");
                        }
                        if (handler.Calls > 0 && Field<CancellationTokenSource>(app, "job") == null)
                        {
                            check(received == 1 && handler.Calls == 1 && handler.Path == "/ait/api/aiTextTranslate",
                                "the first cold WM_HOTKEY reaches one synthetic Baidu LLM translation request");
                            check(main.Visible && Field<RichTextBox>(main, "source").Text == "cold startup selected text",
                                "the first shortcut presents the selected text without opening the translator first");
                            var cards = Field<Dictionary<string, ServiceCard>>(main, "cards");
                            check(cards["baidu_llm"].Value == "synthetic cold translation", "the first cold translation result reaches the UI thread");
                            timer.Stop(); Invoke(app, "Exit");
                        }
                        else if (watch.ElapsedMilliseconds > 8000)
                        {
                            var automation = Field<Task<string>>(reader, "automation");
                            throw new Exception("Cold shortcut did not finish: received=" + received + " requests=" + handler.Calls
                                + " reading=" + Field<bool>(app, "reading") + " foreground=" + (Native.GetForegroundWindow() == foreground)
                                + " automation=" + (automation == null ? "null" : automation.Status.ToString())
                                + " selected=" + (automation != null && automation.Status == TaskStatus.RanToCompletion ? automation.Result : "pending"));
                        }
                    }
                    catch (Exception error) { failure = error; timer.Stop(); Invoke(app, "Exit"); }
                };
                try { timer.Start(); Application.Run(app); }
                finally
                {
                    timer.Stop(); stalled.TrySetResult("");
                    if (fixture != null && !fixture.IsDisposed) fixture.BeginInvoke((Action)(() => fixture.Close()));
                    fixtureThread.Join();
                }
                if (failure != null) throw failure;
            }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static void Invoke(object owner, string name) { owner.GetType().GetMethod(name, Private).Invoke(owner, null); }
        private static Task<string> ReadAutomation(IntPtr window)
        { return (Task<string>)typeof(SelectionReader).GetMethod("ReadAutomationSelection", Private).Invoke(new SelectionReader(), new object[] { window }); }
        private static int AvailableKey()
        {
            using (var probe = new CommandWindow("SnapTransOCR.ColdSelection." + Guid.NewGuid().ToString("N")))
                for (int key = (int)Keys.F13; key <= (int)Keys.F24; key++)
                    if (Native.RegisterHotKey(probe.Handle, 3, 6, (uint)key))
                    { Native.UnregisterHotKey(probe.Handle, 3); return key; }
            throw new InvalidOperationException("No unused synthetic selection shortcut is available.");
        }
        private static Native.INPUT Key(ushort key, bool up)
        { return new Native.INPUT { Type = 1, Data = new Native.InputUnion { Keyboard = new Native.KEYBDINPUT { Key = key, Flags = up ? 2u : 0u } } }; }
        internal static void RunDocumentAncestor(Action<bool, string> check)
        {
            Exception failure = null;
            using (var fixture = new Form { Text = "SnapTransOCR document selection fixture", ClientSize = new Size(480, 160) })
            using (var document = new RichTextBox { Dock = DockStyle.Fill, Text = "synthetic document selection", HideSelection = false })
            using (var focused = new Button { Text = "Synthetic focus target", Bounds = new Rectangle(10, 70, 220, 30) })
            {
                document.Controls.Add(focused); fixture.Controls.Add(document);
                fixture.Shown += async delegate
                {
                    try
                    {
                        Native.SetForegroundWindow(fixture.Handle); document.SelectAll(); focused.Focus();
                        check(Native.GetForegroundWindow() == fixture.Handle, "the document selection fixture owns the foreground");
                        string hierarchy = await Task.Run(delegate
                        {
                            string value = ""; var node = AutomationElement.FocusedElement;
                            for (int depth = 0; node != null && depth < 12; depth++, node = TreeWalker.ControlViewWalker.GetParent(node))
                            { object pattern; value += node.Current.ControlType.ProgrammaticName + ":" + node.TryGetCurrentPattern(TextPattern.Pattern, out pattern) + " "; }
                            return value;
                        });
                        Console.WriteLine("synthetic hierarchy=" + hierarchy);
                        check(hierarchy.StartsWith("ControlType.Button:False ControlType.Document:True", StringComparison.Ordinal),
                            "the native fixture exposes a non-text focus beneath a UIA Document");
                        uint sequence = Native.GetClipboardSequenceNumber();
                        string selected = await ReadAutomation(fixture.Handle);
                        check(selected == document.Text, "a focused non-text child reads its ancestor Document selection through UI Automation");
                        check(Native.GetClipboardSequenceNumber() == sequence, "ancestor document reading leaves the clipboard unchanged");
                        document.Select(0, 0);
                        check(await new SelectionReader().ReadExplicit(fixture.Handle) == "" && Native.GetClipboardSequenceNumber() == sequence,
                            "a Document's confirmed empty selection does not trigger clipboard fallback");
                        document.SelectAll();

                        using (var editor = new RichTextBox { Text = "synthetic caret", Bounds = new Rectangle(250, 70, 150, 30) })
                        using (var password = new TextBox { UseSystemPasswordChar = true, Text = "synthetic", Bounds = new Rectangle(250, 110, 150, 25) })
                        {
                            document.Controls.Add(editor); document.Controls.Add(password);
                            editor.Focus(); editor.Select(0, 0);
                            check(await ReadAutomation(fixture.Handle) == "", "a focused text control's confirmed empty selection does not read an ancestor");
                            password.Focus(); password.SelectAll();
                            check(await ReadAutomation(fixture.Handle) == "", "a focused password field cannot expose an ancestor selection");
                            check(Native.GetClipboardSequenceNumber() == sequence, "empty and password UIA paths leave the clipboard unchanged");
                        }

                        Control container = document;
                        var panels = new List<Panel>();
                        for (int depth = 0; depth < 12; depth++)
                        {
                            var panel = new Panel { Bounds = new Rectangle(0, 0, 450, 150), AccessibleName = "synthetic container " + depth };
                            container.Controls.Add(panel); panels.Add(panel); container = panel;
                        }
                        container.Controls.Add(focused); focused.Focus();
                        check(await ReadAutomation(fixture.Handle) == null, "ancestor document lookup stops after twelve parent steps");
                        panels[10].Controls.Add(focused); focused.Focus();
                        check(await ReadAutomation(fixture.Handle) == document.Text, "a Document at the twelfth parent step remains readable");
                        fixture.Controls.Add(focused); focused.BringToFront(); focused.Focus();
                        check(await ReadAutomation(fixture.Handle) == null, "a non-text focus without a Document stops at its window boundary");
                    }
                    catch (Exception error) { failure = error; }
                    finally { fixture.Close(); }
                };
                Application.Run(fixture);
            }
            if (failure != null) throw failure;
        }
        private sealed class TranslationHandler : HttpMessageHandler
        {
            internal int Calls;
            internal string Path;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Calls++;
                Path = request.RequestUri.AbsolutePath;
                await Task.Yield();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"trans_result\":[{\"dst\":\"synthetic cold translation\"}]}") };
            }
        }
    }
}

using System;
using System.Drawing;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class SelectionRecoveryTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

        internal static void Run(Action<bool, string> check)
        {
            Exception failure = null;
            using (var clipboard = ClipboardSnapshot.Capture())
            using (var fixture = new Form { Text = "SnapTransOCR selection recovery test", ClientSize = new Size(540, 120) })
            {
                var input = new CopyTextBox { Dock = DockStyle.Fill, Text = "selected synthetic text" };
                fixture.Controls.Add(input);
                fixture.Shown += async delegate
                {
                    try
                    {
                        await Focus(fixture, input, "selected synthetic text");
                        Clipboard.SetText("selection recovery clipboard sentinel");
                        await Task.Delay(150); // Let external clipboard notification handlers finish the test's setup write.
                        var reader = new SelectionReader();
                        var stalled = new TaskCompletionSource<string>();
                        typeof(SelectionReader).GetField("automation", Private).SetValue(reader, stalled.Task);
                        try
                        {
                            uint before = Native.GetClipboardSequenceNumber();
                            string selected = await reader.TryReadExplicit(fixture.Handle);
                            if (selected != input.Text) Console.WriteLine("Selection recovery diagnostic: copies=" + input.Copies
                                + " foreground=" + (Native.GetForegroundWindow() == fixture.Handle) + " clipboard=" + before + "->" + Native.GetClipboardSequenceNumber());
                            check(selected == input.Text,
                                "a stalled accessibility provider still permits explicit clipboard selection");
                            input.Text = "second synthetic selection"; input.SelectAll();
                            check(await reader.TryReadExplicit(fixture.Handle) == input.Text,
                                "later shortcuts remain usable while the same accessibility provider is stalled");
                            check(ReferenceEquals(typeof(SelectionReader).GetField("automation", Private).GetValue(reader), stalled.Task),
                                "accessibility fallback does not queue another worker behind a stalled provider");
                            uint restoredSequence = Native.GetClipboardSequenceNumber();
                            await ClipboardContentionTests.CheckReadable(check, restoredSequence,
                                delegate { return Clipboard.GetText() == "selection recovery clipboard sentinel"; },
                                "selection recovery restores the previous clipboard");
                        }
                        finally { stalled.SetResult(""); }

                        input.Select(0, 0);
                        uint sequence = Native.GetClipboardSequenceNumber();
                        check(String.IsNullOrEmpty(await new SelectionReader().TryReadExplicit(fixture.Handle))
                            && Native.GetClipboardSequenceNumber() == sequence,
                            "confirmed empty selection remains silent without sending copy");
                        await ChangedForeground(check, fixture);
                        await ReplacePendingTranslation(check, fixture, input);
                    }
                    catch (Exception error) { failure = error; }
                    finally { fixture.Close(); }
                };
                try { Application.Run(fixture); }
                finally
                {
                    uint sequence = Native.GetClipboardSequenceNumber();
                    try
                    {
                        if (Clipboard.GetText() == "selection recovery clipboard sentinel" && Native.GetClipboardSequenceNumber() == sequence)
                        {
                            if (clipboard.Data != null) Clipboard.SetDataObject(clipboard.Data, true); else Clipboard.Clear();
                        }
                    }
                    catch (System.Runtime.InteropServices.ExternalException) { }
                }
            }
            if (failure != null) throw failure;
        }

        private static async Task ChangedForeground(Action<bool, string> check, Form fixture)
        {
            Form source = null;
            IntPtr sourceHandle = IntPtr.Zero;
            using (var ready = new ManualResetEvent(false))
            {
                var thread = new Thread(delegate()
                {
                    using (source = new Form { Text = "SnapTransOCR independent selection source", ClientSize = new Size(400, 100) })
                    using (var input = new RichTextBox { Dock = DockStyle.Fill, Text = "selection from previous foreground" })
                    {
                        source.Controls.Add(input);
                        source.Shown += delegate { sourceHandle = source.Handle; input.Focus(); input.SelectAll(); ready.Set(); };
                        Application.Run(source);
                    }
                });
                thread.SetApartmentState(ApartmentState.STA); thread.Start(); ready.WaitOne();
                try
                {
                    Native.SetForegroundWindow(sourceHandle); await Task.Delay(100);
                    if (Native.GetForegroundWindow() != sourceHandle) throw new InvalidOperationException("Independent synthetic source did not receive focus.");
                    var reader = new SelectionReader();
                    Task<string> selection = reader.ReadExplicit(sourceHandle);
                    var automation = (Task<string>)typeof(SelectionReader).GetField("automation", Private).GetValue(reader);
                    // The provider runs on another STA. Hold this UI continuation until its result is ready,
                    // then change foreground before ReadExplicit is allowed to consume that result.
                    for (int attempt = 0; attempt < 150 && !automation.IsCompleted; attempt++) Thread.Sleep(10);
                    check(automation.IsCompleted && automation.Result == "selection from previous foreground",
                        "the independent provider finishes reading the previous foreground selection");
                    fixture.Activate(); Native.SetForegroundWindow(fixture.Handle);
                    check(Native.GetForegroundWindow() == fixture.Handle, "foreground switches before the completed selection is consumed");
                    check(String.IsNullOrEmpty(await selection), "a completed accessibility result is discarded after foreground changes");
                }
                finally { source.BeginInvoke((Action)(() => source.Close())); thread.Join(); }
            }
        }

        private static async Task ReplacePendingTranslation(Action<bool, string> check, Form fixture, RichTextBox input)
        {
            using (var handler = new PendingTranslation())
            using (var api = new CloudApi(handler))
            using (var app = new DesktopApp(new AppSettings { Hotkeys = new[] { "", "", "", "" }, HistoryEnabled = false,
                TranslateProvider = "baidu", BaiduTranslateId = "synthetic", BaiduTranslateSecret = "synthetic",
                VolcTranslateEnabled = false, BaiduTranslateEnabled = true, TranslateNearCursor = false }, true, persistSettings: delegate { }))
            {
                Field<CloudApi>(app, "api").Dispose(); typeof(DesktopApp).GetField("api", Private).SetValue(app, api);
                await Focus(fixture, input, "first pending selection");
                Task first = (Task)typeof(DesktopApp).GetMethod("SelectedTranslation", Private).Invoke(app, null);
                try
                {
                    for (int attempt = 0; attempt < 150 && handler.Calls == 0; attempt++) await Task.Delay(10);
                    check(handler.Calls == 1, "first explicit selection reaches a pending synthetic translation request");
                    check(!Field<bool>(app, "reading"), "the selection-reader guard is released while translation is waiting on the network");
                    await Focus(fixture, input, "newer selected text");
                    await (Task)typeof(DesktopApp).GetMethod("SelectedTranslation", Private).Invoke(app, null);
                    check(handler.Calls == 2 && handler.Cancelled, "a new explicit selection cancels and replaces the pending translation");
                    check(Field<RichTextBox>(Field<MainForm>(app, "main"), "source").Text == "newer selected text",
                        "the translator presents the newer selection without waiting for the older request");
                }
                finally { handler.Finish(); await first; }
            }
        }
        private static async Task Focus(Form fixture, RichTextBox input, string text)
        {
            fixture.Activate(); Native.SetForegroundWindow(fixture.Handle); input.Focus(); input.Text = text; input.SelectAll();
            await Task.Delay(100);
            if (Native.GetForegroundWindow() != fixture.Handle) throw new InvalidOperationException("Synthetic fixture did not receive foreground focus; no input is sent.");
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private sealed class CopyTextBox : RichTextBox
        {
            internal int Copies;
            protected override void WndProc(ref Message message)
            {
                if (message.Msg == 0x301) Copies++;
                base.WndProc(ref message);
            }
        }
        private sealed class PendingTranslation : HttpMessageHandler
        {
            private readonly TaskCompletionSource<HttpResponseMessage> first = new TaskCompletionSource<HttpResponseMessage>();
            internal int Calls;
            internal bool Cancelled;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                Calls++;
                if (Calls != 1) return Task.FromResult(Response());
                token.Register(delegate { Cancelled = true; first.TrySetCanceled(); });
                return first.Task;
            }
            internal void Finish() { if (!first.Task.IsCompleted) first.TrySetResult(Response()); }
            private static HttpResponseMessage Response()
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"trans_result\":[{\"dst\":\"synthetic translation\"}]}") };
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class LanguageSelectionTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        [StructLayout(LayoutKind.Sequential)] private struct ComboInfo
        {
            public int Size;
            public Native.RECT Item, Button;
            public int ButtonState;
            public IntPtr Combo, Edit, List;
        }
        [DllImport("user32.dll")] private static extern bool GetComboBoxInfo(IntPtr combo, ref ComboInfo info);
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try { Selection(check); Persistence(check); }
            finally { Ui.RenderOffscreen = offscreen; }
        }

        private static void Selection(Action<bool, string> check)
        {
            using (var main = new MainForm { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) })
            {
                main.RefreshSettings(new AppSettings { TranslateNearCursor = false, TranslateTopmost = false });
                main.Show(); main.SetOutput("retained translation");
                var from = Field<ComboBox>(main, "from"); var to = Field<ComboBox>(main, "to");
                var card = Field<Dictionary<string, ServiceCard>>(main, "cards")["volc"];
                int changes = 0; string source = "", target = "";
                main.LanguagesChanged += delegate(string first, string second) { changes++; source = first; target = second; };
                from.DroppedDown = true; Key(from, Keys.Down); Key(from, Keys.Down);
                check(from.SelectedIndex == 2 && changes == 0 && card.Value == "retained translation", "open language list navigation does not submit temporary choices or discard translations");
                Key(from, Keys.Escape);
                check(from.SelectedIndex == 0 && changes == 0 && card.Value == "retained translation" && main.Visible, "Escape restores the original language without changing translation state or hiding its window");
                from.DroppedDown = true; Key(from, Keys.Down); Key(from, Keys.Enter);
                check(changes == 1 && source == "zh" && card.Value == "", "Enter commits one language change and clears the previous translation");
                Key(from, Keys.Down);
                check(changes == 2 && source == "en", "closed language list keyboard navigation applies immediately");
                Wheel(from);
                check(changes == 3 && source == "ja", "closed language list wheel navigation applies immediately");
                to.DroppedDown = true; Key(to, Keys.Down); to.DroppedDown = false;
                check(to.SelectedIndex == 0 && changes == 3, "closing an uncommitted target-language list restores its original selection");
                Key(to, Keys.Down);
                check(changes == 4 && source == "ja" && target == "zh", "target-language keyboard selection commits the current source and target together");
                Field<GlyphButton>(main, "swap").PerformClick();
                check(changes == 5 && source == "zh" && target == "ja", "language swap submits exactly one consistent pair");
                main.RefreshSettings(new AppSettings { SourceLanguage = "fr", TargetLanguage = "de" });
                check(changes == 5 && from.SelectedIndex == 5 && to.SelectedIndex == 6, "applying settings updates both language controls without issuing extra user changes");
                Key(from, Keys.Up);
                check(changes == 6 && source == "ko" && target == "de", "keyboard selection after a settings refresh still commits immediately");
                from.DroppedDown = true; Wheel(from);
                check(changes == 6 && from.SelectedIndex == 4, "scrolling an open language list does not submit a highlighted language");
                Key(from, Keys.Escape);
                from.DroppedDown = true;
                var info = new ComboInfo { Size = Marshal.SizeOf(typeof(ComboInfo)) };
                if (!GetComboBoxInfo(from.Handle, ref info)) throw new InvalidOperationException("Synthetic combo list unavailable");
                int top = Native.SendMessage(from.Handle, 0x15b, IntPtr.Zero, IntPtr.Zero).ToInt32();
                int y = (5 - top) * from.ItemHeight + from.ItemHeight / 2;
                IntPtr point = new IntPtr((y << 16) | 12);
                Native.SendMessage(info.List, 0x201, new IntPtr(1), point);
                Native.SendMessage(info.List, 0x202, IntPtr.Zero, point);
                check(changes == 7 && source == "fr" && !from.DroppedDown, "clicking a native language list row commits exactly once");
                main.Exiting = true; main.Close();
            }
        }

        private static void Persistence(Action<bool, string> check)
        {
            string directory = Path.Combine(Path.GetTempPath(), "SnapTransOCR-language-save-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(directory, "settings.json");
            int writes = 0, active = 0, maximum = 0, writeThread = 0;
            Action<AppSettings> beforeWrite = null;
            using (var release = new ManualResetEventSlim(false))
            {
                try
                {
                    var initial = new AppSettings { Hotkeys = new[] { "", "", "", "" }, HistoryEnabled = false, TranslateNearCursor = false };
                    using (var app = new DesktopApp(initial, true, null, delegate(AppSettings value)
                    {
                        Interlocked.Increment(ref writes); int concurrent = Interlocked.Increment(ref active);
                        maximum = Math.Max(maximum, concurrent); writeThread = Thread.CurrentThread.ManagedThreadId;
                        try
                        {
                            if (beforeWrite != null) beforeWrite(value);
                            Thread.Sleep(35); // Synthetic slow flush; all writes target this test's temporary directory.
                            SettingsStore.Save(value, path);
                        }
                        finally { Interlocked.Decrement(ref active); }
                    }))
                    {
                        typeof(DesktopApp).GetField("history", Private).SetValue(app, new HistoryStore(Path.Combine(directory, "history")));
                        var main = Field<MainForm>(app, "main");
                        var from = Field<ComboBox>(main, "from"); var to = Field<ComboBox>(main, "to");
                        int uiThread = Thread.CurrentThread.ManagedThreadId;
                        Exception failure = null;
                        main.BeginInvoke((Action)(async delegate
                        {
                            try
                            {
                                var elapsed = Stopwatch.StartNew();
                                Key(from, Keys.Down); Key(from, Keys.Down); Key(from, Keys.Down); Key(from, Keys.Up);
                                Key(to, Keys.Down); Key(to, Keys.Down); elapsed.Stop();
                                check(writes == 0 && initial.SourceLanguage == "en" && initial.TargetLanguage == "en", "six rapid language changes immediately affect requests without performing synchronous disk writes");
                                Console.WriteLine("Language input batch: " + elapsed.Elapsed.TotalMilliseconds.ToString("F1") + " ms; synchronous writes: " + writes + "; injected latency: 35 ms per write");
                                await Task.Delay(550); await Field<Task>(app, "languageWrite");
                                var saved = SettingsStore.Load(path);
                                check(writes == 1 && saved.SourceLanguage == "en" && saved.TargetLanguage == "en" && writeThread != uiThread, "the quiet-period timer coalesces language changes into one background save of the final pair");

                                var started = new TaskCompletionSource<AppSettings>();
                                beforeWrite = delegate(AppSettings value) { started.TrySetResult(value.Copy()); release.Wait(); };
                                Key(from, Keys.Down); Invoke(app, "SaveLanguages");
                                AppSettings snapshot = await started.Task;
                                Task blocked = Field<Task>(app, "languageWrite");
                                await Task.Delay(30);
                                check(!blocked.IsCompleted && snapshot.SourceLanguage == "ja", "a blocked settings flush leaves the UI message loop responsive");
                                Key(from, Keys.Down); Invoke(app, "SaveLanguages");
                                check(writes == 2 && Field<AppSettings>(app, "settings").SourceLanguage == "ko" && Field<bool>(app, "languageSavePending"), "changes during an in-flight write update live settings without starting a concurrent writer");
                                release.Set(); await blocked;
                                check(SettingsStore.Load(path).SourceLanguage == "ja" && snapshot.SourceLanguage == "ja", "background writes own an isolated snapshot that later language choices cannot mutate");
                                Invoke(app, "SaveLanguages"); await Field<Task>(app, "languageWrite");
                                check(writes == 3 && SettingsStore.Load(path).SourceLanguage == "ko", "the pending final choice is saved after the older write finishes");

                                release.Reset(); started = new TaskCompletionSource<AppSettings>();
                                Key(from, Keys.Down); Invoke(app, "SaveLanguages"); await started.Task;
                                var next = Field<AppSettings>(app, "settings").Copy();
                                next.TargetLanguage = "es"; next.TranslateWindowWidth = 640; next.VolcId = "synthetic-id";
                                Task unblock = Task.Run(delegate { Thread.Sleep(30); release.Set(); });
                                Invoke(app, "ApplySettings", next); await unblock;
                                saved = SettingsStore.Load(path);
                                check(saved.SourceLanguage == "fr" && saved.TargetLanguage == "es" && saved.TranslateWindowWidth == 640 && saved.VolcId == "synthetic-id", "explicit settings save follows an in-flight language write and preserves the full new configuration");
                                int explicitWrites = writes;
                                await Task.Delay(450);
                                check(writes == explicitWrites && !Field<bool>(app, "languageSavePending"), "an old quiet-period timer cannot overwrite an explicit settings save");

                                beforeWrite = delegate { throw new IOException("Synthetic language write failure"); };
                                Key(from, Keys.Down); Invoke(app, "SaveLanguages");
                                try { await Field<Task>(app, "languageWrite"); } catch (IOException) { }
                                await Task.Delay(30);
                                check(Field<bool>(app, "languageSavePending") && Field<Label>(main, "status").Text.Contains("语言设置未保存"), "background write failure keeps the latest language pending and reports the failed save");
                                beforeWrite = null; Invoke(app, "SaveSettings", Field<AppSettings>(app, "settings"));
                                check(!Field<bool>(app, "languageSavePending") && SettingsStore.Load(path).SourceLanguage == "de", "an explicit retry recovers a failed background save without losing the chosen language");

                                release.Reset(); started = new TaskCompletionSource<AppSettings>();
                                beforeWrite = delegate(AppSettings value) { started.TrySetResult(value.Copy()); release.Wait(); };
                                Key(from, Keys.Down); Invoke(app, "SaveLanguages"); await started.Task;
                                Key(from, Keys.Down);
                                unblock = Task.Run(delegate { Thread.Sleep(30); release.Set(); });
                                Invoke(app, "Exit"); await unblock;
                                saved = SettingsStore.Load(path);
                                check(saved.SourceLanguage == "es" && saved.TargetLanguage == "es" && saved.VolcId == "synthetic-id" && Field<Task>(app, "languageWrite").IsCompleted, "exit joins an in-flight write and flushes the final pending choice without a UI-thread deadlock");
                                check(maximum == 1 && Directory.GetFiles(directory, "*.tmp").Length == 0, "all automatic, explicit, retry, and exit saves remain serial and leave no temporary files");
                            }
                            catch (Exception ex) { failure = ex; }
                            finally { release.Set(); beforeWrite = null; Invoke(app, "Exit"); }
                        }));
                        Application.Run(app);
                        if (failure != null) throw failure;
                    }
                }
                finally
                {
                    release.Set();
                    if (Directory.Exists(directory))
                    {
                        foreach (string file in Directory.GetFiles(directory)) File.Delete(file);
                        string history = Path.Combine(directory, "history");
                        if (Directory.Exists(history)) Directory.Delete(history);
                        Directory.Delete(directory);
                    }
                }
            }
        }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static void Invoke(DesktopApp owner, string name, params object[] arguments) { typeof(DesktopApp).GetMethod(name, Private).Invoke(owner, arguments); }
        private static void Key(ComboBox combo, Keys key) { Native.SendMessage(combo.Handle, 0x100, new IntPtr((int)key), IntPtr.Zero); }
        private static void Wheel(ComboBox combo) { Native.SendMessage(combo.Handle, 0x20a, new IntPtr(unchecked((int)0xff880000)), IntPtr.Zero); }
    }
}

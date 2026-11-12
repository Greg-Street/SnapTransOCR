using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ArkParallelTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Fast = "deepseek-v4-1-flash-261017", Other = "kimi-k3-1-pro-261024", Primary = "doubao-seed-translation-250915";
        private sealed class Pending
        {
            internal string Model, Key;
            internal TaskCompletionSource<HttpResponseMessage> Completion = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private sealed class Handler : HttpMessageHandler
        {
            internal ConcurrentQueue<Pending> Requests = new ConcurrentQueue<Pending>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                var body = Json.Object(await request.Content.ReadAsStringAsync());
                var pending = new Pending { Model = Json.Text(body, "model"), Key = request.Headers.Authorization.Parameter };
                Requests.Enqueue(pending);
                using (cancellation.Register(() => pending.Completion.TrySetCanceled())) return await pending.Completion.Task;
            }
        }
        private static AppSettings Settings()
        {
            return new AppSettings { TranslateProvider = "ark", ArkEnabled = true, ArkApiKey = "synthetic-ark-key", ArkModel = Primary,
                ArkExtraModels = new[] { Fast, Other }, HistoryEnabled = false, Hotkeys = new[] { "", "", "", "" },
                BaiduTranslateEnabled = false, VolcTranslateEnabled = false, BingTranslateEnabled = false };
        }
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try { ConfigAndSettings(check); ApplicationFlow(check); Layouts(check); }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static void ConfigAndSettings(Action<bool, string> check)
        {
            var settings = Settings();
            check(settings.TranslationRequests().SequenceEqual(new[] { "ark", "ark:" + Fast, "ark:" + Other }), "parallel Ark requests include the default and each explicitly enabled extra model");
            settings.ArkExtraModels = new[] { Primary, Fast, Fast, "", Other };
            check(settings.ExtraArkModels().SequenceEqual(new[] { Fast, Other }), "parallel selection removes duplicate, blank and default model IDs");
            var copy = settings.Copy(); copy.ArkExtraModels[1] = "different";
            check(settings.ArkExtraModels[1] == Fast, "an in-flight settings snapshot owns its model selection array");
            var selected = settings.ForTranslation("ark:" + Fast);
            check(selected.TranslateProvider == "ark" && selected.ArkModel == Fast && selected.ArkApiKey == settings.ArkApiKey && selected.ArkApiMode == "auto", "each parallel model uses its own protocol and the shared Ark API key");
            settings.ArkEnabled = false;
            bool rejected = false; try { settings.ForTranslation("ark:" + Fast); } catch (InvalidOperationException) { rejected = true; }
            check(rejected, "disabled Ark profiles are rejected before contacting the service");
            settings = Settings(); settings.TranslateProvider = "baidu";
            check(settings.TranslationRequests().SequenceEqual(new[] { "baidu" }), "selecting a different translation service does not silently fan out to Ark");
            settings = Settings(); settings.ArkExtraModels = null;
            check(settings.TranslationRequests().Length == 1, "existing single-model configurations stay single-model until extras are enabled");
            string path = Path.Combine(Path.GetTempPath(), "SnapTransOCR-parallel-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                settings = Settings(); settings.NormalizeHotkeys(); SettingsStore.Save(settings, path);
                var loaded = SettingsStore.Load(path);
                check(loaded.ArkExtraModels.SequenceEqual(settings.ArkExtraModels) && loaded.ArkApiKey == settings.ArkApiKey && loaded.Hotkeys.SequenceEqual(settings.Hotkeys), "parallel selection round-trips without modifying credentials or shortcuts");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
            AppSettings saved = null;
            using (var form = new SettingsForm(Settings(), next => saved = next))
            {
                var models = Field<ArkModelSelection>(form, "arkModels"); var checks = Field<Dictionary<string, ToggleSwitch>>(models, "checks");
                check(checks[Primary].Checked && !checks[Primary].Enabled && checks[Fast].Checked, "model selection visibly includes the default and independently toggles additional models");
                checks[Other].Checked = false;
                Invoke(form, "Save");
                check(saved != null && saved.ArkExtraModels.SequenceEqual(new[] { Fast }) && saved.ArkModel == Primary && saved.ArkApiKey == "synthetic-ark-key", "saving only enables checked extras and reuses the existing key");
                var combo = Field<ComboBox>(form, "arkModel"); combo.SelectedIndex = Array.IndexOf((string[])combo.Tag, Fast);
                check(checks[Fast].Checked && !checks[Fast].Enabled && !checks[Primary].Checked && checks[Primary].Enabled, "changing the default unlocks the previous default and never enables it as an extra automatically");
                form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.Show();
                form.SelectPage("翻译设置"); form.PerformLayout();
                check(models.Width <= models.Parent.ClientSize.Width && models.Height == Ui.Scale(models, 210), "parallel-model selection fits the settings card with a bounded scrollable height: " + models.Size + " dpi=" + models.DeviceDpi);
                Field<Panel>(form, "body").AutoScrollPosition = new Point(0, Math.Max(0, models.Parent.Parent.Top));
                using (var image = new Bitmap(form.Width, form.Height))
                { form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Output("ark-model-settings.png")); }
            }
        }
        private static void ApplicationFlow(Action<bool, string> check)
        {
            using (var handler = new Handler())
            using (var app = new DesktopApp(Settings(), true, persistSettings: delegate { }))
            {
                Field<CloudApi>(app, "api").Dispose();
                typeof(DesktopApp).GetField("api", Private).SetValue(app, new CloudApi(handler));
                var main = Field<MainForm>(app, "main"); Exception failure = null;
                main.BeginInvoke((Action)(async delegate
                {
                    try { await Exercise(app, handler, check); }
                    catch (Exception ex) { failure = ex; }
                    finally { Invoke(app, "Exit"); }
                }));
                Application.Run(app);
                if (failure != null) throw failure;
            }
        }
        private static void Layouts(Action<bool, string> check)
        {
            using (var selection = new ArkModelSelection(new[] { "ep-custom-extra" }, Primary))
            {
                var checks = Field<Dictionary<string, ToggleSwitch>>(selection, "checks");
                check(checks.ContainsKey("ep-custom-extra") && checks["ep-custom-extra"].Checked, "an existing custom extra model is visible instead of being silently called from a hidden selection");
                checks["ep-custom-extra"].Checked = false;
                check(!selection.SelectedModels.Contains("ep-custom-extra"), "custom extra model rows can be disabled like catalog models");
            }
            foreach (int dpi in new[] { 96, 144, 192, 240 })
            {
                var fonts = new List<Font>();
                try
                {
                    using (var form = new SettingsForm(Settings(), delegate { }))
                    {
                        SettingsLayoutTests.SimulateDpi(form, dpi, fonts);
                        form.ClientSize = new Size(Ui.Scale(form, 860), Ui.Scale(form, 580)); form.SelectPage("翻译设置"); form.PerformLayout();
                        var models = Field<ArkModelSelection>(form, "arkModels");
                        check(models.Width <= models.Parent.ClientSize.Width && models.Height == Ui.Scale(models, 210) && !models.HorizontalScroll.Visible,
                            "model selection retains bounded height without horizontal scrolling at " + dpi + " DPI size=" + models.Size + " effective=" + models.DeviceDpi + " parent=" + models.Parent.ClientSize + " horizontal=" + models.HorizontalScroll.Visible);
                    }
                }
                finally { foreach (var font in fonts) font.Dispose(); }
            }
        }
        private static async Task Exercise(DesktopApp app, Handler handler, Action<bool, string> check)
        {
            var main = Field<MainForm>(app, "main"); var cards = Field<Dictionary<string, ServiceCard>>(main, "cards");
            Task translating = (Task)Invoke(app, "Translate", "parallel source", null);
            await Until(() => handler.Requests.Count == 3);
            var requests = handler.Requests.ToArray();
            check(!translating.IsCompleted && requests.Select(request => request.Model).Distinct().Count() == 3 && requests.All(request => request.Key == "synthetic-ark-key"), "three different models enter HTTP concurrently with the same key before any response completes");
            Finish(requests.Single(request => request.Model == Fast), "快速译文");
            await Until(() => cards["ark:" + Fast].Value == "快速译文");
            check(!cards["ark:" + Fast].IsNotice && cards["ark"].IsNotice && !translating.IsCompleted && Field<bool>(main, "busy"), "a fast model displays its own complete translation without waiting for the slow model");
            requests.Single(request => request.Model == Other).Completion.SetResult(new HttpResponseMessage((HttpStatusCode)429) { Content = new StringContent("{\"error\":{\"code\":\"QuotaExceeded\",\"message\":\"PRIVATE_ERROR_DO_NOT_SHOW\"}}") });
            await Until(() => cards["ark:" + Other].IsNotice && cards["ark:" + Other].Value.Contains("QuotaExceeded"));
            check(cards["ark:" + Fast].Value == "快速译文" && !Field<GlyphButton>(cards["ark:" + Other], "copy").Enabled && !cards["ark:" + Other].Value.Contains("PRIVATE_ERROR"), "one model failure stays in its own card, disables copying the error and leaves successful results usable");
            Finish(requests.Single(request => request.Model == Primary), "默认模型译文"); await translating;
            check(cards["ark"].Value == "默认模型译文" && cards["ark:" + Fast].Value == "快速译文" && !Field<bool>(main, "busy"), "out-of-order completions keep the correct model labels and end busy state after the group finishes");
            using (var image = new Bitmap(main.Width, main.Height))
            { main.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Output("ark-parallel-results.png")); }
            int before = handler.Requests.Count;
            Task retry = (Task)Invoke(app, "Translate", "retry source", "ark:" + Other);
            await Until(() => handler.Requests.Count == before + 1);
            var retried = handler.Requests.Last(); Finish(retried, "单独重试译文"); await retry;
            check(retried.Model == Other && handler.Requests.Count == before + 1, "retrying one result card requests only that model");
            before = handler.Requests.Count;
            Task cancelled = (Task)Invoke(app, "Translate", "cancel source", null);
            await Until(() => handler.Requests.Count == before + 3); Invoke(app, "Cancel"); await cancelled;
            check(!Field<bool>(main, "busy") && handler.Requests.Skip(before).All(request => request.Completion.Task.IsCanceled), "the stop action cancels every in-flight model in the group");
            before = handler.Requests.Count;
            Task older = (Task)Invoke(app, "Translate", "older source", null); await Until(() => handler.Requests.Count == before + 3);
            Task newer = (Task)Invoke(app, "Translate", "newer source", null); await Until(() => handler.Requests.Count == before + 6);
            foreach (var request in handler.Requests.Skip(before + 3)) Finish(request, "新任务译文");
            await Task.WhenAll(older, newer);
            check(cards["ark"].Value == "新任务译文" && cards["ark:" + Fast].Value == "新任务译文" && cards["ark:" + Other].Value == "新任务译文", "starting a new source cancels the old group and prevents stale responses from overwriting any card");
            main.SetSource("The error bars indicate the standard deviation, not the standard error of the mean.");
            main.SetOutput("误差条表示标准偏差，而非均值的标准误差。", "ark");
            main.SetOutput("误差条表示标准差，而不是均值标准误。", "ark:" + Fast);
            main.SetOutput("误差棒表示标准差，而不是均值的标准误。", "ark:" + Other);
            using (var image = new Bitmap(main.Width, main.Height))
            { main.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size)); image.Save(Output("ark-parallel-preview.png")); }
            var removed = cards["ark:" + Fast]; var settings = Settings(); settings.ArkExtraModels = new string[0]; main.RefreshSettings(settings);
            check(removed.IsDisposed && !cards.ContainsKey("ark:" + Fast) && cards.ContainsKey("ark"), "disabling an extra model releases its card while retaining the original service cards");
        }
        private static void Finish(Pending request, string translation)
        {
            string body = request.Model == Primary
                ? Json.Write(new { status = "completed", output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = translation } } } } })
                : Json.Write(new { choices = new[] { new { finish_reason = "stop", message = new { content = translation } } } });
            request.Completion.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
        private static async Task Until(Func<bool> condition)
        { DateTime limit = DateTime.UtcNow.AddSeconds(5); while (!condition()) { if (DateTime.UtcNow > limit) throw new InvalidOperationException("Parallel translation did not reach the expected state."); await Task.Delay(10); } }
        private static string Output(string name) { return Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "test-output", name)); }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
        private static object Invoke(object owner, string name, params object[] args) { return owner.GetType().GetMethod(name, Private).Invoke(owner, args); }
    }
}

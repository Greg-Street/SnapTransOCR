using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class BingUiTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var legacy = Json.Read<AppSettings>("{\"TranslateServiceOrder\":[\"baidu\",\"volc\"],\"TranslateProvider\":\"baidu\"}");
            legacy.NormalizeProviders();
            check(!legacy.BingTranslateEnabled && legacy.TranslateProvider == "baidu" && legacy.TranslateServiceOrder.SequenceEqual(new[] { "baidu", "volc", "bing", "ark", "baidu_llm", "baidu_domain" }), "old settings append disabled services without changing the chosen translator");
            AppSettings saved = null;
            using (var form = new SettingsForm(new AppSettings(), value => saved = value))
            {
                var switches = Field<Dictionary<string, ToggleSwitch>>(form, "serviceChecks");
                var order = Field<ServiceOrderControl>(form, "translationOrder");
                var selector = Field<ComboBox>(form, "translator");
                check(order.Items.Count == 6 && Codes(selector).SequenceEqual(new[] { "volc", "baidu" }) && !switches["BingTranslateEnabled"].Checked, "disabled Bing remains sortable but absent from the default translator choices");
                switches["BingTranslateEnabled"].Checked = true;
                check(Codes(selector).SequenceEqual(new[] { "volc", "baidu", "bing" }) && selector.SelectedItem.ToString() == "火山引擎翻译", "enabling Bing adds its choice without changing the existing default");
                selector.SelectedIndex = Array.IndexOf(Codes(selector), "bing");
                order.SelectedIndex = 2; order.MoveSelected(-2);
                check(order.Order.SequenceEqual(new[] { "bing", "volc", "baidu", "ark", "baidu_llm", "baidu_domain" }) && Codes(selector).SequenceEqual(new[] { "bing", "volc", "baidu" }) && selector.SelectedItem.ToString() == "必应翻译", "moving Bing to the first row preserves its selected default identity and omits disabled providers");
                var page = Field<Dictionary<string, FlowLayoutPanel>>(form, "pages")["翻译设置"];
                check(page.Controls.Cast<Control>().Where(control => control.Tag is string).Select(control => (string)control.Tag).Distinct().SequenceEqual(new[] { "bing", "volc", "baidu", "ark" }), "Bing service settings follow the display order while Baidu modes share one settings group");
                Invoke(form, "Save", null);
                check(saved != null && saved.BingTranslateEnabled && saved.TranslateProvider == "bing" && saved.TranslateServiceOrder.SequenceEqual(order.Order), "saving retains the enabled Bing default and its display order");
            }
            saved = Json.Read<AppSettings>(Json.Write(saved)); saved.NormalizeProviders();
            bool credentialsRequired = false;
            try { saved.RequireTranslation(); } catch (InvalidOperationException) { credentialsRequired = true; }
            check(!credentialsRequired && String.IsNullOrEmpty(saved.VolcId) && String.IsNullOrEmpty(saved.BaiduTranslateId), "enabled Bing validates without requiring another provider's API credentials");
            using (var reopened = new SettingsForm(saved, delegate { }))
            {
                var selector = Field<ComboBox>(reopened, "translator");
                check(selector.SelectedItem.ToString() == "必应翻译" && Codes(selector).SequenceEqual(saved.TranslateServiceOrder.Where(saved.IsTranslationEnabled)), "serialized Bing settings reopen with the same enabled order and default");
                var switches = Field<Dictionary<string, ToggleSwitch>>(reopened, "serviceChecks");
                switches["BingTranslateEnabled"].Checked = false;
                check(Codes(selector).SequenceEqual(new[] { "volc", "baidu" }) && selector.SelectedItem.ToString() == "火山引擎翻译", "disabling the Bing default selects the first remaining enabled translator");
                switches["VolcTranslateEnabled"].Checked = false; switches["BaiduTranslateEnabled"].Checked = false;
                check(!selector.Enabled && Codes(selector).Length == 0, "disabling all three translators disables the default selector");
                Invoke(reopened, "Save", null);
                check(reopened.Value.TranslateProvider == "" && !reopened.Value.BingTranslateEnabled, "all-disabled translation settings save without retaining a stale Bing default");
                switches["BingTranslateEnabled"].Checked = true;
                Invoke(reopened, "Save", null);
                check(Codes(selector).SequenceEqual(new[] { "bing" }) && reopened.Value.TranslateProvider == "bing", "enabling only Bing restores a usable default selector");
            }
            using (var main = new MainForm())
            using (var ocr = new OcrForm())
            {
                var cards = Field<Dictionary<string, ServiceCard>>(main, "cards");
                check(cards.ContainsKey("bing") && !Field<HashSet<string>>(main, "enabledProviders").Contains("bing"), "the translation window creates Bing's card without enabling it on startup");
                main.RefreshSettings(saved); main.SetSource("synthetic text");
                string request = null;
                main.ServiceTranslateRequested += (text, provider) => request = provider + ":" + text;
                Field<Action>(cards["bing"], "HeaderClick")();
                check(request == "bing:synthetic text", "Bing's result header requests only the selected Bing service");
                main.SetActiveProvider("bing"); main.SetOutput("synthetic result");
                check(cards["bing"].Value == "synthetic result" && cards["bing"].Top < cards["volc"].Top && cards["volc"].Top < cards["baidu"].Top, "Bing receives its own result and appears in the chosen three-service order");
                ocr.RefreshSettings(saved); ocr.SetSource("recognized text");
                var translate = Field<Button>(ocr, "translate");
                string recognized = null; ocr.TranslateRequested += text => recognized = text;
                Invoke(translate, "OnClick", new object[] { EventArgs.Empty });
                check(translate.Enabled && recognized == "recognized text", "OCR can forward recognized text when Bing is the selected translator");
                ocr.Busy(true); check(!translate.Enabled, "OCR disables forwarding during recognition even when Bing is enabled");
                ocr.Busy(false); check(translate.Enabled, "OCR restores forwarding after recognition with Bing enabled");
                var disabled = saved.Copy();
                disabled.BingTranslateEnabled = disabled.VolcTranslateEnabled = disabled.BaiduTranslateEnabled = false;
                disabled.NormalizeProviders(); main.RefreshSettings(disabled); ocr.RefreshSettings(disabled);
                request = null; Field<Action>(cards["bing"], "HeaderClick")();
                check(request == null && !Field<GlyphButton>(main, "translate").Enabled && !translate.Enabled, "all-disabled services prevent both Bing card requests and OCR forwarding");
                bool rejected = false;
                try { disabled.RequireTranslation(); } catch (InvalidOperationException) { rejected = true; }
                check(rejected, "disabled Bing cannot bypass the translation availability check");
            }
        }
        private static string[] Codes(ComboBox combo) { return (string[])combo.Tag; }
        private static T Field<T>(object instance, string name) { return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance); }
        private static void Invoke(object instance, string name, object[] arguments) { instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(instance, arguments); }
    }
}

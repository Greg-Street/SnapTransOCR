using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ExtendedServiceSettingsTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            ModelAndServiceUi(Configuration(check), check);
        }
        private static AppSettings Configuration(Action<bool, string> check)
        {
            var legacy = Json.Read<AppSettings>("{\"TranslateProvider\":\"baidu\",\"TranslateServiceOrder\":[\"baidu\",\"volc\"],\"BaiduTranslateEnabled\":false}");
            check(!legacy.ArkEnabled && !legacy.BaiduLlmEnabled && !legacy.BaiduDomainEnabled && !legacy.BaiduImageTranslateEnabled &&
                !legacy.BaiduQrEnabled && !legacy.BaiduHandwritingEnabled && !legacy.BaiduTableEnabled && !legacy.TencentTableEnabled, "legacy settings leave all added paid capabilities disabled");
            check(legacy.ArkModel == "doubao-seed-translation-250915" && legacy.ArkApiMode == "auto" && legacy.BaiduDomain == "academic" && legacy.ImageTranslateProvider == "pipeline" && legacy.BaiduImageTargetLanguage == "zh", "legacy settings receive explicit model, protocol, domain and image strategy defaults");
            legacy.NormalizeProviders();
            check(!legacy.BaiduTranslateEnabled && legacy.TranslateProvider == "volc" && legacy.TranslateServiceOrder.SequenceEqual(new[] { "baidu", "volc", "bing", "ark", "baidu_llm", "baidu_domain" }), "migration preserves existing flags and appends new services once");
            var legacyProtocol = Json.Read<AppSettings>("{\"ArkApiMode\":null}"); legacyProtocol.NormalizeProviders();
            check(legacyProtocol.ArkApiMode == "auto", "normalization restores automatic protocol selection for an unspecified Ark mode");
            var settings = new AppSettings
            {
                ArkEnabled = true, ArkApiKey = "test-ark", ArkModel = "ep-custom-text-model", ArkApiMode = "chat", BaiduLlmEnabled = true,
                BaiduLlmApiKey = "test-llm", BaiduLlmInstruction = "保留公式", BaiduDomainEnabled = true, BaiduDomain = "it",
                BaiduImageTranslateEnabled = true, ImageTranslateProvider = "baidu_image", BaiduImageTargetLanguage = "en", BaiduQrEnabled = true,
                BaiduHandwritingEnabled = true, BaiduTableEnabled = true, TencentTableEnabled = true, BaiduTranslateId = "test-id", BaiduTranslateSecret = "test-secret"
            };
            var copy = settings.Copy();
            check(SameSettings(copy, settings) && !ReferenceEquals(copy.TranslateServiceOrder, settings.TranslateServiceOrder), "settings snapshot includes all service configuration and owns its provider order");
            string file = Path.Combine(Path.GetTempPath(), "SnapTransOCR-services-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                SettingsStore.Save(settings, file); var restored = SettingsStore.Load(file);
                check(SameSettings(settings, restored), "all added service settings persist through file save and load");
            }
            finally { if (File.Exists(file)) File.Delete(file); }
            foreach (string code in new[] { "ark", "baidu_llm", "baidu_domain" })
            {
                copy = settings.Copy(); copy.TranslateProvider = code; copy.RequireTranslation();
                check(copy.IsTranslationEnabled(code) && AppSettings.TranslationName(code) != AppSettings.TranslationName("bing"), "new provider is independently enabled and named: " + code);
                typeof(AppSettings).GetProperty(AppSettings.TranslationFlags[Array.IndexOf(AppSettings.TranslationProviders, code)]).SetValue(copy, false, null);
                check(Throws(copy.RequireTranslation), "disabled provider fails validation before request: " + code);
            }
            copy = settings.Copy(); copy.TranslateProvider = "ark"; copy.ArkApiKey = "";
            check(Throws(copy.RequireTranslation), "Ark requires its own key even when other keys exist");
            copy.ArkApiKey = "test-ark"; copy.ArkModel = " ";
            check(Throws(copy.RequireTranslation), "Ark rejects an empty model without selecting a different provider");
            copy.ArkModel = "ep-custom-seed"; copy.ArkApiMode = "seed"; copy.RequireTranslation();
            copy.ArkApiMode = "unsupported";
            check(Throws(copy.RequireTranslation), "Ark rejects unknown API protocols before requests");
            copy = settings.Copy(); copy.TranslateProvider = "baidu_llm"; copy.BaiduLlmApiKey = ""; copy.RequireTranslation();
            copy.BaiduTranslateSecret = "";
            check(Throws(copy.RequireTranslation), "Baidu LLM requires either its optional API key or shared translation secret");
            copy.BaiduLlmApiKey = "test-llm"; copy.RequireTranslation();
            copy.BaiduTranslateId = "";
            check(Throws(copy.RequireTranslation), "Baidu LLM API key mode still requires its translation application ID");
            copy = settings.Copy(); copy.TranslateProvider = "baidu_domain"; copy.BaiduDomain = "unsupported";
            check(Throws(copy.RequireTranslation), "domain translation rejects unsupported domain codes");
            return settings;
        }
        private static void ModelAndServiceUi(AppSettings settings, Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            try
            {
                AppSettings saved = null;
                using (var form = new SettingsForm(settings, value => saved = value))
                {
                    form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-32000, -32000);
                    form.Show(); form.SelectPage("翻译设置"); Application.DoEvents();
                    var model = Field<ComboBox>(form, "arkModel");
                    var customModel = Field<Dictionary<string, TextBox>>(form, "fields")["ArkModel"];
                    var protocol = Field<ComboBox>(form, "arkApiMode");
                    check(protocol.SelectedIndex == 2 && protocol.Items.Count == 3, "Ark restores an explicit Chat protocol independently of a custom endpoint ID");
                    var selector = Field<ComboBox>(form, "translator");
                    var switches = Field<Dictionary<string, ToggleSwitch>>(form, "serviceChecks");
                    var imageProvider = Field<ComboBox>(form, "imageTranslator");
                    var imageLanguage = Field<ComboBox>(form, "imageTarget");
                    check(imageLanguage.SelectedIndex == 1 && imageLanguage.Enabled && imageLanguage.Items.Count == 8, "Baidu image translation restores a fixed target language independently of smart text translation");
                    check(model.DropDownStyle == ComboBoxStyle.DropDownList && model.SelectedIndex == model.Items.Count - 1 && customModel.Text == settings.ArkModel && ((string[])model.Tag).Contains("doubao-seed-translation-250915") && ((string[])model.Tag).Contains("doubao-seed-2-1-lite-260915"), "Ark model selector offers current translation and text presets while preserving a separate custom model ID");
                    customModel.Text = "ep-new-custom-model";
                    check(model.AccessibleDescription == "ep-new-custom-model", "editable model field exposes its full untruncated model ID");
                    protocol.SelectedIndex = 1;
                    switches["ArkEnabled"].Checked = false; switches["ArkEnabled"].Checked = true;
                    var order = Field<ServiceOrderControl>(form, "translationOrder"); order.SelectedIndex = Array.IndexOf(order.Order, "baidu_domain"); order.MoveSelected(-1);
                    selector.SelectedIndex = Array.IndexOf((string[])selector.Tag, "baidu_domain");
                    imageProvider.SelectedIndex = 0;
                    check(imageProvider.Enabled && !imageLanguage.Visible, "choosing the OCR pipeline hides the Baidu-only target language");
                    typeof(SettingsForm).GetMethod("Save", Private).Invoke(form, null);
                    check(saved.ArkModel == "ep-new-custom-model" && saved.ArkApiMode == "seed" && saved.TranslateProvider == "baidu_domain" && !saved.BaiduImageTranslateEnabled && saved.ImageTranslateProvider == "pipeline" && saved.BaiduImageTargetLanguage == "en", "saving preserves custom model/protocol, independent domain choice and the explicitly selected image route");
                    check(((string[])selector.Tag).Distinct().Count() == selector.Items.Count && ((string[])selector.Tag).SequenceEqual(order.Order.Where(saved.IsTranslationEnabled)), "provider selector stays unique and ordered after toggle and sort");
                    var translationPage = Field<Dictionary<string, FlowLayoutPanel>>(form, "pages")["翻译设置"];
                    Control imageCard = imageProvider;
                    while (imageCard.Parent != translationPage) imageCard = imageCard.Parent;
                    int imageIndex = translationPage.Controls.GetChildIndex(imageCard);
                    check(imageIndex > 0 && translationPage.Controls[imageIndex - 1] is Label && translationPage.Controls[imageIndex - 1].Text == "图片翻译", "sorting services keeps the untagged image translation card next to its own section heading");
                    imageProvider.SelectedIndex = 0;
                    typeof(SettingsForm).GetMethod("Save", Private).Invoke(form, null);
                    check(!saved.BaiduImageTranslateEnabled && saved.ImageTranslateProvider == "pipeline" && !imageLanguage.Visible, "the selected OCR pipeline saves without enabling Baidu image requests");
                    form.ClientSize = new Size(860, 580); form.SelectPage("翻译设置"); form.PerformLayout();
                    check(model.Right <= model.Parent.ClientSize.Width && order.Height == order.Items.Count * order.ItemHeight, "model selector and expanded service order fit the narrow settings layout");
                }
                using (var reopened = new SettingsForm(saved, delegate { }))
                    check(Field<Dictionary<string, TextBox>>(reopened, "fields")["ArkModel"].Text == saved.ArkModel && Field<ComboBox>(reopened, "arkModel").SelectedIndex == TranslationModels.Ark.Length && Field<ComboBox>(reopened, "arkApiMode").SelectedIndex == 1 && Field<ComboBox>(reopened, "translator").SelectedItem.ToString() == AppSettings.TranslationName("baidu_domain"), "custom model, explicit protocol and domain service choice survive reopening");
                using (var form = new SettingsForm(settings, value => saved = value))
                {
                    var toggles = Field<Dictionary<string, ToggleSwitch>>(form, "toggles");
                    string[] addedFlags = { "ArkEnabled", "BaiduLlmEnabled", "BaiduDomainEnabled", "BaiduQrEnabled", "BaiduHandwritingEnabled", "BaiduTableEnabled", "TencentTableEnabled" };
                    foreach (string flag in addedFlags) { toggles[flag].Checked = false; toggles[flag].Checked = true; toggles[flag].Checked = false; }
                    Field<ComboBox>(form, "imageTranslator").SelectedIndex = 0;
                    typeof(SettingsForm).GetMethod("Save", Private).Invoke(form, null);
                    check(addedFlags.All(flag => !(bool)typeof(AppSettings).GetProperty(flag).GetValue(saved, null)) && saved.ArkApiKey == settings.ArkApiKey && saved.BaiduLlmApiKey == settings.BaiduLlmApiKey && saved.BaiduTranslateSecret == settings.BaiduTranslateSecret, "every added UI opt-in can be disabled and saved while retaining its shared and independent credentials");
                    check(((string[])Field<ComboBox>(form, "translator").Tag).SequenceEqual(new[] { "volc", "baidu" }), "disabling all added translation services removes their selector choices without duplicates");
                }
                using (var form = new MainForm())
                {
                    form.RefreshSettings(settings);
                    var cards = Field<Dictionary<string, ServiceCard>>(form, "cards");
                    check(cards.Count == AppSettings.TranslationProviders.Length && new[] { "ark", "baidu_llm", "baidu_domain" }.All(cards.ContainsKey), "main translation window owns a result card for every added provider");
                    form.SetActiveProvider("ark"); form.SetOutput("Ark result");
                    form.SetActiveProvider("baidu_domain"); form.SetOutput("Domain result");
                    check(cards["ark"].Value == "Ark result" && cards["baidu_domain"].Value == "Domain result", "new provider results remain in their independent cards");
                    var disabled = settings.Copy(); disabled.ArkEnabled = false; form.RefreshSettings(disabled);
                    check(!Field<HashSet<string>>(form, "enabledProviders").Contains("ark"), "disabled Ark card is removed from enabled results");
                }
            }
            finally { Ui.RenderOffscreen = offscreen; }
        }
        private static bool Throws(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        private static bool SameSettings(AppSettings first, AppSettings second)
        {
            return typeof(AppSettings).GetProperties().All(property =>
            {
                object a = property.GetValue(first, null), b = property.GetValue(second, null);
                return a is string[] ? ((string[])a).SequenceEqual((string[])b) : Object.Equals(a, b);
            });
        }
        private static T Field<T>(object instance, string name) { return (T)instance.GetType().GetField(name, Private).GetValue(instance); }
    }
}

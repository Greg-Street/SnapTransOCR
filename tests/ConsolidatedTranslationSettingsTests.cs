using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class ConsolidatedTranslationSettingsTests
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        internal static void Run(Action<bool, string> check)
        {
            bool offscreen = Ui.RenderOffscreen; Ui.RenderOffscreen = true;
            var fonts = new List<Font>();
            try
            {
                AppSettings saved = null;
                var initial = new AppSettings {
                    TranslateProvider = "baidu_llm", BaiduTranslateEnabled = false, BaiduLlmEnabled = true, BaiduDomainEnabled = true,
                    BaiduTranslateId = "test-id", BaiduTranslateSecret = "test-secret", BaiduLlmApiKey = "test-llm-key",
                    BaiduLlmInstruction = "保留公式", BaiduDomain = "academic", BaiduImageTranslateEnabled = false,
                    ImageTranslateProvider = "baidu_image", BaiduImageTargetLanguage = "en", HistoryEnabled = false
                };
                using (var form = new SettingsForm(initial, value => saved = value))
                {
                    form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-30000, -30000); form.ShowInTaskbar = false;
                    form.Show(); form.SelectPage("翻译设置"); Application.DoEvents();
                    var mode = Field<ComboBox>(form, "baiduMode");
                    var fields = Field<Dictionary<string, TextBox>>(form, "fields");
                    var toggles = Field<Dictionary<string, ToggleSwitch>>(form, "toggles");
                    var domain = Field<ComboBox>(form, "baiduDomain");
                    var page = Field<Dictionary<string, FlowLayoutPanel>>(form, "pages")["翻译设置"];
                    check(mode.SelectedIndex == 1 && mode.Items[0].ToString().Contains("未启用") && mode.Items[1].ToString().Contains("已启用"), "the shared Baidu card starts on the active mode and shows each mode's enabled state");
                    check(fields["BaiduTranslateId"].Parent.Parent == mode.Parent && fields["BaiduLlmApiKey"].Parent.Parent == mode.Parent && domain.Parent == mode.Parent, "Baidu credentials and all mode configuration belong to one shared card");
                    check(page.Controls.Cast<Control>().OfType<Label>().Count(label => label.Text == "百度翻译") == 1 && !page.Controls.Cast<Control>().OfType<Label>().Any(label => label.Text == "百度大模型翻译" || label.Text == "百度领域翻译"), "Baidu has one configuration heading without duplicate mode sections");
                    fields["BaiduLlmInstruction"].Text = "保留公式和术语";
                    mode.SelectedIndex = 0;
                    check(toggles["BaiduTranslateEnabled"].Visible && !fields["BaiduLlmApiKey"].Parent.Visible && !domain.Visible && fields["BaiduTranslateId"].Parent.Visible, "general mode hides other mode rows while keeping shared credentials visible");
                    toggles["BaiduTranslateEnabled"].Checked = true;
                    check(mode.SelectedIndex == 0 && mode.Items[0].ToString().Contains("已启用"), "enabling a Baidu mode updates its state label without switching the edited mode");
                    mode.SelectedIndex = 2; domain.SelectedIndex = 1;
                    check(domain.Visible && toggles["BaiduDomainEnabled"].Visible && !toggles["BaiduLlmEnabled"].Visible, "domain mode shows only its independent enable switch and domain choice");
                    mode.SelectedIndex = 1;
                    check(fields["BaiduLlmInstruction"].Text == "保留公式和术语" && domain.SelectedIndex == 1 && toggles["BaiduDomainEnabled"].Checked, "switching modes preserves unsaved fields and independent enabled states");
                    var order = Field<ServiceOrderControl>(form, "translationOrder");
                    order.SelectedIndex = Array.IndexOf(order.Order, "baidu_domain"); order.MoveSelected(-order.SelectedIndex);
                    check(page.Controls.Cast<Control>().Where(control => control.Tag is string).Select(control => (string)control.Tag).First() == "baidu" && page.Controls.Cast<Control>().Count(control => (string)control.Tag == "baidu") == 3, "moving any Baidu provider moves the shared settings group once");
                    var image = Field<ComboBox>(form, "imageTranslator");
                    var imageTarget = Field<ComboBox>(form, "imageTarget");
                    check(!toggles.ContainsKey("BaiduImageTranslateEnabled") && image.SelectedIndex == 0 && !imageTarget.Visible, "legacy disabled Baidu image settings open as OCR pipeline with no redundant opt-in switch");
                    Save(form);
                    check(!saved.BaiduImageTranslateEnabled && saved.ImageTranslateProvider == "pipeline" && saved.TranslateProvider == "baidu_llm" && saved.BaiduTranslateEnabled && saved.BaiduLlmEnabled && saved.BaiduDomainEnabled && saved.BaiduLlmInstruction == "保留公式和术语" && saved.BaiduDomain == "it", "saving unrelated settings keeps a disabled image route off and retains all Baidu modes and drafts");
                    image.SelectedIndex = 1;
                    check(imageTarget.Visible && imageTarget.SelectedIndex == 1, "selecting Baidu image translation exposes its retained fixed target language");
                    Save(form);
                    check(saved.BaiduImageTranslateEnabled && saved.ImageTranslateProvider == "baidu_image", "the single image method choice explicitly enables the selected Baidu route");
                    image.SelectedIndex = 0; Save(form);
                    check(!saved.BaiduImageTranslateEnabled && saved.ImageTranslateProvider == "pipeline" && saved.BaiduImageTargetLanguage == "en", "returning to OCR pipeline disables Baidu image requests without losing its target language");
                    var model = Field<ComboBox>(form, "arkModel");
                    string presetId = "doubao-seed-2-1-lite-260915";
                    check(model.DropDownStyle == ComboBoxStyle.DropDownList && model.Items.Count == TranslationModels.Ark.Length + 1, "the styled model list offers all known presets and one separate custom ID option");
                    check(model.Items.Cast<object>().Count(item => item.ToString().Contains("即将下线")) == 3 && model.Items.Cast<object>().Count(item => item.ToString().Contains("已下线")) == 2, "retired and retiring model versions stay visibly distinguished in the picker");
                    model.SelectedIndex = Array.IndexOf((string[])model.Tag, presetId); Save(form);
                    check(saved.ArkModel == presetId && model.Text == "豆包 Seed 2.1 Lite" && model.AccessibleDescription == presetId && !fields["ArkModel"].Parent.Visible, "a concise model selection saves its API ID while hiding the custom ID row");
                    model.SelectedIndex = TranslationModels.Ark.Length; fields["ArkModel"].Text = "ep-custom-endpoint"; Save(form);
                    check(saved.ArkModel == "ep-custom-endpoint" && saved.ArkApiMode == initial.ArkApiMode, "entering a custom endpoint preserves its ID and the user's protocol selection");
                    foreach (int dpi in new[] { 96, 192 })
                    {
                        SettingsLayoutTests.SimulateDpi(form, dpi, fonts);
                        form.ClientSize = new Size(Ui.Scale(form, 860), Ui.Scale(form, 580)); form.SelectPage("翻译设置");
                        foreach (int selectedMode in new[] { 0, 1, 2 })
                        {
                            mode.SelectedIndex = selectedMode; form.PerformLayout();
                            var table = (TableLayoutPanel)mode.Parent;
                            check(table.Controls.Cast<Control>().Where(control => table.RowStyles[table.GetRow(control)].SizeType != SizeType.Absolute).All(control => table.ClientRectangle.Contains(control.Bounds)), "each visible Baidu mode fits the minimum-width card at " + dpi + " DPI: " + selectedMode);
                            var toggle = toggles[new[] { "BaiduTranslateEnabled", "BaiduLlmEnabled", "BaiduDomainEnabled" }[selectedMode]];
                            check(toggle.Size == new Size(Ui.Scale(form, 46), Ui.Scale(form, 28)), "revealing a Baidu mode restores its full toggle size after hidden-row DPI changes at " + dpi + ": " + selectedMode);
                        }
                    }
                }
                using (var reopened = new SettingsForm(saved, delegate { }))
                    check(Field<ComboBox>(reopened, "baiduMode").SelectedIndex == 1 && Field<Dictionary<string, TextBox>>(reopened, "fields")["BaiduLlmInstruction"].Text == "保留公式和术语", "reopening the shared card follows the saved active Baidu provider and retains its draft fields");
            }
            finally { Ui.RenderOffscreen = offscreen; foreach (var font in fonts) font.Dispose(); }
        }
        private static void Save(SettingsForm form) { typeof(SettingsForm).GetMethod("Save", Private).Invoke(form, null); }
        private static T Field<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Private).GetValue(owner); }
    }
}

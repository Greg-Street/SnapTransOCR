using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class PasswordFieldTests
    {
        internal static void Run(Action<bool, string> check)
        {
            var settings = new AppSettings { VolcId = "example-access-id", VolcSecret = "example-secret", TencentId = "example-tencent-id" };
            AppSettings saved = null;
            using (var form = new SettingsForm(settings, value => saved = value))
            {
                check(Descendants(form).OfType<TextBox>().All(text => text.BorderStyle == BorderStyle.None && text.Parent is RoundedPanel), "all settings inputs share a rounded border without native inset edges");
                var inputs = Descendants(form).OfType<TextInputField>().ToArray();
                var wrappers = CredentialFields(form).ToArray();
                check(wrappers.Length == 10 && wrappers.All(field => field.Controls.OfType<TextBox>().Single().UseSystemPasswordChar), "all ten credentials start masked with a visibility button");
                check(inputs.Length == 22 && inputs.Count(field => !field.Controls.OfType<TextBox>().Single().UseSystemPasswordChar) == 12, "hotkey fields, history search, translation instruction and custom model ID share the rounded input style without password icons");
                foreach (var field in wrappers)
                {
                    var text = field.Controls.OfType<TextBox>().Single();
                    var button = field.Controls.OfType<GlyphButton>().Single();
                    string value = text.Text; int textChanges = 0; text.TextChanged += delegate { textChanges++; };
                    text.Select(Math.Min(2, text.TextLength), Math.Min(3, Math.Max(0, text.TextLength - 2)));
                    int start = text.SelectionStart, length = text.SelectionLength;
                    button.PerformClick();
                    var hint = (ToolTip)typeof(GlyphButton).GetField("hints", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(button);
                    check(!text.UseSystemPasswordChar && button.Glyph == "eye-off" && button.AccessibleName == "隐藏凭据" && hint.GetToolTip(button) == "隐藏凭据", "credential reveal updates visibility and accessible hint for " + text.AccessibleName);
                    button.PerformClick();
                    check(text.UseSystemPasswordChar && text.Text == value && textChanges == 0 && text.SelectionStart == start && text.SelectionLength == length && hint.GetToolTip(button) == "显示凭据", "credential hide preserves value and selection for " + text.AccessibleName);
                }
                check(saved == null, "visibility toggles never save settings");
                var hotkey = Descendants(form).OfType<TextBox>().Single(control => control.AccessibleName == "hotkey0");
                var key = new KeyEventArgs(Keys.Control | Keys.Shift | Keys.F9);
                typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(hotkey, new object[] { key });
                check(key.SuppressKeyPress && hotkey.Text == "Ctrl+Shift+F9", "rounded hotkey input retains combination capture");
                var secret = Descendants(form).OfType<TextBox>().Single(control => control.AccessibleName == "VolcSecret");
                secret.Parent.Controls.OfType<GlyphButton>().Single().PerformClick(); secret.Text = "edited-example-secret";
                typeof(SettingsForm).GetMethod("Save", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                check(saved != null && saved.VolcSecret == "edited-example-secret" && saved.VolcId == settings.VolcId && saved.Hotkeys[0] == "Ctrl+Shift+F9", "rounded credential and hotkey edits save through the original field references");
            }
            using (var reopened = new SettingsForm(saved, delegate { }))
                check(CredentialFields(reopened).All(field => field.Controls.OfType<TextBox>().Single().UseSystemPasswordChar), "new settings windows mask credentials again after a visible save");
            using (var form = new SettingsForm(settings, delegate { }) { StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000), ShowInTaskbar = false })
            {
                form.SelectPage("识别设置"); form.Show(); Application.DoEvents();
                var text = Descendants(form).OfType<TextBox>().Single(control => control.AccessibleName == "TencentId");
                var field = (TextInputField)text.Parent;
                text.Focus();
                check(text.Focused && field.BorderColor == Ui.Accent, "keyboard focus gives the rounded input a blue border");
                var button = field.Controls.OfType<GlyphButton>().Single();
                button.Focus();
                check(button.Focused && field.BorderColor == Ui.Accent, "credential icon retains the surrounding focus border");
                form.SelectPage("热键设置");
                Descendants(form).OfType<TextBox>().First(control => control.AccessibleName == "hotkey0").Focus();
                check(field.BorderColor == Color.FromArgb(216, 220, 227), "leaving the input restores its muted border");
            }
            var helper = typeof(Control).Assembly.GetType("System.Windows.Forms.DpiHelper");
            var dpiField = helper.GetField("deviceDpi", BindingFlags.NonPublic | BindingFlags.Static);
            var factorField = helper.GetField("logicalToDeviceUnitsScalingFactor", BindingFlags.NonPublic | BindingFlags.Static);
            object previousDpi = dpiField.GetValue(null), previousFactor = factorField.GetValue(null);
            try
            {
                foreach (int dpi in new[] { 96, 144, 192, 240 })
                {
                    dpiField.SetValue(null, (double)dpi); factorField.SetValue(null, dpi / 96.0);
                    using (var form = new SettingsForm(new AppSettings(), delegate { }))
                    {
                        form.ClientSize = new Size(860 * dpi / 96, 580 * dpi / 96);
                        foreach (string page in new[] { "历史记录", "热键设置", "翻译设置", "识别设置" })
                        {
                            form.SelectPage(page);
                            check(Descendants(form).OfType<TextInputField>().All(field => Fits(field, dpi)), "rounded inputs and inset icons fit narrow settings at " + dpi + " DPI on " + page);
                        }
                    }
                }
            }
            finally { dpiField.SetValue(null, previousDpi); factorField.SetValue(null, previousFactor); }
        }
        private static bool Fits(TextInputField field, int dpi)
        {
            var text = field.Controls.OfType<TextBox>().Single();
            var button = field.Controls.OfType<GlyphButton>().SingleOrDefault(control => control.Visible);
            bool widthFits = text.AccessibleName == "搜索历史原文或译文"
                ? field.Left >= 0 && field.Right <= field.Parent.ClientSize.Width
                : field.Width <= 250 * dpi / 96;
            return widthFits && text.Left >= 10 * dpi / 96 && text.Top > 0
                && (button == null || text.Right < button.Left)
                && field.Controls.Cast<Control>().All(control => control.Left >= 0 && control.Right < field.ClientSize.Width && control.Bottom < field.ClientSize.Height);
        }
        private static IEnumerable<TextInputField> CredentialFields(Control parent)
        {
            return Descendants(parent).OfType<TextInputField>().Where(field => field.Controls.OfType<GlyphButton>().Any(button => button.Glyph == "eye" || button.Glyph == "eye-off"));
        }
        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control nested in Descendants(child)) yield return nested;
            }
        }
    }
}

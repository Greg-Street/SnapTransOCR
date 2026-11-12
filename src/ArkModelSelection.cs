using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal sealed class ArkModelSelection : Panel
    {
        private readonly Dictionary<string, ToggleSwitch> checks = new Dictionary<string, ToggleSwitch>();
        private readonly Dictionary<string, Label> labels = new Dictionary<string, Label>();
        private readonly HashSet<string> selected;
        private readonly ToolTip hints = new ToolTip();
        private string primary;
        private bool updating, arranging;
        internal event Action Changed;
        internal ArkModelSelection(string[] models, string primaryModel)
        {
            selected = new HashSet<string>(models ?? new string[0], StringComparer.Ordinal);
            AutoScroll = true; BackColor = Color.White; Width = 250; Height = 210;
            AccessibleName = "方舟并行模型";
            var modelsToShow = new List<string>();
            foreach (var model in TranslationModels.Ark) modelsToShow.Add(model.Id);
            foreach (string model in selected) if (!String.IsNullOrWhiteSpace(model) && !modelsToShow.Contains(model)) modelsToShow.Add(model);
            foreach (string id in modelsToShow)
            {
                var model = TranslationModels.Find(id);
                var toggle = new ToggleSwitch { AccessibleName = "同时翻译：" + TranslationModels.Caption(id) };
                var label = new Label { Text = TranslationModels.Caption(id), AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Cursor = Cursors.Hand };
                toggle.CheckedChanged += delegate
                {
                    if (updating || id == primary) return;
                    if (toggle.Checked) selected.Add(id); else selected.Remove(id);
                    if (Changed != null) Changed();
                };
                label.Click += delegate { if (toggle.Enabled) toggle.Checked = !toggle.Checked; };
                checks.Add(id, toggle); labels.Add(id, label);
                hints.SetToolTip(label, model == null ? id : model.DisplayName); hints.SetToolTip(toggle, id);
                Controls.Add(toggle); Controls.Add(label);
            }
            SetPrimary(primaryModel);
        }
        internal string[] SelectedModels
        {
            get
            {
                var result = new List<string>();
                foreach (var model in TranslationModels.Ark) if (selected.Contains(model.Id) && model.Id != primary) result.Add(model.Id);
                foreach (string model in selected) if (TranslationModels.Find(model) == null && model != primary && !String.IsNullOrWhiteSpace(model)) result.Add(model);
                return result.ToArray();
            }
        }
        internal void SetPrimary(string model)
        {
            primary = model; updating = true;
            try
            {
                selected.Remove(primary ?? "");
                foreach (var pair in checks)
                {
                    pair.Value.Checked = pair.Key == primary || selected.Contains(pair.Key);
                    pair.Value.Enabled = pair.Key != primary;
                    labels[pair.Key].Text = (pair.Key == primary ? "默认 · " : "") + TranslationModels.Caption(pair.Key);
                    labels[pair.Key].ForeColor = pair.Key == primary ? Ui.Accent : Ui.Ink;
                }
            }
            finally { updating = false; }
            PerformLayout();
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (checks == null || arranging) return;
            arranging = true;
            try
            {
                int row = Math.Max(Ui.Scale(this, 38), Font.Height + Ui.Scale(this, 14)), index = 0;
                Height = Ui.Scale(this, 210);
                AutoScrollMinSize = new Size(0, row * checks.Count);
                int contentWidth = Math.Max(1, Math.Min(ClientSize.Width, Width - Math.Max(SystemInformation.VerticalScrollBarWidth, Ui.Scale(this, 24))));
                var order = new List<string>();
                if (checks.ContainsKey(primary ?? "")) order.Add(primary);
                foreach (string id in checks.Keys) if (id != primary) order.Add(id);
                foreach (string id in order)
                {
                    int top = index++ * row + AutoScrollPosition.Y;
                    checks[id].SetBounds(0, top + (row - Ui.Scale(this, 28)) / 2, Ui.Scale(this, 46), Ui.Scale(this, 28));
                    int left = Ui.Scale(this, 54);
                    labels[id].SetBounds(left, top, Math.Max(1, contentWidth - left - Ui.Scale(this, 4)), row);
                }
                AdjustFormScrollbars(true);
            }
            finally { arranging = false; }
        }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); PerformLayout(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); PerformLayout(); }
        protected override void Dispose(bool disposing) { if (disposing) hints.Dispose(); base.Dispose(disposing); }
    }
}

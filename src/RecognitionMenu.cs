using System;
using System.Drawing;
using System.Windows.Forms;

namespace SnapTransOCR
{
    internal static class RecognitionMenu
    {
        internal static ContextMenuStrip Attach(Control button, AppSettings settings, Action<RecognitionKind> select)
        {
            var menu = new ContextMenuStrip { ShowImageMargin = false };
            Add(menu, "百度手写文字识别", RecognitionKind.Handwriting, select);
            Add(menu, "百度二维码 / 条形码", RecognitionKind.QrCode, select);
            Add(menu, "百度表格识别 V2", RecognitionKind.BaiduTable, select);
            Add(menu, "腾讯表格识别 V3", RecognitionKind.Table, select);
            button.ContextMenuStrip = menu;
            Update(button, settings);
            button.Click += delegate { menu.Show(button, new Point(0, button.Height)); };
            button.Disposed += delegate { menu.Dispose(); };
            return menu;
        }

        internal static void Update(Control button, AppSettings settings)
        {
            foreach (ToolStripMenuItem item in button.ContextMenuStrip.Items)
            {
                var kind = (RecognitionKind)item.Tag;
                item.Enabled = kind == RecognitionKind.Handwriting ? settings.BaiduHandwritingEnabled : kind == RecognitionKind.QrCode ? settings.BaiduQrEnabled : kind == RecognitionKind.BaiduTable ? settings.BaiduTableEnabled : settings.TencentTableEnabled;
                item.ToolTipText = item.Enabled ? item.Text : "请先在识别设置中启用此服务";
            }
        }

        private static void Add(ContextMenuStrip menu, string name, RecognitionKind kind, Action<RecognitionKind> select)
        {
            var item = new ToolStripMenuItem(name) { Tag = kind };
            item.Click += delegate { select(kind); };
            menu.Items.Add(item);
        }

        internal static string Name(RecognitionKind kind)
        {
            switch (kind)
            {
                case RecognitionKind.Handwriting: return "百度手写文字识别";
                case RecognitionKind.QrCode: return "百度二维码 / 条形码";
                case RecognitionKind.BaiduTable: return "百度表格识别 V2";
                case RecognitionKind.Table: return "腾讯表格识别 V3";
                default: return "文字识别";
            }
        }
    }
}

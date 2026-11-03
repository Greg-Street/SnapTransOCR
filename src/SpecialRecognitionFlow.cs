using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal sealed partial class DesktopApp
    {
        private async Task SpecialOcr(Bitmap image, RecognitionKind kind)
        {
            if (exiting) { image.Dispose(); return; }
            CancelOcr();
            int stamp = ++ocrVersion;
            var cancellation = ocrJob = new CancellationTokenSource();
            var window = OcrWindow();
            var snapshot = settings.Copy();
            Func<bool> current = delegate { return !exiting && stamp == ocrVersion; };
            window.Present("", snapshot.TranslateNearCursor); window.Busy(true);
            window.SetStatus("正在" + RecognitionMenu.Name(kind) + "…");
            try
            {
                RecognitionResult result;
                if (kind == RecognitionKind.Handwriting)
                {
                    // Handwriting may be tiled; a table or QR must remain intact.
                    var text = new List<string>();
                    int characters = 0;
                    Rectangle[] regions = await Task.Run(delegate { return ScreenCapture.OcrRegions(image).ToArray(); });
                    foreach (Rectangle region in regions)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        using (var tile = image.Clone(region, PixelFormat.Format24bppRgb))
                        {
                            byte[] data = await Task.Run(delegate { return ScreenCapture.EncodeOcr(tile); });
                            var part = await api.RecognizeSpecial(snapshot, data, kind, cancellation.Token);
                            if (!String.IsNullOrWhiteSpace(part.Text))
                            {
                                characters += part.Text.Length + Environment.NewLine.Length;
                                if (characters > 2000000) throw new InvalidOperationException("识别文字超过处理上限，请缩小截图区域。");
                                text.Add(part.Text);
                            }
                        }
                        if (region != regions[regions.Length - 1]) await Task.Delay(550, cancellation.Token);
                    }
                    result = new RecognitionResult(String.Join(Environment.NewLine, text));
                }
                else
                {
                    byte[] data = await Task.Run(delegate { return ScreenCapture.EncodeOcr(image); });
                    result = await api.RecognizeSpecial(snapshot, data, kind, cancellation.Token);
                }
                cancellation.Token.ThrowIfCancellationRequested();
                if (!current()) return;
                window.SetRecognitionResult(result, kind);
                if (String.IsNullOrWhiteSpace(result.Text)) { window.SetStatus("没有识别到结果。"); return; }
                snapshot.OcrProvider = kind == RecognitionKind.Table ? "tencent_table" : kind == RecognitionKind.BaiduTable ? "baidu_table" : kind == RecognitionKind.QrCode ? "baidu_qrcode" : "baidu_handwriting";
                string status = await RecordHistory(snapshot, "ocr", result.Text, "", image);
                if (current()) window.SetStatus(status.Length > 0 ? status : (kind == RecognitionKind.Table || kind == RecognitionKind.BaiduTable) ? (result.Spreadsheet != null ? "复制后可粘贴到 Excel；保存按钮可导出原始工作簿。" : "复制后可粘贴到 Excel。") : "");
            }
            catch (OperationCanceledException) { if (current()) window.SetStatus("请求已取消"); }
            catch (Exception ex) { if (current()) window.SetStatus(ex is InvalidOperationException ? ex.Message : "识别未完成（" + ex.GetType().Name + "）。"); }
            finally
            {
                image.Dispose();
                if (current()) { window.Busy(false); ocrJob = null; }
                cancellation.Dispose();
            }
        }

        private static void RequireBaiduImage(AppSettings settings)
        {
            if (!settings.BaiduImageTranslateEnabled) throw new InvalidOperationException("请先在翻译设置中启用百度图片翻译。");
            if (String.IsNullOrWhiteSpace(settings.BaiduTranslateId) || String.IsNullOrWhiteSpace(settings.BaiduTranslateSecret))
                throw new InvalidOperationException("请在翻译设置中填写百度翻译 APP ID 和密钥。");
        }

        internal static byte[] EncodeBaiduImage(Bitmap tile)
        {
            // Native pixels and origin stay unchanged; white padding satisfies the vendor's 3:1 limit.
            int width = Math.Max(30, Math.Max(tile.Width, (tile.Height + 2) / 3));
            int height = Math.Max(30, Math.Max(tile.Height, (tile.Width + 2) / 3));
            using (var padded = new Bitmap(width, height, PixelFormat.Format24bppRgb))
            {
                using (var graphics = Graphics.FromImage(padded)) { graphics.Clear(Color.White); graphics.DrawImageUnscaled(tile, Point.Empty); }
                return ScreenCapture.EncodeOcr(padded);
            }
        }
    }
}

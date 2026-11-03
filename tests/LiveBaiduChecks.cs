using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    // Explicitly invoked only; credentials are process environment variables, never settings files.
    internal static class LiveBaiduChecks
    {
        internal static async Task Run(string directory)
        {
            var settings = new AppSettings {
                BaiduOcrKey = Environment.GetEnvironmentVariable("SNAP_TEST_OCR_KEY"),
                BaiduOcrSecret = Environment.GetEnvironmentVariable("SNAP_TEST_OCR_SECRET"),
                BaiduTranslateId = Environment.GetEnvironmentVariable("SNAP_TEST_TRANS_ID"),
                BaiduTranslateSecret = Environment.GetEnvironmentVariable("SNAP_TEST_TRANS_SECRET"),
                OcrProvider = "baidu", AccurateOcr = true, TranslateProvider = "baidu_llm", BaiduLlmEnabled = true,
                BaiduDomainEnabled = true, BaiduDomain = "academic", BaiduImageTranslateEnabled = true,
                BaiduQrEnabled = true, BaiduHandwritingEnabled = true, BaiduTableEnabled = true,
                SourceLanguage = "en", TargetLanguage = "zh"
            };
            settings.RequireOcr(); settings.RequireTranslation();
            var reports = new List<object>();
            using (var api = new CloudApi())
            using (var sample = new Bitmap(720, 320))
            {
                using (var graphics = Graphics.FromImage(sample))
                using (var font = new Font("Segoe UI", 24))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawString("SnapTransOCR test", font, Brushes.Black, 30, 20);
                    graphics.DrawString("Name", font, Brushes.Black, 40, 100);
                    graphics.DrawString("Value", font, Brushes.Black, 380, 100);
                    graphics.DrawString("Alpha", font, Brushes.Black, 40, 180);
                    graphics.DrawString("42", font, Brushes.Black, 380, 180);
                    graphics.DrawRectangle(Pens.Black, 20, 85, 650, 165);
                    graphics.DrawLine(Pens.Black, 20, 165, 670, 165);
                    graphics.DrawLine(Pens.Black, 340, 85, 340, 250);
                }
                byte[] image;
                using (var stream = new MemoryStream()) { sample.Save(stream, ImageFormat.Png); image = stream.ToArray(); }
                await Check(reports, "baidu_accurate_text", async delegate { string text = await api.Ocr(settings, image, CancellationToken.None); return "chars=" + text.Length; });
                await Check(reports, "baidu_accurate_layout", async delegate { var blocks = await api.OcrWithLayout(settings, image, CancellationToken.None); return "blocks=" + blocks.Length; });
                await Check(reports, "baidu_llm_legacy_auth", async delegate { string text = await api.Translate(settings, "The measurement result is 42.", CancellationToken.None); return "chars=" + text.Length; });
                settings.TranslateProvider = "baidu_domain";
                await Check(reports, "baidu_domain", async delegate { string text = await api.Translate(settings, "The measurement result is 42.", CancellationToken.None); return "chars=" + text.Length; });
                await Check(reports, "baidu_image", async delegate { var result = await api.BaiduImageTranslate(settings, image, CancellationToken.None); return "blocks=" + result.Blocks.Length + ";translations=" + result.Translations.Length; });
                await Check(reports, "baidu_table_v2", async delegate { var result = await api.RecognizeSpecial(settings, image, RecognitionKind.BaiduTable, CancellationToken.None); return "chars=" + result.Text.Length + ";xlsx=" + (result.Spreadsheet == null ? 0 : result.Spreadsheet.Length); });
                await Check(reports, "baidu_handwriting_printed_fixture", async delegate { var result = await api.RecognizeSpecial(settings, image, RecognitionKind.Handwriting, CancellationToken.None); return "chars=" + result.Text.Length; });
                string qr = Path.Combine(directory, "synthetic-qr.png");
                if (File.Exists(qr)) await Check(reports, "baidu_qrcode", async delegate { var result = await api.RecognizeSpecial(settings, File.ReadAllBytes(qr), RecognitionKind.QrCode, CancellationToken.None); if (result.Text != "SnapTransOCR synthetic QR test") throw new InvalidOperationException("Synthetic QR value differs."); return "matches=true"; });
            }
            File.WriteAllText(Path.Combine(directory, "live-baidu-0.2.16.json"), Json.Write(reports));
        }
        private static async Task Check(List<object> reports, string name, Func<Task<string>> action)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            try { string detail = await action().ConfigureAwait(false); reports.Add(new { name, success = true, elapsedMs = watch.ElapsedMilliseconds, detail }); Console.WriteLine(name + ": PASS " + detail); }
            catch (Exception ex) { string error = ex is InvalidOperationException ? ex.Message : ex.GetType().Name; reports.Add(new { name, success = false, elapsedMs = watch.ElapsedMilliseconds, error }); Console.WriteLine(name + ": " + error); }
            await Task.Delay(1100).ConfigureAwait(false);
        }
    }
}

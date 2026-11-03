using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    // Explicit opt-in only. One generated table, credentials in process environment, no user images.
    internal static class LiveTencentTableChecks
    {
        private sealed class ContractCapture : DelegatingHandler
        {
            internal readonly List<object> Blocks = new List<object>();
            internal ContractCapture() : base(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                HttpResponseMessage response = await base.SendAsync(request, cancellation).ConfigureAwait(false);
                var root = Json.Map(Json.Get(Json.Object(await response.Content.ReadAsStringAsync().ConfigureAwait(false)), "Response"));
                var blocks = Json.Get(root, "TableDetections") as IList;
                if (blocks != null) foreach (object item in blocks)
                {
                    var block = Json.Map(item); var cells = Json.Get(block, "Cells") as IList; var spans = new List<object>();
                    if (cells != null) foreach (object value in cells)
                    {
                        var cell = Json.Map(value);
                        spans.Add(new { type = Json.Text(cell, "Type"), rowStart = Json.Get(cell, "RowTl"), rowEnd = Json.Get(cell, "RowBr"), colStart = Json.Get(cell, "ColTl"), colEnd = Json.Get(cell, "ColBr") });
                    }
                    Blocks.Add(new { type = Json.Get(block, "Type"), cells = spans });
                }
                return response;
            }
        }
        internal static async Task Run(string directory)
        {
            var settings = new AppSettings { TencentId = Environment.GetEnvironmentVariable("SNAP_TEST_TENCENT_ID"), TencentSecret = Environment.GetEnvironmentVariable("SNAP_TEST_TENCENT_SECRET"), TencentTableEnabled = true };
            var capture = new ContractCapture();
            using (var api = new CloudApi(capture))
            using (var sample = new Bitmap(720, 350))
            {
                using (var graphics = Graphics.FromImage(sample))
                using (var font = new Font("Segoe UI", 22))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawString("Measurement table", font, Brushes.Black, 25, 15);
                    graphics.DrawString("Name", font, Brushes.Black, 40, 95);
                    graphics.DrawString("Value", font, Brushes.Black, 390, 95);
                    graphics.DrawString("Alpha", font, Brushes.Black, 40, 175);
                    graphics.DrawString("42", font, Brushes.Black, 390, 175);
                    graphics.DrawRectangle(Pens.Black, 20, 80, 650, 165);
                    graphics.DrawLine(Pens.Black, 20, 160, 670, 160);
                    graphics.DrawLine(Pens.Black, 350, 80, 350, 245);
                    graphics.DrawString("Synthetic test only", font, Brushes.Black, 25, 275);
                }
                byte[] png;
                using (var stream = new MemoryStream()) { sample.Save(stream, ImageFormat.Png); png = stream.ToArray(); }
                try
                {
                    RecognitionResult result = await api.RecognizeSpecial(settings, png, RecognitionKind.Table, CancellationToken.None);
                    if (!result.Text.Contains("Alpha") || !result.Text.Contains("42")) throw new InvalidOperationException("Synthetic table result did not preserve expected cells.");
                    File.WriteAllText(Path.Combine(directory, "live-tencent-table.json"), Json.Write(new { passed = true, chars = result.Text.Length, workbookBytes = result.Spreadsheet == null ? 0 : result.Spreadsheet.Length, blocks = capture.Blocks }));
                    Console.WriteLine("PASS: Tencent V3 synthetic table, chars=" + result.Text.Length + ", blocks=" + capture.Blocks.Count);
                }
                catch (Exception error)
                {
                    File.WriteAllText(Path.Combine(directory, "live-tencent-table.json"), Json.Write(new { passed = false, error = error.Message, blocks = capture.Blocks }));
                    throw;
                }
            }
        }
    }
}

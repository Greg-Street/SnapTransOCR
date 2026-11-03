using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class SpecialRecognitionTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            internal int Requests;
            internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Handle;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                Requests++;
                return Handle(request);
            }
        }

        private static HttpResponseMessage Response(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }

        private static byte[] Png(int width = 64, int height = 64)
        {
            using (var bitmap = new Bitmap(width, height))
            using (var stream = new MemoryStream()) { bitmap.Save(stream, ImageFormat.Png); return stream.ToArray(); }
        }

        private static AppSettings Settings()
        {
            return new AppSettings {
                BaiduQrEnabled = true, BaiduHandwritingEnabled = true, BaiduTableEnabled = true, TencentTableEnabled = true,
                BaiduImageTranslateEnabled = true, ImageTranslateProvider = "baidu_image", TargetLanguage = "zh",
                BaiduOcrKey = "ocr-id", BaiduOcrSecret = "ocr-secret", TencentId = "tencent-id", TencentSecret = "tencent-secret",
                BaiduTranslateId = "translation-id", BaiduTranslateSecret = "translation-secret"
            };
        }

        private static async Task<bool> Fails(Func<Task> operation, string message = null)
        {
            try { await operation().ConfigureAwait(false); return false; }
            catch (InvalidOperationException ex) { return message == null || ex.Message.Contains(message); }
        }

        internal static async Task Run(Action<bool, string> check)
        {
            await DisabledAndLimits(check).ConfigureAwait(false);
            await BaiduSpecial(check).ConfigureAwait(false);
            await BaiduTable(check).ConfigureAwait(false);
            await TencentTable(check).ConfigureAwait(false);
            await TencentTableTextBlocks(check).ConfigureAwait(false);
            await PictureTranslation(check).ConfigureAwait(false);
        }

        private static async Task DisabledAndLimits(Action<bool, string> check)
        {
            var handler = new Handler { Handle = request => { throw new Exception("Unexpected HTTP request"); } };
            var settings = Settings();
            byte[] image = Png();
            using (var api = new CloudApi(handler))
            {
                settings.BaiduQrEnabled = settings.BaiduHandwritingEnabled = settings.BaiduTableEnabled = settings.TencentTableEnabled = settings.BaiduImageTranslateEnabled = false;
                foreach (RecognitionKind kind in new[] { RecognitionKind.QrCode, RecognitionKind.Handwriting, RecognitionKind.BaiduTable, RecognitionKind.Table })
                    check(await Fails(async () => { await api.RecognizeSpecial(settings, image, kind, CancellationToken.None); }), "disabled " + kind + " sends no HTTP");
                check(await Fails(async () => { await api.BaiduImageTranslate(settings, image, CancellationToken.None); }), "disabled picture translation sends no HTTP");
                settings = Settings(); settings.BaiduTranslateId = "";
                check(await Fails(async () => { await api.BaiduImageTranslate(settings, image, CancellationToken.None); }), "picture translation does not borrow OCR credentials");
                settings = Settings(); settings.TencentSecret = "";
                check(await Fails(async () => { await api.RecognizeSpecial(settings, image, RecognitionKind.Table, CancellationToken.None); }), "unconfigured table provider sends no HTTP");
                settings = Settings(); settings.BaiduOcrSecret = "";
                check(await Fails(async () => { await api.RecognizeSpecial(settings, image, RecognitionKind.QrCode, CancellationToken.None); }), "unconfigured QR provider sends no HTTP");
                settings = Settings(); settings.TargetLanguage = "smart";
                check(await Fails(async () => { await api.BaiduImageTranslate(settings, image, CancellationToken.None); }), "native picture translation requires fixed target");
                settings = Settings();
                check(await Fails(async () => { await api.BaiduImageTranslate(settings, new byte[4 * 1024 * 1024 + 1], CancellationToken.None); }), "picture translation checks binary size before HTTP");
                check(await Fails(async () => { await api.BaiduImageTranslate(settings, Png(31, 100), CancellationToken.None); }), "picture translation checks aspect ratio before HTTP");
                check(await Fails(async () => { await api.BaiduImageTranslate(settings, Png(29, 60), CancellationToken.None); }), "picture translation checks shortest edge before HTTP");
                check(await Fails(async () => { await api.RecognizeSpecial(settings, new byte[8 * 1024 * 1024], RecognitionKind.Handwriting, CancellationToken.None); }), "handwriting checks escaped base64 limit before HTTP");
                check(await Fails(async () => { await api.RecognizeSpecial(settings, new byte[8 * 1024 * 1024], RecognitionKind.BaiduTable, CancellationToken.None); }), "Baidu table checks escaped base64 limit before HTTP");
                check(await Fails(async () => { await api.RecognizeSpecial(settings, Png(14, 30), RecognitionKind.BaiduTable, CancellationToken.None); }), "Baidu table checks minimum image dimension before HTTP");
                check(await Fails(async () => { await api.RecognizeSpecial(settings, new byte[8 * 1024 * 1024], RecognitionKind.Table, CancellationToken.None); }), "table checks base64 limit before HTTP");
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel(); bool cancelled = false;
                    try { await api.RecognizeSpecial(settings, image, RecognitionKind.QrCode, cancellation.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled, "special recognition observes cancellation before HTTP");
                }
                check(handler.Requests == 0, "invalid and disabled specialized services performed no requests");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var cancelledHandler = new Handler { Handle = request => { cancellation.Cancel(); return Task.FromCanceled<HttpResponseMessage>(cancellation.Token); } };
                using (var api = new CloudApi(cancelledHandler))
                {
                    bool cancelled = false;
                    try { await api.BaiduImageTranslate(Settings(), image, cancellation.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled && cancelledHandler.Requests == 1, "in-flight picture cancellation propagates without fallback");
                }
            }
        }

        private static async Task BaiduSpecial(Action<bool, string> check)
        {
            int tokens = 0, calls = 0;
            var handler = new Handler { Handle = async request =>
            {
                string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                {
                    check(body.Contains("client_id=ocr-id") && body.Contains("client_secret=ocr-secret"), "special OCR token uses OCR credentials");
                    return Response("{\"access_token\":\"token-" + (++tokens) + "\",\"expires_in\":2592000}");
                }
                calls++;
                check(request.Content.Headers.ContentType.MediaType == "application/x-www-form-urlencoded" && body.StartsWith("image="), "Baidu special OCR uses escaped image form");
                if (calls == 1) return Response("{\"error_code\":111}");
                if (request.RequestUri.AbsolutePath.EndsWith("/handwriting", StringComparison.Ordinal)) return Response("{\"words_result\":[{\"words\":\"手写文字\"},{\"words\":\"second\"}]}");
                check(request.RequestUri.AbsolutePath.EndsWith("/qrcode", StringComparison.Ordinal), "QR uses dedicated documented endpoint");
                return Response("{\"codes_result\":[{\"type\":\"QR_CODE\",\"text\":[\"https://example.com\",\"中文\"]},{\"type\":\"CODE_128\",\"text\":[\"12345\"]}]}");
            } };
            using (var api = new CloudApi(handler))
            {
                var settings = Settings(); settings.OcrProvider = "tencent";
                var handwriting = await api.RecognizeSpecial(settings, Png(), RecognitionKind.Handwriting, CancellationToken.None);
                check(handwriting.Text == "手写文字" + Environment.NewLine + "second" && tokens == 2 && calls == 2, "handwriting refreshes rejected token once and preserves lines");
                var qr = await api.RecognizeSpecial(settings, Png(), RecognitionKind.QrCode, CancellationToken.None);
                check(qr.Text == "https://example.com" + Environment.NewLine + "中文" + Environment.NewLine + "12345" && tokens == 2, "QR extracts codes_result text arrays and shares refreshed token");
            }
            foreach (string json in new[] { "{\"error_code\":17}", "{\"codes_result\":[]}", "{}" })
            {
                var failure = new Handler { Handle = request => Task.FromResult(Response(request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) ? "{\"access_token\":\"token\"}" : json)) };
                using (var api = new CloudApi(failure))
                    check(await Fails(async () => { await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.QrCode, CancellationToken.None); }), "QR quota, empty and malformed results fail clearly");
            }
            var rejected = new Handler { Handle = request => Task.FromResult(Response(request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) ? "{\"access_token\":\"rejected\"}" : "{\"error_code\":111}")) };
            using (var api = new CloudApi(rejected))
                check(await Fails(async () => { await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.QrCode, CancellationToken.None); }, "111") && rejected.Requests == 4, "special OCR refresh stops after one rejected-token retry");
        }

        private static object Cell(int row, int col, int bottom, int right, string text)
        {
            return new Dictionary<string, object> { { "RowTl", row }, { "ColTl", col }, { "RowBr", bottom }, { "ColBr", right }, { "Text", text } };
        }

        private static object BaiduCell(int row, int col, int bottom, int right, string text)
        {
            return new Dictionary<string, object> { { "row_start", row }, { "col_start", col }, { "row_end", bottom }, { "col_end", right }, { "words", text } };
        }

        private static string BaiduTableJson(object[] cells, string spreadsheet = "")
        {
            return Json.Write(new Dictionary<string, object> {
                { "table_num", 1 }, { "excel_file", spreadsheet },
                { "tables_result", new[] { new Dictionary<string, object> {
                    { "body", cells }, { "header", new[] { new Dictionary<string, object> { { "words", "Table title" } } } },
                    { "footer", new[] { new Dictionary<string, object> { { "words", "Table note" } } } }
                } } }
            });
        }

        private static async Task BaiduTable(Action<bool, string> check)
        {
            int tokens = 0, calls = 0; byte[] workbook = Workbook();
            var handler = new Handler { Handle = async request =>
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                    return Response("{\"access_token\":\"table-token-" + (++tokens) + "\",\"expires_in\":2592000}");
                calls++;
                string body = await request.Content.ReadAsStringAsync();
                check(request.RequestUri.Host == "aip.baidubce.com" && request.RequestUri.AbsolutePath == "/rest/2.0/ocr/v1/table" && request.Content.Headers.ContentType.MediaType == "application/x-www-form-urlencoded", "Baidu table V2 uses its own endpoint and image form");
                check(body.Contains("return_excel=true") && body.Contains("cell_contents=false") && body.Contains("image="), "Baidu table requests direct base64 workbook and structured cells");
                if (calls == 1) return Response("{\"error_code\":110}");
                return Response(BaiduTableJson(new[] { BaiduCell(0, 0, 2, 1, "merged"), BaiduCell(0, 1, 1, 3, "heading"), BaiduCell(1, 1, 2, 2, "a"), BaiduCell(1, 2, 2, 3, "b") }, Convert.ToBase64String(workbook)));
            } };
            using (var api = new CloudApi(handler))
            {
                var settings = Settings(); settings.OcrProvider = "tencent";
                var result = await api.RecognizeSpecial(settings, Png(), RecognitionKind.BaiduTable, CancellationToken.None);
                string expected = "Table title" + Environment.NewLine + "merged\theading\t" + Environment.NewLine + "\ta\tb" + Environment.NewLine + "Table note";
                check(result.Text == expected, "Baidu table V2 preserves exclusive row/column spans, merged placeholders, title and note");
                check(result.Spreadsheet.SequenceEqual(workbook) && tokens == 2 && calls == 2, "Baidu table refreshes once and preserves validated inline workbook without downloading URLs");
            }
            var multiple = Json.Object(BaiduTableJson(new[] { BaiduCell(0, 0, 1, 1, "x") }));
            var single = (System.Collections.IList)Json.Get(multiple, "tables_result");
            multiple["table_num"] = 2; multiple["tables_result"] = new[] { single[0], single[0] };
            using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) ? "{\"access_token\":\"table-token\"}" : Json.Write(multiple))) }))
            {
                var result = await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.BaiduTable, CancellationToken.None);
                string tableText = "Table title" + Environment.NewLine + "x" + Environment.NewLine + "Table note";
                check(result.Text == tableText + Environment.NewLine + Environment.NewLine + tableText && result.Spreadsheet == null, "Baidu table preserves separate tables and optional workbook absence");
            }
            foreach (string json in new[] {
                "{\"table_num\":0,\"tables_result\":[]}", "{\"table_num\":1,\"tables_result\":[]}",
                BaiduTableJson(new[] { BaiduCell(0, 0, Int32.MaxValue, 1, "sparse") }),
                BaiduTableJson(new[] { BaiduCell(0, 0, 2, 2, "merge"), BaiduCell(1, 1, 2, 2, "overlap") }),
                BaiduTableJson(new[] { BaiduCell(0, 0, 1, 1, "a") }, "https://example.com/untrusted.xlsx"),
                "{\"error_code\":17}"
            })
            {
                var failure = new Handler { Handle = request => Task.FromResult(Response(request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) ? "{\"access_token\":\"table-token\"}" : json)) };
                using (var api = new CloudApi(failure))
                    check(await Fails(async () => { await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.BaiduTable, CancellationToken.None); }) && failure.Requests == 2, "Baidu table rejects no tables, count mismatch, sparse/overlap, URL workbook and quota without fallback");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var handlerCancelled = new Handler { Handle = request =>
                {
                    if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal)) return Task.FromResult(Response("{\"access_token\":\"table-token\"}"));
                    cancellation.Cancel(); return Task.FromCanceled<HttpResponseMessage>(cancellation.Token);
                } };
                using (var api = new CloudApi(handlerCancelled))
                {
                    bool cancelled = false;
                    try { await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.BaiduTable, cancellation.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled && handlerCancelled.Requests == 2, "Baidu table propagates in-flight cancellation without requests to another provider");
                }
            }
        }

        private static string TableJson(object[] cells, string spreadsheet = "")
        {
            return Json.Write(new Dictionary<string, object> { { "Response", new Dictionary<string, object> {
                { "Data", spreadsheet }, { "TableDetections", new[] { new Dictionary<string, object> { { "Cells", cells } } } }
            } } });
        }

        private static byte[] Workbook()
        {
            using (var stream = new MemoryStream())
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
                    foreach (string name in new[] { "[Content_Types].xml", "xl/workbook.xml", "xl/worksheets/sheet1.xml" })
                        using (var writer = new StreamWriter(zip.CreateEntry(name).Open(), Encoding.UTF8))
                            writer.Write(name == "[Content_Types].xml" ? "<Types><Override ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/></Types>" : "<sheet/>");
                return stream.ToArray();
            }
        }

        private static object TencentBlock(int type, params object[] cells)
        {
            return new Dictionary<string, object> { { "Type", type }, { "Cells", cells } };
        }

        private static string TencentBlocksJson(params object[] blocks)
        {
            return Json.Write(new Dictionary<string, object> { { "Response", new Dictionary<string, object> { { "TableDetections", blocks } } } });
        }

        private static async Task TencentTableTextBlocks(Action<bool, string> check)
        {
            // TableInfo.Type=0 is non-table text; row/column placeholders are not a cell span.
            object zeroSpan = Cell(0, 0, 0, 0, "paragraph before table");
            object unknownSpan = Cell(-1, -1, -1, -1, "paragraph after table");
            string mixed = TencentBlocksJson(TencentBlock(0, zeroSpan), TencentBlock(1, Cell(0, 0, 1, 2, "merged"), Cell(1, 0, 2, 1, "a"), Cell(1, 1, 2, 2, "b")), TencentBlock(0, unknownSpan));
            using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(mixed)) }))
            {
                var result = await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.Table, CancellationToken.None);
                check(result.Text == "paragraph before table" + Environment.NewLine + Environment.NewLine + "merged\t" + Environment.NewLine + "a\tb" + Environment.NewLine + Environment.NewLine + "paragraph after table", "Tencent non-table blocks preserve text without treating placeholder indices as table spans");
            }
            var header = (Dictionary<string, object>)Cell(-1, -1, -1, -1, "table title"); header["Type"] = "header";
            var footer = new Dictionary<string, object> { { "Type", "footer" }, { "Text", "table note" } };
            foreach (object caption in new object[] { header, footer })
            using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(TencentBlocksJson(TencentBlock(1, caption, Cell(0, 0, 1, 1, ""))))) }))
                check(!String.IsNullOrWhiteSpace((await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.Table, CancellationToken.None)).Text), "Tencent blank form retains nonempty header or footer instead of rejecting all text");
            string captions = TencentBlocksJson(TencentBlock(2, header, Cell(0, 0, 1, 1, "body"), footer));
            using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(captions)) }))
            {
                var result = await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.Table, CancellationToken.None);
                check(result.Text == "table title" + Environment.NewLine + "body" + Environment.NewLine + "table note", "Tencent header and footer captions do not require body grid indices");
            }
            string textOnly = TencentBlocksJson(TencentBlock(0, new Dictionary<string, object> { { "Text", "first" } }, new Dictionary<string, object> { { "Text", "second" } }));
            using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(textOnly)) }))
            {
                var result = await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.Table, CancellationToken.None);
                check(result.Text == "first" + Environment.NewLine + "second", "Tencent text-only detection remains readable when no table grid is present");
            }
            string emptyAndTable = TencentBlocksJson(new Dictionary<string, object> { { "Type", 0 }, { "Cells", null } }, TencentBlock(1, Cell(0, 0, 1, 1, "body")));
            using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(emptyAndTable)) }))
                check((await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.Table, CancellationToken.None)).Text == "body", "Tencent nullable empty text block does not invalidate a table");
            foreach (string json in new[] {
                TencentBlocksJson(TencentBlock(1, Cell(0, 0, 0, 0, "invalid body"))),
                TencentBlocksJson(TencentBlock(2, new Dictionary<string, object> { { "Text", "missing body indices" } })),
                TencentBlocksJson(TencentBlock(1, Cell(0, 0, 5001, 1, "too many rows"))),
                TencentBlocksJson(TencentBlock(0, new Dictionary<string, object> { { "Text", new string('x', 2000001) } }))
            })
                using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(json)) }))
                    check(await Fails(async () => { await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.Table, CancellationToken.None); }), "Tencent actual body indices and text resource limits remain validated");
            string invalidBaidu = BaiduTableJson(new[] { BaiduCell(0, 0, 0, 0, "invalid") });
            using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal) ? "{\"access_token\":\"table-token\"}" : invalidBaidu)) }))
                check(await Fails(async () => { await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.BaiduTable, CancellationToken.None); }), "Baidu V2 still rejects zero-width/height exclusive body spans");
        }

        private static async Task TencentTable(Action<bool, string> check)
        {
            var handler = new Handler { Handle = async request =>
            {
                string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                long timestamp = Int64.Parse(request.Headers.GetValues("X-TC-Timestamp").Single());
                check(request.RequestUri.Host == "ocr.tencentcloudapi.com" && request.Headers.GetValues("X-TC-Action").Single() == "RecognizeTableAccurateOCR" && request.Headers.GetValues("X-TC-Version").Single() == "2018-11-19", "table V3 endpoint and version match official API");
                check(!request.Headers.Contains("X-TC-Region") && Json.Text(Json.Object(body), "ImageBase64") == Convert.ToBase64String(Png()), "table V3 sends image and omits optional region");
                check(request.Headers.GetValues("Authorization").Single() == Signing.Tencent("tencent-id", "tencent-secret", body, "RecognizeTableAccurateOCR", timestamp), "table V3 uses TC3 signing");
                return Response(TableJson(new[] { Cell(0, 0, 1, 2, "merged"), Cell(1, 0, 2, 1, "line1\nline2"), Cell(1, 1, 2, 2, "a\tb\"c") }));
            } };
            using (var api = new CloudApi(handler))
            {
                var result = await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.Table, CancellationToken.None);
                check(result.Text == "merged\t" + Environment.NewLine + "\"line1\nline2\"\t\"a\tb\"\"c\"", "table TSV preserves merged placeholders, multiline and quoted cells");
                check(result.Spreadsheet == null, "table export is optional when provider returns no workbook");
            }
            byte[] workbook = Workbook();
            using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(TableJson(new[] { Cell(0, 0, 1, 1, "a") }, Convert.ToBase64String(workbook)))) }))
            {
                var result = await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.Table, CancellationToken.None);
                check(workbook.SequenceEqual(result.Spreadsheet), "table preserves provider workbook bytes after bounded xlsx package validation");
            }
            foreach (string json in new[] {
                TableJson(new[] { Cell(0, 0, Int32.MaxValue, Int32.MaxValue, "sparse") }),
                TableJson(new[] { Cell(0, 0, 1, 2, "merged"), Cell(0, 1, 1, 2, "overlap") }),
                TableJson(new object[0]), TableJson(new[] { Cell(0, 0, 1, 1, "a") }, "not-base64"),
                TableJson(new[] { Cell(0, 0, 1, 1, "a") }, Convert.ToBase64String(Encoding.UTF8.GetBytes("not-an-xlsx"))),
                "{\"Response\":{\"Error\":{\"Code\":\"RequestLimitExceeded\"},\"RequestId\":\"test-id\"}}"
            })
            {
                using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(json)) }))
                    check(await Fails(async () => { await api.RecognizeSpecial(Settings(), Png(), RecognitionKind.Table, CancellationToken.None); }), "table rejects sparse, overlapping, empty, invalid workbook and quota responses");
            }
        }

        private static async Task PictureTranslation(Action<bool, string> check)
        {
            byte[] image = Png();
            var handler = new Handler { Handle = async request =>
            {
                var parts = (MultipartFormDataContent)request.Content;
                var fields = new Dictionary<string, string>(); byte[] sentImage = null;
                foreach (var part in parts)
                {
                    string name = part.Headers.ContentDisposition.Name.Trim('"');
                    if (name == "image") sentImage = await part.ReadAsByteArrayAsync();
                    else fields[name] = await part.ReadAsStringAsync();
                }
                string imageHash;
                using (var md5 = MD5.Create()) imageHash = Signing.Hex(md5.ComputeHash(image));
                check(request.RequestUri.AbsoluteUri == "https://fanyi-api.baidu.com/api/trans/sdk/picture" && image.SequenceEqual(sentImage), "Baidu image translation posts original bytes to translation platform");
                check(fields["appid"] == "translation-id" && fields["from"] == "auto" && fields["to"] == "zh" && fields["version"] == "3" && fields["paste"] == "0", "Baidu image translation uses platform id and selectable-text response mode");
                string expectedSign;
                using (var md5 = MD5.Create()) expectedSign = Signing.Hex(md5.ComputeHash(Encoding.UTF8.GetBytes("translation-id" + imageHash + fields["salt"] + fields["cuid"] + fields["mac"] + "translation-secret")));
                check(fields["sign"] == expectedSign, "Baidu image sign hashes original binary then appid/hash/salt/cuid/mac/secret");
                return Response("{\"error_code\":\"0\",\"data\":{\"content\":[{\"src\":\"hello\",\"dst\":\"你好\",\"rect\":\"1 2 30 20\",\"points\":[{\"x\":3,\"y\":4},{\"x\":33,\"y\":4},{\"x\":33,\"y\":24},{\"x\":3,\"y\":24}]}]}}");
            } };
            using (var api = new CloudApi(handler))
            {
                var result = await api.BaiduImageTranslate(Settings(), image, CancellationToken.None);
                check(result.Blocks.Length == 1 && result.Blocks[0].Text == "hello" && result.Blocks[0].Bounds == new Rectangle(1, 2, 30, 20) && result.Translations[0] == "你好", "Baidu image translation returns paired selectable original, translation and image coordinates");
                check(result.TranslationBounds[0] == new Rectangle(3, 4, 30, 20), "Baidu image translation preserves distinct translated placement");
            }
            foreach (string json in new[] {
                "{\"error_code\":54003}", "{\"error_code\":0,\"data\":{\"content\":[]}}",
                "{\"error_code\":0,\"data\":{\"content\":[{\"src\":\"hello\",\"dst\":\"你好\"}]}}",
                "{\"error_code\":0,\"data\":{\"content\":[{\"src\":\"hello\",\"dst\":\"你好\",\"rect\":\"1 2 30 20\"}]}}",
                "{\"error_code\":0,\"data\":{\"content\":[{\"src\":\"hello\",\"dst\":\"你好\",\"rect\":\"63 0 30 20\"}]}}",
                "{\"error_code\":0,\"data\":{\"content\":[{\"src\":\"hello\",\"dst\":\"\",\"rect\":\"1 2 30 20\"}]}}"
            })
            {
                using (var api = new CloudApi(new Handler { Handle = request => Task.FromResult(Response(json)) }))
                    check(await Fails(async () => { await api.BaiduImageTranslate(Settings(), image, CancellationToken.None); }), "Baidu image quota, empty, missing/outside coordinates and missing translation fail clearly");
            }
        }
    }
}

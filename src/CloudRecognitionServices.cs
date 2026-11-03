using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal enum RecognitionKind { Text, Handwriting, QrCode, Table, BaiduTable }

    internal sealed class RecognitionResult
    {
        public string Text { get; private set; }
        public byte[] Spreadsheet { get; private set; }
        public RecognitionResult(string text, byte[] spreadsheet = null) { Text = text; Spreadsheet = spreadsheet; }
    }

    internal sealed class ImageTranslationResult
    {
        public OcrTextBlock[] Blocks { get; private set; }
        public string[] Translations { get; private set; }
        public Rectangle[] TranslationBounds { get; private set; }
        public ImageTranslationResult(OcrTextBlock[] blocks, string[] translations, Rectangle[] translationBounds)
        { Blocks = blocks; Translations = translations; TranslationBounds = translationBounds; }
    }

    internal sealed partial class CloudApi
    {
        public async Task<RecognitionResult> RecognizeSpecial(AppSettings settings, byte[] image, RecognitionKind kind, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (kind == RecognitionKind.Text) return new RecognitionResult(await Ocr(settings, image, cancellation).ConfigureAwait(false));
            if (kind == RecognitionKind.Table)
            {
                RequireRecognition(settings.TencentTableEnabled, settings.TencentId, settings.TencentSecret, "腾讯表格识别");
                return await RecognizeTable(settings, image, cancellation).ConfigureAwait(false);
            }
            if (kind != RecognitionKind.Handwriting && kind != RecognitionKind.QrCode && kind != RecognitionKind.BaiduTable) throw new ArgumentOutOfRangeException("kind");
            bool handwriting = kind == RecognitionKind.Handwriting, table = kind == RecognitionKind.BaiduTable;
            RequireRecognition(table ? settings.BaiduTableEnabled : handwriting ? settings.BaiduHandwritingEnabled : settings.BaiduQrEnabled,
                settings.BaiduOcrKey, settings.BaiduOcrSecret, table ? "百度表格识别 V2" : handwriting ? "百度手写识别" : "百度二维码识别");
            RequireImageBytes(image, 8 * 1024 * 1024);
            string escapedImage = WebUtility.UrlEncode(Convert.ToBase64String(image));
            if (escapedImage.Length > 8 * 1024 * 1024) throw new InvalidOperationException("图片编码后超过百度专项 OCR 的 8 MB 限制，请缩小截图区域。");
            RecognitionImageSize(image, 15, handwriting ? 4096 : 8192, false, false);
            string access = await BaiduToken(settings, cancellation).ConfigureAwait(false);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://aip.baidubce.com/rest/2.0/ocr/v1/" + (table ? "table" : handwriting ? "handwriting" : "qrcode") + "?access_token=" + Uri.EscapeDataString(access)))
                {
                    request.Content = new StringContent("image=" + escapedImage + (table ? "&return_excel=true&cell_contents=false" : ""), Encoding.UTF8, "application/x-www-form-urlencoded");
                    var result = await Send(request, cancellation).ConfigureAwait(false);
                    cancellation.ThrowIfCancellationRequested();
                    string code = Json.Text(result, "error_code");
                    if ((code == "110" || code == "111") && attempt == 0)
                    { access = await BaiduToken(settings, cancellation, access).ConfigureAwait(false); continue; }
                    if (code.Length > 0 && code != "0") throw ApiError("百度专项 OCR", code, Json.Text(result, "log_id"));
                    if (table) return BaiduTableResult(result, cancellation);
                    var values = Json.Get(result, handwriting ? "words_result" : "codes_result") as IList;
                    if (values == null || values.Count > 10000) throw new InvalidOperationException("识别服务返回的文字结果无效。");
                    var output = new StringBuilder();
                    foreach (object value in values)
                    {
                        var line = Json.Map(value);
                        if (handwriting) AppendRecognitionLine(output, Json.Text(line, "words"));
                        else
                        {
                            var texts = Json.Get(line, "text") as IList;
                            if (texts == null) throw new InvalidOperationException("二维码识别响应缺少解码内容。");
                            foreach (object text in texts)
                            {
                                if (!(text is string)) throw new InvalidOperationException("二维码识别返回的内容格式无效。");
                                AppendRecognitionLine(output, (string)text);
                            }
                        }
                    }
                    if (output.Length == 0) throw new InvalidOperationException(handwriting ? "未识别到手写文字。" : "未识别到二维码或条形码内容。");
                    return new RecognitionResult(output.ToString());
                }
            }
            throw new InvalidOperationException("百度专项 OCR 认证失败，请检查密钥。");
        }

        private static void RequireRecognition(bool enabled, string id, string secret, string service)
        {
            if (!enabled) throw new InvalidOperationException("请先在设置中启用" + service + "。");
            if (String.IsNullOrWhiteSpace(id) || String.IsNullOrWhiteSpace(secret)) throw new InvalidOperationException("请先在 API 设置中填写" + service + "的对应密钥。");
        }

        private static void RequireImageBytes(byte[] image, int limit)
        {
            if (image == null || image.Length == 0) throw new InvalidOperationException("图片为空。");
            if (image.Length > limit) throw new InvalidOperationException("图片超过服务大小限制，请缩小截图区域。");
        }

        private static Size RecognitionImageSize(byte[] image, int minimum, int maximum, bool aspectLimit, bool translation)
        {
            try
            {
                using (var stream = new MemoryStream(image, false))
                using (var decoded = Image.FromStream(stream, false, false))
                {
                    if (decoded.RawFormat.Guid != ImageFormat.Png.Guid && decoded.RawFormat.Guid != ImageFormat.Jpeg.Guid &&
                        (translation || decoded.RawFormat.Guid != ImageFormat.Bmp.Guid)) throw new InvalidOperationException("图片格式不受支持，请使用 PNG 或 JPEG。");
                    int small = Math.Min(decoded.Width, decoded.Height), large = Math.Max(decoded.Width, decoded.Height);
                    if (small < minimum || large > maximum || aspectLimit && (long)large > (long)small * 3)
                        throw new InvalidOperationException("图片尺寸不符合服务要求（短边至少 " + minimum + " 像素，长边最多 " + maximum + " 像素" + (aspectLimit ? "，长宽比不超过 3:1" : "") + "）。");
                    return decoded.Size;
                }
            }
            catch (ArgumentException) { throw new InvalidOperationException("图片数据无法读取，请重新截图。"); }
        }

        private static void AppendRecognitionLine(StringBuilder output, string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            if ((long)output.Length + text.Length + (output.Length > 0 ? Environment.NewLine.Length : 0) > 2000000) throw new InvalidOperationException("识别服务返回的文字超过处理上限。");
            if (output.Length > 0) output.Append(Environment.NewLine);
            output.Append(text);
        }

        private async Task<RecognitionResult> RecognizeTable(AppSettings settings, byte[] image, CancellationToken cancellation)
        {
            RequireImageBytes(image, 8 * 1024 * 1024);
            string encoded = Convert.ToBase64String(image);
            if (encoded.Length > 10 * 1024 * 1024) throw new InvalidOperationException("图片编码后超过腾讯表格 OCR 的 10 MB 限制。");
            string body = Json.Write(new Dictionary<string, object> { { "ImageBase64", encoded } });
            const string action = "RecognizeTableAccurateOCR";
            long timestamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://ocr.tencentcloudapi.com/"))
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.Host = "ocr.tencentcloudapi.com";
                request.Headers.TryAddWithoutValidation("Authorization", Signing.Tencent(settings.TencentId, settings.TencentSecret, body, action, timestamp));
                request.Headers.Add("X-TC-Action", action);
                request.Headers.Add("X-TC-Version", "2018-11-19");
                request.Headers.Add("X-TC-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
                var result = Json.Map(Json.Get(await Send(request, cancellation).ConfigureAwait(false), "Response"));
                cancellation.ThrowIfCancellationRequested();
                var error = Json.Map(Json.Get(result, "Error"));
                if (error != null) throw ApiError("腾讯表格识别", Json.Text(error, "Code"), Json.Text(result, "RequestId"));
                return new RecognitionResult(TableText(TencentTableBlocks(Json.Get(result, "TableDetections") as IList, cancellation), cancellation), TableSpreadsheet(Json.Text(result, "Data")));
            }
        }

        // TableInfo.Type=0 denotes non-table text, not cells with a valid row/column span.
        // https://cloud.tencent.com/document/api/866/33527#TableInfo
        private static IList TencentTableBlocks(IList blocks, CancellationToken cancellation)
        {
            if (blocks == null || blocks.Count == 0 || blocks.Count > 100) throw new InvalidOperationException("表格识别未返回有效单元格。");
            var normalized = new List<object>(); int records = 0;
            foreach (object value in blocks)
            {
                cancellation.ThrowIfCancellationRequested();
                var block = Json.Map(value); int type = -1;
                if (Json.Get(block, "Type") != null && (!Coordinate(block, "Type", out type) || type > 2)) throw new InvalidOperationException("腾讯表格识别返回的文本块类型无效。");
                var cells = Json.Get(block, "Cells") as IList;
                if (type == 0 && (cells == null || cells.Count == 0)) continue;
                if (cells == null || cells.Count == 0 || cells.Count > 100000 - records) throw new InvalidOperationException("表格识别未返回有效单元格，或单元格数量超过处理上限。");
                records += cells.Count;
                var body = new List<object>(); var header = new StringBuilder(); var footer = new StringBuilder();
                foreach (object item in cells)
                {
                    var cell = Json.Map(item);
                    if (cell == null) throw new InvalidOperationException("腾讯表格识别返回的文字结果无效。");
                    string cellType = Json.Text(cell, "Type");
                    if (type == 0 || cellType == "header") AppendRecognitionLine(header, Json.Text(cell, "Text"));
                    else if (cellType == "footer") AppendRecognitionLine(footer, Json.Text(cell, "Text"));
                    else body.Add(cell);
                }
                if (body.Count == 0)
                {
                    AppendRecognitionLine(header, footer.ToString());
                    if (header.Length > 0) normalized.Add(new Dictionary<string, object> { { "PlainText", header.ToString() } });
                }
                else normalized.Add(new Dictionary<string, object> { { "Cells", body }, { "HeaderText", header.ToString() }, { "FooterText", footer.ToString() } });
            }
            return normalized;
        }

        private static string TableText(IList tables, CancellationToken cancellation)
        {
            if (tables == null || tables.Count == 0 || tables.Count > 100) throw new InvalidOperationException("表格识别未返回有效单元格。");
            var output = new StringBuilder();
            int totalCells = 0; bool hasText = false;
            foreach (object table in tables)
            {
                cancellation.ThrowIfCancellationRequested();
                string plain = Json.Text(Json.Map(table), "PlainText");
                if (plain.Length > 0)
                {
                    if (output.Length > 0) output.Append(Environment.NewLine);
                    AppendRecognitionLine(output, plain);
                    hasText |= !String.IsNullOrWhiteSpace(plain);
                    continue;
                }
                var cells = Json.Get(Json.Map(table), "Cells") as IList;
                if (cells == null || cells.Count == 0) throw new InvalidOperationException("表格识别未返回有效单元格。");
                int rows = 0, columns = 0;
                foreach (object value in cells)
                {
                    int top, left, bottom, right; var cell = Json.Map(value);
                    if (!Coordinate(cell, "RowTl", out top) || !Coordinate(cell, "ColTl", out left) || !Coordinate(cell, "RowBr", out bottom) || !Coordinate(cell, "ColBr", out right) ||
                        bottom <= top || right <= left || bottom > 5000 || right > 1000) throw new InvalidOperationException("表格识别返回的行列索引无效或超出处理上限。");
                    rows = Math.Max(rows, bottom); columns = Math.Max(columns, right);
                }
                if ((long)rows * columns > 100000 - totalCells) throw new InvalidOperationException("表格单元格数量超过处理上限。");
                totalCells += rows * columns;
                var occupied = new bool[rows, columns]; var text = new string[rows, columns];
                foreach (object value in cells)
                {
                    var cell = Json.Map(value);
                    int top, left, bottom, right;
                    Coordinate(cell, "RowTl", out top); Coordinate(cell, "ColTl", out left); Coordinate(cell, "RowBr", out bottom); Coordinate(cell, "ColBr", out right);
                    for (int row = top; row < bottom; row++)
                        for (int column = left; column < right; column++)
                        {
                            if (occupied[row, column]) throw new InvalidOperationException("表格识别返回了重叠的单元格。");
                            occupied[row, column] = true;
                        }
                    text[top, left] = Json.Text(cell, "Text");
                    hasText |= !String.IsNullOrWhiteSpace(text[top, left]);
                }
                if (output.Length > 0) output.Append(Environment.NewLine).Append(Environment.NewLine);
                string header = Json.Text(Json.Map(table), "HeaderText"), footer = Json.Text(Json.Map(table), "FooterText");
                hasText |= !String.IsNullOrWhiteSpace(header) || !String.IsNullOrWhiteSpace(footer);
                if ((long)output.Length + header.Length > 2000000) throw new InvalidOperationException("表格文字超过处理上限。");
                if (header.Length > 0) output.Append(header).Append(Environment.NewLine);
                for (int row = 0; row < rows; row++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (row > 0) output.Append(Environment.NewLine);
                    for (int column = 0; column < columns; column++)
                    {
                        if (column > 0) output.Append('\t');
                        string content = text[row, column] ?? "";
                        if ((long)output.Length + content.Length * 2L > 2000000) throw new InvalidOperationException("表格文字超过处理上限。");
                        bool quote = content.IndexOfAny(new[] { '\t', '\r', '\n', '"' }) >= 0;
                        output.Append(quote ? "\"" + content.Replace("\"", "\"\"") + "\"" : content);
                    }
                }
                if (footer.Length > 0) AppendRecognitionLine(output, footer);
            }
            if (!hasText) throw new InvalidOperationException("表格识别未返回文字内容。");
            return output.ToString();
        }

        // https://ai.baidu.com/ai-doc/OCR/Al1zvpylt : line indices are zero-based, end indices exclusive.
        private static RecognitionResult BaiduTableResult(Dictionary<string, object> result, CancellationToken cancellation)
        {
            var tables = Json.Get(result, "tables_result") as IList;
            int count;
            if (!Coordinate(result, "table_num", out count) || count == 0 || count > 100 || tables == null || tables.Count != count)
                throw new InvalidOperationException("百度表格识别未返回有效表格，或表格数量不匹配。");
            var normalized = new List<object>(); int cellCount = 0;
            foreach (object value in tables)
            {
                cancellation.ThrowIfCancellationRequested();
                var table = Json.Map(value); var body = Json.Get(table, "body") as IList;
                if (body == null || body.Count == 0 || body.Count > 100000 - cellCount) throw new InvalidOperationException("百度表格识别未返回有效单元格，或单元格数量超过处理上限。");
                cellCount += body.Count; var cells = new List<object>();
                foreach (object item in body)
                {
                    var cell = Json.Map(item);
                    cells.Add(new Dictionary<string, object> {
                        { "RowTl", Json.Get(cell, "row_start") }, { "ColTl", Json.Get(cell, "col_start") },
                        { "RowBr", Json.Get(cell, "row_end") }, { "ColBr", Json.Get(cell, "col_end") }, { "Text", Json.Text(cell, "words") }
                    });
                }
                normalized.Add(new Dictionary<string, object> {
                    { "Cells", cells }, { "HeaderText", BaiduTableCaption(Json.Get(table, "header") as IList) },
                    { "FooterText", BaiduTableCaption(Json.Get(table, "footer") as IList) }
                });
            }
            return new RecognitionResult(TableText(normalized, cancellation), TableSpreadsheet(Json.Text(result, "excel_file")));
        }

        private static string BaiduTableCaption(IList lines)
        {
            var output = new StringBuilder();
            if (lines != null)
                foreach (object line in lines) AppendRecognitionLine(output, Json.Text(Json.Map(line), "words"));
            return output.ToString();
        }

        private static byte[] TableSpreadsheet(string encoded)
        {
            if (String.IsNullOrEmpty(encoded)) return null;
            if (encoded.Length > 22 * 1024 * 1024) throw new InvalidOperationException("表格 Excel 文件超过处理上限。");
            try
            {
                byte[] bytes = Convert.FromBase64String(encoded);
                if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException();
                using (var stream = new MemoryStream(bytes, false))
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Read))
                {
                    if (zip.Entries.Count > 1024) throw new InvalidDataException();
                    long expanded = 0; bool sheet = false; var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in zip.Entries)
                    {
                        expanded += entry.Length;
                        if (entry.Length > 16 * 1024 * 1024 || expanded > 64 * 1024 * 1024 || !names.Add(entry.FullName) || entry.FullName.Contains("..") || entry.FullName.StartsWith("/", StringComparison.Ordinal) || entry.FullName.Contains("\\") || entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                        sheet |= entry.FullName.StartsWith("xl/worksheets/", StringComparison.Ordinal) && entry.FullName.EndsWith(".xml", StringComparison.Ordinal);
                    }
                    var types = zip.GetEntry("[Content_Types].xml");
                    if (types == null || types.Length > 65536 || zip.GetEntry("xl/workbook.xml") == null || !sheet) throw new InvalidDataException();
                    using (var reader = new StreamReader(types.Open(), Encoding.UTF8))
                        if (!reader.ReadToEnd().Contains("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml")) throw new InvalidDataException();
                }
                return bytes;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidDataException || ex is IOException || ex is NotSupportedException)
            { throw new InvalidOperationException("表格服务返回的 Excel 文件格式无效。"); }
        }

        // Translation-platform legacy API, distinct from intelligent-cloud OCR credentials.
        // https://fanyi-api.baidu.com/doc/26
        public async Task<ImageTranslationResult> BaiduImageTranslate(AppSettings settings, byte[] image, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            RequireRecognition(settings.BaiduImageTranslateEnabled, settings.BaiduTranslateId, settings.BaiduTranslateSecret, "百度图片翻译（翻译开放平台 APP ID / 密钥）");
            if (String.IsNullOrWhiteSpace(settings.TargetLanguage) || settings.TargetLanguage == "smart" || settings.TargetLanguage == "auto") throw new InvalidOperationException("百度图片翻译需要选择固定的目标语言。");
            RequireImageBytes(image, 4 * 1024 * 1024);
            Size size = RecognitionImageSize(image, 30, 4096, true, true);
            string digest;
            using (var md5 = MD5.Create()) digest = Signing.Hex(md5.ComputeHash(image));
            string salt = Guid.NewGuid().ToString("N");
            const string cuid = "APICUID", mac = "mac";
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://fanyi-api.baidu.com/api/trans/sdk/picture"))
            {
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(settings.BaiduTranslateId), "appid");
                form.Add(new StringContent(BaiduLanguage(settings.SourceLanguage ?? "auto")), "from");
                form.Add(new StringContent(BaiduLanguage(settings.TargetLanguage)), "to");
                form.Add(new StringContent(salt), "salt"); form.Add(new StringContent(cuid), "cuid"); form.Add(new StringContent(mac), "mac");
                form.Add(new StringContent("3"), "version"); form.Add(new StringContent("0"), "paste");
                form.Add(new StringContent(Signing.Baidu(settings.BaiduTranslateId, digest, salt + cuid + mac, settings.BaiduTranslateSecret)), "sign");
                var file = new ByteArrayContent(image);
                bool png = image[0] == 137;
                file.Headers.ContentType = new MediaTypeHeaderValue(png ? "image/png" : "image/jpeg");
                form.Add(file, "image", png ? "image.png" : "image.jpg");
                request.Content = form;
                var result = await Send(request, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                string code = Json.Text(result, "error_code");
                if (code != "0") throw ApiError("百度图片翻译", code, "");
                var content = Json.Get(Json.Map(Json.Get(result, "data")), "content") as IList;
                if (content == null || content.Count == 0 || content.Count > 10000) throw new InvalidOperationException("百度图片翻译未返回可选择的文字块。");
                var blocks = new OcrTextBlock[content.Count]; var translations = new string[content.Count]; var translatedBounds = new Rectangle[content.Count];
                int characters = 0;
                for (int index = 0; index < content.Count; index++)
                {
                    var value = Json.Map(content[index]); string original = Json.Text(value, "src"), translated = Json.Text(value, "dst");
                    Rectangle bounds;
                    if (String.IsNullOrWhiteSpace(original) || String.IsNullOrWhiteSpace(translated)) throw new InvalidOperationException("百度图片翻译返回了缺失的原文或译文。");
                    if (!PictureRectangle(Json.Text(value, "rect"), size, out bounds)) throw new InvalidOperationException("百度图片翻译缺少有效原文坐标，无法保留文字选择。");
                    characters += original.Length + translated.Length;
                    if (characters > 2000000) throw new InvalidOperationException("百度图片翻译文字超过处理上限。");
                    blocks[index] = new OcrTextBlock(original, bounds); translations[index] = translated;
                    translatedBounds[index] = PictureTranslatedBounds(Json.Get(value, "points") as IList, size);
                }
                return new ImageTranslationResult(blocks, translations, translatedBounds);
            }
        }

        private static bool PictureRectangle(string text, Size imageSize, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            string[] values = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            int left, top, width, height;
            if (values.Length != 4 || !Int32.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out left) || !Int32.TryParse(values[1], NumberStyles.None, CultureInfo.InvariantCulture, out top) ||
                !Int32.TryParse(values[2], NumberStyles.None, CultureInfo.InvariantCulture, out width) || !Int32.TryParse(values[3], NumberStyles.None, CultureInfo.InvariantCulture, out height) ||
                width <= 0 || height <= 0 || (long)left + width > imageSize.Width || (long)top + height > imageSize.Height) return false;
            bounds = new Rectangle(left, top, width, height); return true;
        }

        private static Rectangle PictureTranslatedBounds(IList points, Size imageSize)
        {
            if (points == null || points.Count != 4) throw new InvalidOperationException("百度图片翻译缺少有效译文坐标，无法保留文字选择。");
            int left = imageSize.Width, top = imageSize.Height, right = 0, bottom = 0;
            foreach (object item in points)
            {
                int x, y; var point = Json.Map(item);
                if (!Coordinate(point, "x", out x) || !Coordinate(point, "y", out y) || x > imageSize.Width || y > imageSize.Height) throw new InvalidOperationException("百度图片翻译的译文坐标超出图片范围。");
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
            if (right <= left || bottom <= top) throw new InvalidOperationException("百度图片翻译的译文坐标无效。");
            return Rectangle.FromLTRB(left, top, right, bottom);
        }
    }
}

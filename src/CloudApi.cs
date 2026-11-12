using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class Signing
    {
        public static string Hex(byte[] data) { return BitConverter.ToString(data).Replace("-", "").ToLowerInvariant(); }
        public static string Sha(string value)
        {
            using (var hash = SHA256.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }
        public static byte[] Hmac(byte[] key, string text)
        {
            using (var hash = new HMACSHA256(key)) return hash.ComputeHash(Encoding.UTF8.GetBytes(text));
        }
        public static string Baidu(string id, string text, string salt, string secret)
        {
            using (var hash = MD5.Create()) return Hex(hash.ComputeHash(Encoding.UTF8.GetBytes(id + text + salt + secret)));
        }
        // Protocol implementation cross-checked against the vendors' canonical-request examples.
        public static string Tencent(string id, string secret, string body, string action, long timestamp)
        {
            string date = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(timestamp).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            const string headers = "content-type;host;x-tc-action";
            string canonical = "POST\n/\n\ncontent-type:application/json; charset=utf-8\nhost:ocr.tencentcloudapi.com\nx-tc-action:" + action.ToLowerInvariant() + "\n\n" + headers + "\n" + Sha(body);
            string scope = date + "/ocr/tc3_request";
            string signing = "TC3-HMAC-SHA256\n" + timestamp.ToString(CultureInfo.InvariantCulture) + "\n" + scope + "\n" + Sha(canonical);
            byte[] key = Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes("TC3" + secret), date), "ocr"), "tc3_request");
            return "TC3-HMAC-SHA256 Credential=" + id + "/" + scope + ", SignedHeaders=" + headers + ", Signature=" + Hex(Hmac(key, signing));
        }
        public static string Volc(string id, string secret, string body, DateTime utc)
        {
            string date = utc.ToUniversalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string full = utc.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            const string headers = "content-type;host;x-content-sha256;x-date";
            string canonical = "POST\n/\nAction=TranslateText&Version=2020-06-01\ncontent-type:application/json\nhost:translate.volcengineapi.com\nx-content-sha256:" + Sha(body) + "\nx-date:" + full + "\n\n" + headers + "\n" + Sha(body);
            string scope = date + "/cn-north-1/translate/request";
            string signing = "HMAC-SHA256\n" + full + "\n" + scope + "\n" + Sha(canonical);
            byte[] key = Hmac(Hmac(Hmac(Hmac(Encoding.UTF8.GetBytes(secret), date), "cn-north-1"), "translate"), "request");
            return "HMAC-SHA256 Credential=" + id + "/" + scope + ", SignedHeaders=" + headers + ", Signature=" + Hex(Hmac(key, signing));
        }
    }

    internal sealed partial class CloudApi : IDisposable
    {
        private readonly HttpClient client;
        private readonly BingTranslator bing;
        private string token, tokenIdentity;
        private DateTime tokenExpiry;
        private readonly SemaphoreSlim tokenLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim baiduTranslationLock = new SemaphoreSlim(1, 1);
        private long baiduNextRequest;
        private readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        private readonly Queue<string> cacheOrder = new Queue<string>();

        public CloudApi() : this(new HttpClientHandler { AllowAutoRedirect = false, MaxConnectionsPerServer = 8, AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate }) { }
        internal CloudApi(HttpMessageHandler handler)
        {
            client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = 24 * 1024 * 1024 };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SnapTransOCR/0.2.29");
            bing = new BingTranslator(client);
        }

        public async Task<string> Ocr(AppSettings settings, byte[] image, CancellationToken cancellation)
        {
            var result = await OcrResponse(settings, image, false, cancellation).ConfigureAwait(false);
            return settings.OcrProvider == "tencent" ? Json.Lines(result, "TextDetections", "DetectedText") : Json.Lines(result, "words_result", "words");
        }

        public async Task<OcrTextBlock[]> OcrWithLayout(AppSettings settings, byte[] image, CancellationToken cancellation)
        {
            var result = await OcrResponse(settings, image, true, cancellation).ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            bool tencent = settings.OcrProvider == "tencent";
            var items = Json.Get(result, tencent ? "TextDetections" : "words_result") as IList;
            if (items == null) throw new InvalidOperationException("识别服务响应缺少文字位置结果。");
            var blocks = new List<OcrTextBlock>();
            double angle;
            bool unrotated = !tencent || Json.Get(result, "Angle") == null ||
                Double.TryParse(Json.Text(result, "Angle"), NumberStyles.Float, CultureInfo.InvariantCulture, out angle) && angle % 360 == 0;
            foreach (object item in items)
            {
                var line = Json.Map(item);
                string text = Json.Text(line, tencent ? "DetectedText" : "words").Trim();
                Rectangle bounds;
                bool positioned = tencent
                    ? PolygonBounds(Json.Get(line, "Polygon") as IList, out bounds)
                    : BoxBounds(Json.Map(Json.Get(line, "location")), false, out bounds);
                // Tencent ItemPolygon uses the deskewed image, while Polygon addresses the original image.
                if (!positioned && tencent && unrotated) positioned = BoxBounds(Json.Map(Json.Get(line, "ItemPolygon")), true, out bounds);
                if (text.Length > 0 && positioned) blocks.Add(new OcrTextBlock(text, bounds));
            }
            return blocks.ToArray();
        }

        private async Task<Dictionary<string, object>> OcrResponse(AppSettings settings, byte[] image, bool layout, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            settings.RequireOcr();
            if (image == null || image.Length == 0) throw new InvalidOperationException("图片为空。");
            string base64 = Convert.ToBase64String(image);
            if (settings.OcrProvider == "tencent")
            {
                if (base64.Length > 10 * 1024 * 1024) throw new InvalidOperationException("图片超过腾讯 OCR 大小限制，请缩小截图区域。");
                string action = settings.AccurateOcr ? "GeneralAccurateOCR" : "GeneralBasicOCR";
                var payload = new Dictionary<string, object> { { "ImageBase64", base64 } };
                if (!settings.AccurateOcr) payload.Add("LanguageType", "auto");
                string body = Json.Write(payload);
                long stamp = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://ocr.tencentcloudapi.com/"))
                {
                    request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    request.Headers.Host = "ocr.tencentcloudapi.com";
                    request.Headers.TryAddWithoutValidation("Authorization", Signing.Tencent(settings.TencentId, settings.TencentSecret, body, action, stamp));
                    request.Headers.Add("X-TC-Action", action);
                    request.Headers.Add("X-TC-Version", "2018-11-19");
                    request.Headers.Add("X-TC-Timestamp", stamp.ToString(CultureInfo.InvariantCulture));
                    var result = Json.Map(Json.Get(await Send(request, cancellation).ConfigureAwait(false), "Response"));
                    var error = Json.Map(Json.Get(result, "Error"));
                    if (error != null) throw ApiError("腾讯 OCR", Json.Text(error, "Code"), Json.Text(result, "RequestId"));
                    return result;
                }
            }
            // Reserve headroom for form escaping, independently of the binary PNG/JPEG size.
            string formImage = WebUtility.UrlEncode(base64);
            if (formImage.Length > 10 * 1024 * 1024) throw new InvalidOperationException("图片超过百度 OCR 大小限制，请缩小截图区域。");
            string access = await BaiduToken(settings, cancellation).ConfigureAwait(false);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string mode = (settings.AccurateOcr ? "accurate" : "general") + (layout ? "" : "_basic");
                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://aip.baidubce.com/rest/2.0/ocr/v1/" + mode + "?access_token=" + Uri.EscapeDataString(access)))
                {
                    request.Content = new StringContent("image=" + formImage + "&language_type=auto_detect&detect_direction=true", Encoding.UTF8, "application/x-www-form-urlencoded");
                    var result = await Send(request, cancellation).ConfigureAwait(false);
                    string code = Json.Text(result, "error_code");
                    if ((code == "110" || code == "111") && attempt == 0)
                    {
                        access = await BaiduToken(settings, cancellation, access).ConfigureAwait(false);
                        continue;
                    }
                    if (code.Length > 0) throw ApiError("百度 OCR", code, Json.Text(result, "log_id"));
                    return result;
                }
            }
            throw new InvalidOperationException("百度 OCR 认证失败，请检查密钥。");
        }

        private static bool Coordinate(Dictionary<string, object> value, string key, out int number)
        {
            number = 0;
            object raw = Json.Get(value, key);
            if (!(raw is int || raw is long || raw is double || raw is decimal)) return false;
            double parsed = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            if (Double.IsNaN(parsed) || parsed < 0 || parsed > Int32.MaxValue || parsed != Math.Truncate(parsed)) return false;
            number = (int)parsed; return true;
        }
        private static bool BoxBounds(Dictionary<string, object> value, bool tencent, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            int x, y, width, height;
            if (!Coordinate(value, tencent ? "X" : "left", out x) || !Coordinate(value, tencent ? "Y" : "top", out y) ||
                !Coordinate(value, tencent ? "Width" : "width", out width) || !Coordinate(value, tencent ? "Height" : "height", out height) ||
                width == 0 || height == 0 || (long)x + width > Int32.MaxValue || (long)y + height > Int32.MaxValue) return false;
            bounds = new Rectangle(x, y, width, height); return true;
        }
        private static bool PolygonBounds(IList points, out Rectangle bounds)
        {
            bounds = Rectangle.Empty;
            if (points == null || points.Count != 4) return false;
            int left = Int32.MaxValue, top = Int32.MaxValue, right = 0, bottom = 0;
            foreach (object point in points)
            {
                int x, y;
                if (!Coordinate(Json.Map(point), "X", out x) || !Coordinate(Json.Map(point), "Y", out y)) return false;
                left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
            }
            if (right <= left || bottom <= top) return false;
            bounds = Rectangle.FromLTRB(left, top, right, bottom); return true;
        }

        private async Task<string> BaiduToken(AppSettings settings, CancellationToken cancellation, string rejectedToken = null)
        {
            string identity = Signing.Sha(settings.BaiduOcrKey + "\n" + settings.BaiduOcrSecret);
            await tokenLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (identity == tokenIdentity && !String.IsNullOrEmpty(token) && token != rejectedToken && DateTime.UtcNow < tokenExpiry) return token;
                using (var request = new HttpRequestMessage(HttpMethod.Post, "https://aip.baidubce.com/oauth/2.0/token"))
                {
                    request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                        { "grant_type", "client_credentials" }, { "client_id", settings.BaiduOcrKey }, { "client_secret", settings.BaiduOcrSecret } });
                    var result = await Send(request, cancellation).ConfigureAwait(false);
                    string received = Json.Text(result, "access_token");
                    if (received.Length == 0) throw ApiError("百度 OCR 认证", Json.Text(result, "error"), "");
                    double seconds;
                    if (!Double.TryParse(Json.Text(result, "expires_in"), out seconds)) seconds = 3600;
                    token = received;
                    tokenIdentity = identity;
                    tokenExpiry = DateTime.UtcNow.AddSeconds(Math.Max(0, Math.Min(seconds - 60, 2592000)));
                    return token;
                }
            }
            finally { tokenLock.Release(); }
        }

        public async Task<string> Translate(AppSettings settings, string text, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            settings.RequireTranslation();
            if (String.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("没有可翻译的文字。");
            if (text.Length > 100000) throw new InvalidOperationException("文字过长，请分段翻译（每次最多 10 万字符）。");
            string target = settings.EffectiveTarget(text);
            bool extended = settings.TranslateProvider == "ark" || settings.TranslateProvider == "baidu_llm" || settings.TranslateProvider == "baidu_domain";
            string id = extended || settings.TranslateProvider == "bing" ? "" : settings.TranslateProvider == "volc" ? settings.VolcId : settings.BaiduTranslateId;
            string secret = extended || settings.TranslateProvider == "bing" ? "" : settings.TranslateProvider == "volc" ? settings.VolcSecret : settings.BaiduTranslateSecret;
            string cacheKey = Signing.Sha(Json.Write(new[] { settings.TranslateProvider, id, secret, settings.SourceLanguage, target, text, extended ? ExtendedCacheIdentity(settings) : "" }));
            string cached;
            lock (cache) { if (cache.TryGetValue(cacheKey, out cached)) return cached; }
            var output = new List<string>();
            int outputLength = 0;
            int limit = settings.TranslateProvider == "baidu" ? 5500 : 4500;
            if (extended) output.Add(await TranslateExtended(settings, text, target, cancellation).ConfigureAwait(false));
            else foreach (string chunk in SplitText(text, limit, settings.TranslateProvider == "baidu" ? 1000 : Int32.MaxValue))
            {
                cancellation.ThrowIfCancellationRequested();
                if (settings.TranslateProvider == "baidu")
                {
                    await WaitBaiduTranslation(cancellation).ConfigureAwait(false);
                    string salt = Guid.NewGuid().ToString("N");
                    using (var request = new HttpRequestMessage(HttpMethod.Post, "https://fanyi-api.baidu.com/api/trans/vip/translate"))
                    {
                        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                            { "q", chunk }, { "from", BaiduLanguage(settings.SourceLanguage ?? "auto") }, { "to", BaiduLanguage(target) }, { "appid", settings.BaiduTranslateId },
                            { "salt", salt }, { "sign", Signing.Baidu(settings.BaiduTranslateId, chunk, salt, settings.BaiduTranslateSecret) } });
                        var result = await Send(request, cancellation).ConfigureAwait(false);
                        string code = Json.Text(result, "error_code");
                        if (code.Length > 0 && code != "52000") throw ApiError("百度翻译", code, "");
                        output.Add(Json.Lines(result, "trans_result", "dst"));
                    }
                }
                else if (settings.TranslateProvider == "bing")
                {
                    output.Add((await bing.Translate(new[] { chunk }, settings.SourceLanguage, target, cancellation).ConfigureAwait(false))[0]);
                }
                else if (settings.TranslateProvider == "volc")
                {
                    output.Add((await VolcTranslate(settings, new[] { chunk }, target, cancellation).ConfigureAwait(false))[0]);
                }
                else throw new InvalidOperationException("未知翻译服务，请重新选择服务。");
                outputLength += output[output.Count - 1].Length + Environment.NewLine.Length;
                if (outputLength > 2000000) throw new InvalidOperationException("翻译结果超过处理上限，请缩短原文。");
            }
            string translated = String.Join(Environment.NewLine, output).Trim();
            if (translated.Length == 0) throw new InvalidOperationException("翻译服务返回了空结果。");
            if (text.Length <= 8000 && translated.Length <= 16000)
            {
                lock (cache)
                {
                    if (!cache.ContainsKey(cacheKey))
                    {
                        if (cache.Count >= 32) cache.Remove(cacheOrder.Dequeue());
                        cacheOrder.Enqueue(cacheKey);
                        cache[cacheKey] = translated;
                    }
                }
            }
            return translated;
        }

        public async Task<string[]> TranslateBlocks(AppSettings settings, string[] text, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            settings.RequireTranslation();
            if (text == null) throw new ArgumentNullException("text");
            if (text.Sum(value => (long)(value ?? "").Length) > 100000) throw new InvalidOperationException("文字过长，请分段翻译（每次最多 10 万字符）。");
            var snapshot = settings.Copy();
            snapshot.TargetLanguage = settings.EffectiveTarget(String.Join("\n", text));
            var output = Enumerable.Repeat("", text.Length).ToArray();
            int outputCharacters = 0;
            for (int index = 0; index < text.Length; )
            {
                cancellation.ThrowIfCancellationRequested();
                if (String.IsNullOrWhiteSpace(text[index])) { index++; continue; }
                if (snapshot.TranslateProvider != "volc" && snapshot.TranslateProvider != "bing" || text[index].Length > 5000)
                {
                    output[index] = await Translate(snapshot, text[index], cancellation).ConfigureAwait(false);
                    outputCharacters += output[index].Length;
                    if (outputCharacters > 2000000) throw new InvalidOperationException("图片译文超过处理上限，请缩小截图区域。");
                    index++; continue;
                }
                var batch = new List<string>();
                var positions = new List<int>();
                int characters = 0;
                // Keep image batches within Volcengine's limits; use the same conservative bound for Bing.
                while (index < text.Length && batch.Count < 16)
                {
                    if (String.IsNullOrWhiteSpace(text[index])) { index++; continue; }
                    if (characters + text[index].Length > 5000) break;
                    batch.Add(text[index]); positions.Add(index);
                    characters += text[index++].Length;
                }
                string[] translated = snapshot.TranslateProvider == "bing"
                    ? await bing.Translate(batch.ToArray(), snapshot.SourceLanguage, snapshot.TargetLanguage, cancellation).ConfigureAwait(false)
                    : await VolcTranslate(snapshot, batch.ToArray(), snapshot.TargetLanguage, cancellation).ConfigureAwait(false);
                for (int item = 0; item < positions.Count; item++)
                {
                    outputCharacters += translated[item].Length;
                    if (outputCharacters > 2000000) throw new InvalidOperationException("图片译文超过处理上限，请缩小截图区域。");
                    output[positions[item]] = translated[item];
                }
            }
            cancellation.ThrowIfCancellationRequested();
            return output;
        }

        private async Task<string[]> VolcTranslate(AppSettings settings, string[] text, string target, CancellationToken cancellation)
        {
            var payload = new Dictionary<string, object> { { "TargetLanguage", target }, { "TextList", text } };
            if (!String.IsNullOrEmpty(settings.SourceLanguage) && settings.SourceLanguage != "auto") payload.Add("SourceLanguage", settings.SourceLanguage);
            string body = Json.Write(payload);
            DateTime now = DateTime.UtcNow;
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://translate.volcengineapi.com/?Action=TranslateText&Version=2020-06-01"))
            {
                request.Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                request.Headers.Host = "translate.volcengineapi.com";
                request.Headers.Add("X-Date", now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
                request.Headers.Add("X-Content-Sha256", Signing.Sha(body));
                request.Headers.TryAddWithoutValidation("Authorization", Signing.Volc(settings.VolcId, settings.VolcSecret, body, now));
                var result = await Send(request, cancellation).ConfigureAwait(false);
                cancellation.ThrowIfCancellationRequested();
                var metadata = Json.Map(Json.Get(result, "ResponseMetadata"));
                var error = Json.Map(Json.Get(metadata, "Error"));
                if (error != null) throw ApiError("火山引擎翻译", Json.Text(error, "Code"), Json.Text(metadata, "RequestId"));
                var items = Json.Get(result, "TranslationList") as IList;
                if (items == null || items.Count != text.Length) throw new InvalidOperationException("翻译服务返回的文本块数量不匹配，请重试。");
                var output = new string[items.Count];
                for (int index = 0; index < output.Length; index++)
                {
                    output[index] = Json.Text(Json.Map(items[index]), "Translation").Trim();
                    if (output[index].Length == 0) throw new InvalidOperationException("翻译服务返回了空结果。");
                }
                return output;
            }
        }

        private async Task WaitBaiduTranslation(CancellationToken cancellation)
        {
            // Standard accounts allow one request per second, including separate translations.
            await baiduTranslationLock.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                double remaining = (baiduNextRequest - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                if (remaining > 0) await Task.Delay((int)Math.Ceiling(remaining), cancellation).ConfigureAwait(false);
                baiduNextRequest = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 1.05);
            }
            finally { baiduTranslationLock.Release(); }
        }

        internal static IEnumerable<string> SplitText(string text, int maxBytes, int maxCharacters = Int32.MaxValue)
        {
            int start = 0;
            while (start < text.Length)
            {
                int end = start, bytes = 0, boundary = -1;
                while (end < text.Length)
                {
                    int chars = Char.IsHighSurrogate(text[end]) && end + 1 < text.Length && Char.IsLowSurrogate(text[end + 1]) ? 2 : 1;
                    int size = Encoding.UTF8.GetByteCount(text.Substring(end, chars));
                    if (bytes + size > maxBytes || end - start + chars > maxCharacters) break;
                    bytes += size;
                    if (text[end] == '\n' || text[end] == ' ' || text[end] == '。') boundary = end + chars;
                    end += chars;
                }
                if (end == start) throw new ArgumentException("分段长度太小。");
                if (end < text.Length && boundary > start + (end - start) / 2) end = boundary;
                yield return text.Substring(start, end - start);
                start = end;
            }
        }

        private static string BaiduLanguage(string target)
        {
            switch (target) { case "ja": return "jp"; case "ko": return "kor"; case "fr": return "fra"; case "es": return "spa"; default: return target; }
        }

        private async Task<Dictionary<string, object>> Send(HttpRequestMessage request, CancellationToken cancellation, int timeoutSeconds = 25)
        {
            using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                requestCancellation.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
                try
                {
                    using (var response = await client.SendAsync(request, requestCancellation.Token).ConfigureAwait(false))
                    {
                        string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        requestCancellation.Token.ThrowIfCancellationRequested();
                        if (!response.IsSuccessStatusCode)
                        {
                            if (request.RequestUri.Host == "ark.cn-beijing.volces.com")
                            {
                                Dictionary<string, object> errorResponse = null;
                                try { errorResponse = Json.Object(body); }
                                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { }
                                var error = Json.Map(Json.Get(errorResponse, "error"));
                                if (error != null) throw ApiError("火山方舟", Json.Text(error, "code"), Json.Text(errorResponse, "request_id"));
                            }
                            string metadataField = request.RequestUri.Host == "ocr.tencentcloudapi.com" ? "Response" : request.RequestUri.Host == "translate.volcengineapi.com" ? "ResponseMetadata" : null;
                            if (metadataField != null)
                            {
                                Dictionary<string, object> errorResponse = null;
                                try { errorResponse = Json.Object(body); }
                                catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException) { }
                                var metadata = Json.Map(Json.Get(errorResponse, metadataField));
                                var error = Json.Map(Json.Get(metadata, "Error"));
                                if (error != null) throw ApiError(metadataField == "Response" ? "腾讯 OCR" : "火山引擎翻译", Json.Text(error, "Code"), Json.Text(metadata, "RequestId"));
                            }
                            throw new InvalidOperationException("服务请求失败（HTTP " + (int)response.StatusCode + "）。请检查服务权限、配额和网络。");
                        }
                        try { return Json.Object(body); }
                        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                        { throw new InvalidOperationException("服务返回了无法解析的内容，请检查网络代理和服务状态。"); }
                    }
                }
                catch (HttpRequestException error) { throw ConnectionFailure(request.RequestUri, error); }
                catch (OperationCanceledException)
                {
                    if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                    throw new InvalidOperationException("API 请求超时（" + timeoutSeconds + " 秒），请稍后重试。");
                }
            }
        }

        internal static InvalidOperationException ConnectionFailure(Uri endpoint, Exception error)
        {
            string service;
            switch (endpoint.Host)
            {
                case "ocr.tencentcloudapi.com": service = "腾讯 OCR"; break;
                case "aip.baidubce.com": service = "百度 OCR"; break;
                case "translate.volcengineapi.com": service = "火山引擎翻译"; break;
                case "ark.cn-beijing.volces.com": service = "火山方舟"; break;
                case "fanyi-api.baidu.com": service = "百度翻译"; break;
                case "edge.microsoft.com": service = "必应翻译"; break;
                default: service = "当前服务"; break;
            }
            WebException web = null; SocketException socket = null; bool tls = false;
            for (Exception cause = error; cause != null; cause = cause.InnerException)
            {
                if (web == null) web = cause as WebException;
                if (socket == null) socket = cause as SocketException;
                tls |= cause is AuthenticationException;
            }
            string reason = "网络连接失败", code = web == null ? "" : Enum.GetName(typeof(WebExceptionStatus), web.Status) ?? "";
            if (web != null)
                switch (web.Status)
                {
                    case WebExceptionStatus.NameResolutionFailure: reason = "服务域名解析失败"; break;
                    case WebExceptionStatus.ProxyNameResolutionFailure: reason = "代理域名解析失败"; break;
                    case WebExceptionStatus.ConnectFailure: reason = "无法建立连接"; break;
                    case WebExceptionStatus.SendFailure: reason = "请求未完整发送"; break;
                    case WebExceptionStatus.ReceiveFailure: case WebExceptionStatus.ConnectionClosed: case WebExceptionStatus.KeepAliveFailure: reason = "响应未完整接收"; break;
                    case WebExceptionStatus.Timeout: reason = "网络连接超时"; break;
                    case WebExceptionStatus.RequestProhibitedByProxy: reason = "代理拒绝请求"; break;
                }
            if (socket != null)
            {
                string socketCode = Enum.GetName(typeof(SocketError), socket.SocketErrorCode);
                if (socketCode != null) code += (code.Length == 0 ? "" : "/") + socketCode;
                switch (socket.SocketErrorCode)
                {
                    case SocketError.HostNotFound: case SocketError.TryAgain: reason = "域名解析失败"; break;
                    case SocketError.ConnectionRefused: reason = "连接被拒绝"; break;
                    case SocketError.ConnectionReset: case SocketError.ConnectionAborted: reason = "连接被中断或重置"; break;
                    case SocketError.TimedOut: reason = "网络连接超时"; break;
                }
            }
            if (tls || web != null && web.Status == WebExceptionStatus.SecureChannelFailure) { reason = "TLS 握手失败"; if (code.Length == 0) code = "TLS"; }
            if (web != null && web.Status == WebExceptionStatus.TrustFailure) reason = "服务证书验证失败";
            // Only fixed labels and enum names cross the UI boundary; messages and inner exceptions can contain secrets.
            return new InvalidOperationException(service + "无法连接 API 服务（" + reason + "；" + (code.Length == 0 ? "Unknown" : code) + "）。请检查网络或系统代理。");
        }

        private static Exception ApiError(string service, string code, string requestId)
        {
            // Do not display arbitrary response bodies: a provider or proxy can echo credentials.
            string safeCode = Regex.IsMatch(code ?? "", @"\A[A-Za-z0-9_.-]{1,80}\z") ? code : "Unknown";
            string safeId = Regex.IsMatch(requestId ?? "", @"\A[A-Za-z0-9-]{1,80}\z") ? "\n请求编号：" + requestId : "";
            if (safeCode.IndexOf("SetLimitExceeded", StringComparison.OrdinalIgnoreCase) >= 0)
                return new InvalidOperationException(service + "额度或设置的用量上限已达到，服务已暂停（" + safeCode + "）。请在控制台检查免费额度；软件不会自动切换付费服务。" + safeId);
            return new InvalidOperationException(service + "返回错误：" + safeCode + "。请检查密钥、服务开通状态和剩余额度。" + safeId);
        }

        // In-flight cancellation continuations still release the managed semaphores; no WaitHandle is allocated.
        public void Dispose() { client.Dispose(); }
    }
}

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class ColdImageApiTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            internal int Tokens, Ocr, Translation, Requests;
            internal bool FailFirst;
            internal readonly List<string> OcrRequests = new List<string>();
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Requests++;
                await Task.Yield();
                cancellation.ThrowIfCancellationRequested();
                if (FailFirst) { FailFirst = false; throw new HttpRequestException("Synthetic connection failed; not a real endpoint."); }
                string body = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                string host = request.RequestUri.Host, path = request.RequestUri.AbsolutePath;
                if (path.EndsWith("/token", StringComparison.Ordinal))
                {
                    Tokens++;
                    return Response("{\"access_token\":\"synthetic-token\",\"expires_in\":2592000}");
                }
                if (host == "ocr.tencentcloudapi.com")
                {
                    Ocr++;
                    OcrRequests.Add(path + "\n" + request.Headers.GetValues("X-TC-Action").Single() + "\n" + body);
                    return Response("{\"Response\":{\"Angle\":0,\"TextDetections\":[{\"DetectedText\":\"hello\",\"ItemPolygon\":{\"X\":7,\"Y\":9,\"Width\":70,\"Height\":20}},{\"DetectedText\":\"world\",\"ItemPolygon\":{\"X\":7,\"Y\":39,\"Width\":70,\"Height\":20}}]}}");
                }
                if (host == "aip.baidubce.com")
                {
                    Ocr++;
                    OcrRequests.Add(path + "\n" + body);
                    return Response("{\"words_result\":[{\"words\":\"hello\",\"location\":{\"left\":7,\"top\":9,\"width\":70,\"height\":20}},{\"words\":\"world\",\"location\":{\"left\":7,\"top\":39,\"width\":70,\"height\":20}}]}");
                }
                Translation++;
                if (host == "translate.volcengineapi.com")
                {
                    var texts = ((IList)Json.Get(Json.Object(body), "TextList")).Cast<string>();
                    return Response(Json.Write(new { TranslationList = texts.Select(text => new { Translation = "translated:" + text }).ToArray() }));
                }
                if (host == "edge.microsoft.com")
                {
                    string[] texts = Json.Read<string[]>(body);
                    return Response(Json.Write(texts.Select(text => new { translations = new[] { new { text = "translated:" + text } } }).ToArray()));
                }
                if (host == "fanyi-api.baidu.com")
                {
                    string text = WebUtility.UrlDecode(body.Split('&').Single(field => field.StartsWith("q=", StringComparison.Ordinal)).Substring(2));
                    return Response(Json.Write(new { trans_result = new[] { new { dst = "translated:" + text } } }));
                }
                throw new InvalidOperationException("Unexpected endpoint in synthetic cold image test.");
            }
        }
        private static AppSettings Settings(string ocr, string translation, bool accurate)
        {
            return new AppSettings { OcrProvider = ocr, TranslateProvider = translation, AccurateOcr = accurate, HistoryEnabled = false,
                TencentId = "synthetic-id", TencentSecret = "synthetic-secret", BaiduOcrKey = "synthetic-key", BaiduOcrSecret = "synthetic-secret",
                VolcId = "synthetic-id", VolcSecret = "synthetic-secret", BaiduTranslateId = "synthetic-id", BaiduTranslateSecret = "synthetic-secret", BingTranslateEnabled = true };
        }
        internal static async Task Run(Action<bool, string> check)
        {
            foreach (string ocr in new[] { "tencent", "baidu" })
                foreach (string translation in new[] { "volc", "baidu", "bing" })
                    foreach (bool accurate in new[] { true, false })
                        await ColdAndWarm(check, Settings(ocr, translation, accurate)).ConfigureAwait(false);
            foreach (string ocr in new[] { "tencent", "baidu" })
                await FailedConnection(check, Settings(ocr, "volc", true)).ConfigureAwait(false);
        }
        private static async Task ColdAndWarm(Action<bool, string> check, AppSettings settings)
        {
            var cold = new Handler(); var warm = new Handler();
            string label = settings.OcrProvider + "/" + settings.TranslateProvider + "/" + (settings.AccurateOcr ? "accurate" : "standard");
            byte[] image = { 1, 2, 3 };
            using (var coldApi = new CloudApi(cold))
            using (var warmApi = new CloudApi(warm))
            {
                var direct = await coldApi.OcrWithLayout(settings.Copy(), image, CancellationToken.None).ConfigureAwait(false);
                string[] directResult = await coldApi.TranslateBlocks(settings.Copy(), direct.Select(block => block.Text).ToArray(), CancellationToken.None).ConfigureAwait(false);
                string plain = await warmApi.Ocr(settings.Copy(), image, CancellationToken.None).ConfigureAwait(false);
                var afterPlain = await warmApi.OcrWithLayout(settings.Copy(), image, CancellationToken.None).ConfigureAwait(false);
                string[] warmedResult = await warmApi.TranslateBlocks(settings.Copy(), afterPlain.Select(block => block.Text).ToArray(), CancellationToken.None).ConfigureAwait(false);
                check(direct.Length == 2 && direct[0].Bounds == new Rectangle(7, 9, 70, 20) && direct[1].Bounds == new Rectangle(7, 39, 70, 20)
                    && directResult.SequenceEqual(new[] { "translated:hello", "translated:world" }), "cold image OCR and translation need no preceding plain OCR: " + label);
                check(warmedResult.SequenceEqual(directResult) && plain.Contains("hello") && afterPlain.Select(block => block.Bounds).SequenceEqual(direct.Select(block => block.Bounds)), "plain OCR priming does not change image text, locations or translations: " + label);
                check(cold.Ocr == 1 && warm.Ocr == 2 && cold.Tokens == (settings.OcrProvider == "baidu" ? 1 : 0) && warm.Tokens == cold.Tokens
                    && cold.Translation == (settings.TranslateProvider == "baidu" ? 2 : 1) && warm.Translation == cold.Translation,
                    "cold/warm calls contain only the required authentication, OCR and translation requests: " + label);
                check(cold.OcrRequests[0] == warm.OcrRequests[1], "coordinate OCR request is identical before and after ordinary OCR: " + label);
                if (settings.OcrProvider == "tencent") check(warm.OcrRequests[0] == warm.OcrRequests[1], "Tencent plain and layout OCR share the same action and payload: " + label);
                else check(warm.OcrRequests[0].Replace("_basic", "") == warm.OcrRequests[1], "Baidu plain and layout OCR differ only in the documented location endpoint: " + label);
            }
        }
        private static async Task FailedConnection(Action<bool, string> check, AppSettings settings)
        {
            var handler = new Handler { FailFirst = true };
            using (var api = new CloudApi(handler))
            {
                bool failed = false;
                try { await api.OcrWithLayout(settings, new byte[] { 1 }, CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException error) { failed = error.Message.Contains("无法连接 API") && !error.Message.Contains("synthetic-secret"); }
                check(failed && handler.Requests == 1, "first connection failure is reported without automatic paid retry: " + settings.OcrProvider);
                await api.Ocr(settings, new byte[] { 1 }, CancellationToken.None).ConfigureAwait(false);
                check((await api.OcrWithLayout(settings, new byte[] { 1 }, CancellationToken.None).ConfigureAwait(false)).Length == 2,
                    "an explicitly invoked OCR after a failed connection does not poison later layout requests: " + settings.OcrProvider);
            }
        }
        private static HttpResponseMessage Response(string json)
        { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }; }
    }
}

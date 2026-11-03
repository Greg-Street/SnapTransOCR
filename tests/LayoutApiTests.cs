using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class LayoutApiTests
    {
        internal static async Task Run(Action<bool, string> check)
        {
            await Coordinates(check);
            await Batches(check);
            await FailureCases(check);
        }
        private static AppSettings Settings()
        {
            return new AppSettings { OcrProvider = "tencent", TencentId = "test", TencentSecret = "test", VolcId = "test", VolcSecret = "test", BaiduOcrKey = "test", BaiduOcrSecret = "test", BaiduTranslateId = "test", BaiduTranslateSecret = "test" };
        }
        private static async Task Coordinates(Action<bool, string> check)
        {
            var settings = Settings();
            string item = "{\"X\":10,\"Y\":20,\"Width\":80,\"Height\":25}";
            var handler = new Handler { Handle = (request, body) =>
            {
                check(request.Headers.GetValues("X-TC-Action").Single() == (settings.AccurateOcr ? "GeneralAccurateOCR" : "GeneralBasicOCR"), "layout OCR uses selected Tencent precision");
                return Response("{\"Response\":{\"Angle\":0,\"TextDetections\":[" +
                    "{\"DetectedText\":\"polygon\",\"ItemPolygon\":" + item + ",\"Polygon\":[{\"X\":100,\"Y\":120},{\"X\":180,\"Y\":130},{\"X\":175,\"Y\":150},{\"X\":95,\"Y\":140}]}," +
                    "{\"DetectedText\":\"box\",\"ItemPolygon\":" + item + "}," +
                    "{\"DetectedText\":\"   \",\"ItemPolygon\":" + item + "}," +
                    "{\"DetectedText\":\"missing\"}," +
                    "{\"DetectedText\":\"negative\",\"ItemPolygon\":{\"X\":-1,\"Y\":0,\"Width\":5,\"Height\":5}}," +
                    "{\"DetectedText\":\"zero\",\"ItemPolygon\":{\"X\":0,\"Y\":0,\"Width\":0,\"Height\":5}}," +
                    "{\"DetectedText\":\"overflow\",\"ItemPolygon\":{\"X\":2147483647,\"Y\":0,\"Width\":5,\"Height\":5}}," +
                    "{\"DetectedText\":\"fractional\",\"ItemPolygon\":{\"X\":1.5,\"Y\":0,\"Width\":5,\"Height\":5}}]}}");
            } };
            using (var api = new CloudApi(handler))
                foreach (bool accurate in new[] { false, true })
                {
                    settings.AccurateOcr = accurate;
                    var blocks = await api.OcrWithLayout(settings, new byte[] { 1 }, CancellationToken.None);
                    check(blocks.Length == 2 && blocks[0].Text == "polygon" && blocks[0].Bounds == new Rectangle(95, 120, 85, 30), "Tencent original polygon bounds take precedence over deskewed ItemPolygon");
                    check(blocks[1].Text == "box" && blocks[1].Bounds == new Rectangle(10, 20, 80, 25), "layout OCR keeps valid fallback and skips empty text or invalid coordinates");
                }
            handler = new Handler { Handle = (request, body) => Response("{\"Response\":{\"Angle\":90,\"TextDetections\":[{\"DetectedText\":\"rotated\",\"ItemPolygon\":" + item + "}]}}") };
            using (var api = new CloudApi(handler))
                check((await api.OcrWithLayout(settings, new byte[] { 1 }, CancellationToken.None)).Length == 0, "rotated Tencent fallback cannot place deskewed coordinates over the original image");
            settings.OcrProvider = "baidu";
            var paths = new List<string>();
            handler = new Handler { Handle = (request, body) =>
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/token")) return Response("{\"access_token\":\"test-token\",\"expires_in\":3600}");
                paths.Add(request.RequestUri.AbsolutePath);
                return Response("{\"words_result\":[{\"words\":\"location\",\"location\":{\"left\":7,\"top\":9,\"width\":50,\"height\":15}},{\"words\":\"missing\"}]}");
            } };
            using (var api = new CloudApi(handler))
            {
                settings.AccurateOcr = false;
                var blocks = await api.OcrWithLayout(settings, new byte[] { 1 }, CancellationToken.None);
                settings.AccurateOcr = true;
                await api.OcrWithLayout(settings, new byte[] { 1 }, CancellationToken.None);
                string plain = await api.Ocr(settings, new byte[] { 1 }, CancellationToken.None);
                check(blocks.Length == 1 && blocks[0].Bounds == new Rectangle(7, 9, 50, 15), "Baidu location becomes original-image pixel bounds");
                check(paths.Select(path => path.Substring(path.LastIndexOf('/') + 1)).SequenceEqual(new[] { "general", "accurate", "accurate_basic" }) && plain.Contains("missing"), "Baidu layout endpoints include locations while original OCR retains its endpoint and text-only results");
            }
        }
        private static async Task Batches(Action<bool, string> check)
        {
            var sent = new List<string[]>();
            var targets = new List<string>();
            var handler = new Handler { Handle = (request, body) =>
            {
                var payload = Json.Object(body);
                string[] text = ((IList)Json.Get(payload, "TextList")).Cast<string>().ToArray();
                sent.Add(text); targets.Add(Json.Text(payload, "TargetLanguage"));
                return Response(Json.Write(new { TranslationList = text.Select(value => new { Translation = "translated:" + value }).ToArray() }));
            } };
            var settings = Settings();
            using (var api = new CloudApi(handler))
            {
                string[] input = Enumerable.Range(0, 19).Select(index => "line" + index).ToArray(); input[5] = " "; input[8] = null;
                string[] output = await api.TranslateBlocks(settings, input, CancellationToken.None);
                check(output.Length == input.Length && output.Where((value, index) => value != (String.IsNullOrWhiteSpace(input[index]) ? "" : "translated:" + input[index])).Count() == 0, "block translation keeps original indices across empty entries and multiple batches");
                check(sent.Count == 2 && sent[0].Length == 16 && sent[1].Length == 1, "Volc batching respects sixteen text entries");
                sent.Clear(); targets.Clear();
                await api.TranslateBlocks(settings, new[] { new string('中', 2600), new string('a', 2400), "last" }, CancellationToken.None);
                check(sent.Count == 2 && sent[0].Sum(value => value.Length) == 5000 && sent[1].Length == 1, "Volc batching respects 5000 total characters");
                check(targets.All(target => target == "en"), "smart target is consistent for every block in one screenshot");
                sent.Clear();
                output = await api.TranslateBlocks(settings, new[] { new string('x', 5001), "short" }, CancellationToken.None);
                check(output.Length == 2 && sent.Count == 3 && sent.All(batch => batch.Length <= 16 && batch.Sum(value => value.Length) <= 5000) && output[1] == "translated:short", "oversized single block reuses bounded text splitting without shifting later blocks");
                sent.Clear();
                check((await api.TranslateBlocks(settings, new string[0], CancellationToken.None)).Length == 0 &&
                    (await api.TranslateBlocks(settings, new[] { "", " ", null }, CancellationToken.None)).All(value => value == "") && sent.Count == 0, "empty blocks need no network requests");
            }
            settings.TranslateProvider = "baidu";
            var ticks = new List<long>();
            handler = new Handler { Handle = (request, body) => { ticks.Add(Stopwatch.GetTimestamp()); return Response("{\"trans_result\":[{\"dst\":\"result" + ticks.Count + "\"}]}"); } };
            using (var api = new CloudApi(handler))
            {
                string[] output = await api.TranslateBlocks(settings, new[] { "one", "", "two" }, CancellationToken.None);
                check(output.SequenceEqual(new[] { "result1", "", "result2" }) && ticks.Count == 2 && (ticks[1] - ticks[0]) * 1000.0 / Stopwatch.Frequency >= 1000, "Baidu translates nonempty blocks in order with existing account throttling");
            }
        }
        private static async Task FailureCases(Action<bool, string> check)
        {
            var settings = Settings(); int requests = 0;
            var handler = new Handler { Handle = (request, body) => { requests++; return Response("{\"TranslationList\":[{\"Translation\":\"only one\"}]}"); } };
            using (var api = new CloudApi(handler))
            {
                check(await Fails(() => api.TranslateBlocks(settings, new[] { "one", "two" }, CancellationToken.None)), "block output count mismatch fails instead of shifting image labels");
                int before = requests;
                settings.VolcTranslateEnabled = false;
                check(await Fails(() => api.TranslateBlocks(settings, new[] { "one" }, CancellationToken.None)) && requests == before, "disabled block translation sends no request");
                settings.TencentOcrEnabled = false;
                check(await Fails(() => api.OcrWithLayout(settings, new byte[] { 1 }, CancellationToken.None)) && requests == before, "disabled layout OCR sends no request");
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    check(await Cancelled(() => api.TranslateBlocks(settings, new[] { "one" }, cancellation.Token)) &&
                        await Cancelled(() => api.OcrWithLayout(settings, new byte[] { 1 }, cancellation.Token)) && requests == before, "pre-cancelled layout and block calls send no request");
                }
            }
            settings = Settings();
            foreach (string body in new[] { "{}", "{\"TranslationList\":[{}]}", "{\"TranslationList\":[{\"Translation\":\" \"}]}" })
                using (var api = new CloudApi(new Handler { Handle = (request, payload) => Response(body) }))
                    check(await Fails(() => api.TranslateBlocks(settings, new[] { "one" }, CancellationToken.None)), "missing or empty block translation is rejected");
            using (var api = new CloudApi(new Handler { Handle = (request, body) => Response("{\"Response\":{}}") }))
                check(await Fails(() => api.OcrWithLayout(settings, new byte[] { 1 }, CancellationToken.None)), "missing OCR layout array is reported");
            using (var cancellation = new CancellationTokenSource())
            using (var api = new CloudApi(new Handler { Handle = (request, body) => { cancellation.Cancel(); return Response("{\"TranslationList\":[{\"Translation\":\"late\"}]}"); } }))
                check(await Cancelled(() => api.TranslateBlocks(settings, new[] { "one" }, cancellation.Token)), "cancelled block request does not return a late result");
        }
        private static async Task<bool> Fails(Func<Task> action)
        { try { await action(); return false; } catch (InvalidOperationException) { return true; } }
        private static async Task<bool> Cancelled(Func<Task> action)
        { try { await action(); return false; } catch (OperationCanceledException) { return true; } }
        private static HttpResponseMessage Response(string body)
        { return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") }; }
        private sealed class Handler : HttpMessageHandler
        {
            internal Func<HttpRequestMessage, string, HttpResponseMessage> Handle;
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            { return Handle(request, await request.Content.ReadAsStringAsync()); }
        }
    }
}

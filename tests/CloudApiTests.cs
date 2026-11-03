using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class CloudApiTests
    {
        private sealed class Handler : HttpMessageHandler
        {
            internal Func<HttpRequestMessage, Task<HttpResponseMessage>> Handle;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                cancellation.ThrowIfCancellationRequested();
                return Handle(request);
            }
        }

        private static HttpResponseMessage Response(string body)
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        internal static async Task Run(Action<bool, string> check)
        {
            await TranslationCache(check).ConfigureAwait(false);
            await BaiduChunking(check).ConfigureAwait(false);
            await ConcurrentTokenRefresh(check).ConfigureAwait(false);
        }

        private static async Task TranslationCache(Action<bool, string> check)
        {
            int requests = 0;
            var settings = new AppSettings { TranslateProvider = "volc", VolcId = "a", VolcSecret = "bc", TargetLanguage = "zh" };
            var handler = new Handler { Handle = request => Task.FromResult(Response("{\"TranslationList\":[{\"Translation\":\"result " + (++requests) + "\"}]}")) };
            using (var api = new CloudApi(handler))
            {
                await api.Translate(settings, "hello", CancellationToken.None).ConfigureAwait(false);
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel();
                    bool cancelled = false;
                    try { await api.Translate(settings, "hello", cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled && requests == 1, "cancelled cached translation does not return stale success");
                }
                settings.VolcId = "ab";
                settings.VolcSecret = "c";
                check(await api.Translate(settings, "hello", CancellationToken.None).ConfigureAwait(false) == "result 2", "translation cache separates credential pairs");
                settings.VolcTranslateEnabled = false;
                bool disabled = false;
                try { await api.Translate(settings, "hello", CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { disabled = true; }
                check(disabled && requests == 2, "disabled translation provider cannot return cached data or send requests");
                settings.BaiduOcrEnabled = false;
                disabled = false;
                try { await api.Ocr(settings, new byte[] { 1 }, CancellationToken.None).ConfigureAwait(false); }
                catch (InvalidOperationException) { disabled = true; }
                check(disabled && requests == 2, "disabled OCR provider sends no requests");
            }
        }

        private static async Task BaiduChunking(Action<bool, string> check)
        {
            var sent = new List<string>();
            var times = new List<long>();
            var handler = new Handler { Handle = async request =>
            {
                times.Add(Stopwatch.GetTimestamp());
                string form = await request.Content.ReadAsStringAsync().ConfigureAwait(false);
                foreach (string field in form.Split('&'))
                    if (field.StartsWith("q=", StringComparison.Ordinal)) sent.Add(WebUtility.UrlDecode(field.Substring(2)));
                return Response("{\"trans_result\":[{\"dst\":\"translated\"}]}");
            } };
            var settings = new AppSettings { TranslateProvider = "baidu", BaiduTranslateId = "test", BaiduTranslateSecret = "test", TargetLanguage = "zh" };
            string text = new string('a', 999) + "😀" + new string('中', 3);
            using (var api = new CloudApi(handler))
                await api.Translate(settings, text, CancellationToken.None).ConfigureAwait(false);
            check(sent.Count == 2 && sent.TrueForAll(x => x.Length <= 1000 && Encoding.UTF8.GetByteCount(x) <= 5500), "Baidu standard account chunk limits");
            check(String.Concat(sent) == text && !Char.IsHighSurrogate(sent[0][sent[0].Length - 1]), "Baidu chunking preserves Unicode and all input");
            check(times.Count == 2 && (times[1] - times[0]) * 1000.0 / Stopwatch.Frequency >= 1000, "Baidu request starts respect standard account QPS");
        }

        private static async Task ConcurrentTokenRefresh(Action<bool, string> check)
        {
            int tokenRequests = 0, oldRequests = 0;
            var firstStarted = new TaskCompletionSource<bool>();
            var secondStarted = new TaskCompletionSource<bool>();
            var firstExpired = new TaskCompletionSource<bool>();
            var secondExpired = new TaskCompletionSource<bool>();
            var handler = new Handler { Handle = async request =>
            {
                if (request.RequestUri.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                    return Response("{\"access_token\":\"token-" + Interlocked.Increment(ref tokenRequests) + "\",\"expires_in\":2592000}");
                if (request.RequestUri.Query.Contains("token-1"))
                {
                    if (Interlocked.Increment(ref oldRequests) == 1)
                    {
                        firstStarted.SetResult(true);
                        await firstExpired.Task.ConfigureAwait(false);
                    }
                    else
                    {
                        secondStarted.SetResult(true);
                        await secondExpired.Task.ConfigureAwait(false);
                    }
                    return Response("{\"error_code\":111}");
                }
                return Response("{\"words_result\":[{\"words\":\"recognized\"}]}");
            } };
            var settings = new AppSettings { OcrProvider = "baidu", BaiduOcrKey = "test", BaiduOcrSecret = "test" };
            using (var api = new CloudApi(handler))
            {
                Task<string> first = api.Ocr(settings, new byte[] { 1 }, CancellationToken.None);
                await firstStarted.Task.ConfigureAwait(false);
                Task<string> second = api.Ocr(settings, new byte[] { 2 }, CancellationToken.None);
                await secondStarted.Task.ConfigureAwait(false);
                firstExpired.SetResult(true);
                check(await first.ConfigureAwait(false) == "recognized", "first expired OCR token refresh succeeds");
                secondExpired.SetResult(true);
                check(await second.ConfigureAwait(false) == "recognized" && tokenRequests == 2, "late expired-token response reuses refreshed token");
            }
        }
    }
}

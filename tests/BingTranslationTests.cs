using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class BingTranslationTests
    {
        private const string PrivateContent = "synthetic-content-do-not-display";
        private sealed class Handler : HttpMessageHandler
        {
            internal Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Handle;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            { cancellation.ThrowIfCancellationRequested(); return Handle(request, cancellation); }
        }
        private static AppSettings Settings() { return new AppSettings { TranslateProvider = "bing", BingTranslateEnabled = true, SourceLanguage = "auto", TargetLanguage = "zh" }; }
        private static HttpResponseMessage Response(string text, HttpStatusCode status = HttpStatusCode.OK)
        { return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") }; }
        private static async Task<string[]> Input(HttpRequestMessage request)
        { return Json.Read<string[]>(await request.Content.ReadAsStringAsync().ConfigureAwait(false)); }
        private static HttpResponseMessage Translated(params string[] input)
        { return Response(Json.Write(input.Select(value => new { translations = new[] { new { text = "translated " + value } } }).ToArray())); }
        internal static async Task Run(Action<bool, string> check)
        {
            await CacheAndLanguage(check).ConfigureAwait(false);
            await ChunksAndBlocks(check).ConfigureAwait(false);
            await Failures(check).ConfigureAwait(false);
            await Cancellation(check).ConfigureAwait(false);
        }
        private static async Task CacheAndLanguage(Action<bool, string> check)
        {
            int posts = 0; bool protocol = true; var queries = new List<string>();
            var handler = new Handler { Handle = async (request, cancel) =>
            {
                posts++; queries.Add(request.RequestUri.Query);
                protocol &= request.Headers.UserAgent.ToString().StartsWith("SnapTransOCR/") && request.Headers.Referrer == null &&
                    !request.Headers.Contains("sec-ch-ua") && request.Headers.Authorization == null && request.Method == HttpMethod.Post &&
                    request.RequestUri.Host == "edge.microsoft.com" && request.RequestUri.AbsolutePath == "/translate/translatetext" &&
                    request.RequestUri.Query.Contains("isEnterpriseClient=false");
                return Translated(await Input(request).ConfigureAwait(false));
            } };
            var settings = Settings();
            using (var api = new CloudApi(handler))
            {
                check(await api.Translate(settings, "hello", CancellationToken.None).ConfigureAwait(false) == "translated hello", "Bing translates without user credentials or a token request");
                await api.Translate(settings, "hello again", CancellationToken.None).ConfigureAwait(false);
                check(posts == 2 && protocol, "Bing sends string arrays to the current Edge endpoint with normal application headers");
                check(queries.All(query => query.Contains("to=zh-Hans") && !query.Contains("from=")), "Bing auto source omits from and Simplified Chinese uses zh-Hans");
                await api.Translate(settings, "hello", CancellationToken.None).ConfigureAwait(false);
                check(posts == 2, "Bing reuses the existing bounded translation result cache");
                using (var cancellation = new CancellationTokenSource())
                {
                    cancellation.Cancel(); bool cancelled = false;
                    try { await api.Translate(settings, "hello", cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled && posts == 2, "cancelled Bing cached translation cannot return stale success");
                }
                settings.SourceLanguage = "zh-TW"; settings.TargetLanguage = "en";
                await api.Translate(settings, "文字", CancellationToken.None).ConfigureAwait(false);
                check(queries.Last().Contains("from=zh-Hant") && queries.Last().Contains("to=en"), "Bing maps Traditional Chinese source while preserving an explicit target");
                settings.TargetLanguage = "zh-TW"; settings.SourceLanguage = "en";
                await api.Translate(settings, "hello", CancellationToken.None).ConfigureAwait(false);
                check(queries.Last().Contains("to=zh-Hant") && queries.Last().Contains("from=en"), "Bing maps Traditional Chinese target");
                settings.SourceLanguage = "auto"; settings.TargetLanguage = "smart";
                await api.TranslateBlocks(settings, new[] { "中文", "English" }, CancellationToken.None).ConfigureAwait(false);
                check(queries.Last().Contains("to=en"), "Bing image blocks use one smart target chosen from all source text");
                settings.BingTranslateEnabled = false; int before = posts;
                check(await Fails(() => api.Translate(settings, "hello", CancellationToken.None)).ConfigureAwait(false) != null && posts == before,
                    "disabled Bing cannot return cached text or send a request");
                check(await Fails(() => api.TranslateBlocks(settings, new[] { "hello" }, CancellationToken.None)).ConfigureAwait(false) != null && posts == before,
                    "disabled Bing image blocks cannot send a request");
            }
        }
        private static async Task ChunksAndBlocks(Action<bool, string> check)
        {
            var batches = new List<string[]>();
            var handler = new Handler { Handle = async (request, cancel) =>
            {
                string[] input = await Input(request).ConfigureAwait(false); batches.Add(input); return Translated(input);
            } };
            using (var api = new CloudApi(handler))
            {
                string text = new string('a', 4499) + "😀" + new string('中', 1500);
                await api.Translate(Settings(), text, CancellationToken.None).ConfigureAwait(false);
                check(batches.Count >= 3 && batches.All(batch => batch.Length == 1 && Encoding.UTF8.GetByteCount(batch[0]) <= 4500), "Bing long text uses bounded requests through existing UTF-8 chunking");
                check(String.Concat(batches.SelectMany(value => value)) == text && batches.All(batch => !Char.IsLowSurrogate(batch[0][0]) && !Char.IsHighSurrogate(batch[0][batch[0].Length - 1])),
                    "Bing chunks preserve all Unicode without splitting a surrogate pair");
                batches.Clear();
                string[] output = await api.TranslateBlocks(Settings(), new[] { "one", "", null, "two" }, CancellationToken.None).ConfigureAwait(false);
                check(output.SequenceEqual(new[] { "translated one", "", "", "translated two" }) && batches.Count == 1, "Bing block batching preserves original positions and empty blocks");
                batches.Clear();
                output = await api.TranslateBlocks(Settings(), Enumerable.Range(0, 17).Select(i => "item " + i).ToArray(), CancellationToken.None).ConfigureAwait(false);
                check(batches.Count == 2 && batches[0].Length == 16 && batches[1].Length == 1 && output[16] == "translated item 16", "Bing image batches stay within 16 entries without changing block order");
                batches.Clear();
                await api.TranslateBlocks(Settings(), new[] { new string('a', 3000), new string('b', 2000), "last" }, CancellationToken.None).ConfigureAwait(false);
                check(batches.Count == 2 && batches.All(batch => batch.Sum(value => value.Length) <= 5000), "Bing image batch total remains within 5000 characters");
                batches.Clear();
                output = await api.TranslateBlocks(Settings(), new[] { new string('z', 5101), "short" }, CancellationToken.None).ConfigureAwait(false);
                check(output.Length == 2 && output[1] == "translated short" && batches.Count == 3, "oversized Bing image blocks split independently without shifting later blocks");
                batches.Clear();
                output = await api.TranslateBlocks(Settings(), new[] { "", " ", null }, CancellationToken.None).ConfigureAwait(false);
                check(batches.Count == 0 && output.All(value => value == ""), "empty Bing blocks do not make network requests");
            }
        }
        private static async Task Failures(Action<bool, string> check)
        {
            foreach (int status in new[] { 401, 403, 404, 429, 500 })
            {
                int requests = 0;
                var handler = new Handler { Handle = (request, cancel) => { requests++; return Task.FromResult(Response(PrivateContent, (HttpStatusCode)status)); } };
                using (var api = new CloudApi(handler))
                {
                    Exception error = await Fails(() => api.Translate(Settings(), "hello", CancellationToken.None)).ConfigureAwait(false);
                    check(error != null && error.Message.Contains(status.ToString()) && !error.ToString().Contains(PrivateContent) && requests == 1,
                        "Bing HTTP " + status + " gives a sanitized error without silent retries or another provider");
                }
            }
            foreach (string body in new[] { "<html>" + PrivateContent, "[]", "[{\"translations\":[]}]", "[{\"translations\":[{\"text\":123}]}]" })
            {
                using (var api = new CloudApi(new Handler { Handle = (request, cancel) => Task.FromResult(Response(body)) }))
                {
                    Exception error = await Fails(() => api.TranslateBlocks(Settings(), new[] { "hello" }, CancellationToken.None)).ConfigureAwait(false);
                    check(error != null && !error.ToString().Contains(PrivateContent), "Bing malformed or missing results fail without exposing response content");
                }
            }
            using (var api = new CloudApi(new Handler { Handle = (request, cancel) => { throw new HttpRequestException(PrivateContent); } }))
            {
                Exception error = await Fails(() => api.Translate(Settings(), "hello", CancellationToken.None)).ConfigureAwait(false);
                check(error != null && !error.ToString().Contains(PrivateContent), "Bing network failures do not leak exception contents");
            }
            using (var api = new CloudApi(new Handler { Handle = (request, cancel) => { throw new TaskCanceledException(PrivateContent); } }))
            {
                Exception error = await Fails(() => api.Translate(Settings(), "hello", CancellationToken.None)).ConfigureAwait(false);
                check(error != null && error.Message.Contains("超时") && !error.ToString().Contains(PrivateContent), "Bing HTTP timeout is distinct from caller cancellation and hides exception contents");
            }
        }
        private static async Task Cancellation(Action<bool, string> check)
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var handler = new Handler { Handle = (request, cancel) => { cancellation.Cancel(); return Task.FromResult(Translated("hello")); } };
                using (var api = new CloudApi(handler))
                {
                    bool cancelled = false;
                    try { await api.Translate(Settings(), "hello", cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled, "Bing cancellation discards a late HTTP translation result");
                }
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); int requests = 0;
                using (var api = new CloudApi(new Handler { Handle = (request, cancel) => { requests++; return Task.FromResult(Translated("hello")); } }))
                {
                    bool cancelled = false;
                    try { await api.TranslateBlocks(Settings(), new[] { "hello" }, cancellation.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { cancelled = true; }
                    check(cancelled && requests == 0, "pre-cancelled Bing blocks perform no network requests");
                }
            }
        }
        private static async Task<Exception> Fails(Func<Task> action)
        {
            try { await action().ConfigureAwait(false); return null; }
            catch (InvalidOperationException error) { return error; }
        }
    }
}
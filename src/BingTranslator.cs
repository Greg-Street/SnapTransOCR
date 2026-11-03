using System;
using System.Collections;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    // Current Edge endpoint: its retired /translate/auth flow now returns HTTP 404.
    internal sealed class BingTranslator
    {
        private readonly HttpClient client;
        internal BingTranslator(HttpClient client) { this.client = client; }

        internal async Task<string[]> Translate(string[] text, string source, string target, CancellationToken cancellation)
        {
            string url = "https://edge.microsoft.com/translate/translatetext?isEnterpriseClient=false&to=" + Uri.EscapeDataString(Language(target));
            if (!String.IsNullOrEmpty(source) && source != "auto") url += "&from=" + Uri.EscapeDataString(Language(source));
            try
            {
                cancellation.ThrowIfCancellationRequested();
                using (var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                using (var request = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    requestCancellation.CancelAfter(TimeSpan.FromSeconds(25));
                    request.Content = new StringContent(Json.Write(text), Encoding.UTF8, "application/json");
                    using (var response = await client.SendAsync(request, requestCancellation.Token).ConfigureAwait(false))
                    {
                        requestCancellation.Token.ThrowIfCancellationRequested();
                        if (!response.IsSuccessStatusCode)
                        {
                            int status = (int)response.StatusCode;
                            string hint = status == 429 ? "请求过于频繁，请稍后重试。"
                                : status == 401 || status == 403 ? "服务拒绝访问，请稍后重试。" : "请检查网络和服务状态。";
                            throw new InvalidOperationException("必应翻译请求失败（HTTP " + status + "）：" + hint);
                        }
                        string received = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        requestCancellation.Token.ThrowIfCancellationRequested();
                        IList items;
                        try { items = Json.Read<object>(received) as IList; }
                        catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
                        { throw new InvalidOperationException("必应翻译返回了无法解析的内容，请检查网络代理和服务状态。"); }
                        if (items == null || items.Count != text.Length) throw new InvalidOperationException("必应翻译返回的文本块数量不匹配，请重试。");
                        var output = new string[items.Count];
                        for (int index = 0; index < output.Length; index++)
                        {
                            var translations = Json.Get(Json.Map(items[index]), "translations") as IList;
                            string value = translations == null || translations.Count == 0 ? null : Json.Get(Json.Map(translations[0]), "text") as string;
                            if (String.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("必应翻译返回了空结果。");
                            output[index] = value.Trim();
                        }
                        return output;
                    }
                }
            }
            // Do not include arbitrary response or exception text: a proxy may echo submitted content.
            catch (HttpRequestException error) { throw CloudApi.ConnectionFailure(new Uri(url), error); }
            catch (OperationCanceledException)
            {
                if (cancellation.IsCancellationRequested) throw new OperationCanceledException(cancellation);
                throw new InvalidOperationException("必应翻译请求超时（25 秒），请稍后重试。");
            }
        }

        internal static string Language(string value)
        {
            switch ((value ?? "").ToLowerInvariant())
            {
                case "zh": case "zh-cn": case "zh-hans": return "zh-Hans";
                case "zh-tw": case "zh-hk": case "zh-hant": return "zh-Hant";
                default: return value ?? "";
            }
        }
    }
}

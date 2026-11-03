using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class NetworkFailureTests
    {
        private const string Private = "DO_NOT_EXPOSE_SECRET_71";
        private sealed class Failure
        {
            internal Exception Cause;
            internal string Code, Reason;
            internal Failure(Exception cause, string code, string reason) { Cause = cause; Code = code; Reason = reason; }
        }
        private sealed class Handler : HttpMessageHandler
        {
            internal Exception Cause;
            internal int Requests;
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            {
                Requests++;
                throw new HttpRequestException(Private + " https://example.invalid/?token=" + Private, Cause);
            }
        }
        internal static async Task Run(Action<bool, string> check)
        {
            var failures = new[] {
                new Failure(Web(WebExceptionStatus.NameResolutionFailure, new SocketException((int)SocketError.HostNotFound)), "NameResolutionFailure/HostNotFound", "域名解析失败"),
                new Failure(Web(WebExceptionStatus.ProxyNameResolutionFailure), "ProxyNameResolutionFailure", "代理域名解析失败"),
                new Failure(Web(WebExceptionStatus.ConnectFailure, new SocketException((int)SocketError.ConnectionRefused)), "ConnectFailure/ConnectionRefused", "连接被拒绝"),
                new Failure(Web(WebExceptionStatus.SendFailure, new IOException(Private)), "SendFailure", "请求未完整发送"),
                new Failure(Web(WebExceptionStatus.ReceiveFailure, new SocketException((int)SocketError.ConnectionReset)), "ReceiveFailure/ConnectionReset", "连接被中断或重置"),
                new Failure(Web(WebExceptionStatus.SecureChannelFailure, new AuthenticationException(Private)), "SecureChannelFailure", "TLS 握手失败"),
                new Failure(Web(WebExceptionStatus.TrustFailure), "TrustFailure", "证书验证失败"),
                new Failure(Web(WebExceptionStatus.Timeout), "Timeout", "网络连接超时"),
                new Failure(new AuthenticationException(Private), "TLS", "TLS 握手失败"),
                new Failure(new IOException(Private), "Unknown", "网络连接失败")
            };
            foreach (string provider in new[] { "tencent", "baidu-ocr", "volc", "baidu", "bing" })
                foreach (Failure failure in failures)
                {
                    var settings = new AppSettings { OcrProvider = provider == "tencent" ? "tencent" : "baidu", TranslateProvider = provider,
                        TencentId = "test", TencentSecret = Private, BaiduOcrKey = "test", BaiduOcrSecret = Private,
                        VolcId = "test", VolcSecret = Private, BaiduTranslateId = "test", BaiduTranslateSecret = Private, BingTranslateEnabled = true };
                    var handler = new Handler { Cause = failure.Cause };
                    using (var api = new CloudApi(handler))
                    {
                        InvalidOperationException reported = null;
                        try
                        {
                            if (provider == "tencent" || provider == "baidu-ocr") await api.OcrWithLayout(settings, new byte[] { 1 }, CancellationToken.None).ConfigureAwait(false);
                            else await api.TranslateBlocks(settings, new[] { Private }, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (InvalidOperationException error) { reported = error; }
                        string service = provider == "tencent" ? "腾讯 OCR" : provider == "baidu-ocr" ? "百度 OCR" : AppSettings.TranslationName(provider);
                        check(reported != null && reported.Message.Contains(service) && reported.Message.Contains(failure.Code) && reported.Message.Contains(failure.Reason),
                            "network failure identifies service and stable cause: " + provider + "/" + failure.Code);
                        check(reported != null && !reported.ToString().Contains(Private) && !reported.ToString().Contains("example.invalid") && handler.Requests == 1,
                            "network failure hides exception text, credentials and request data without retry: " + provider + "/" + failure.Code);
                    }
                }
            var direct = CloudApi.ConnectionFailure(new Uri("https://ocr.tencentcloudapi.com/?token=" + Private), new HttpRequestException(Private));
            check(direct.Message.Contains("腾讯 OCR") && direct.Message.Contains("Unknown") && !direct.ToString().Contains(Private), "diagnostic endpoint labels never include URI queries");
        }
        private static WebException Web(WebExceptionStatus status, Exception inner = null)
        { return new WebException(Private, inner, status, null); }
    }
}

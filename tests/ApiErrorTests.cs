using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SnapTransOCR
{
    internal static class ApiErrorTests
    {
        internal static async Task Run(Action<bool, string> check)
        {
            string tencent = await Failure(true, HttpStatusCode.BadRequest,
                "{\"Response\":{\"Error\":{\"Code\":\"AuthFailure.SignatureFailure\",\"Message\":\"DO_NOT_SHOW\"},\"RequestId\":\"test-request-1\"}}");
            check(tencent.Contains("腾讯 OCR") && tencent.Contains("AuthFailure.SignatureFailure") && tencent.Contains("test-request-1") && !tencent.Contains("DO_NOT_SHOW"), "Tencent non-success HTTP preserves safe error code and request ID without response message");
            string volc = await Failure(false, HttpStatusCode.Forbidden,
                "{\"ResponseMetadata\":{\"Error\":{\"Code\":\"AccessDenied\",\"Message\":\"DO_NOT_SHOW\"},\"RequestId\":\"test-request-2\"}}");
            check(volc.Contains("火山引擎翻译") && volc.Contains("AccessDenied") && volc.Contains("test-request-2") && !volc.Contains("DO_NOT_SHOW"), "Volc non-success HTTP preserves safe error code and request ID without response message");
            string html = await Failure(false, HttpStatusCode.BadGateway, "<html>DO_NOT_SHOW</html>");
            check(html.Contains("HTTP 502") && !html.Contains("DO_NOT_SHOW"), "non-JSON HTTP failure remains a safe HTTP error");
            string unexpected = await Failure(true, HttpStatusCode.BadRequest, "{\"unknown\":\"DO_NOT_SHOW\"}");
            check(unexpected.Contains("HTTP 400") && !unexpected.Contains("DO_NOT_SHOW"), "unrecognized JSON failure retains HTTP status without response body");
            string unsafeFields = await Failure(false, HttpStatusCode.Forbidden,
                "{\"ResponseMetadata\":{\"Error\":{\"Code\":\"bad code\\nDO_NOT_SHOW\"},\"RequestId\":\"bad id\\nDO_NOT_SHOW\"}}");
            check(unsafeFields.Contains("Unknown") && !unsafeFields.Contains("DO_NOT_SHOW"), "non-success HTTP sanitizes malformed error code and request ID");
            string trailingNewline = await Failure(false, HttpStatusCode.Forbidden,
                "{\"ResponseMetadata\":{\"Error\":{\"Code\":\"AccessDenied\\n\"},\"RequestId\":\"test-request-2\\n\"}}");
            check(trailingNewline.Contains("Unknown") && !trailingNewline.Contains("test-request-2"), "error tokens reject trailing line breaks");
            string wrongSchema = await Failure(false, HttpStatusCode.Forbidden,
                "{\"Response\":{\"Error\":{\"Code\":\"DO_NOT_SHOW\"},\"RequestId\":\"DO_NOT_SHOW\"}}");
            check(wrongSchema.Contains("HTTP 403") && !wrongSchema.Contains("DO_NOT_SHOW"), "non-success HTTP only reads the requested provider error schema");
            string business = await Failure(true, HttpStatusCode.OK,
                "{\"Response\":{\"Error\":{\"Code\":\"FailedOperation.ServiceNotOpened\"},\"RequestId\":\"test-request-3\"}}");
            check(business.Contains("FailedOperation.ServiceNotOpened") && business.Contains("test-request-3"), "successful HTTP with business error retains existing safe error handling");
        }
        private static async Task<string> Failure(bool ocr, HttpStatusCode status, string body)
        {
            var settings = new AppSettings { OcrProvider = "tencent", TencentId = "test-id", TencentSecret = "test-secret", VolcId = "test-id", VolcSecret = "test-secret" };
            using (var api = new CloudApi(new ResponseHandler(status, body)))
            {
                try
                {
                    if (ocr) await api.Ocr(settings, new byte[] { 1 }, CancellationToken.None).ConfigureAwait(false);
                    else await api.Translate(settings, "hello", CancellationToken.None).ConfigureAwait(false);
                }
                catch (InvalidOperationException error) { return error.Message; }
            }
            return "";
        }
        private sealed class ResponseHandler : HttpMessageHandler
        {
            private readonly HttpStatusCode status;
            private readonly string body;
            internal ResponseHandler(HttpStatusCode status, string body) { this.status = status; this.body = body; }
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            { return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") }); }
        }
    }
}

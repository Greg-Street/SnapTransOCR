# 服务与接口

本工程不提供任何共享 API 密钥。使用者自行申请并配置服务，按服务商的权限、额度、隐私条款和计费规则使用。

- [百度 OCR](https://cloud.baidu.com/doc/OCR/s/1k3h7y3db)：文字、手写、表格等识别接口，服务权限相互独立。
- [腾讯 OCR](https://cloud.tencent.com/document/product/866)：文字、表格等接口。
- [百度翻译](https://fanyi-api.baidu.com/doc/21)：文本翻译；凭据与百度 OCR 独立。
- [火山引擎机器翻译](https://www.volcengine.com/product/machine-translation)：文本翻译与签名认证。
- [火山方舟](https://www.volcengine.com/product/ark)：豆包 / Ark 模型使用独立的 API Key 与模型配置。
- 必应：使用 Edge 在线翻译接口，不要求填写密钥，但不是长期可用性承诺，仍需联网并可能限流。

服务请求签名、错误处理与缓存由源码实现。当前支持的模式和字段以设置页面为准。各服务仅在被启用并触发操作后调用，不自动改用其他收费接口。

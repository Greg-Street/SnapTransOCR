# 来源与许可

SnapTransOCR 是 C# / Win32 / Windows Forms 实现，采用 GPL-3.0-only 发布；完整许可见根目录 LICENSE。

调研和接口交叉核对参考了以下项目。便携包没有打包这些项目的程序、浏览器运行时、本地 OCR 引擎、模型或商标图标。

- Pot，Pylogmon / pot-app，GPL-3.0-only：https://github.com/pot-app/pot-desktop 。交叉核对了 OCR、翻译服务的工作流和协议实现。
- Ebook-Translator-Calibre-Plugin，bookfere：https://github.com/bookfere/Ebook-Translator-Calibre-Plugin 。核对微软 Edge 当前翻译请求协议，未复制代码；服务说明见 docs/SERVICES.md。
- TiLex，BearKouber，GPL-3.0-only：https://github.com/BearKouber/TiLex 。参考原生窗口、划词按钮、虚拟桌面截图的实现思路。
- ShareX：https://github.com/ShareX/ShareX 。调研截图流程和常用标注功能。
- PowerToys：https://github.com/microsoft/PowerToys 。参考选区像素转换与异步会话有效性检查。
- ScreenToGif：https://github.com/NickeManarin/ScreenToGif 。参考显示器 DPI 变化时的选区布局顺序。
- Greenshot：https://github.com/greenshot/greenshot 。调研原生 Windows 截图实现路线。
- Text Grab：https://github.com/TheJoeFin/Text-Grab 。参考基于 OCR 位置的图像文字选择交互，未复制代码。
- manga-image-translator：https://github.com/zyddnys/manga-image-translator 。参考文字区域与字号适配思路，未复制代码或引入模型；验证范围见 docs/VALIDATION.md。
- .NET Windows Forms 与 Aspire：https://github.com/dotnet/winforms 、https://github.com/microsoft/aspire 。核对窗口显示生命周期及 DWM 系统圆角调用方式，未复制代码。
- Flameshot：https://github.com/flameshot-org/flameshot 。调研选区旁悬浮工具条的交互形式。
- Volcengine OpenAPI demos，Beijing Volcano Engine Technology Ltd.，Apache-2.0：https://github.com/volcengine/volc-openapi-demos 。签名实现参照其公开签名协议与 C# 示例重新组织；Apache 许可保留在 docs/LICENSE-Apache-2.0.txt。

服务名称归各服务提供商所有。百度、腾讯与火山引擎需要使用者提供有权限的账户凭据；必应使用免填写密钥的 Edge 在线接口。API 文档和签名向量来源列在 docs/SERVICES.md。

界面使用原生 Windows 绘制；图标为程序绘制的线条图标，未复制 Manggo 的品牌图形或付费标志。

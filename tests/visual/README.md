# 原生窗口缩放可视检查

探针编译实际产品控件，显示自己的合成窗口和绿色背景。它不加载真实配置、历史、API 凭据，不调用 OCR/翻译服务，也不读写剪贴板。历史模式只在该次输出目录中创建合成文字及 PNG。`--auto` 在确认命中自身窗口边缘后拖动鼠标；等待原生尺寸循环开始才移动，结束时释放鼠标并恢复光标。运行时会遮住主屏幕，请不要同时操作鼠标或运行其他 UI 测试。

需要 Windows、.NET SDK 和 .NET Framework 4.8 Targeting Pack。在源码根目录执行；构建脚本只编译，不启动 UI。下列检查依次完成，任一失败即停止：

```powershell
$probe = & .\tests\visual\BuildProbe.ps1
& $probe translation --auto --verify
if ($LASTEXITCODE -ne 0) { throw '翻译窗口检查失败' }
& $probe ocr --ocr --auto --verify
if ($LASTEXITCODE -ne 0) { throw 'OCR 窗口检查失败' }
& $probe settings --settings --auto --verify
if ($LASTEXITCODE -ne 0) { throw '设置窗口检查失败' }
& $probe history --history --auto --verify
if ($LASTEXITCODE -ne 0) { throw '历史页面检查失败' }
& $probe pin --pin --auto --verify
if ($LASTEXITCODE -ne 0) { throw '贴图检查失败' }
```

默认分别拖动右边缘和下边缘，扩大后返回；添加 `--corner` 改为右下角。移除 `--auto` 后可手动调整测试窗，关闭测试窗结束。每次运行的首个参数是结果名称；重复名称会覆盖同名采样文件。产物默认位于 `test-output\resize-probe`，可通过构建参数 `-Output` 修改。

0.2.12 及以后可在翻译窗口探针添加 `--bing`，同时启用并填充三个服务的合成结果；仍不调用网络。旧版本对照不要传此参数。

对比旧版本时，将兼容此探针接口的旧版 `src` 保存在独立目录，使用同一探针、屏幕/DPI 和操作参数，依次运行两个版本：

```powershell
$old = & .\tests\visual\BuildProbe.ps1 -Sources ..\old-version\src -Label old
$new = & .\tests\visual\BuildProbe.ps1 -Label current
& $old old-translation --auto --verify
$oldExit = $LASTEXITCODE
& $new current-translation --auto --verify
$newExit = $LASTEXITCODE
"旧版退出码：$oldExit；当前退出码：$newExit"
```

每次拖动保存 CSV、原生消息耗时、初尾尺寸、首尾及最差采样 PNG。必须先确认原生命中、方向、尺寸变化次数和最终尺寸恢复，才能比较视觉结果；原生消息嵌套，耗时不能相加。历史页面会等待合成缩略图加载；随高度移动的正文区域只检查绿色穿透。贴图不比较文字掩码，避免把图像正常缩放算作文字消失。

`--verify` 要求至少有一个有效样本，固定文字暗色掩码缺失比例不超过 50%，且客户区向内缩进 32 个 DPI 缩放像素后的绿色像素数为零；否则退出码为 1。外缘绿色另行统计，可能涉及窗口几何与桌面合成的时序，不能直接称作内容消失。无 `--verify` 时仍保存视觉异常，但只把原生拖动未完成等错误作为失败。

采样目标间隔约 8 ms，实际截图及分析有开销；CSV 保存每次采样耗时和丢弃数。像素采样不是显示刷新率，多次采样可能对应同一显示帧。阈值检查也不能涵盖所有短暂残影、文字栅格化变化或未采到的帧；通过仅说明当前设备、DPI 和本次操作的样本满足条件，不能推断所有硬件都没有闪烁。

## 快捷键首次显示

`PresentProbe.cs` 使用自己的选区窗口、隐藏的翻译窗口和延迟返回的 HTTP 替身，发送 `WM_HOTKEY` 到实际 CommandWindow 后经过取词及翻译流程。检查点击窗口之前、等待译文期间和结果显示后的背景；8 次唤出包含固定尺寸与自动高度。还检查 OCR、设置、图片编辑器和贴图的显示与再显示。不读取用户配置、不调用真实 API。运行时请让测试夹具保持前台，否则保护检查会中止取词。

```powershell
$present = & .\tests\visual\BuildPresentProbe.ps1 -Label current
& $present current-frames
if ($LASTEXITCODE -ne 0) { throw '窗口首次显示检查失败' }
```

同样必须串行运行，不能与其他窗口/取词测试同时执行。每帧截取后在窗口内部采样绿色背景，要求零穿透；PNG 及日志用于区分持续父背景缺块与尺寸变化时的桌面合成过渡。Windows 11 系统圆角偏好能否设置会记录 API 返回值，Windows 10 采用直角。此探针不是对每个显示帧的完整证明。

## 设置页滚轮对照

`ScrollProbe.cs` 通过反射加载指定版本的实际产品 EXE，使用合成配置，分别向翻译和识别设置页发送 120/30 的原生滚轮增量。每组向下 60 次、向上 60 次，记录滚动位置、Layout/Paint 次数、处理耗时分位数及进程 CPU。界面显示在当前屏幕，因此也须串行运行。

```powershell
$scroll = & .\tests\visual\BuildScrollProbe.ps1
& $scroll .\dist\SnapTransOCR.exe .\test-output\scroll-current.json
```

使用同一探针分别加载旧版和新版 EXE，可在固定屏幕/DPI下交替比较。计时包括 SendMessage、消息泵及 Update，不含每次间隔的 8 ms Sleep；不是输入到屏幕显示的延迟或刷新率。到达边界后可能无位移，必须一起检查 `moving` 和 positions，不能仅比较所有事件的中位数。探针不读取用户配置或调用 API。

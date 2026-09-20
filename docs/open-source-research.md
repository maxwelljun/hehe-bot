# 通用画面监控：开源方案调研

调研日期：2026-09-14。查询了项目仓库元数据、README 与图像匹配文档；以下为文档分析，没有声称在 Windows 上实际运行过这些工具。

| 方案 | 可用能力 | 依赖与取舍 | 对本项目的结论 |
| --- | --- | --- | --- |
| [AutoHotkey](https://github.com/AutoHotkey/AutoHotkey) | `ImageSearch` 在屏幕区域内查找图片，支持颜色容差 | Windows 脚本方案轻，脚本运行需解释器，也可通过打包工具分发可执行文件；界面和状态管理需自行编写。GPL-2.0 | 最接近轻量原型的选择；屏幕图片必须可见、尺寸需匹配 |
| [SikuliX1](https://github.com/oculix-org/SikuliX1) / [Oculix](https://github.com/oculix-org/Oculix) | 屏幕图像定位、视觉自动化 IDE | SikuliX1 README 指向 Oculix 作为当前延续版本；涉及 Java 17+ 和 OpenCV 等组件。MIT | 视觉能力丰富，依赖和分发复杂度超出单一区域提醒需要 |
| [ShareX](https://github.com/ShareX/ShareX) | 成熟的截图、录屏和截图后任务 | Windows C# 桌面程序，功能范围较广。GPL-3.0 | 适合参考截图交互，不是直接提供本项目连续匹配与提醒状态机的成品 |
| [OpenCV](https://github.com/opencv/opencv) | `matchTemplate` 在大图内寻找参考图位置 | C++ 原生库或语言绑定；作为识别组件还需自行构建桌面程序。Apache-2.0 | 若未来需要在移动区域内搜索图像，可以评估；固定区域比对暂不引入 |

## 选择

本项目采用独立实现的 C#/.NET Windows Forms 程序，不直接引入上述开源项目的代码或二进制。使用系统截图能力和固定区域彩色比对，使程序无需 Python、Java、浏览器驱动或第三方 NuGet 依赖。

发布时自带 .NET 运行时，用户只需启动一个 EXE；代价是文件体积大于仅分发脚本或依赖预装运行时的程序。开发阶段需要 .NET SDK。

为减少实现和维护复杂度，第一版限定为一个监控区域、一张参考图片、本地提醒。对于窗口移动、页面布局重排和浏览器标签在后台等场景，纯屏幕截图并不适合；这类需求需要另行选择浏览器扩展或与具体应用对接的方案。

## 查阅来源

- [AutoHotkey v2 ImageSearch 文档镜像](https://doggy8088.github.io/AutoHotkeyDocs/docs/lib/ImageSearch.htm)：查询区域、颜色容差、图像尺寸限制及可见性要求。
- [SikuliX1 README](https://github.com/oculix-org/SikuliX1/blob/master/README.md)：视觉识别机制、OpenCV、迁移到 Oculix 的说明。
- [Oculix 仓库](https://github.com/oculix-org/Oculix)：当前项目入口及许可证元数据。
- [ShareX README](https://github.com/ShareX/ShareX/blob/master/README.md)：截图、文件分享和生产力工具定位。
- [OpenCV Template Matching 文档](https://docs.opencv.org/4.x/d4/dc6/tutorial_py_template_matching.html)：滑窗模板匹配原理及 `matchTemplate` 接口。

许可证列来自调研时 GitHub 项目元数据；如果以后分发这些项目的代码或二进制，需要按所选版本的实际许可证核对。

<div align="center">

[![LOGO](Ink%20Canvas/Resources/InkCanvas.png?raw=true "LOGO")](# "LOGO")

# Ink-Canvas-Plus

[![QQ 群 996760298](https://img.shields.io/badge/QQ%20群-996760298-blue)](http://qm.qq.com/cgi-bin/qm/qr?_wv=1027&k=KBN8I8M6E24RFoeFw7FNlXdGpOQybxTW&authKey=CNheMzaibvP5cGRwSGP9HTLiTQtpFfPwySrJ0%2BpoCYYF22JqhINFi3Mi8lNLuXCV&noverify=0&group_code=996760298) ![GitHub issues](https://img.shields.io/github/issues/clover-yan/Ink-Canvas-Plus?logo=github)

Ink Canvas Plus (IC+) 是一款 Windows 画板应用，适用于课堂教学和演示批注场景。支持笔触输入、几何作图、PowerPoint 批注、计时器、随机点名等功能。

</div>

## 项目渊源

本仓库复刻自 [clover-yan/Ink-Canvas-Plus](https://github.com/clover-yan/Ink-Canvas-Plus)，后者是 Clover Yan 在 [WXRIW/Ink-Canvas](https://github.com/WXRIW/Ink-Canvas) 基础上维护的增强版本。在此基础上，本分支进行了重构和优化。

### 本分支新增/修改功能

- **几何工具**：黑板/白板模式新增直尺、三角尺、量角器，支持拖动、旋转、缩放，自动切换刻度颜色；直尺和三角尺支持贴边辅助画线；量角器支持点击刻度弧定点标记
- **直线拟合优化**：采用总最小二乘法作为直线拟合算法，支持设置规范化阈值
- **快捷键增强**：添加全局激活、清空笔迹快捷键及快捷键指南；支持矩形和圆形绘制快捷键
- **视觉与交互优化**：
  - 浮动工具栏改为左下悬浮球风格并默认折叠，支持自动折叠
  - 窗口标题实时反映当前画板状态
  - 新增 Plus 豆沙绿等主题选项
- **设置重构**：简化设置绑定逻辑，消除大量冗余 if/else
- **代码质量改进**：修复异常日志空实现、资源泄漏、硬编码 UI 字符串判断等不合理代码

## 📗 FAQ

### 在 Windows 10 以下版本系统中，部分图标显示为 "□" 怎么办？

[点击下载](https://aka.ms/SegoeFonts "SegoeFonts") SegoeFonts 文件，安装压缩包中 `SegMDL2.ttf` 字体后重启即可解决。

### 点击放映后一翻页就闪退

请[激活 Microsoft Office](https://www.coolhub.top/archives/14)。

### 放映后画板程序不会切换到 PPT 模式

1. PowerPoint 处在保护模式下（只读），请退出保护模式，方法如下：
   1. 打开 PowerPoint，点击左上角的"文件"选项；
   2. 在"信息"标签内，点击右侧的"启用编辑"按钮。
2. 曾经安装过 WPS Office 办公软件，导致 COM 组件被破坏，解决方法为完全卸载 WPS Office 后重新安装 Microsoft Office Mondo 2016 即可解决。
3. 请确保 PowerPoint 和本应用运行在同一权限下，如果 PowerPoint 以管理员身份运行而本应用以普通用户身份运行，也会出现无法切换到 PPT 模式的现象，您可以通过检查 PowerPoint 的兼容性设置或提权本应用运行来解决该问题。

### 程序无法正常启动

请检查你的电脑上是否安装了 `.Net Framework 4.7.2` 或更高版本。若没有，请[前往官网](https://dotnet.microsoft.com/zh-cn/download/dotnet-framework/thank-you/net472-offline-installer "下载 .Net Framework 4.7.2")下载安装。

如果仍无法运行，请[安装 `Microsoft Office`](https://www.coolhub.top/archives/11)。

## 感谢

- [WXRIW/Ink-Canvas](https://github.com/WXRIW/Ink-Canvas) — 原始项目
- [clover-yan](https://github.com/clover-yan) — 上游维护者
- [yuwenhui2020](https://github.com/yuwenhui2020) — Ink Canvas 使用说明贡献
- [CN-Ironegg](https://github.com/CN-Ironegg)、[jiajiaxd](https://github.com/jiajiaxd)、[Kengwang](https://github.com/kengwang)、[Raspberry Kan](https://github.com/Raspberry-Monster)、[STBBRD](https://github.com/STBBRD)、[ChangSakura](https://github.com/WuChanging)、[Dubi906w](https://github.com/dubi906w) 为本项目贡献代码

## License

GPLv3

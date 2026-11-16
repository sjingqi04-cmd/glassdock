# GlassDock

Windows 10 / 11 的开源液态玻璃 Dock + 小组件。MIT 许可，免费，无账号、无服务器、无遥测。

> 灵感来自 macOS Dock 与 Atrium Desktop（MIT）的液态玻璃风格；功能参考了 lumabar 的公开功能清单，但没有使用它的任何代码、素材或品牌。

## 下载与运行

1. 到 [Releases](../../releases) 下载 `GlassDock-win-x64.zip`。
2. 解压到任意位置（例如 `D:\Apps\GlassDock`），双击 `GlassDock.exe`。不用安装 .NET。
3. 右键 Dock：小组件库、添加应用、新建文件夹、设置、退出。

设置保存在程序目录的 `data\settings.json`（目录不可写时改存 `%AppData%\GlassDock`）。

## 第一阶段功能

- 玻璃 Dock：底部或顶部，Mac 式悬停放大、点击弹跳，应用文件夹（右键应用 → 移到文件夹）。
- 拖放：把 .exe / 快捷方式拖到 Dock 上固定；在 Dock 上拖动重新排序；从小组件库把卡片拖到 Dock。
- 小组件：时钟、日历、天气（Open-Meteo，免密钥）、系统状态（CPU / 内存 / 电池 / 网速）、音乐控制（系统媒体会话）、待办。
- 6 种样式（透明玻璃、磨砂玻璃、染色玻璃、深色、浅色、午夜）× 3 种尺寸。
- 玻璃模糊由 DWM 在 GPU 上实时合成，背后内容变化时程序本身不重画；小组件定时器在 Dock 隐藏时停止。
- 可隐藏 Windows 任务栏；可为 Dock 预留屏幕空间（AppBar）。
- 开机启动（写入当前用户的 Run 注册表项，可随时关闭）。

## 安全兜底

- GlassDock 不向 explorer.exe 或任何系统进程注入代码，只对任务栏窗口调用 `ShowWindow`。
- 独立的看护进程 `GlassDock.Watchdog.exe`：GlassDock 退出、崩溃、被结束，或界面卡住约 15 秒时，自动恢复 Windows 任务栏。
- 万一任务栏仍不见：按 `Ctrl+Shift+Esc` 打开任务管理器，重启“Windows 资源管理器”即可。

## 从源码构建

需要 .NET 8 SDK：

```
dotnet publish src/GlassDock/GlassDock.csproj -c Release -r win-x64 --self-contained true -o out/GlassDock
dotnet publish src/GlassDock.Watchdog/GlassDock.Watchdog.csproj -c Release -r win-x64 --self-contained true -o out/GlassDock
```

每次推送到 main，GitHub Actions 都会在 windows-latest 上编译并上传 zip；打 `v*` 标签会自动发布 Release。

## 路线图（第二阶段）

开始菜单（拖动排序、文件夹、拼音首字母搜索）、控制中心（音量 / 亮度 / Wi-Fi / 蓝牙）、通知中心、台前调度、多显示器、中英文界面。

## 许可

MIT

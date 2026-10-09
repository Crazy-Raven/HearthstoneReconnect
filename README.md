# HearthstoneReconnect · 炉石战棋拔线

<img src="assets/icon.png" width="96" alt="HearthstoneReconnect 图标">

一个约 136 × 44 的 Windows 悬浮按钮，用于手动重置炉石当前对局的一条 TCP 连接。独立运行，无须安装火石（Firestone）或 Overwolf。

**[下载最新版单文件 EXE](https://github.com/Crazy-Raven/HearthstoneReconnect/releases/latest/download/HearthstoneReconnect.exe)** · [查看所有版本](https://github.com/Crazy-Raven/HearthstoneReconnect/releases)

## 使用

适用于 Windows 10 / 11 的 64 位系统，使用系统自带的 .NET Framework。下载 `HearthstoneReconnect.exe`，放在方便的位置并双击打开，无须安装。

1. 首次使用时，如果尚未开启炉石日志，先完全退出炉石，右键小按钮 → **开启炉石日志…**，然后重新启动炉石。已有其他工具开启日志时可跳过。
2. 点击 **管理员运行**，同意 Windows 权限提示。管理员权限用于重置连接；普通启动不会自动提权。
3. 进入酒馆战棋对局，等待按钮显示 **拔线**。
4. 需要拔线时，手动点击按钮，或按 **Ctrl + Alt + F8**。

| 操作 | 用途 |
| --- | --- |
| 拖动左侧小点 | 移动窗口，并保存位置 |
| 鼠标悬停 | 查看详细状态 |
| 右键按钮 | 开启日志、查看快捷键状态、退出 |
| Ctrl + Alt + F8 | 手动拔线，快捷键可用时生效 |

每次成功重置连接后冷却 15 秒。按钮上的倒计时表示冷却时间；炉石是否以及何时重连由游戏决定。独占全屏可能遮住窗口，可使用快捷键或窗口模式。

## 工作方式与限制

工具读取当前炉石进程对应的最新日志会话，取得当前对局地址，再匹配该进程的 TCP 连接。只有找到唯一的 IPv4 `ESTABLISHED` 连接时才允许操作；登录端口 `1119` 不会被选中。

- 仅点击按钮或按快捷键会尝试断线。启动、状态轮询、自测和诊断不会断线。
- 不自动识别酒馆战棋模式，请在你想操作的战棋对局中手动使用。
- 多个炉石进程、多个匹配连接、仅 IPv6 连接或缺少有效日志时，按钮保持等待。
- 不修改火石或防火墙，不设置开机启动；仅主动选择日志设置时才修改炉石日志配置，并备份已有配置。
- 当前版本尚未完成真实对局断线重连验证。自动自测使用构造数据；发布的 EXE 未做数字签名。

按钮持续显示“等待炉石”或“等待对局”时，先确认管理员权限、日志设置和当前对局。开启日志后须重启炉石。详细操作见 [使用说明.txt](使用说明.txt)。

## 数据与隐私

窗口位置和检查报告保存在 `%LOCALAPPDATA%\HearthstoneReconnect\`，移动或更新 EXE 不影响这些设置。炉石日志配置位于 `%LOCALAPPDATA%\Blizzard\Hearthstone\log.config`。

诊断报告不输出日志原文、账号或连接密钥。反馈问题时请检查附件内容，避免上传炉石原始日志、个人配置或备份。

## 从源码构建

在 Windows 64 位系统的 PowerShell 中进入项目目录：

```powershell
.\build.ps1
```

默认生成 `build\HearthstoneReconnect.exe`。也可用 `-OutputPath` 指定输出路径。脚本使用系统的 .NET Framework C# 编译器，无须安装 .NET SDK；图标与权限清单嵌入 EXE。

运行构造数据自测：

```powershell
$test = Start-Process -FilePath (Resolve-Path .\build\HearthstoneReconnect.exe).Path -ArgumentList '--self-test' -Wait -PassThru
$test.ExitCode
Get-Content "$env:LOCALAPPDATA\HearthstoneReconnect\self-test.txt"
```

自测成功时退出码为 `0`。当前包含 33 项日志解析、连接筛选和日志配置合并检查。

其他命令：

```powershell
# 只读检查本机进程、日志和连接，不断线
Start-Process -FilePath (Resolve-Path .\build\HearthstoneReconnect.exe).Path -ArgumentList '--diagnose' -Wait

# 明确开启日志；必须先退出炉石
Start-Process -FilePath (Resolve-Path .\build\HearthstoneReconnect.exe).Path -ArgumentList '--enable-logging' -Wait
```

报告写入上面的数据目录。更多源码说明见 [源代码说明.txt](源代码说明.txt)。

## 开源许可

Copyright (c) 2026 Crazy-Raven。使用 [MIT License](LICENSE)，可修改和分发，请保留许可与版权声明。单文件 EXE 内也嵌入了 MIT 许可文本。本项目由社区独立开发。

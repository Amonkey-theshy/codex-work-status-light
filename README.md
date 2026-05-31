# Codex 工作状态指示灯

一个本地实时状态灯工具。它包含两种显示方式：

- 桌面悬浮灯：常置顶，自动吸附在 Codex 窗口旁边。
- 网页面板：用于调试或手动控制。

状态和灯色对应关系：

- `waiting`：红灯，等待中
- `working`：黄灯，工作中
- `done`：绿灯，已完成

## 直接使用

下载发布包后，解压到任意目录，双击：

```text
启动桌面指示灯.bat
```

悬浮灯会自动贴到 Codex 窗口旁边，并优先根据 Codex 本地会话事件自动判断：

- 存在尚未结束的会话任务：黄灯
- 会话写入最终回复：绿灯，并闪烁提醒
- 完成提示保留一段时间后：红灯

如果本地会话记录不可用，程序才会退回到 CPU 活动和前台窗口判断。

右键悬浮灯可以关闭自动检测、关闭吸附、手动切换灯色或退出。左键拖动后会自动吸附回 Codex 边框。

如果 Codex 没有打开，悬浮灯会隐藏在后台；Codex 窗口出现后会自动显示并吸附。

## 设置自启动

双击：

```text
install-autostart.bat
```

或者运行：

```powershell
.\Install-Autostart.ps1
```

取消自启动：

```text
uninstall-autostart.bat
```

或者运行：

```powershell
.\Uninstall-Autostart.ps1
```

## 从源码运行

```powershell
.\Build-DesktopLight.ps1
.\Start-DesktopLight.ps1
```

项目使用 Windows 自带的 .NET Framework C# 编译器，不需要安装 Visual Studio。发布包会带已编译好的 `DesktopLight.exe`。

## 打发布包

```powershell
.\Create-ReleaseZip.ps1
```

生成文件：

```text
dist\CodexWorkStatusLight.zip
```

## 启动网页面板（可选）

```powershell
npm start
```

打开：

```text
http://127.0.0.1:5058
```

## 切换状态

可以右键悬浮灯切换，也可以在网页里点按钮，或用脚本：

```powershell
.\Set-Status.ps1 waiting
.\Set-Status.ps1 working "正在处理任务"
.\Set-Status.ps1 done "任务完成"
```

也可以直接调用接口：

```powershell
Invoke-RestMethod -Method Post -Uri "http://127.0.0.1:5058/api/status" -ContentType "application/json" -Body '{"state":"working"}'
```

状态会保存到 `status.json`，刷新网页后仍会保留。

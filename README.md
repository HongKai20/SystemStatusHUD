# System Status HUD

一款轻量的 Windows 硬件状态悬浮窗，实时显示 CPU、GPU、内存和磁盘状态。

## 功能特点

- 实时显示 CPU 使用率与功耗
- 实时显示 GPU 使用率与功耗
- 显示内存使用率、已用容量和总容量
- 显示磁盘活动率及实时读写速度
- 无边框毛玻璃风格界面，默认置顶显示
- 支持拖动悬浮窗位置
- 托盘菜单支持开机自启和关闭程序
- 单实例运行，避免重复启动
- 数据仅在本机采集，不进行网络上传

> CPU 功耗取决于系统是否提供 Energy Meter/RAPL 性能计数器。NVIDIA 显卡通过 `nvidia-smi` 获取使用率和功耗；其他显卡使用 Windows GPU 性能计数器，部分功耗数据可能无法显示。

## 下载与运行

1. 下载仓库中的 [SystemStatusHUD.exe](./SystemStatusHUD.exe)。
2. 双击运行，无需安装。
3. 程序会显示在桌面右上角，并在系统托盘中创建图标。
4. 右键托盘图标可设置“开机自启”或选择“关闭”。

系统要求：Windows 10 或 Windows 11。

## 从源码编译

项目使用单文件 C# WinForms 实现，可通过 Windows 自带的 .NET Framework C# 编译器构建：

```powershell
& "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe" `
  /target:winexe `
  /optimize+ `
  /out:SystemStatusHUD.exe `
  /win32icon:SystemStatusHUD.ico `
  /reference:System.dll `
  /reference:System.Drawing.dll `
  /reference:System.Windows.Forms.dll `
  SystemStatusHUD.cs
```

## 项目文件

- `SystemStatusHUD.cs`：完整程序源代码
- `SystemStatusHUD.ico`：应用程序图标
- `SystemStatusHUD.exe`：已编译的 Windows 可执行文件

## 数据来源

- CPU：Windows `GetSystemTimes` 与 Energy Meter 性能计数器
- GPU：NVIDIA `nvidia-smi` 或 Windows GPU Engine 性能计数器
- 内存：Windows `GlobalMemoryStatusEx`
- 磁盘：Windows PhysicalDisk 性能计数器

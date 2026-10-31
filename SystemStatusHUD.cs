using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("System Status HUD")]
[assembly: System.Reflection.AssemblyDescription("Windows hardware status overlay")]
[assembly: System.Reflection.AssemblyProduct("System Status HUD")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]
[assembly: System.Reflection.AssemblyInformationalVersion("2.0a")]

namespace SystemStatusHUD
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            NativeMethods.SetProcessDPIAware();
            bool isFirstInstance;
            using (Mutex mutex = new Mutex(true, "Local\\SystemStatusHUD.SingleInstance", out isFirstInstance))
            {
                if (!isFirstInstance)
                    return;

                Forms.Application.EnableVisualStyles();
                Forms.Application.SetCompatibleTextRenderingDefault(false);

                System.Windows.Application application = new System.Windows.Application();
                application.ShutdownMode = ShutdownMode.OnMainWindowClose;
                application.Run(new StatusWindow());
            }
        }
    }

    internal sealed class StatusWindow : Window
    {
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "SystemStatusHUD";
        private readonly MetricCard _cpu;
        private readonly MetricCard _gpu;
        private readonly MetricCard _memory;
        private readonly MetricCard _disk;
        private readonly Forms.NotifyIcon _trayIcon;
        private readonly Forms.ContextMenuStrip _trayMenu;
        private readonly Forms.ToolStripMenuItem _startupMenuItem;
        private Drawing.Icon _applicationIcon;
        private System.Threading.Timer _collectorTimer;
        private MetricCollector _collector;
        private int _updateInProgress;

        public StatusWindow()
        {
            Title = "系统状态监控";
            Width = 356;
            Height = 266;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _cpu = new MetricCard("CPU", "处理器占用", Color.FromRgb(14, 139, 196));
            _gpu = new MetricCard("GPU", "图形处理器", Color.FromRgb(124, 82, 202));
            _memory = new MetricCard("内存", "物理内存", Color.FromRgb(19, 148, 108));
            _disk = new MetricCard("硬盘", "磁盘活动", Color.FromRgb(214, 117, 35));
            Content = CreateTransparentLayout();
            MouseLeftButtonDown += HandleMouseLeftButtonDown;

            try
            {
                _applicationIcon = Drawing.Icon.ExtractAssociatedIcon(Forms.Application.ExecutablePath);
                if (_applicationIcon != null)
                {
                    ImageSource iconSource = Imaging.CreateBitmapSourceFromHIcon(
                        _applicationIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    iconSource.Freeze();
                    Icon = iconSource;
                }
            }
            catch { }

            _startupMenuItem = new Forms.ToolStripMenuItem("开机自启");
            _startupMenuItem.Name = "startup";
            _startupMenuItem.Checked = IsStartupEnabled();
            _startupMenuItem.Click += delegate { ToggleStartup(); };

            Forms.ToolStripMenuItem closeMenuItem = new Forms.ToolStripMenuItem("关闭");
            closeMenuItem.Name = "close";
            closeMenuItem.Click += delegate
            {
                _trayIcon.Visible = false;
                Dispatcher.BeginInvoke(new Action(Close));
            };

            _trayMenu = new Forms.ContextMenuStrip();
            _trayMenu.ShowImageMargin = false;
            _trayMenu.Items.Add(_startupMenuItem);
            _trayMenu.Items.Add(new Forms.ToolStripSeparator());
            _trayMenu.Items.Add(closeMenuItem);
            _trayMenu.Opening += delegate { _startupMenuItem.Checked = IsStartupEnabled(); };

            _trayIcon = new Forms.NotifyIcon();
            _trayIcon.Text = "系统状态监控";
            _trayIcon.Icon = _applicationIcon ?? Drawing.SystemIcons.Application;
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Visible = true;
        }

        private Canvas CreateTransparentLayout()
        {
            Canvas surface = new Canvas();
            surface.Width = 356;
            surface.Height = 266;
            surface.Background = Brushes.Transparent;
            AddCard(surface, _cpu, 12);
            AddCard(surface, _gpu, 74);
            AddCard(surface, _memory, 136);
            AddCard(surface, _disk, 198);
            return surface;
        }

        private static void AddCard(Canvas surface, MetricCard card, double top)
        {
            Canvas.SetLeft(card.Root, 12);
            Canvas.SetTop(card.Root, top);
            surface.Children.Add(card.Root);
        }

        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            Rect area = SystemParameters.WorkArea;
            Left = area.Right - Width - 14;
            Top = area.Top + 14;
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);
            _collectorTimer = new System.Threading.Timer(UpdateMetrics, null, 0, 1000);
        }

        private void HandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed)
                return;
            try
            {
                DragMove();
            }
            catch (InvalidOperationException) { }
        }

        private void UpdateMetrics(object state)
        {
            if (Interlocked.Exchange(ref _updateInProgress, 1) != 0)
                return;

            HardwareSnapshot snapshot;
            try
            {
                if (_collector == null)
                    _collector = new MetricCollector();
                snapshot = _collector.Collect();
            }
            catch
            {
                snapshot = new HardwareSnapshot();
            }
            finally
            {
                Interlocked.Exchange(ref _updateInProgress, 0);
            }

            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                return;
            try
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    ApplySnapshot(snapshot);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void ApplySnapshot(HardwareSnapshot snapshot)
        {
            _cpu.Update(snapshot.CpuLoad, "功耗  " + FormatPower(snapshot.CpuPowerWatts));
            _gpu.Update(snapshot.GpuLoad, "功耗  " + FormatPower(snapshot.GpuPowerWatts));
            _memory.Update(snapshot.MemoryLoad,
                FormatBytes(snapshot.MemoryUsedBytes) + " / " + FormatBytes(snapshot.MemoryTotalBytes));
            _disk.Update(snapshot.DiskActive,
                "读 " + FormatRate(snapshot.DiskReadBytesPerSecond) + "   写 " + FormatRate(snapshot.DiskWriteBytesPerSecond));
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_collectorTimer != null)
                _collectorTimer.Dispose();
            if (_collector != null)
                _collector.Dispose();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            if (_applicationIcon != null)
                _applicationIcon.Dispose();
            base.OnClosed(e);
        }

        private static string StartupCommand
        {
            get { return "\"" + Forms.Application.ExecutablePath + "\""; }
        }

        private static bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, false))
                {
                    if (key == null)
                        return false;
                    string command = Convert.ToString(key.GetValue(StartupValueName));
                    return string.Equals(command, StartupCommand, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        private void ToggleStartup()
        {
            bool enable = !IsStartupEnabled();
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(StartupRegistryPath))
                {
                    if (enable)
                        key.SetValue(StartupValueName, StartupCommand, RegistryValueKind.String);
                    else
                        key.DeleteValue(StartupValueName, false);
                }
                _startupMenuItem.Checked = enable;
            }
            catch
            {
                _startupMenuItem.Checked = IsStartupEnabled();
                System.Windows.MessageBox.Show("无法修改开机自启设置。", "系统状态监控",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string FormatPower(double? watts)
        {
            if (!watts.HasValue || double.IsNaN(watts.Value) || watts.Value < 0)
                return "--";
            return watts.Value.ToString("0.0", CultureInfo.CurrentCulture) + " W";
        }

        private static string FormatBytes(ulong bytes)
        {
            if (bytes == 0)
                return "--";
            return (bytes / 1073741824.0).ToString("0.0", CultureInfo.CurrentCulture) + " GB";
        }

        private static string FormatRate(double? bytesPerSecond)
        {
            if (!bytesPerSecond.HasValue || double.IsNaN(bytesPerSecond.Value) || bytesPerSecond.Value < 0)
                return "--";
            double value = bytesPerSecond.Value;
            if (value >= 1073741824.0)
                return (value / 1073741824.0).ToString("0.0", CultureInfo.CurrentCulture) + " GB/s";
            if (value >= 1048576.0)
                return (value / 1048576.0).ToString("0.0", CultureInfo.CurrentCulture) + " MB/s";
            return (value / 1024.0).ToString("0.0", CultureInfo.CurrentCulture) + " KB/s";
        }
    }

    internal sealed class MetricCard
    {
        private const double ProgressWidth = 303;
        private readonly TextBlock _value;
        private readonly TextBlock _detail;
        private readonly Border _progressFill;
        public readonly Border Root;

        public MetricCard(string name, string caption, Color accent)
        {
            SolidColorBrush accentBrush = FrozenBrush(Color.FromArgb(255, accent.R, accent.G, accent.B));
            SolidColorBrush primaryText = FrozenBrush(Color.FromRgb(25, 48, 66));
            SolidColorBrush secondaryText = FrozenBrush(Color.FromRgb(68, 91, 108));

            Root = new Border();
            Root.Width = 332;
            Root.Height = 56;
            Root.CornerRadius = new CornerRadius(11);
            Root.Background = FrozenBrush(Color.FromArgb(108, 238, 249, 255));
            Root.BorderBrush = FrozenBrush(Color.FromArgb(178, 255, 255, 255));
            Root.BorderThickness = new Thickness(1);
            Root.SnapsToDevicePixels = true;

            Grid layout = new Grid();
            layout.Margin = new Thickness(9, 4, 10, 5);
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(22) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(17) });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3) });
            Root.Child = layout;

            Grid header = new Grid();
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(header, 0);
            layout.Children.Add(header);

            TextBlock nameText = new TextBlock();
            nameText.Text = name;
            nameText.FontFamily = new FontFamily("Microsoft YaHei UI");
            nameText.FontSize = 13;
            nameText.FontWeight = FontWeights.Bold;
            nameText.Foreground = accentBrush;
            nameText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(nameText, 0);
            header.Children.Add(nameText);

            TextBlock captionText = new TextBlock();
            captionText.Text = caption;
            captionText.FontFamily = new FontFamily("Microsoft YaHei UI");
            captionText.FontSize = 10.5;
            captionText.Foreground = secondaryText;
            captionText.Margin = new Thickness(7, 1, 0, 0);
            captionText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(captionText, 1);
            header.Children.Add(captionText);

            _value = new TextBlock();
            _value.Text = "--";
            _value.FontFamily = new FontFamily("Consolas");
            _value.FontSize = 17;
            _value.FontWeight = FontWeights.Bold;
            _value.Foreground = primaryText;
            _value.HorizontalAlignment = HorizontalAlignment.Right;
            _value.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(_value, 2);
            header.Children.Add(_value);

            _detail = new TextBlock();
            _detail.Text = "--";
            _detail.FontFamily = new FontFamily("Consolas");
            _detail.FontSize = 11.5;
            _detail.Foreground = secondaryText;
            _detail.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(_detail, 1);
            layout.Children.Add(_detail);

            Grid track = new Grid();
            track.Width = ProgressWidth;
            track.Height = 3;
            track.HorizontalAlignment = HorizontalAlignment.Left;
            track.Background = FrozenBrush(Color.FromArgb(58, 53, 83, 102));
            track.ClipToBounds = true;
            Grid.SetRow(track, 2);
            layout.Children.Add(track);

            _progressFill = new Border();
            _progressFill.Width = 0;
            _progressFill.Height = 3;
            _progressFill.HorizontalAlignment = HorizontalAlignment.Left;
            _progressFill.Background = accentBrush;
            track.Children.Add(_progressFill);
        }

        public void Update(double? percent, string detail)
        {
            _value.Text = percent.HasValue
                ? percent.Value.ToString("0", CultureInfo.CurrentCulture) + "%"
                : "--";
            _detail.Text = detail;
            double bounded = percent.HasValue ? Math.Max(0, Math.Min(100, percent.Value)) : 0;
            _progressFill.Width = ProgressWidth * bounded / 100.0;
        }

        private static SolidColorBrush FrozenBrush(Color color)
        {
            SolidColorBrush brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }

    internal sealed class HardwareSnapshot
    {
        public double? CpuLoad;
        public double? CpuPowerWatts;
        public double? GpuLoad;
        public double? GpuPowerWatts;
        public double? MemoryLoad;
        public ulong MemoryUsedBytes;
        public ulong MemoryTotalBytes;
        public double? DiskActive;
        public double? DiskReadBytesPerSecond;
        public double? DiskWriteBytesPerSecond;
    }

    internal sealed class MetricCollector : IDisposable
    {
        private PerformanceCounter _diskRead;
        private PerformanceCounter _diskWrite;
        private PerformanceCounter _diskActive;
        private readonly List<PerformanceCounter> _cpuPowerCounters = new List<PerformanceCounter>();
        private readonly List<PerformanceCounter> _gpuUsageCounters = new List<PerformanceCounter>();
        private readonly string _nvidiaSmiPath;
        private ulong _previousIdle;
        private ulong _previousKernel;
        private ulong _previousUser;
        private bool _hasCpuBaseline;
        private int _gpuRefreshTick;

        public MetricCollector()
        {
            InitializeDiskCounters();
            InitializeCpuPowerCounters();
            _nvidiaSmiPath = FindNvidiaSmi();
            if (_nvidiaSmiPath == null)
                RefreshGpuUsageCounters();
            ReadCpuLoad();
        }

        public HardwareSnapshot Collect()
        {
            HardwareSnapshot snapshot = new HardwareSnapshot();
            snapshot.CpuLoad = ReadCpuLoad();
            snapshot.CpuPowerWatts = ReadCpuPower();
            ReadMemory(snapshot);
            ReadDisk(snapshot);
            ReadGpu(snapshot);
            return snapshot;
        }

        private void InitializeDiskCounters()
        {
            try
            {
                _diskRead = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
                _diskWrite = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
                _diskActive = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", true);
                _diskRead.NextValue();
                _diskWrite.NextValue();
                _diskActive.NextValue();
            }
            catch
            {
                DisposeCounter(ref _diskRead);
                DisposeCounter(ref _diskWrite);
                DisposeCounter(ref _diskActive);
            }
        }

        private void InitializeCpuPowerCounters()
        {
            try
            {
                PerformanceCounterCategory category = new PerformanceCounterCategory("Energy Meter");
                foreach (string instance in category.GetInstanceNames())
                {
                    if (instance.StartsWith("RAPL_Package", StringComparison.OrdinalIgnoreCase) &&
                        instance.EndsWith("_PKG", StringComparison.OrdinalIgnoreCase))
                    {
                        PerformanceCounter counter = new PerformanceCounter("Energy Meter", "Power", instance, true);
                        counter.NextValue();
                        _cpuPowerCounters.Add(counter);
                    }
                }
            }
            catch
            {
                DisposeCounters(_cpuPowerCounters);
            }
        }

        private double? ReadCpuLoad()
        {
            NativeMethods.FileTime idle;
            NativeMethods.FileTime kernel;
            NativeMethods.FileTime user;
            if (!NativeMethods.GetSystemTimes(out idle, out kernel, out user))
                return null;
            ulong idleValue = idle.ToUInt64();
            ulong kernelValue = kernel.ToUInt64();
            ulong userValue = user.ToUInt64();
            if (!_hasCpuBaseline)
            {
                _previousIdle = idleValue;
                _previousKernel = kernelValue;
                _previousUser = userValue;
                _hasCpuBaseline = true;
                return null;
            }
            ulong idleDelta = idleValue - _previousIdle;
            ulong kernelDelta = kernelValue - _previousKernel;
            ulong userDelta = userValue - _previousUser;
            ulong total = kernelDelta + userDelta;
            _previousIdle = idleValue;
            _previousKernel = kernelValue;
            _previousUser = userValue;
            if (total == 0)
                return null;
            return Math.Max(0, Math.Min(100, 100.0 * (total - idleDelta) / total));
        }

        private double? ReadCpuPower()
        {
            if (_cpuPowerCounters.Count == 0)
                return null;
            double totalMilliwatts = 0;
            bool hasValue = false;
            foreach (PerformanceCounter counter in _cpuPowerCounters)
            {
                try
                {
                    float value = counter.NextValue();
                    if (!float.IsNaN(value) && value >= 0)
                    {
                        totalMilliwatts += value;
                        hasValue = true;
                    }
                }
                catch { }
            }
            return hasValue ? (double?)(totalMilliwatts / 1000.0) : null;
        }

        private static void ReadMemory(HardwareSnapshot snapshot)
        {
            NativeMethods.MemoryStatus status = new NativeMethods.MemoryStatus();
            status.Length = (uint)Marshal.SizeOf(typeof(NativeMethods.MemoryStatus));
            if (!NativeMethods.GlobalMemoryStatusEx(ref status))
                return;
            snapshot.MemoryTotalBytes = status.TotalPhysical;
            snapshot.MemoryUsedBytes = status.TotalPhysical - status.AvailablePhysical;
            snapshot.MemoryLoad = status.MemoryLoad;
        }

        private void ReadDisk(HardwareSnapshot snapshot)
        {
            try
            {
                if (_diskRead != null)
                    snapshot.DiskReadBytesPerSecond = Math.Max(0, _diskRead.NextValue());
                if (_diskWrite != null)
                    snapshot.DiskWriteBytesPerSecond = Math.Max(0, _diskWrite.NextValue());
                if (_diskActive != null)
                    snapshot.DiskActive = Math.Max(0, Math.Min(100, _diskActive.NextValue()));
            }
            catch { }
        }

        private void ReadGpu(HardwareSnapshot snapshot)
        {
            bool readByNvidia = _nvidiaSmiPath != null && ReadNvidiaGpu(snapshot);
            if (readByNvidia)
                return;
            _gpuRefreshTick++;
            if (_gpuUsageCounters.Count == 0 || _gpuRefreshTick >= 15)
            {
                _gpuRefreshTick = 0;
                RefreshGpuUsageCounters();
            }
            snapshot.GpuLoad = ReadWindowsGpuUsage();
        }

        private bool ReadNvidiaGpu(HardwareSnapshot snapshot)
        {
            try
            {
                ProcessStartInfo startInfo = new ProcessStartInfo();
                startInfo.FileName = _nvidiaSmiPath;
                startInfo.Arguments = "--query-gpu=utilization.gpu,power.draw --format=csv,noheader,nounits";
                startInfo.UseShellExecute = false;
                startInfo.CreateNoWindow = true;
                startInfo.RedirectStandardOutput = true;
                startInfo.RedirectStandardError = true;
                using (Process process = Process.Start(startInfo))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    if (!process.WaitForExit(1500))
                    {
                        process.Kill();
                        return false;
                    }
                    double highestLoad = -1;
                    double totalPower = 0;
                    bool hasLoad = false;
                    bool hasPower = false;
                    string[] lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string line in lines)
                    {
                        string[] parts = line.Split(',');
                        if (parts.Length < 2)
                            continue;
                        double load;
                        if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out load))
                        {
                            highestLoad = Math.Max(highestLoad, load);
                            hasLoad = true;
                        }
                        double power;
                        if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out power))
                        {
                            totalPower += power;
                            hasPower = true;
                        }
                    }
                    snapshot.GpuLoad = hasLoad ? (double?)Math.Max(0, Math.Min(100, highestLoad)) : null;
                    snapshot.GpuPowerWatts = hasPower ? (double?)Math.Max(0, totalPower) : null;
                    return hasLoad || hasPower;
                }
            }
            catch
            {
                return false;
            }
        }

        private void RefreshGpuUsageCounters()
        {
            DisposeCounters(_gpuUsageCounters);
            try
            {
                PerformanceCounterCategory category = new PerformanceCounterCategory("GPU Engine");
                foreach (string instance in category.GetInstanceNames())
                {
                    if (instance.IndexOf("engtype_3D", StringComparison.OrdinalIgnoreCase) < 0 &&
                        instance.IndexOf("engtype_Graphics", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    try
                    {
                        PerformanceCounter counter = new PerformanceCounter("GPU Engine", "Utilization Percentage", instance, true);
                        counter.NextValue();
                        _gpuUsageCounters.Add(counter);
                    }
                    catch { }
                }
            }
            catch { }
        }

        private double? ReadWindowsGpuUsage()
        {
            if (_gpuUsageCounters.Count == 0)
                return null;
            double total = 0;
            bool hasValue = false;
            foreach (PerformanceCounter counter in _gpuUsageCounters)
            {
                try
                {
                    float value = counter.NextValue();
                    if (!float.IsNaN(value) && value >= 0)
                    {
                        total += value;
                        hasValue = true;
                    }
                }
                catch { }
            }
            return hasValue ? (double?)Math.Max(0, Math.Min(100, total)) : null;
        }

        private static string FindNvidiaSmi()
        {
            string systemPath = Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe");
            if (File.Exists(systemPath))
                return systemPath;
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string installedPath = Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe");
            return File.Exists(installedPath) ? installedPath : null;
        }

        public void Dispose()
        {
            DisposeCounter(ref _diskRead);
            DisposeCounter(ref _diskWrite);
            DisposeCounter(ref _diskActive);
            DisposeCounters(_cpuPowerCounters);
            DisposeCounters(_gpuUsageCounters);
        }

        private static void DisposeCounter(ref PerformanceCounter counter)
        {
            if (counter == null)
                return;
            counter.Dispose();
            counter = null;
        }

        private static void DisposeCounters(List<PerformanceCounter> counters)
        {
            foreach (PerformanceCounter counter in counters)
                counter.Dispose();
            counters.Clear();
        }
    }

    internal static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GlobalMemoryStatusEx(ref MemoryStatus buffer);

        [StructLayout(LayoutKind.Sequential)]
        public struct FileTime
        {
            public uint Low;
            public uint High;
            public ulong ToUInt64() { return ((ulong)High << 32) | Low; }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }
    }
}

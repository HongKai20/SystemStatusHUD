using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("System Status HUD")]
[assembly: System.Reflection.AssemblyDescription("Windows hardware status overlay")]
[assembly: System.Reflection.AssemblyProduct("System Status HUD")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

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

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new StatusForm());
            }
        }
    }

    internal sealed class StatusForm : Form
    {
        private readonly MetricView _cpu = new MetricView("CPU", "处理器占用", Color.FromArgb(14, 139, 196));
        private readonly MetricView _gpu = new MetricView("GPU", "图形处理器", Color.FromArgb(124, 82, 202));
        private readonly MetricView _memory = new MetricView("内存", "物理内存", Color.FromArgb(19, 148, 108));
        private readonly MetricView _disk = new MetricView("硬盘", "磁盘活动", Color.FromArgb(214, 117, 35));
        private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string StartupValueName = "SystemStatusHUD";
        private readonly Font _nameFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _captionFont = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _valueFont = new Font("Consolas", 12.5f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _detailFont = new Font("Consolas", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly ToolStripMenuItem _startupMenuItem;
        private Icon _applicationIcon;
        private System.Threading.Timer _collectorTimer;
        private MetricCollector _collector;
        private int _updateInProgress;
        private Bitmap _desktopSnapshot;
        private Bitmap _frostedBackground;
        private Rectangle _desktopBounds;

        public StatusForm()
        {
            Text = "系统状态监控";
            ClientSize = new Size(356, 266);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            BackColor = Color.FromArgb(224, 238, 247);
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);

            try
            {
                _applicationIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (_applicationIcon != null)
                    Icon = _applicationIcon;
            }
            catch { }

            _startupMenuItem = new ToolStripMenuItem("开机自启");
            _startupMenuItem.Name = "startup";
            _startupMenuItem.Checked = IsStartupEnabled();
            _startupMenuItem.Click += delegate { ToggleStartup(); };

            ToolStripMenuItem closeMenuItem = new ToolStripMenuItem("关闭");
            closeMenuItem.Name = "close";
            closeMenuItem.Click += delegate
            {
                _trayIcon.Visible = false;
                Close();
            };

            _trayMenu = new GlassContextMenuStrip();
            _trayMenu.Items.Add(_startupMenuItem);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(closeMenuItem);
            _trayMenu.Opening += delegate { _startupMenuItem.Checked = IsStartupEnabled(); };

            _trayIcon = new NotifyIcon();
            _trayIcon.Text = "系统状态监控";
            _trayIcon.Icon = _applicationIcon ?? SystemIcons.Application;
            _trayIcon.ContextMenuStrip = _trayMenu;
            _trayIcon.Visible = true;
        }

        protected override void OnLoad(EventArgs e)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 14, area.Top + 14);
            CaptureDesktopSnapshot();
            RefreshFrostedBackground();
            base.OnLoad(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ApplyRoundedRegion();
            _collectorTimer = new System.Threading.Timer(UpdateMetrics, null, 0, 1000);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;

            if (_frostedBackground != null)
                g.DrawImageUnscaled(_frostedBackground, 0, 0);
            else
            {
                using (Brush fallback = new SolidBrush(Color.FromArgb(224, 238, 247)))
                    g.FillRectangle(fallback, ClientRectangle);
            }

            using (LinearGradientBrush veil = new LinearGradientBrush(
                ClientRectangle, Color.FromArgb(174, 250, 254, 255), Color.FromArgb(142, 207, 234, 247), LinearGradientMode.Vertical))
                g.FillRectangle(veil, ClientRectangle);
            using (Brush coolGlow = new SolidBrush(Color.FromArgb(24, 84, 174, 255)))
                g.FillEllipse(coolGlow, -74, -88, 258, 186);
            using (Brush softGlow = new SolidBrush(Color.FromArgb(22, 255, 255, 255)))
                g.FillEllipse(softGlow, 196, 176, 224, 214);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (Pen border = new Pen(Color.FromArgb(210, 255, 255, 255), 1f))
                DrawRoundedRectangle(g, border, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), 16f);
            using (Pen innerBorder = new Pen(Color.FromArgb(48, 70, 116, 145), 1f))
                DrawRoundedRectangle(g, innerBorder, new RectangleF(1.5f, 1.5f, Width - 3f, Height - 3f), 15f);

            DrawMetric(g, _cpu, new Rectangle(12, 12, 332, 56));
            DrawMetric(g, _gpu, new Rectangle(12, 74, 332, 56));
            DrawMetric(g, _memory, new Rectangle(12, 136, 332, 56));
            DrawMetric(g, _disk, new Rectangle(12, 198, 332, 56));
        }

        private void DrawMetric(Graphics g, MetricView metric, Rectangle bounds)
        {
            RectangleF shadowBounds = new RectangleF(bounds.Left + 1, bounds.Top + 3, bounds.Width - 2, bounds.Height - 1);
            using (Brush shadow = new SolidBrush(Color.FromArgb(25, 38, 73, 96)))
                FillRoundedRectangle(g, shadow, shadowBounds, 11f);

            using (LinearGradientBrush glass = new LinearGradientBrush(
                bounds, Color.FromArgb(202, 255, 255, 255), Color.FromArgb(108, 220, 239, 249), LinearGradientMode.Vertical))
                FillRoundedRectangle(g, glass, bounds, 11f);

            GraphicsState state = g.Save();
            using (GraphicsPath clip = CreateRoundedPath(bounds, 11f))
            {
                g.SetClip(clip);
                RectangleF sheenBounds = new RectangleF(bounds.Left, bounds.Top, bounds.Width, 27);
                using (LinearGradientBrush sheen = new LinearGradientBrush(
                    sheenBounds, Color.FromArgb(124, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                    g.FillRectangle(sheen, sheenBounds);
                using (Brush lowerGlow = new SolidBrush(Color.FromArgb(25, metric.Accent)))
                    g.FillEllipse(lowerGlow, bounds.Right - 104, bounds.Bottom - 36, 132, 62);
            }
            g.Restore(state);

            using (Pen border = new Pen(Color.FromArgb(210, 255, 255, 255), 1f))
                DrawRoundedRectangle(g, border, bounds, 11f);
            using (Pen innerBorder = new Pen(Color.FromArgb(40, 57, 101, 130), 1f))
                DrawRoundedRectangle(g, innerBorder,
                    new RectangleF(bounds.Left + 1, bounds.Top + 1, bounds.Width - 2, bounds.Height - 2), 10f);

            RectangleF accentPill = new RectangleF(bounds.Left + 9, bounds.Top + 11, 3, bounds.Height - 22);
            using (LinearGradientBrush accentBrush = new LinearGradientBrush(
                accentPill, Color.FromArgb(235, metric.Accent), Color.FromArgb(112, metric.Accent), LinearGradientMode.Vertical))
                FillRoundedRectangle(g, accentBrush, accentPill, 1.5f);

            using (Brush accent = new SolidBrush(metric.Accent))
                g.DrawString(metric.Name, _nameFont, accent, bounds.Left + 18, bounds.Top + 6);
            float nameWidth = g.MeasureString(metric.Name, _nameFont).Width;
            using (Brush caption = new SolidBrush(Color.FromArgb(82, 104, 120)))
                g.DrawString(metric.Caption, _captionFont, caption, bounds.Left + 21 + nameWidth, bounds.Top + 9);

            string value = metric.Percent.HasValue
                ? metric.Percent.Value.ToString("0", CultureInfo.CurrentCulture) + "%"
                : "--";
            RectangleF valueBounds = new RectangleF(bounds.Right - 80, bounds.Top + 3, 68, 23);
            using (Brush valueBrush = new SolidBrush(Color.FromArgb(25, 48, 66)))
                DrawRightAlignedString(g, value, _valueFont, valueBrush, valueBounds);

            using (Brush detailBrush = new SolidBrush(Color.FromArgb(68, 91, 108)))
                g.DrawString(metric.Detail, _detailFont, detailBrush, bounds.Left + 18, bounds.Top + 28);

            RectangleF track = new RectangleF(bounds.Left + 18, bounds.Bottom - 8, bounds.Width - 29, 3);
            using (Brush trackBrush = new SolidBrush(Color.FromArgb(54, 53, 83, 102)))
                FillRoundedRectangle(g, trackBrush, track, 2f);
            float percent = metric.Percent.HasValue ? (float)Math.Max(0, Math.Min(100, metric.Percent.Value)) : 0f;
            if (percent > 0)
            {
                RectangleF fill = new RectangleF(track.Left, track.Top, track.Width * percent / 100f, track.Height);
                using (LinearGradientBrush fillBrush = new LinearGradientBrush(
                    fill, Color.FromArgb(235, metric.Accent), Color.FromArgb(145, metric.Accent), LinearGradientMode.Horizontal))
                    FillRoundedRectangle(g, fillBrush, fill, 2f);
            }
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

            if (IsDisposed || !IsHandleCreated)
                return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    ApplySnapshot(snapshot);
                }));
            }
            catch (InvalidOperationException) { }
        }

        private void ApplySnapshot(HardwareSnapshot snapshot)
        {
            _cpu.Percent = snapshot.CpuLoad;
            _cpu.Detail = "功耗  " + FormatPower(snapshot.CpuPowerWatts);
            _gpu.Percent = snapshot.GpuLoad;
            _gpu.Detail = "功耗  " + FormatPower(snapshot.GpuPowerWatts);
            _memory.Percent = snapshot.MemoryLoad;
            _memory.Detail = FormatBytes(snapshot.MemoryUsedBytes) + " / " + FormatBytes(snapshot.MemoryTotalBytes);
            _disk.Percent = snapshot.DiskActive;
            _disk.Detail = "读 " + FormatRate(snapshot.DiskReadBytesPerSecond) + "   写 " + FormatRate(snapshot.DiskWriteBytesPerSecond);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
            RefreshFrostedBackground();
            Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            if (_collectorTimer != null)
                _collectorTimer.Dispose();
            if (_collector != null)
                _collector.Dispose();
            if (_frostedBackground != null)
                _frostedBackground.Dispose();
            if (_desktopSnapshot != null)
                _desktopSnapshot.Dispose();
            DisposeFonts();
            if (_applicationIcon != null)
                _applicationIcon.Dispose();
            base.OnFormClosed(e);
        }

        private void DisposeFonts()
        {
            _nameFont.Dispose();
            _captionFont.Dispose();
            _valueFont.Dispose();
            _detailFont.Dispose();
        }

        private void ApplyRoundedRegion()
        {
            using (GraphicsPath path = CreateRoundedPath(new RectangleF(0, 0, Width, Height), 16f))
                Region = new Region(path);
        }

        private static string StartupCommand
        {
            get { return "\"" + Application.ExecutablePath + "\""; }
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
                MessageBox.Show("无法修改开机自启设置。", "系统状态监控",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void CaptureDesktopSnapshot()
        {
            _desktopBounds = SystemInformation.VirtualScreen;
            try
            {
                Bitmap snapshot = new Bitmap(_desktopBounds.Width, _desktopBounds.Height);
                using (Graphics g = Graphics.FromImage(snapshot))
                    g.CopyFromScreen(_desktopBounds.Location, Point.Empty, _desktopBounds.Size, CopyPixelOperation.SourceCopy);
                if (_desktopSnapshot != null)
                    _desktopSnapshot.Dispose();
                _desktopSnapshot = snapshot;
            }
            catch
            {
                if (_desktopSnapshot != null)
                {
                    _desktopSnapshot.Dispose();
                    _desktopSnapshot = null;
                }
            }
        }

        private void RefreshFrostedBackground()
        {
            if (_desktopSnapshot == null)
                return;

            Bitmap source = new Bitmap(Width, Height);
            using (Graphics g = Graphics.FromImage(source))
            {
                g.Clear(Color.FromArgb(215, 232, 243));
                Rectangle windowBounds = new Rectangle(Location, Size);
                Rectangle visible = Rectangle.Intersect(windowBounds, _desktopBounds);
                if (!visible.IsEmpty)
                {
                    Rectangle sourceBounds = new Rectangle(
                        visible.Left - _desktopBounds.Left,
                        visible.Top - _desktopBounds.Top,
                        visible.Width,
                        visible.Height);
                    Rectangle destinationBounds = new Rectangle(
                        visible.Left - windowBounds.Left,
                        visible.Top - windowBounds.Top,
                        visible.Width,
                        visible.Height);
                    g.DrawImage(_desktopSnapshot, destinationBounds, sourceBounds, GraphicsUnit.Pixel);
                }
            }

            const int blurScale = 12;
            int smallWidth = Math.Max(1, Width / blurScale);
            int smallHeight = Math.Max(1, Height / blurScale);
            using (Bitmap small = new Bitmap(smallWidth, smallHeight))
            {
                using (Graphics g = Graphics.FromImage(small))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, new Rectangle(0, 0, smallWidth, smallHeight));
                }

                Bitmap blurred = new Bitmap(Width, Height);
                using (Graphics g = Graphics.FromImage(blurred))
                {
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(small, ClientRectangle);
                }

                if (_frostedBackground != null)
                    _frostedBackground.Dispose();
                _frostedBackground = blurred;
            }
            source.Dispose();
        }

        private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
        {
            float diameter = radius * 2f;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }

        private static void FillRoundedRectangle(Graphics g, Brush brush, RectangleF bounds, float radius)
        {
            using (GraphicsPath path = CreateRoundedPath(bounds, radius))
                g.FillPath(brush, path);
        }

        private static void DrawRoundedRectangle(Graphics g, Pen pen, RectangleF bounds, float radius)
        {
            using (GraphicsPath path = CreateRoundedPath(bounds, radius))
                g.DrawPath(pen, path);
        }

        private static void DrawCenteredString(Graphics g, string text, Font font, Brush brush, RectangleF bounds)
        {
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Center;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, bounds, format);
            }
        }

        private static void DrawRightAlignedString(Graphics g, string text, Font font, Brush brush, RectangleF bounds)
        {
            using (StringFormat format = new StringFormat())
            {
                format.Alignment = StringAlignment.Far;
                format.LineAlignment = StringAlignment.Center;
                g.DrawString(text, font, brush, bounds, format);
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

    internal sealed class GlassContextMenuStrip : ContextMenuStrip
    {
        private readonly Font _menuFont;
        private Bitmap _frostedBackground;

        public GlassContextMenuStrip()
        {
            _menuFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point);
            Font = _menuFont;
            BackColor = Color.FromArgb(246, 246, 246);
            ForeColor = Color.FromArgb(24, 24, 24);
            ShowImageMargin = false;
            ShowCheckMargin = false;
            Padding = new Padding(5);
            MinimumSize = new Size(136, 0);
            DropShadowEnabled = true;
            Renderer = new GlassMenuRenderer();
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        internal Bitmap FrostedBackground
        {
            get { return _frostedBackground; }
        }

        protected override void OnItemAdded(ToolStripItemEventArgs e)
        {
            base.OnItemAdded(e);
            e.Item.AutoSize = false;
            e.Item.Size = e.Item is ToolStripSeparator ? new Size(132, 9) : new Size(132, 32);
            e.Item.Margin = Padding.Empty;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            if (Visible)
            {
                // Capture before the popup is painted, so its own pixels cannot enter the blur.
                CaptureFrostedBackground();
                using (GraphicsPath path = GlassMenuRenderer.RoundedPath(
                    new RectangleF(0, 0, Width, Height), 12f))
                {
                    Region oldRegion = Region;
                    Region = new Region(path);
                    if (oldRegion != null)
                        oldRegion.Dispose();
                }
            }
            base.OnVisibleChanged(e);
        }

        private void CaptureFrostedBackground()
        {
            if (_frostedBackground != null)
            {
                _frostedBackground.Dispose();
                _frostedBackground = null;
            }

            Bitmap blurred = null;
            try
            {
                using (Bitmap source = new Bitmap(Width, Height))
                {
                    using (Graphics g = Graphics.FromImage(source))
                    {
                        g.Clear(BackColor);
                        Rectangle windowBounds = new Rectangle(Location, Size);
                        Rectangle visible = Rectangle.Intersect(windowBounds, SystemInformation.VirtualScreen);
                        if (!visible.IsEmpty)
                            g.CopyFromScreen(visible.Location,
                                new Point(visible.Left - Left, visible.Top - Top), visible.Size,
                                CopyPixelOperation.SourceCopy);
                    }
                    using (Bitmap small = new Bitmap(Math.Max(1, Width / 10), Math.Max(1, Height / 10)))
                    {
                        using (Graphics g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(source, new Rectangle(0, 0, small.Width, small.Height));
                        }
                        blurred = new Bitmap(Width, Height);
                        using (Graphics g = Graphics.FromImage(blurred))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                            g.DrawImage(small, new Rectangle(0, 0, Width, Height));
                        }
                    }
                }
                _frostedBackground = blurred;
            }
            catch
            {
                if (blurred != null)
                    blurred.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_frostedBackground != null)
                {
                    _frostedBackground.Dispose();
                    _frostedBackground = null;
                }
                _menuFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class GlassMenuRenderer : ToolStripRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = e.ToolStrip.ClientRectangle;
            GlassContextMenuStrip menu = (GlassContextMenuStrip)e.ToolStrip;
            if (menu.FrostedBackground != null)
                g.DrawImageUnscaled(menu.FrostedBackground, 0, 0);
            else
                using (Brush background = new SolidBrush(menu.BackColor))
                    g.FillRectangle(background, bounds);

            using (LinearGradientBrush surface = new LinearGradientBrush(bounds,
                Color.FromArgb(232, 251, 251, 251), Color.FromArgb(224, 241, 241, 241),
                LinearGradientMode.Vertical))
                g.FillRectangle(surface, bounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath path = RoundedPath(
                new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1f, e.ToolStrip.Height - 1f), 12f))
            using (Pen edge = new Pen(Color.FromArgb(232, 255, 255, 255)))
                g.DrawPath(edge, path);
            using (GraphicsPath path = RoundedPath(
                new RectangleF(1.5f, 1.5f, e.ToolStrip.Width - 3f, e.ToolStrip.Height - 3f), 11f))
            using (Pen edge = new Pen(Color.FromArgb(72, 112, 112, 112)))
                g.DrawPath(edge, path);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (e.Item.Selected)
            {
                RectangleF bounds = new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2);
                using (GraphicsPath path = RoundedPath(bounds, 6f))
                using (Brush hover = new SolidBrush(Color.FromArgb(188, 225, 225, 225)))
                    g.FillPath(hover, path);
            }

            ToolStripMenuItem menuItem = e.Item as ToolStripMenuItem;
            if (menuItem != null && menuItem.Name == "startup" && menuItem.Checked)
                DrawCheckMark(g, e.Item.Width - 20, e.Item.Height / 2);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, e.Text, e.TextFont,
                new Rectangle(12, 0, e.Item.Width - 38, e.Item.Height), Color.FromArgb(24, 24, 24),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (Pen line = new Pen(Color.FromArgb(130, 150, 150, 150)))
                e.Graphics.DrawLine(line, 10, y, e.Item.Width - 10, y);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e) { }

        private static void DrawCheckMark(Graphics g, int centerX, int centerY)
        {
            using (Pen pen = new Pen(Color.FromArgb(0, 95, 184), 1.8f))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] {
                    new Point(centerX - 4, centerY),
                    new Point(centerX - 1, centerY + 3),
                    new Point(centerX + 5, centerY - 4) });
            }
        }

        internal static GraphicsPath RoundedPath(RectangleF bounds, float radius)
        {
            float diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    internal sealed class MetricView
    {
        public readonly string Name;
        public readonly string Caption;
        public readonly Color Accent;
        public double? Percent;
        public string Detail = "--";

        public MetricView(string name, string caption, Color accent)
        {
            Name = name;
            Caption = caption;
            Accent = accent;
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

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

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

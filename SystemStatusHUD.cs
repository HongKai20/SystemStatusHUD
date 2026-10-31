using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("System Status HUD")]
[assembly: System.Reflection.AssemblyDescription("Windows hardware status overlay")]
[assembly: System.Reflection.AssemblyProduct("System Status HUD")]
[assembly: System.Reflection.AssemblyVersion("2.0.0.0")]

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
        private const int WsExLayered = 0x00080000;
        private const int BackgroundRefreshInterval = 33;
        private const int BackgroundPadding = 12;
        private const float WindowCornerRadius = 20f;
        private readonly Font _nameFont = new Font("Microsoft YaHei UI", 9.5f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _captionFont = new Font("Microsoft YaHei UI", 7.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Font _valueFont = new Font("Consolas", 12.5f, FontStyle.Bold, GraphicsUnit.Point);
        private readonly Font _detailFont = new Font("Consolas", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _trayMenu;
        private readonly ToolStripMenuItem _startupMenuItem;
        private Icon _applicationIcon;
        private System.Threading.Timer _collectorTimer;
        private System.Windows.Forms.Timer _backgroundTimer;
        private MetricCollector _collector;
        private int _updateInProgress;
        private bool _refreshingBackground;
        private bool _dragging;
        private bool _displayAffinityHeld;
        private Point _dragOffset;
        private Bitmap _captureSurface;
        private Bitmap _blurSurface;
        private Bitmap _frostedBackground;
        private Bitmap _glassOverlay;
        private Bitmap _metricOverlay;
        private Bitmap _frameBitmap;
        private byte[] _framePixels;
        private byte[] _cornerMask;

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

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                parameters.ExStyle |= WsExLayered;
                return parameters;
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width - 14, area.Top + 14);
            InitializeRenderingSurfaces();
            base.OnLoad(e);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _displayAffinityHeld = NativeMethods.SetWindowDisplayAffinity(Handle, 0x11);
            if (_displayAffinityHeld)
                NativeMethods.DwmFlush();
            RefreshFrostedBackground();
            RenderLayeredWindow();
            ReleaseDisplayAffinity();
            _backgroundTimer = new System.Windows.Forms.Timer();
            _backgroundTimer.Interval = BackgroundRefreshInterval;
            _backgroundTimer.Tick += delegate
            {
                if (_dragging)
                    RefreshRealtimeFrame();
            };
            _backgroundTimer.Start();
            _collectorTimer = new System.Threading.Timer(UpdateMetrics, null, 0, 1000);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // UpdateLayeredWindow presents the cached surface instead of WM_PAINT.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
        }

        private static void ConfigureLiquidGraphics(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.CompositingMode = CompositingMode.SourceOver;
            g.CompositingQuality = CompositingQuality.GammaCorrected;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        }

        private void DrawLiquidBackground(Graphics g)
        {
            ConfigureLiquidGraphics(g);
            RectangleF shell = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
            GraphicsState state = g.Save();
            using (GraphicsPath clip = CreateRoundedPath(shell, WindowCornerRadius))
                g.SetClip(clip);

            using (LinearGradientBrush veil = new LinearGradientBrush(
                ClientRectangle, Color.FromArgb(54, 248, 252, 254), Color.FromArgb(30, 185, 198, 207), LinearGradientMode.Vertical))
                g.FillRectangle(veil, ClientRectangle);

            using (LinearGradientBrush crown = new LinearGradientBrush(
                new RectangleF(0, 0, Width, 74), Color.FromArgb(70, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                g.FillRectangle(crown, 0, 0, Width, 74);

            using (LinearGradientBrush leftRefraction = new LinearGradientBrush(
                new RectangleF(0, 0, 46, Height), Color.FromArgb(38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Horizontal))
                g.FillRectangle(leftRefraction, 0, 0, 46, Height);

            using (LinearGradientBrush lowerShade = new LinearGradientBrush(
                new RectangleF(0, Height - 72, Width, 72), Color.FromArgb(0, 38, 59, 72), Color.FromArgb(34, 28, 47, 60), LinearGradientMode.Vertical))
                g.FillRectangle(lowerShade, 0, Height - 72, Width, 72);

            using (Brush coolGlow = new SolidBrush(Color.FromArgb(9, 91, 183, 255)))
                g.FillEllipse(coolGlow, -62, -78, 242, 168);
            using (Brush spectralGlow = new SolidBrush(Color.FromArgb(7, 185, 126, 255)))
                g.FillEllipse(spectralGlow, Width - 130, -58, 186, 142);
            g.Restore(state);
        }

        private void DrawLiquidContent(Graphics g)
        {
            ConfigureLiquidGraphics(g);
            RectangleF outer = new RectangleF(0.75f, 0.75f, Width - 1.5f, Height - 1.5f);
            using (LinearGradientBrush rimBrush = new LinearGradientBrush(
                outer, Color.FromArgb(242, 255, 255, 255), Color.FromArgb(92, 66, 88, 103), 112f))
            {
                ColorBlend rimBlend = new ColorBlend();
                rimBlend.Colors = new Color[]
                {
                    Color.FromArgb(242, 255, 255, 255),
                    Color.FromArgb(176, 222, 244, 255),
                    Color.FromArgb(118, 216, 204, 255),
                    Color.FromArgb(78, 58, 78, 91)
                };
                rimBlend.Positions = new float[] { 0f, 0.32f, 0.68f, 1f };
                rimBrush.InterpolationColors = rimBlend;
                using (Pen rim = new Pen(rimBrush, 1.35f))
                    DrawRoundedRectangle(g, rim, outer, WindowCornerRadius);
            }

            RectangleF inner = new RectangleF(2.1f, 2.1f, Width - 4.2f, Height - 4.2f);
            using (LinearGradientBrush innerRimBrush = new LinearGradientBrush(
                inner, Color.FromArgb(98, 255, 255, 255), Color.FromArgb(54, 25, 45, 58), LinearGradientMode.Vertical))
            using (Pen innerRim = new Pen(innerRimBrush, 0.9f))
                DrawRoundedRectangle(g, innerRim, inner, WindowCornerRadius - 1.4f);

            DrawMetricGlass(g, _cpu, new Rectangle(12, 12, 332, 56));
            DrawMetricGlass(g, _gpu, new Rectangle(12, 74, 332, 56));
            DrawMetricGlass(g, _memory, new Rectangle(12, 136, 332, 56));
            DrawMetricGlass(g, _disk, new Rectangle(12, 198, 332, 56));
        }

        private void InitializeRenderingSurfaces()
        {
            _captureSurface = new Bitmap(Width + BackgroundPadding * 2, Height + BackgroundPadding * 2, PixelFormat.Format32bppRgb);
            _blurSurface = new Bitmap(Math.Max(1, _captureSurface.Width / 5), Math.Max(1, _captureSurface.Height / 5), PixelFormat.Format32bppRgb);
            _frostedBackground = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(_frostedBackground))
                g.Clear(Color.FromArgb(215, 226, 233));
            _frameBitmap = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
            _framePixels = new byte[Width * Height * 4];
            _cornerMask = new byte[Width * Height];
            _glassOverlay = CreateLiquidLayer(delegate(Graphics g)
            {
                DrawLiquidBackground(g);
                DrawLiquidContent(g);
            });
            RefreshMetricOverlay();

            using (Bitmap mask = CreateLiquidLayer(delegate(Graphics g)
            {
                using (Brush fill = new SolidBrush(Color.White))
                    FillRoundedRectangle(g, fill, new RectangleF(0, 0, Width, Height), WindowCornerRadius);
            }))
            {
                BitmapData data = mask.LockBits(ClientRectangle, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    Marshal.Copy(data.Scan0, _framePixels, 0, _framePixels.Length);
                    for (int pixel = 0; pixel < _cornerMask.Length; pixel++)
                        _cornerMask[pixel] = _framePixels[pixel * 4 + 3];
                }
                finally
                {
                    mask.UnlockBits(data);
                }
            }
        }

        private Bitmap CreateLiquidLayer(Action<Graphics> draw)
        {
            const int renderScale = 3;
            using (Bitmap highResolution = new Bitmap(Width * renderScale, Height * renderScale, PixelFormat.Format32bppPArgb))
            {
                using (Graphics g = Graphics.FromImage(highResolution))
                {
                    g.Clear(Color.Transparent);
                    ConfigureLiquidGraphics(g);
                    g.ScaleTransform(renderScale, renderScale);
                    draw(g);
                }

                Bitmap layer = new Bitmap(Width, Height, PixelFormat.Format32bppPArgb);
                using (Graphics g = Graphics.FromImage(layer))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.CompositingQuality = CompositingQuality.GammaCorrected;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(highResolution, ClientRectangle,
                        0, 0, highResolution.Width, highResolution.Height, GraphicsUnit.Pixel);
                }
                return layer;
            }
        }

        private void RefreshMetricOverlay()
        {
            Bitmap layer = CreateLiquidLayer(delegate(Graphics g)
            {
                DrawMetricValues(g, _cpu, new Rectangle(12, 12, 332, 56));
                DrawMetricValues(g, _gpu, new Rectangle(12, 74, 332, 56));
                DrawMetricValues(g, _memory, new Rectangle(12, 136, 332, 56));
                DrawMetricValues(g, _disk, new Rectangle(12, 198, 332, 56));
            });
            if (_metricOverlay != null)
                _metricOverlay.Dispose();
            _metricOverlay = layer;
        }

        private void RefreshRealtimeFrame()
        {
            if (_refreshingBackground || _frameBitmap == null || IsDisposed || !IsHandleCreated || !Visible)
                return;

            _refreshingBackground = true;
            try
            {
                RefreshFrostedBackground();
                RenderLayeredWindow();
            }
            finally
            {
                _refreshingBackground = false;
            }
        }

        private void RenderLayeredWindow()
        {
            if (!IsHandleCreated || IsDisposed || _frameBitmap == null)
                return;

            using (Graphics g = Graphics.FromImage(_frameBitmap))
            {
                g.CompositingQuality = CompositingQuality.HighSpeed;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(_frostedBackground, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImageUnscaled(_glassOverlay, 0, 0);
                g.DrawImageUnscaled(_metricOverlay, 0, 0);
            }

            BitmapData data = _frameBitmap.LockBits(ClientRectangle, ImageLockMode.ReadWrite, PixelFormat.Format32bppPArgb);
            try
            {
                Marshal.Copy(data.Scan0, _framePixels, 0, _framePixels.Length);
                for (int pixel = 0; pixel < _cornerMask.Length; pixel++)
                {
                    int alpha = _cornerMask[pixel];
                    if (alpha == 255)
                        continue;
                    int offset = pixel * 4;
                    for (int channel = 0; channel < 4; channel++)
                        _framePixels[offset + channel] = (byte)((_framePixels[offset + channel] * alpha + 127) / 255);
                }
                Marshal.Copy(_framePixels, 0, data.Scan0, _framePixels.Length);
            }
            finally
            {
                _frameBitmap.UnlockBits(data);
            }
            UpdateLayeredBitmap(_frameBitmap);
        }

        private void UpdateLayeredBitmap(Bitmap bitmap)
        {
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            IntPtr memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
            IntPtr bitmapHandle = IntPtr.Zero;
            IntPtr oldBitmap = IntPtr.Zero;
            try
            {
                bitmapHandle = bitmap.GetHbitmap(Color.FromArgb(0));
                oldBitmap = NativeMethods.SelectObject(memoryDc, bitmapHandle);
                NativeMethods.NativePoint source = new NativeMethods.NativePoint(0, 0);
                NativeMethods.NativePoint destination = new NativeMethods.NativePoint(Left, Top);
                NativeMethods.NativeSize size = new NativeMethods.NativeSize(Width, Height);
                NativeMethods.BlendFunction blend = new NativeMethods.BlendFunction();
                blend.BlendOp = 0;
                blend.SourceConstantAlpha = 255;
                blend.AlphaFormat = 1;
                NativeMethods.UpdateLayeredWindow(
                    Handle, screenDc, ref destination, ref size, memoryDc,
                    ref source, 0, ref blend, 2);
            }
            finally
            {
                if (oldBitmap != IntPtr.Zero)
                    NativeMethods.SelectObject(memoryDc, oldBitmap);
                if (bitmapHandle != IntPtr.Zero)
                    NativeMethods.DeleteObject(bitmapHandle);
                NativeMethods.DeleteDC(memoryDc);
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        private void DrawMetricGlass(Graphics g, MetricView metric, Rectangle bounds)
        {
            RectangleF shadowBounds = new RectangleF(bounds.Left + 1, bounds.Top + 3, bounds.Width - 2, bounds.Height - 1);
            using (Brush shadow = new SolidBrush(Color.FromArgb(14, 28, 43, 52)))
                FillRoundedRectangle(g, shadow, shadowBounds, 11f);

            using (LinearGradientBrush glass = new LinearGradientBrush(
                bounds, Color.FromArgb(62, 255, 255, 255), Color.FromArgb(24, 205, 215, 222), LinearGradientMode.Vertical))
                FillRoundedRectangle(g, glass, bounds, 11f);

            GraphicsState state = g.Save();
            using (GraphicsPath clip = CreateRoundedPath(bounds, 11f))
            {
                g.SetClip(clip);
                RectangleF sheenBounds = new RectangleF(bounds.Left, bounds.Top, bounds.Width, 27);
                using (LinearGradientBrush sheen = new LinearGradientBrush(
                    sheenBounds, Color.FromArgb(38, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
                    g.FillRectangle(sheen, sheenBounds);
                using (Brush lowerGlow = new SolidBrush(Color.FromArgb(10, metric.Accent)))
                    g.FillEllipse(lowerGlow, bounds.Right - 104, bounds.Bottom - 36, 132, 62);
            }
            g.Restore(state);

            using (Pen border = new Pen(Color.FromArgb(118, 255, 255, 255), 1f))
                DrawRoundedRectangle(g, border, bounds, 11f);
            using (Pen innerBorder = new Pen(Color.FromArgb(30, 48, 62, 70), 1f))
                DrawRoundedRectangle(g, innerBorder,
                    new RectangleF(bounds.Left + 1, bounds.Top + 1, bounds.Width - 2, bounds.Height - 2), 10f);

            RectangleF accentPill = new RectangleF(bounds.Left + 9, bounds.Top + 11, 3, bounds.Height - 22);
            using (LinearGradientBrush accentBrush = new LinearGradientBrush(
                accentPill, Color.FromArgb(235, metric.Accent), Color.FromArgb(112, metric.Accent), LinearGradientMode.Vertical))
                FillRoundedRectangle(g, accentBrush, accentPill, 1.5f);
        }

        private void DrawMetricValues(Graphics g, MetricView metric, Rectangle bounds)
        {
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
            if (IsDisposed || Disposing)
                return;
            _cpu.Percent = snapshot.CpuLoad;
            _cpu.Detail = "功耗  " + FormatPower(snapshot.CpuPowerWatts);
            _gpu.Percent = snapshot.GpuLoad;
            _gpu.Detail = "功耗  " + FormatPower(snapshot.GpuPowerWatts);
            _memory.Percent = snapshot.MemoryLoad;
            _memory.Detail = FormatBytes(snapshot.MemoryUsedBytes) + " / " + FormatBytes(snapshot.MemoryTotalBytes);
            _disk.Percent = snapshot.DiskActive;
            _disk.Detail = "读 " + FormatRate(snapshot.DiskReadBytesPerSecond) + "   写 " + FormatRate(snapshot.DiskWriteBytesPerSecond);
            RefreshMetricOverlay();
            RenderLayeredWindow();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
                return;
            _dragOffset = e.Location;
            _displayAffinityHeld = NativeMethods.SetWindowDisplayAffinity(Handle, 0x11);
            if (_displayAffinityHeld)
                NativeMethods.DwmFlush();
            _dragging = true;
            Capture = true;
            RefreshRealtimeFrame();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!_dragging)
                return;
            Point pointer = Cursor.Position;
            Location = new Point(pointer.X - _dragOffset.X, pointer.Y - _dragOffset.Y);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !_dragging)
                return;
            _dragging = false;
            RefreshRealtimeFrame();
            Capture = false;
            ReleaseDisplayAffinity();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _dragging)
            {
                _dragging = false;
                RefreshRealtimeFrame();
                ReleaseDisplayAffinity();
            }
        }

        private void ReleaseDisplayAffinity()
        {
            if (!_displayAffinityHeld)
                return;
            NativeMethods.SetWindowDisplayAffinity(Handle, 0);
            _displayAffinityHeld = false;
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (_dragging)
                RefreshRealtimeFrame();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            ReleaseDisplayAffinity();
            if (_backgroundTimer != null)
            {
                _backgroundTimer.Stop();
                _backgroundTimer.Dispose();
            }
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _trayMenu.Dispose();
            if (_collectorTimer != null)
                _collectorTimer.Dispose();
            if (_collector != null)
                _collector.Dispose();
            if (_captureSurface != null)
                _captureSurface.Dispose();
            if (_blurSurface != null)
                _blurSurface.Dispose();
            if (_frostedBackground != null)
                _frostedBackground.Dispose();
            if (_glassOverlay != null)
                _glassOverlay.Dispose();
            if (_metricOverlay != null)
                _metricOverlay.Dispose();
            if (_frameBitmap != null)
                _frameBitmap.Dispose();
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

        private void RefreshFrostedBackground()
        {
            Rectangle captureBounds = new Rectangle(Left - BackgroundPadding, Top - BackgroundPadding, _captureSurface.Width, _captureSurface.Height);
            Rectangle visible = Rectangle.Intersect(captureBounds, SystemInformation.VirtualScreen);

            // The initial frame is captured while hidden; visible frames are captured only
            // while drag-time exclusion remains stable, so the HUD cannot sample itself.
            if (Visible && !_displayAffinityHeld)
                return;
            try
            {
                using (Graphics g = Graphics.FromImage(_captureSurface))
                {
                    g.Clear(Color.FromArgb(215, 226, 233));
                    if (!visible.IsEmpty)
                        g.CopyFromScreen(visible.Location,
                            new Point(visible.Left - captureBounds.Left, visible.Top - captureBounds.Top),
                            visible.Size, CopyPixelOperation.SourceCopy);
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return;
            }

            using (Graphics g = Graphics.FromImage(_blurSurface))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(_captureSurface, new Rectangle(0, 0, _blurSurface.Width, _blurSurface.Height),
                    0, 0, _captureSurface.Width, _captureSurface.Height, GraphicsUnit.Pixel);
            }

            using (Graphics g = Graphics.FromImage(_frostedBackground))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                RectangleF sourceBounds = new RectangleF(
                    (float)BackgroundPadding * _blurSurface.Width / _captureSurface.Width,
                    (float)BackgroundPadding * _blurSurface.Height / _captureSurface.Height,
                    (float)Width * _blurSurface.Width / _captureSurface.Width,
                    (float)Height * _blurSurface.Height / _captureSurface.Height);
                g.DrawImage(_blurSurface, ClientRectangle, sourceBounds, GraphicsUnit.Pixel);
            }
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

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateLayeredWindow(
            IntPtr hWnd, IntPtr destinationDc, ref NativePoint destination,
            ref NativeSize size, IntPtr sourceDc, ref NativePoint source,
            int colorKey, ref BlendFunction blend, int flags);

        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDc);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteDC(IntPtr hDc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDc, IntPtr handle);

        [DllImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool DeleteObject(IntPtr handle);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativePoint
        {
            public int X;
            public int Y;

            public NativePoint(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeSize
        {
            public int Width;
            public int Height;

            public NativeSize(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BlendFunction
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

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

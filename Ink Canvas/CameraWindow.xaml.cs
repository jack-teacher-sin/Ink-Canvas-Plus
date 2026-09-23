using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AForge.Video;
using AForge.Video.DirectShow;
using InkCanvasPlus.Helpers;

namespace InkCanvasPlus
{
    /// <summary>
    /// 展台（USB 免驱摄像头）预览窗口。
    /// 抓拍当前画面并交给主窗口贴到画布上；窗口保持打开以便连续抓拍。
    /// </summary>
    public partial class CameraWindow : Window
    {
        /// <summary>抓拍完成。参数是已 Freeze 的位图，可跨线程安全持有。</summary>
        public event Action<BitmapSource> SnapshotCaptured;

        /// <summary>启动后多少毫秒还没收到画面就提示用户（驱动协商分辨率失败时会出现这种情况）。</summary>
        private const int FirstFrameWarnMilliseconds = 3000;

        private FilterInfoCollection _videoDevices;
        private VideoCaptureDevice _videoSource;
        private WriteableBitmap _previewBitmap;
        /// <summary>最近一帧，已 Freeze。抓拍直接用它，因此照片与所见画面完全一致。</summary>
        private BitmapSource _latestFrame;
        /// <summary>0/1。保证同时只有一帧在 UI 线程排队，避免 Dispatcher 积压撑爆 32 位进程。</summary>
        private int _framePending;
        private volatile bool _isFrozen;
        private bool _suppressDeviceChanged;
        private bool _suppressResolutionChanged;
        private bool _isFullScreen;
        private WindowStyle _styleBeforeFullScreen;
        private WindowState _stateBeforeFullScreen;
        private ResizeMode _resizeBeforeFullScreen;
        private Rect _boundsBeforeFullScreen;
        private DispatcherTimer _firstFrameTimer;
        private bool _gotFirstFrame;

        public CameraWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Settings.Camera 可能为 null（旧配置文件里显式写了 "camera": null），这里兜一下。
        /// </summary>
        private static Camera CameraSettings
        {
            get
            {
                var settings = MainWindow.Settings;
                if (settings.Camera == null)
                {
                    settings.Camera = new Camera();
                }
                return settings.Camera;
            }
        }

        #region 设备枚举

        /// <summary>
        /// 重新枚举视频输入设备。这是 COM 调用且必须在 UI 线程（STA）上执行，
        /// 设备被其他程序独占时可能抛异常或卡顿，所以始终包在 try/catch 里。
        /// </summary>
        private void RefreshDeviceList()
        {
            string previousMoniker = null;
            if (ComboBoxDevice.SelectedItem is FilterInfo previous)
            {
                previousMoniker = previous.MonikerString;
            }

            try
            {
                _videoDevices = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            }
            catch (Exception ex)
            {
                LogHelper.NewLog(ex);
                SetStatus("枚举展台设备失败：" + ex.Message);
                return;
            }

            _suppressDeviceChanged = true;
            try
            {
                ComboBoxDevice.Items.Clear();
                ComboBoxDevice.DisplayMemberPath = "Name";
                // FilterInfoCollection 只实现了非泛型 IEnumerable，必须显式写元素类型
                foreach (FilterInfo info in _videoDevices)
                {
                    ComboBoxDevice.Items.Add(info);
                }

                if (ComboBoxDevice.Items.Count == 0)
                {
                    TextBlockPreviewHint.Text = "未检测到免驱展台或摄像头。\r\n请插好设备后点「刷新」。";
                    TextBlockPreviewHint.Visibility = Visibility.Visible;
                    SetStatus("未检测到展台设备");
                    return;
                }

                ComboBoxDevice.SelectedItem = PickDeviceToRestore(previousMoniker);
            }
            finally
            {
                _suppressDeviceChanged = false;
            }

            // 上面的赋值被抑制了，这里显式开一次流
            OpenSelectedDevice();
        }

        /// <summary>
        /// 选择要打开的设备：优先用本次会话已选中的，其次按上次记住的名字，
        /// 再次按上次记住的 moniker，最后退回第一个。
        /// </summary>
        /// <remarks>
        /// 名字优先于 moniker：moniker 里含设备路径 GUID，换个 USB 口就会变，
        /// 而名字在换口后通常不变。两台同型号设备时名字会重，此时 moniker 才更准，
        /// 所以两个都记，按这个顺序尝试。
        /// </remarks>
        private FilterInfo PickDeviceToRestore(string previousMoniker)
        {
            var saved = CameraSettings;

            if (!string.IsNullOrEmpty(previousMoniker))
            {
                foreach (FilterInfo info in ComboBoxDevice.Items)
                {
                    if (info.MonikerString == previousMoniker) return info;
                }
            }

            if (!string.IsNullOrEmpty(saved.DeviceName))
            {
                foreach (FilterInfo info in ComboBoxDevice.Items)
                {
                    if (info.Name == saved.DeviceName) return info;
                }
            }

            if (!string.IsNullOrEmpty(saved.DeviceMoniker))
            {
                foreach (FilterInfo info in ComboBoxDevice.Items)
                {
                    if (info.MonikerString == saved.DeviceMoniker) return info;
                }
            }

            return (FilterInfo)ComboBoxDevice.Items[0];
        }

        private void ComboBoxDevice_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDeviceChanged) return;
            if (!(ComboBoxDevice.SelectedItem is FilterInfo info)) return;

            var saved = CameraSettings;
            saved.DeviceName = info.Name;
            saved.DeviceMoniker = info.MonikerString;
            MainWindow.SaveSettingsToFile();

            OpenSelectedDevice();
        }

        private void ButtonRefresh_Click(object sender, RoutedEventArgs e)
        {
            RefreshDeviceList();
        }

        #endregion

        #region 打开 / 关闭采集

        private void OpenSelectedDevice()
        {
            if (!(ComboBoxDevice.SelectedItem is FilterInfo info))
            {
                return;
            }

            StopStream();
            _latestFrame = null;
            _previewBitmap = null;

            try
            {
                // VideoCapabilities 在构造之后、Start() 之前就可以读（已实测确认），
                // 所以分辨率下拉框不需要"先开流再列模式"那套探测流程。
                _videoSource = new VideoCaptureDevice(info.MonikerString);
                FillResolutionList(_videoSource.VideoCapabilities);

                // 分辨率必须在 Start() 之前赋值才生效
                var wanted = FindSavedResolution();
                if (wanted != null)
                {
                    _videoSource.VideoResolution = wanted;
                }

                _videoSource.NewFrame += VideoSource_NewFrame;
                _videoSource.VideoSourceError += VideoSource_Error;
                _videoSource.PlayingFinished += VideoSource_PlayingFinished;
                _gotFirstFrame = false;
                _videoSource.Start();

                TextBlockPreviewHint.Text = "正在连接展台…";
                TextBlockPreviewHint.Visibility = Visibility.Visible;
                SetStatus("正在连接 " + info.Name + "…");
                StartFirstFrameWatchdog();
            }
            catch (Exception ex)
            {
                LogHelper.NewLog(ex);
                StopStream();
                TextBlockPreviewHint.Text = "展台打开失败。\r\n设备可能被其他程序（相机、会议软件）占用。";
                TextBlockPreviewHint.Visibility = Visibility.Visible;
                SetStatus("展台打开失败：" + ex.Message);
            }
        }

        /// <summary>按启动时记住的尺寸挑一个能力项；记住的尺寸已不存在时返回 null（用设备默认）。</summary>
        private VideoCapabilities FindSavedResolution()
        {
            if (_videoSource == null) return null;
            var caps = _videoSource.VideoCapabilities;
            if (caps == null || caps.Length == 0) return null;

            var saved = CameraSettings.VideoResolution;
            if (string.IsNullOrEmpty(saved)) return null;

            var parts = saved.Split('x');
            if (parts.Length != 2) return null;
            if (!int.TryParse(parts[0], out int w) || !int.TryParse(parts[1], out int h)) return null;

            VideoCapabilities best = null;
            foreach (var cap in caps)
            {
                if (cap.FrameSize.Width != w || cap.FrameSize.Height != h) continue;
                if (best == null || cap.AverageFrameRate > best.AverageFrameRate) best = cap;
            }
            return best;
        }

        /// <summary>
        /// 填充分辨率下拉框。同一尺寸只保留帧率最高的一项——展台常把同一尺寸
        /// 同时以 YUY2（低帧率）和 MJPEG（高帧率）两种子类型暴露，取高的那项。
        /// </summary>
        private void FillResolutionList(VideoCapabilities[] capabilities)
        {
            _suppressResolutionChanged = true;
            try
            {
                ComboBoxResolution.Items.Clear();
                if (capabilities == null || capabilities.Length == 0)
                {
                    ComboBoxResolution.IsEnabled = false;
                    return;
                }

                var modes = capabilities
                    .GroupBy(c => new { c.FrameSize.Width, c.FrameSize.Height })
                    .Select(g => g.OrderByDescending(c => c.AverageFrameRate).First())
                    .OrderByDescending(c => (long)c.FrameSize.Width * c.FrameSize.Height)
                    .ToList();

                ComboBoxResolution.IsEnabled = modes.Count > 1;

                var saved = CameraSettings.VideoResolution;
                int index = 0;
                for (int i = 0; i < modes.Count; i++)
                {
                    var option = new ResolutionOption(modes[i]);
                    ComboBoxResolution.Items.Add(option);
                    if (option.ToSettingValue() == saved) index = i;
                }
                ComboBoxResolution.SelectedIndex = index;
            }
            finally
            {
                _suppressResolutionChanged = false;
            }
        }

        private void ComboBoxResolution_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressResolutionChanged) return;
            if (!(ComboBoxResolution.SelectedItem is ResolutionOption option)) return;

            CameraSettings.VideoResolution = option.ToSettingValue();
            MainWindow.SaveSettingsToFile();

            // VideoResolution 只在 Start() 之前生效，所以换分辨率必须重开一次流
            ReopenWithResolution(option);
        }

        private void ReopenWithResolution(ResolutionOption option)
        {
            if (!(ComboBoxDevice.SelectedItem is FilterInfo info)) return;

            StopStream();
            _latestFrame = null;
            _previewBitmap = null;
            ImagePreview.Source = null;

            try
            {
                _videoSource = new VideoCaptureDevice(info.MonikerString);
                _videoSource.VideoResolution = option.Capability;
                _videoSource.NewFrame += VideoSource_NewFrame;
                _videoSource.VideoSourceError += VideoSource_Error;
                _videoSource.PlayingFinished += VideoSource_PlayingFinished;
                _gotFirstFrame = false;
                _videoSource.Start();

                TextBlockPreviewHint.Text = "正在切换分辨率…";
                TextBlockPreviewHint.Visibility = Visibility.Visible;
                SetStatus("正在切换到 " + option.ToSettingValue() + "…");
                StartFirstFrameWatchdog();
            }
            catch (Exception ex)
            {
                LogHelper.NewLog(ex);
                StopStream();
                SetStatus("切换分辨率失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 停止采集并释放设备。先摘事件再停流：否则关窗过程中回调还会往已销毁的 UI 上推帧。
        /// </summary>
        private void StopStream()
        {
            StopFirstFrameWatchdog();

            var source = _videoSource;
            _videoSource = null;
            if (source == null) return;

            try
            {
                source.NewFrame -= VideoSource_NewFrame;
                source.VideoSourceError -= VideoSource_Error;
                source.PlayingFinished -= VideoSource_PlayingFinished;
                if (source.IsRunning)
                {
                    source.SignalToStop();
                    source.WaitForStop();
                }
            }
            catch (Exception ex)
            {
                LogHelper.NewLog(ex);
            }
        }

        #endregion

        #region 取帧

        private void VideoSource_NewFrame(object sender, NewFrameEventArgs e)
        {
            if (_isFrozen) return;
            // 上一帧还没渲染完就丢掉这一帧。绝不能排队——Dispatcher 积压是 32 位 WPF 最典型的 OOM 原因。
            if (Interlocked.Exchange(ref _framePending, 1) == 1) return;

            if (e.Frame == null)
            {
                Interlocked.Exchange(ref _framePending, 0);
                return;
            }

            BitmapSource frame;
            try
            {
                // AForge 复用同一个 Bitmap 缓冲，事件返回后内容即失效，
                // 所以必须在这里把像素拷出来，既不能保存引用也不能 Dispose 它。
                frame = CreateFrozenBitmap(e.Frame);
            }
            catch (Exception ex)
            {
                LogHelper.NewLog(ex);
                Interlocked.Exchange(ref _framePending, 0);
                return;
            }

            // NewFrame 在采集线程上触发，一律用非阻塞的 InvokeAsync 回 UI 线程（不用 Invoke，避免死锁）
            _ = Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    if (_isFrozen) return;
                    _gotFirstFrame = true;
                    _latestFrame = frame;
                    RenderFrame(frame);
                }
                catch (Exception ex)
                {
                    LogHelper.NewLog(ex);
                }
                finally
                {
                    Interlocked.Exchange(ref _framePending, 0);
                }
            }, DispatcherPriority.Render);
        }

        /// <summary>
        /// 把一张 System.Drawing.Bitmap 拷成已 Freeze 的 WPF 位图。调用后与源位图再无关系。
        /// </summary>
        private static BitmapSource CreateFrozenBitmap(System.Drawing.Bitmap bitmap)
        {
            var rect = new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height);

            // 优先按位图自身的像素格式拷（AForge 出来的就是 24bppRgb，等于零转换）；
            // 只有 WPF 没有对应格式时才让 GDI+ 转成 32bppArgb。
            var nativeFormat = bitmap.PixelFormat;
            var mapped = ToWpfPixelFormat(nativeFormat);
            var lockFormat = mapped.HasValue
                ? nativeFormat
                : System.Drawing.Imaging.PixelFormat.Format32bppArgb;
            var wpfFormat = mapped ?? PixelFormats.Bgra32;

            var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, lockFormat);
            try
            {
                var source = BitmapSource.Create(
                    bitmap.Width, bitmap.Height, 96, 96, wpfFormat, null,
                    data.Scan0, data.Stride * bitmap.Height, data.Stride);
                source.Freeze(); // 冻结后没有 Dispatcher 亲和性，可以放心跨线程传递和长期持有
                return source;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        private static PixelFormat? ToWpfPixelFormat(System.Drawing.Imaging.PixelFormat format)
        {
            switch (format)
            {
                case System.Drawing.Imaging.PixelFormat.Format24bppRgb: return PixelFormats.Bgr24;
                case System.Drawing.Imaging.PixelFormat.Format32bppRgb: return PixelFormats.Bgr32;
                case System.Drawing.Imaging.PixelFormat.Format32bppArgb: return PixelFormats.Bgra32;
                case System.Drawing.Imaging.PixelFormat.Format32bppPArgb: return PixelFormats.Pbgra32;
                case System.Drawing.Imaging.PixelFormat.Format16bppRgb565: return PixelFormats.Bgr565;
                case System.Drawing.Imaging.PixelFormat.Format48bppRgb: return PixelFormats.Rgb48;
                default: return null;
            }
        }

        private void RenderFrame(BitmapSource frame)
        {
            var width = frame.PixelWidth;
            var height = frame.PixelHeight;

            // 驱动可能自行换模式，尺寸变了就重建目标位图
            if (_previewBitmap == null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
            {
                _previewBitmap = new WriteableBitmap(width, height, 96, 96, frame.Format, null);
                ImagePreview.Source = _previewBitmap;
                TextBlockPreviewHint.Visibility = Visibility.Collapsed;
            }

            _previewBitmap.Lock();
            try
            {
                frame.CopyPixels(new Int32Rect(0, 0, width, height),
                                 _previewBitmap.BackBuffer,
                                 _previewBitmap.BackBufferStride * height,
                                 _previewBitmap.BackBufferStride);
                _previewBitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
            }
            finally
            {
                _previewBitmap.Unlock();
            }

            SetStatus(width + " x " + height + " · " + (_isFrozen ? "已冻结" : "实时"));
        }

        /// <summary>
        /// 开流后迟迟没有画面时给个提示。某些展台在选定分辨率下协商不出解码器，
        /// 会"打开成功但永远不出图"，只能靠这个看出来。
        /// </summary>
        private void StartFirstFrameWatchdog()
        {
            StopFirstFrameWatchdog();
            _firstFrameTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(FirstFrameWarnMilliseconds) };
            _firstFrameTimer.Tick += (s, e) =>
            {
                StopFirstFrameWatchdog();
                if (_gotFirstFrame) return;
                TextBlockPreviewHint.Text = "已打开设备但迟迟没有画面。\r\n请换一个分辨率，或用「设备设置」调整驱动选项。";
                TextBlockPreviewHint.Visibility = Visibility.Visible;
                SetStatus("该分辨率下未收到画面，建议换一个分辨率");
            };
            _firstFrameTimer.Start();
        }

        private void StopFirstFrameWatchdog()
        {
            if (_firstFrameTimer == null) return;
            _firstFrameTimer.Stop();
            _firstFrameTimer = null;
        }

        #endregion

        #region 错误与断开

        private void VideoSource_Error(object sender, VideoSourceErrorEventArgs e)
        {
            LogHelper.NewLog("展台采集出错：" + e.Description);
            _ = Dispatcher.InvokeAsync(() => HandleDeviceLost("展台采集出错：" + e.Description));
        }

        private void VideoSource_PlayingFinished(object sender, ReasonToFinishPlaying reason)
        {
            // 正常关闭时我们自己也停了流，此时不需要报错给用户
            if (_videoSource == null) return;
            _ = Dispatcher.InvokeAsync(() => HandleDeviceLost("展台已断开（" + reason + "）"));
        }

        /// <summary>
        /// 设备拔出或驱动崩了：停流、提示、重新枚举设备列表。
        /// 不做这个处理的话拔线后只是静默黑屏，用户完全不知道发生了什么。
        /// </summary>
        private void HandleDeviceLost(string message)
        {
            if (_videoSource == null) return; // 已经处理过了
            StopStream();
            _latestFrame = null;
            _previewBitmap = null;
            ImagePreview.Source = null;
            TextBlockPreviewHint.Text = "展台已断开。\r\n请重新插好设备后点「刷新」。";
            TextBlockPreviewHint.Visibility = Visibility.Visible;
            SetStatus(message);
            RefreshDeviceList();
        }

        #endregion

        #region 按钮

        private void ButtonSnapshot_Click(object sender, RoutedEventArgs e)
        {
            if (_latestFrame == null)
            {
                SetStatus("还没有画面可抓拍，请先选择展台设备。");
                return;
            }

            var handler = SnapshotCaptured;
            if (handler == null)
            {
                SetStatus("未连接到画板，无法贴图。");
                return;
            }

            // _latestFrame 是我们自己拷出来并 Freeze 过的，与采集缓冲无关，
            // 可以直接交给画布长期持有，无需再复制一份。
            handler(_latestFrame);
            SetStatus("已抓拍并贴到画板。窗口保持打开，可继续抓拍。");
        }

        private void ButtonFreeze_Click(object sender, RoutedEventArgs e)
        {
            _isFrozen = !_isFrozen;
            ButtonFreeze.Content = _isFrozen ? "恢复" : "冻结";
            BorderFrozenBadge.Visibility = _isFrozen ? Visibility.Visible : Visibility.Collapsed;
            // 只是停止刷新，采集仍在跑：解冻立刻恢复，且冻结期间抓拍抓到的就是定格的这一帧
            SetStatus(_isFrozen ? "画面已冻结，可继续抓拍" : "已恢复实时画面");
        }

        private void ButtonDeviceSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!(ComboBoxDevice.SelectedItem is FilterInfo info))
            {
                SetStatus("请先选择展台设备。");
                return;
            }

            try
            {
                // 驱动属性页会改设备状态，先停流再打开
                StopStream();
                var device = new VideoCaptureDevice(info.MonikerString);
                device.DisplayPropertyPage(new WindowInteropHelper(this).Handle);
            }
            catch (Exception ex)
            {
                LogHelper.NewLog(ex);
                SetStatus("打开展台属性页失败：" + ex.Message);
            }

            // 属性页可能改掉了分辨率等能力，重开一次流
            OpenSelectedDevice();
        }

        private void ButtonFullScreen_Click(object sender, RoutedEventArgs e)
        {
            ToggleFullScreen();
        }

        private void BorderPreview_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Border 是 FrameworkElement 而不是 Control，没有 MouseDoubleClick 事件，只能用 ClickCount 判断
            if (e.ClickCount == 2)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        }

        private void ToggleFullScreen()
        {
            if (!_isFullScreen)
            {
                _styleBeforeFullScreen = WindowStyle;
                _stateBeforeFullScreen = WindowState;
                _resizeBeforeFullScreen = ResizeMode;
                _boundsBeforeFullScreen = new Rect(Left, Top, Width, Height);

                StackPanelDevice.Visibility = Visibility.Collapsed;
                StackPanelControls.Visibility = Visibility.Collapsed;
                RootGrid.Margin = new Thickness(0);
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                // 必须先把状态置回 Normal 再最大化：WindowStyle=None 时直接切 Maximized
                // 会盖住任务栏（WPF 的已知行为）
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
                // 主窗口是 Topmost 的，全屏时不保持置顶就会被压住
                Topmost = true;
                ButtonFullScreen.Content = "还原";
                Focus();
            }
            else
            {
                WindowState = WindowState.Normal;
                WindowStyle = _styleBeforeFullScreen;
                ResizeMode = _resizeBeforeFullScreen;
                // 位置和尺寸要显式还原，只还 WindowStyle/WindowState 是回不去的
                Left = _boundsBeforeFullScreen.Left;
                Top = _boundsBeforeFullScreen.Top;
                Width = _boundsBeforeFullScreen.Width;
                Height = _boundsBeforeFullScreen.Height;
                RootGrid.Margin = new Thickness(10);
                StackPanelDevice.Visibility = Visibility.Visible;
                StackPanelControls.Visibility = Visibility.Visible;
                ButtonFullScreen.Content = "全屏";
            }

            _isFullScreen = !_isFullScreen;
        }

        private void SetStatus(string text)
        {
            TextBlockStatus.Text = text;
        }

        #endregion

        #region 生命周期

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Topmost = CameraSettings.IsCameraTopmost;
            RefreshDeviceList();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            // 不停流的话摄像头的灯会一直亮着，而且设备会被本进程一直占住
            StopStream();
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && _isFullScreen)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
            else if (e.Key == Key.F11)
            {
                ToggleFullScreen();
                e.Handled = true;
            }
        }

        #endregion

        /// <summary>分辨率下拉框的一项。</summary>
        private sealed class ResolutionOption
        {
            public readonly VideoCapabilities Capability;
            private readonly int _width;
            private readonly int _height;
            private readonly int _frameRate;

            public ResolutionOption(VideoCapabilities capability)
            {
                Capability = capability;
                _width = capability.FrameSize.Width;
                _height = capability.FrameSize.Height;
                _frameRate = capability.AverageFrameRate;
            }

            /// <summary>存进 Settings 的形式，形如 "1280x720"。</summary>
            public string ToSettingValue()
            {
                return _width + "x" + _height;
            }

            public override string ToString()
            {
                return _frameRate > 0
                    ? _width + " x " + _height + " @" + _frameRate + "fps"
                    : _width + " x " + _height;
            }
        }
    }
}

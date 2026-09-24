using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using WpfCanvas = System.Windows.Controls.Canvas;

namespace InkCanvasPlus
{
    /// <summary>
    /// 展台抓拍画面在画板上的呈现与操作。
    /// </summary>
    /// <remarks>
    /// 图层安排是这个功能的关键：
    /// <list type="bullet">
    /// <item>图片本体放在 CameraSnapshotCanvas 里，而该 Canvas 声明在 inkCanvas <b>之前</b>，
    /// 因此墨迹始终画在照片之上——这是"照片可批注"的实现方式。</item>
    /// <item>选中框与操作手柄放在 CameraControlsOverlayCanvas 里，声明在 inkCanvas <b>之后</b>，
    /// 因此手柄永远压在墨迹之上、且能可靠接收鼠标与触控笔事件。</item>
    /// </list>
    /// 手柄之所以必须放在 inkCanvas 之上：在 WPF 里对提升后的鼠标事件设置 e.Handled
    /// 并不能阻止 InkCanvas 采集笔迹（笔迹是在触控笔管线里采集的，早于提升的鼠标事件）。
    /// 把可拖拽的控件放在 inkCanvas 之上，是几何工具已经在用的、唯一可靠的做法。
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>单个照片缩放的下限与上限。</summary>
        private const double CameraSnapshotMinSize = 80;
        private const double CameraSnapshotMaxScale = 4.0;

        /// <summary>手柄尺寸，以及手柄与照片边缘之间的间隙。</summary>
        private const double CameraSnapshotGripSize = 22;
        private const double CameraSnapshotGripGap = 2;

        /// <summary>
        /// 画布上所有照片的像素总量上限（按 32 位 BGRA 计约 66MB）。
        /// 项目是 Prefer32Bit 的 32 位进程，地址空间有限，不设这个上限的话
        /// 连拍几张 4K 照片（每张约 33MB）就会把内存吃满。
        /// </summary>
        private const long CameraSnapshotPixelBudget = 1920L * 1080L * 8;

        private enum CameraDragMode { None, Move, Scale, Rotate }

        private sealed class CameraSnapshot
        {
            public Image Image;
            public WpfCanvas Frame;
            public Rectangle Outline;
            public Border MoveGrip;
            public Border ScaleGrip;
            public Border RotateGrip;
            public Border CloseGrip;
            public Border CropGrip;
            /// <summary>未旋转时的左上角位置与尺寸（照片层坐标系）。</summary>
            public double X, Y, W, H;
            public double AngleDeg;
        }

        private readonly List<CameraSnapshot> _cameraSnapshots = new List<CameraSnapshot>();
        private CameraWindow _cameraWindow;
        private CameraSnapshot _cameraDragTarget;
        private CameraDragMode _cameraDragMode = CameraDragMode.None;
        private Point _cameraGrabOffset;
        private double _cameraBaseW, _cameraBaseH;
        private double _cameraRotateOffsetDeg;
        private int _cameraSnapshotTopZ = 1;

        #region 入口

        private void CameraWindowButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            HideSubPanels();

            // 单例：展厅里只该有一个展台窗口，两个窗口会争抢同一个摄像头设备
            if (_cameraWindow != null && _cameraWindow.IsLoaded)
            {
                if (_cameraWindow.WindowState == WindowState.Minimized)
                {
                    _cameraWindow.WindowState = WindowState.Normal;
                }
                _cameraWindow.Activate();
                e.Handled = true;
                return;
            }

            _cameraWindow = new CameraWindow { Owner = this };
            _cameraWindow.SnapshotCaptured += AddCameraSnapshot;
            _cameraWindow.Closed += (s, args) => _cameraWindow = null;
            _cameraWindow.Show();
            e.Handled = true;
        }

        /// <summary>应用退出时主动关掉展台窗口，确保采集停止、摄像头设备被释放。</summary>
        private void CloseCameraWindow()
        {
            if (_cameraWindow == null) return;
            try
            {
                _cameraWindow.Close();
            }
            catch (Exception ex)
            {
                Helpers.LogHelper.NewLog(ex);
            }
            _cameraWindow = null;
        }

        #endregion

        #region 添加 / 移除

        /// <summary>把一张抓拍画面贴到画布上。</summary>
        private void AddCameraSnapshot(BitmapSource source)
        {
            if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0) return;

            EvictCameraSnapshotsFor((long)source.PixelWidth * source.PixelHeight);

            var areaW = CameraSnapshotCanvas.ActualWidth;
            var areaH = CameraSnapshotCanvas.ActualHeight;
            if (areaW <= 0 || areaH <= 0)
            {
                areaW = inkCanvas.ActualWidth;
                areaH = inkCanvas.ActualHeight;
            }
            if (areaW <= 0 || areaH <= 0)
            {
                areaW = 800;
                areaH = 600;
            }

            // 初始尺寸：不超过画布 60%，并保持宽高比；小图不放大，只保证不低于下限
            var ratio = Math.Min(areaW * 0.6 / source.PixelWidth, areaH * 0.6 / source.PixelHeight);
            if (ratio > 1) ratio = 1;
            var w = source.PixelWidth * ratio;
            var h = source.PixelHeight * ratio;
            if (w < CameraSnapshotMinSize)
            {
                var k = CameraSnapshotMinSize / Math.Max(1, w);
                w *= k;
                h *= k;
            }

            var rec = new CameraSnapshot { W = w, H = h, AngleDeg = 0 };

            rec.Image = new Image
            {
                Source = source,
                Stretch = Stretch.Fill, // 宽高比已由 W/H 保证，Fill 即精确填充
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0),
                SnapsToDevicePixels = true,
            };
            // 静态照片只在外观变化时重绘，用高质量缩放（实时预览才需要 LowQuality）
            RenderOptions.SetBitmapScalingMode(rec.Image, BitmapScalingMode.HighQuality);

            rec.Frame = new WpfCanvas
            {
                // 不设背景：Canvas 自身不参与命中测试，笔迹才能照常落在照片上
                Background = null,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new RotateTransform(0),
            };

            rec.Outline = new Rectangle
            {
                StrokeThickness = 1.5,
                StrokeDashArray = new DoubleCollection { 4, 3 },
                Fill = null,
                IsHitTestVisible = false,
            };
            rec.Outline.SetResourceReference(Shape.StrokeProperty, "FloatBarForeground");

            rec.MoveGrip = CreateCameraGrip("✥", Cursors.SizeAll, "拖动照片");
            rec.ScaleGrip = CreateCameraGrip("⤢", Cursors.SizeNWSE, "缩放照片");
            rec.RotateGrip = CreateCameraGrip("↻", Cursors.Hand, "旋转照片");
            rec.CloseGrip = CreateCameraGrip("✕", Cursors.Hand, "删除这张照片", "#FFD93535");
            rec.CropGrip = CreateCameraSymbolGrip(iNKORE.UI.WPF.Modern.Controls.Symbol.Crop, Cursors.Hand, "裁剪这张照片");

            AttachCameraGripHandlers(rec, rec.MoveGrip, CameraDragMode.Move);
            AttachCameraGripHandlers(rec, rec.ScaleGrip, CameraDragMode.Scale);
            AttachCameraGripHandlers(rec, rec.RotateGrip, CameraDragMode.Rotate);
            rec.CloseGrip.MouseLeftButtonUp += (s, e) =>
            {
                RemoveCameraSnapshot(rec);
                e.Handled = true;
            };
            rec.CropGrip.MouseLeftButtonUp += (s, e) =>
            {
                BeginCameraCrop(rec);
                e.Handled = true;
            };

            rec.Frame.Children.Add(rec.Outline);
            rec.Frame.Children.Add(rec.MoveGrip);
            rec.Frame.Children.Add(rec.ScaleGrip);
            rec.Frame.Children.Add(rec.RotateGrip);
            rec.Frame.Children.Add(rec.CloseGrip);
            rec.Frame.Children.Add(rec.CropGrip);

            rec.X = (areaW - w) / 2.0;
            rec.Y = (areaH - h) / 2.0;

            CameraSnapshotCanvas.Children.Add(rec.Image);
            CameraControlsOverlayCanvas.Children.Add(rec.Frame);
            _cameraSnapshots.Add(rec);

            BringCameraSnapshotToFront(rec);
            ApplyCameraSnapshotTransform(rec);
        }

        /// <summary>
        /// 按数量上限和像素预算腾出空间，从最早的一张开始淘汰。
        /// </summary>
        private void EvictCameraSnapshotsFor(long incomingPixels)
        {
            var settings = Settings.Camera;
            var maxCount = settings != null && settings.MaxSnapshots > 0 ? settings.MaxSnapshots : 8;

            while (_cameraSnapshots.Count > 0)
            {
                var overCount = _cameraSnapshots.Count >= maxCount;
                var overBudget = TotalCameraSnapshotPixels() + incomingPixels > CameraSnapshotPixelBudget;
                if (!overCount && !overBudget) break;
                RemoveCameraSnapshot(_cameraSnapshots[0]);
            }
        }

        private long TotalCameraSnapshotPixels()
        {
            var total = 0L;
            foreach (var rec in _cameraSnapshots)
            {
                if (rec.Image != null && rec.Image.Source is BitmapSource bitmap)
                {
                    total += (long)bitmap.PixelWidth * bitmap.PixelHeight;
                }
            }
            return total;
        }

        private void RemoveCameraSnapshot(CameraSnapshot rec)
        {
            if (rec == null) return;

            // 正在裁这张就先把裁剪状态收掉，否则会话里会留着一个已经不在画布上的照片
            if (_cameraCrop != null && ReferenceEquals(_cameraCrop.Target, rec)) EndCameraCrop();

            if (ReferenceEquals(_cameraDragTarget, rec))
            {
                _cameraDragTarget = null;
                _cameraDragMode = CameraDragMode.None;
            }

            CameraSnapshotCanvas.Children.Remove(rec.Image);
            CameraControlsOverlayCanvas.Children.Remove(rec.Frame);
            // 主动断开位图引用：照片是临时对象，不主动放手会一直占着几 MB 内存
            if (rec.Image != null) rec.Image.Source = null;
            _cameraSnapshots.Remove(rec);
        }

        /// <summary>清屏时一并清除照片：「清屏」的语义是把这一页清干净。</summary>
        private void ClearCameraSnapshots()
        {
            EndCameraCrop();
            _cameraDragTarget = null;
            _cameraDragMode = CameraDragMode.None;
            foreach (var rec in _cameraSnapshots.ToArray())
            {
                RemoveCameraSnapshot(rec);
            }
            CameraSnapshotCanvas.Children.Clear();
            CameraControlsOverlayCanvas.Children.Clear();
        }

        private void BringCameraSnapshotToFront(CameraSnapshot rec)
        {
            var z = _cameraSnapshotTopZ++;
            Panel.SetZIndex(rec.Image, z);
            Panel.SetZIndex(rec.Frame, z);
        }

        #endregion

        #region 手柄与手势

        private Border CreateCameraGrip(string glyph, Cursor cursor, string tooltip, string foreground = null)
        {
            var text = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            if (foreground == null)
            {
                text.SetResourceReference(TextBlock.ForegroundProperty, "FloatBarForeground");
            }
            else
            {
                text.Foreground = (Brush)new BrushConverter().ConvertFromString(foreground);
            }

            return CreateCameraGripCore(cursor, tooltip, text);
        }

        /// <summary>
        /// 用 iNKORE 那套微软符号字体画手柄图标。
        /// 尺规那组几何字符里没有像样的"裁剪"，而 Symbol 枚举的取值本身就是
        /// Segoe MDL2 Assets 的码位（Crop = 0xE123，和 Camera = 0xE114 一致），
        /// 所以直接取码位交给字体渲染，不用再引一个 SymbolIcon 的默认样式进来。
        /// </summary>
        private Border CreateCameraSymbolGrip(iNKORE.UI.WPF.Modern.Controls.Symbol symbol, Cursor cursor, string tooltip)
        {
            var text = new TextBlock
            {
                Text = char.ConvertFromUtf32((int)symbol),
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "FloatBarForeground");

            return CreateCameraGripCore(cursor, tooltip, text);
        }

        private Border CreateCameraGripCore(Cursor cursor, string tooltip, UIElement child)
        {
            var grip = new Border
            {
                Width = CameraSnapshotGripSize,
                Height = CameraSnapshotGripSize,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Cursor = cursor,
                ToolTip = tooltip,
                Child = child,
                SnapsToDevicePixels = true,
            };
            // 用 SetResourceReference（等价于 XAML 的 DynamicResource），
            // 12 套主题字典切换时会自动重取颜色，不需要额外的刷新钩子
            grip.SetResourceReference(Border.BackgroundProperty, "FloatBarBackground");
            grip.SetResourceReference(Border.BorderBrushProperty, "FloatBarBorderBrush");
            return grip;
        }

        private void AttachCameraGripHandlers(CameraSnapshot rec, UIElement grip, CameraDragMode mode)
        {
            grip.MouseLeftButtonDown += (s, e) => BeginCameraDrag(rec, grip, e, mode);
            grip.MouseMove += (s, e) => ContinueCameraDrag(rec, e);
            grip.MouseLeftButtonUp += (s, e) => EndCameraDrag(grip, e);
        }

        private void BeginCameraDrag(CameraSnapshot rec, UIElement grip, MouseButtonEventArgs e, CameraDragMode mode)
        {
            BringCameraSnapshotToFront(rec);
            _cameraDragTarget = rec;
            _cameraDragMode = mode;
            _cameraBaseW = rec.W;
            _cameraBaseH = rec.H;

            var p = e.GetPosition(CameraControlsOverlayCanvas);
            _cameraGrabOffset = new Point(p.X - rec.X, p.Y - rec.Y);
            if (mode == CameraDragMode.Rotate)
            {
                _cameraRotateOffsetDeg = GetPointerAngleAround(CameraSnapshotCenter(rec), p) - rec.AngleDeg;
            }

            grip.CaptureMouse();
            e.Handled = true;
        }

        private void ContinueCameraDrag(CameraSnapshot rec, MouseEventArgs e)
        {
            if (_cameraDragMode == CameraDragMode.None) return;
            if (!ReferenceEquals(_cameraDragTarget, rec)) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var p = e.GetPosition(CameraControlsOverlayCanvas);

            switch (_cameraDragMode)
            {
                case CameraDragMode.Move:
                    rec.X = p.X - _cameraGrabOffset.X;
                    rec.Y = p.Y - _cameraGrabOffset.Y;
                    break;

                case CameraDragMode.Scale:
                {
                    // 把指针换算到照片未旋转时的局部坐标系，两个方向取较大的缩放系数，
                    // 宽高同乘一个系数，因此宽高比天然保持不变
                    var center = CameraSnapshotCenter(rec);
                    var v = RotateCameraVector(new Point(p.X - center.X, p.Y - center.Y), -rec.AngleDeg);
                    var halfW = Math.Max(1, _cameraBaseW / 2.0);
                    var halfH = Math.Max(1, _cameraBaseH / 2.0);
                    var scale = Math.Max(Math.Abs(v.X) / halfW, Math.Abs(v.Y) / halfH);
                    scale = Clamp(scale, 1.0 / CameraSnapshotMaxScale, CameraSnapshotMaxScale);
                    rec.W = Math.Max(CameraSnapshotMinSize, _cameraBaseW * scale);
                    rec.H = Math.Max(CameraSnapshotMinSize, _cameraBaseH * scale);
                    break;
                }

                case CameraDragMode.Rotate:
                    rec.AngleDeg = NormalizeAngle(GetPointerAngleAround(CameraSnapshotCenter(rec), p) - _cameraRotateOffsetDeg);
                    break;
            }

            ApplyCameraSnapshotTransform(rec);

            if (_cameraDragMode == CameraDragMode.Rotate)
            {
                // 旋转边界约束可能把角度顶回去，这里重算一次基准角度，
                // 否则指针回到原处时照片已经转过了，继续拖就会累积漂移
                _cameraRotateOffsetDeg = GetPointerAngleAround(CameraSnapshotCenter(rec), p) - rec.AngleDeg;
            }

            e.Handled = true;
        }

        private void EndCameraDrag(UIElement grip, MouseButtonEventArgs e)
        {
            if (_cameraDragMode == CameraDragMode.None) return;
            _cameraDragMode = CameraDragMode.None;
            _cameraDragTarget = null;
            grip.ReleaseMouseCapture();
            e.Handled = true;
        }

        private static Point CameraSnapshotCenter(CameraSnapshot rec)
        {
            return new Point(rec.X + rec.W / 2.0, rec.Y + rec.H / 2.0);
        }

        private static Point RotateCameraVector(Point v, double angleDeg)
        {
            var rad = angleDeg * Math.PI / 180.0;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            return new Point(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
        }

        #endregion

        #region 布局与定位

        /// <summary>
        /// 把照片的位置、尺寸与旋转同时应用到图片层和控制层，并把手柄摆到当前该在的位置。
        /// 两层用同一套 Left/Top/Width/Height 与同一个 RenderTransformOrigin，
        /// 因此图片与其手柄天然保持像素对齐。
        /// </summary>
        private void ApplyCameraSnapshotTransform(CameraSnapshot rec)
        {
            var origin = ClampCameraSnapshotOrigin(new Point(rec.X, rec.Y), rec.W, rec.H, rec.AngleDeg);
            rec.X = origin.X;
            rec.Y = origin.Y;

            WpfCanvas.SetLeft(rec.Image, rec.X);
            WpfCanvas.SetTop(rec.Image, rec.Y);
            rec.Image.Width = rec.W;
            rec.Image.Height = rec.H;
            ((RotateTransform)rec.Image.RenderTransform).Angle = rec.AngleDeg;

            WpfCanvas.SetLeft(rec.Frame, rec.X);
            WpfCanvas.SetTop(rec.Frame, rec.Y);
            rec.Frame.Width = rec.W;
            rec.Frame.Height = rec.H;
            ((RotateTransform)rec.Frame.RenderTransform).Angle = rec.AngleDeg;

            LayoutCameraSnapshotChrome(rec);
        }

        private void LayoutCameraSnapshotChrome(CameraSnapshot rec)
        {
            var g = CameraSnapshotGripSize;
            var gap = CameraSnapshotGripGap;

            rec.Outline.Width = rec.W;
            rec.Outline.Height = rec.H;
            WpfCanvas.SetLeft(rec.Outline, 0);
            WpfCanvas.SetTop(rec.Outline, 0);

            // 手柄一律放在照片矩形之外（Frame 不裁剪子元素，负坐标可以正常显示）。
            // 这样手柄既不遮住照片内容，也不会挡住在照片上写字。
            // 布局：左上删除、上中旋转、右中移动、左中裁剪、右下缩放。
            PlaceCameraGrip(rec.MoveGrip, rec.W + gap, rec.H / 2 - g / 2);
            PlaceCameraGrip(rec.ScaleGrip, rec.W + gap, rec.H + gap);
            PlaceCameraGrip(rec.RotateGrip, rec.W / 2 - g / 2, -g - gap);
            PlaceCameraGrip(rec.CloseGrip, -g - gap, -g - gap);
            PlaceCameraGrip(rec.CropGrip, -g - gap, rec.H / 2 - g / 2);

            // 正在裁这张照片的话，确认/取消两个按钮也要跟着照片的尺寸走
            if (_cameraCrop != null && ReferenceEquals(_cameraCrop.Target, rec)) LayoutCameraCropGrips(_cameraCrop);
        }

        private static void PlaceCameraGrip(FrameworkElement grip, double left, double top)
        {
            WpfCanvas.SetLeft(grip, left);
            WpfCanvas.SetTop(grip, top);
        }

        /// <summary>
        /// 把照片约束在画板可见区域内。按旋转后的实际包围盒来夹取，
        /// 否则旋转 45 度后照片会有一角探出画布。
        /// </summary>
        /// <remarks>
        /// 这里复用几何工具的三个静态助手（GetRotatedVisualBounds / ClampAxis）。
        /// 没有直接复用 ClampToolOrigin：它把目标画布硬编码成了 GeometryToolsOverlayCanvas，
        /// 依赖两个画布恰好同尺寸并不牢靠，所以在自己的文件里写一份更安全。
        /// </remarks>
        private Point ClampCameraSnapshotOrigin(Point origin, double w, double h, double angleDeg)
        {
            // 手柄在照片外侧，旋转后要连这部分一起算，否则手柄会被切在画布外
            var pad = CameraSnapshotGripSize + CameraSnapshotGripGap;
            var extentW = w + pad * 2;
            var extentH = h + pad * 2;
            var bounds = GetRotatedVisualBounds(extentW, extentH, extentW / 2.0, extentH / 2.0, angleDeg);

            // 底部再留 80px 给左下角的浮动工具栏，和尺具的处理保持一致
            var maxLeft = Math.Max(0, CameraSnapshotCanvas.ActualWidth - bounds.Width);
            var maxTop = Math.Max(0, CameraSnapshotCanvas.ActualHeight - bounds.Height - 80);

            return new Point(
                ClampAxis(origin.X - pad, -bounds.X, maxLeft - bounds.X) + pad,
                ClampAxis(origin.Y - pad, -bounds.Y, maxTop - bounds.Y) + pad);
        }

        #endregion
    }
}

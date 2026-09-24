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
    /// 展台抓拍照片的裁剪。
    /// </summary>
    /// <remarks>
    /// 交互跟常见的裁剪工具一致：点照片左侧的裁剪手柄进入裁剪态，照片上浮出一个裁剪框，
    /// 拖八个把手改大小、拖框内挪位置，右上角确认、左上角取消。
    ///
    /// 坐标系是这里的关键：裁剪框始终存在照片自己的局部坐标里（未旋转、左上角为原点，
    /// 范围就是 0..W / 0..H），所以照片转过多少度都不影响拖拽的手感。
    /// 指针位置一律用 e.GetPosition(rec.Frame) 取 —— Frame 的 RenderTransform 就是那张照片的
    /// 旋转，GetPosition 会把它反算掉，拿到的正好是这套局部坐标。
    /// （裁剪过程中不会调用 ApplyCameraSnapshotTransform，Frame 的变换不变，
    /// 所以这里不存在黑板拖动那边"上一次位移被减掉"的问题。）
    ///
    /// 确认裁剪是破坏性的：直接换掉 Image.Source，原图不再保留。
    /// 换成 WriteableBitmap 而不是直接用 CroppedBitmap，是因为 CroppedBitmap 内部还引用着整张原图，
    /// 那样像素预算会把已经裁掉的部分继续算进去，内存也就放不掉。
    /// </remarks>
    public partial class MainWindow
    {
        /// <summary>
        /// 裁剪框的最小显示尺寸（画布像素）。直接沿用照片自己的最小尺寸，不另立一个更小的值：
        /// 裁得比缩放手柄的下限还小的话，下一次拖缩放手柄会把它猛地弹回下限。
        /// </summary>
        private const double CameraSnapshotCropMinSize = CameraSnapshotMinSize;

        /// <summary>裁剪框八个把手的边长。</summary>
        private const double CameraSnapshotCropHandleSize = 20;

        /// <summary>裁剪框的八个把手：四角加四边，Move 表示拖框内整体挪动。</summary>
        private enum CameraCropHandle { None, Move, NW, N, NE, W, E, SW, S, SE }

        private sealed class CameraCropSession
        {
            public CameraSnapshot Target;

            /// <summary>裁剪框，照片局部坐标（未旋转）。</summary>
            public Rect Rect;

            public CameraCropHandle Active = CameraCropHandle.None;
            /// <summary>本次拖拽的起点与起始裁剪框，拖拽过程中一切以它们为基准算，避免误差累积。</summary>
            public Point DragStart;
            public Rect StartRect;

            // 遮罩用一条 EvenOdd 的 Path 挖洞，内外两个矩形留着随时改
            public Path Mask;
            public RectangleGeometry MaskOuter;
            public RectangleGeometry MaskInner;

            public Rectangle Border;
            /// <summary>框内的可拖拽区（Fill 给 Transparent 才参与命中测试）。</summary>
            public Rectangle Interior;

            public Border ConfirmGrip;
            public Border CancelGrip;

            public readonly List<Border> Handles = new List<Border>();
            public readonly List<CameraCropHandle> HandleKinds = new List<CameraCropHandle>();
        }

        private CameraCropSession _cameraCrop;

        #region 进入 / 退出

        /// <summary>对某张照片进入裁剪状态。一次只裁一张，换目标时先收掉上一张。</summary>
        private void BeginCameraCrop(CameraSnapshot rec)
        {
            if (rec == null || rec.Image == null || rec.Image.Source == null) return;

            EndCameraCrop();
            BringCameraSnapshotToFront(rec);

            var session = new CameraCropSession { Target = rec };
            // 起始框就是整张照片：什么都不动直接点确认等于没裁，不会莫名少掉一圈
            session.Rect = new Rect(0, 0, rec.W, rec.H);

            CreateCameraCropChrome(session);

            // 加进 Frame 的先后就是绘制顺序：遮罩 → 边框 → 框内 → 八个把手 → 确认/取消
            rec.Frame.Children.Add(session.Mask);
            rec.Frame.Children.Add(session.Border);
            rec.Frame.Children.Add(session.Interior);
            foreach (var handle in session.Handles) rec.Frame.Children.Add(handle);
            rec.Frame.Children.Add(session.ConfirmGrip);
            rec.Frame.Children.Add(session.CancelGrip);

            _cameraCrop = session;
            SetCameraGripsForCrop(rec, true);
            LayoutCameraCropGrips(session);
            UpdateCameraCropChrome(session);
        }

        /// <summary>退出裁剪状态并撤掉裁剪界面（确认和取消都走这里，区别只在调用前做了什么）。</summary>
        private void EndCameraCrop()
        {
            var session = _cameraCrop;
            if (session == null) return;

            _cameraCrop = null;
            session.Active = CameraCropHandle.None;

            var frame = session.Target != null ? session.Target.Frame : null;
            if (frame != null)
            {
                frame.Children.Remove(session.Mask);
                frame.Children.Remove(session.Border);
                frame.Children.Remove(session.Interior);
                foreach (var handle in session.Handles) frame.Children.Remove(handle);
                frame.Children.Remove(session.ConfirmGrip);
                frame.Children.Remove(session.CancelGrip);
            }

            SetCameraGripsForCrop(session.Target, false);
        }

        /// <summary>裁剪时把移动/缩放/旋转/删除/裁剪手柄都收起来，只留确认和取消，免得误触。</summary>
        private static void SetCameraGripsForCrop(CameraSnapshot rec, bool cropping)
        {
            if (rec == null) return;

            var visibility = cropping ? Visibility.Collapsed : Visibility.Visible;
            rec.MoveGrip.Visibility = visibility;
            rec.ScaleGrip.Visibility = visibility;
            rec.RotateGrip.Visibility = visibility;
            rec.CloseGrip.Visibility = visibility;
            rec.CropGrip.Visibility = visibility;
        }

        #endregion

        #region 裁剪界面

        private void CreateCameraCropChrome(CameraCropSession session)
        {
            // 裁剪框以外压一层半透明黑。用一条 FillRule=EvenOdd 的 Path 把内外两个矩形一减，
            // 就是一个"挖洞"效果 —— 比铺四块挡板省事，更新时也只改两个 Rect
            var shade = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0));
            shade.Freeze();
            session.MaskOuter = new RectangleGeometry();
            session.MaskInner = new RectangleGeometry();
            var group = new GeometryGroup { FillRule = FillRule.EvenOdd };
            group.Children.Add(session.MaskOuter);
            group.Children.Add(session.MaskInner);
            session.Mask = new Path { Fill = shade, Data = group, IsHitTestVisible = false };

            session.Border = new Rectangle { Fill = null, StrokeThickness = 1.5, IsHitTestVisible = false };
            session.Border.SetResourceReference(Shape.StrokeProperty, "FloatBarForeground");

            // Fill 必须是 Transparent 而不是 null：null 的画布不参与命中测试，框内就拖不动了
            session.Interior = new Rectangle
            {
                Fill = Brushes.Transparent,
                Cursor = Cursors.SizeAll,
                ToolTip = "拖动裁剪框",
            };
            session.Interior.MouseLeftButtonDown += (s, e) => BeginCameraCropDrag(session, CameraCropHandle.Move, session.Interior, e);
            session.Interior.MouseMove += (s, e) => ContinueCameraCropDrag(session, e);
            session.Interior.MouseLeftButtonUp += (s, e) => EndCameraCropDrag(session, session.Interior, e);

            // 确认/取消占用原来缩放手柄和删除手柄的位置，是照片外侧顺手够得着的两个角
            session.ConfirmGrip = CreateCameraGrip("✓", Cursors.Hand, "完成裁剪");
            session.ConfirmGrip.MouseLeftButtonUp += (s, e) =>
            {
                ConfirmCameraCrop();
                e.Handled = true;
            };
            session.CancelGrip = CreateCameraGrip("✕", Cursors.Hand, "取消裁剪", "#FFD93535");
            session.CancelGrip.MouseLeftButtonUp += (s, e) =>
            {
                EndCameraCrop();
                e.Handled = true;
            };

            var kinds = new[]
            {
                CameraCropHandle.NW, CameraCropHandle.N, CameraCropHandle.NE, CameraCropHandle.W,
                CameraCropHandle.E, CameraCropHandle.SW, CameraCropHandle.S, CameraCropHandle.SE,
            };
            foreach (var kind in kinds)
            {
                var handle = CreateCameraCropHandle(CameraCropCursor(kind));
                AttachCameraCropHandle(session, handle, kind);
                session.Handles.Add(handle);
                session.HandleKinds.Add(kind);
            }
        }

        private Border CreateCameraCropHandle(Cursor cursor)
        {
            var handle = new Border
            {
                Width = CameraSnapshotCropHandleSize,
                Height = CameraSnapshotCropHandleSize,
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(2),
                Cursor = cursor,
                SnapsToDevicePixels = true,
            };
            handle.SetResourceReference(Border.BackgroundProperty, "FloatBarBackground");
            handle.SetResourceReference(Border.BorderBrushProperty, "FloatBarForeground");
            return handle;
        }

        private static void LayoutCameraCropGrips(CameraCropSession session)
        {
            var rec = session.Target;
            var g = CameraSnapshotGripSize;
            var gap = CameraSnapshotGripGap;

            PlaceCameraGrip(session.ConfirmGrip, rec.W + gap, rec.H + gap);
            PlaceCameraGrip(session.CancelGrip, -g - gap, -g - gap);
        }

        /// <summary>把裁剪框的位置同步到遮罩、边框和八个把手上。</summary>
        private void UpdateCameraCropChrome(CameraCropSession session)
        {
            var rec = session.Target;
            var rect = session.Rect;
            var half = CameraSnapshotCropHandleSize / 2.0;

            session.MaskOuter.Rect = new Rect(0, 0, rec.W, rec.H);
            session.MaskInner.Rect = rect;

            session.Border.Width = rect.Width;
            session.Border.Height = rect.Height;
            PlaceCameraGrip(session.Border, rect.Left, rect.Top);

            session.Interior.Width = rect.Width;
            session.Interior.Height = rect.Height;
            PlaceCameraGrip(session.Interior, rect.Left, rect.Top);

            for (var i = 0; i < session.Handles.Count; i++)
            {
                var kind = session.HandleKinds[i];
                PlaceCameraGrip(
                    session.Handles[i],
                    rect.Left + CameraCropAnchorX(kind) * rect.Width - half,
                    rect.Top + CameraCropAnchorY(kind) * rect.Height - half);
            }
        }

        #endregion

        #region 拖拽

        private void AttachCameraCropHandle(CameraCropSession session, UIElement handle, CameraCropHandle kind)
        {
            handle.MouseLeftButtonDown += (s, e) => BeginCameraCropDrag(session, kind, handle, e);
            handle.MouseMove += (s, e) => ContinueCameraCropDrag(session, e);
            handle.MouseLeftButtonUp += (s, e) => EndCameraCropDrag(session, handle, e);
        }

        private void BeginCameraCropDrag(CameraCropSession session, CameraCropHandle kind, UIElement handle, MouseButtonEventArgs e)
        {
            if (!ReferenceEquals(_cameraCrop, session)) return;

            session.Active = kind;
            session.DragStart = e.GetPosition(session.Target.Frame);
            session.StartRect = session.Rect;

            handle.CaptureMouse();
            e.Handled = true;
        }

        private void ContinueCameraCropDrag(CameraCropSession session, MouseEventArgs e)
        {
            if (!ReferenceEquals(_cameraCrop, session)) return;
            if (session.Active == CameraCropHandle.None) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var rec = session.Target;
            var pos = e.GetPosition(rec.Frame);
            var dx = pos.X - session.DragStart.X;
            var dy = pos.Y - session.DragStart.Y;

            if (session.Active == CameraCropHandle.Move)
            {
                // 整框平移：把目标左上角夹回照片范围内，位移由夹过的位置反推，
                // 这样框贴着边时不会先滑出去一半再被顶回来
                var maxLeft = Math.Max(0, rec.W - session.StartRect.Width);
                var maxTop = Math.Max(0, rec.H - session.StartRect.Height);
                var left = Clamp(session.StartRect.Left + dx, 0, maxLeft);
                var top = Clamp(session.StartRect.Top + dy, 0, maxTop);
                session.Rect = new Rect(left, top, session.StartRect.Width, session.StartRect.Height);

                UpdateCameraCropChrome(session);
                e.Handled = true;
                return;
            }

            // 照片本身可能已经被裁得比最小裁剪尺寸还小，两个下限都得跟着收
            var minW = Math.Min(CameraSnapshotCropMinSize, rec.W);
            var minH = Math.Min(CameraSnapshotCropMinSize, rec.H);

            var l = session.StartRect.Left;
            var t = session.StartRect.Top;
            var r = session.StartRect.Right;
            var b = session.StartRect.Bottom;

            // 只动这次抓的那个把手对应的边，其余三条边保持不动
            if (HasCameraCropLeft(session.Active)) l = Math.Min(pos.X, r - minW);
            if (HasCameraCropRight(session.Active)) r = Math.Max(pos.X, l + minW);
            if (HasCameraCropTop(session.Active)) t = Math.Min(pos.Y, b - minH);
            if (HasCameraCropBottom(session.Active)) b = Math.Max(pos.Y, t + minH);

            l = Clamp(l, 0, rec.W);
            r = Clamp(r, 0, rec.W);
            t = Clamp(t, 0, rec.H);
            b = Clamp(b, 0, rec.H);

            // Rect(Point, Point) 会自动把顺序摆正，不会像 (x,y,w,h) 那样在反手拖出负宽度时抛异常
            session.Rect = new Rect(new Point(l, t), new Point(r, b));

            UpdateCameraCropChrome(session);
            e.Handled = true;
        }

        private void EndCameraCropDrag(CameraCropSession session, UIElement handle, MouseButtonEventArgs e)
        {
            if (ReferenceEquals(_cameraCrop, session)) session.Active = CameraCropHandle.None;
            handle.ReleaseMouseCapture();
            e.Handled = true;
        }

        #endregion

        #region 确认

        private void ConfirmCameraCrop()
        {
            var session = _cameraCrop;
            if (session == null) return;

            var rec = session.Target;
            var source = rec.Image != null ? rec.Image.Source as BitmapSource : null;
            if (source == null || source.PixelWidth <= 0 || source.PixelHeight <= 0)
            {
                EndCameraCrop();
                return;
            }

            // 框没动过就什么都不做，省得白白复制一张位图
            var rect = session.Rect;
            if (rect.Left <= 0.01 && rect.Top <= 0.01 &&
                rect.Right >= rec.W - 0.01 && rect.Bottom >= rec.H - 0.01)
            {
                EndCameraCrop();
                return;
            }

            var pixelRect = CameraCropToPixelRect(rect, rec.W, rec.H, source.PixelWidth, source.PixelHeight);
            var cropped = CreateDetachedCameraCrop(source, pixelRect);
            if (cropped != null)
            {
                // 沿用它原来的显示比例：裁剪只该去掉多余的部分，不该顺带把照片放大或缩小
                var scaleX = rec.W / source.PixelWidth;
                var scaleY = rec.H / source.PixelHeight;
                var newW = Math.Max(1, pixelRect.Width * scaleX);
                var newH = Math.Max(1, pixelRect.Height * scaleY);

                // 旋转是绕矩形中心做的。要让保留下来的那块待在原地不动，
                // 新矩形的中心得按"旧中心 + 旋转后的偏移"来定，否则照片会跳一下
                var center = CameraSnapshotCenter(rec);
                var offset = new Point(
                    rect.Left + rect.Width / 2.0 - center.X,
                    rect.Top + rect.Height / 2.0 - center.Y);
                var rotated = RotateCameraVector(offset, rec.AngleDeg);

                rec.X = center.X + rotated.X - newW / 2.0;
                rec.Y = center.Y + rotated.Y - newH / 2.0;
                rec.W = newW;
                rec.H = newH;
                rec.Image.Source = cropped;
            }

            EndCameraCrop();
            ApplyCameraSnapshotTransform(rec);
        }

        /// <summary>把显示坐标里的裁剪框换算成原图的像素矩形。</summary>
        private static Int32Rect CameraCropToPixelRect(Rect rect, double dispW, double dispH, int pixelW, int pixelH)
        {
            var x0 = (int)Clamp(Math.Round(rect.Left / dispW * pixelW), 0, pixelW - 1);
            var y0 = (int)Clamp(Math.Round(rect.Top / dispH * pixelH), 0, pixelH - 1);
            var x1 = (int)Clamp(Math.Round(rect.Right / dispW * pixelW), x0 + 1, pixelW);
            var y1 = (int)Clamp(Math.Round(rect.Bottom / dispH * pixelH), y0 + 1, pixelH);

            return new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        }

        /// <summary>
        /// 裁下指定像素区域，并复制成一张不再依赖原图的独立位图。
        /// </summary>
        /// <remarks>
        /// CroppedBitmap 是惰性的：它内部仍然引用着整张原图，只裁一小块也放不掉原来的内存，
        /// 而 AddCameraSnapshot 的像素预算又是按 Image.Source 的尺寸算的，会被少算。
        /// 所以这里再用 WriteableBitmap 复制一份，裁掉的部分才真的还回去。
        /// </remarks>
        private static BitmapSource CreateDetachedCameraCrop(BitmapSource source, Int32Rect rect)
        {
            try
            {
                var copy = new WriteableBitmap(new CroppedBitmap(source, rect));
                copy.Freeze();
                return copy;
            }
            catch (Exception ex)
            {
                // 个别像素格式可能不被 WriteableBitmap 接受，那就退回到惰性裁剪，
                // 画面是对的，只是内存还留着原图
                Helpers.LogHelper.NewLog(ex);
                try
                {
                    var fallback = new CroppedBitmap(source, rect);
                    fallback.Freeze();
                    return fallback;
                }
                catch (Exception inner)
                {
                    Helpers.LogHelper.NewLog(inner);
                    return null;
                }
            }
        }

        #endregion

        #region 几何小工具

        private static bool HasCameraCropLeft(CameraCropHandle handle)
        {
            return handle == CameraCropHandle.NW || handle == CameraCropHandle.W || handle == CameraCropHandle.SW;
        }

        private static bool HasCameraCropRight(CameraCropHandle handle)
        {
            return handle == CameraCropHandle.NE || handle == CameraCropHandle.E || handle == CameraCropHandle.SE;
        }

        private static bool HasCameraCropTop(CameraCropHandle handle)
        {
            return handle == CameraCropHandle.NW || handle == CameraCropHandle.N || handle == CameraCropHandle.NE;
        }

        private static bool HasCameraCropBottom(CameraCropHandle handle)
        {
            return handle == CameraCropHandle.SW || handle == CameraCropHandle.S || handle == CameraCropHandle.SE;
        }

        /// <summary>把手在裁剪框里的横向锚点比例：0 贴左边、0.5 居中、1 贴右边。</summary>
        private static double CameraCropAnchorX(CameraCropHandle handle)
        {
            if (HasCameraCropLeft(handle)) return 0;
            if (HasCameraCropRight(handle)) return 1;
            return 0.5;
        }

        private static double CameraCropAnchorY(CameraCropHandle handle)
        {
            if (HasCameraCropTop(handle)) return 0;
            if (HasCameraCropBottom(handle)) return 1;
            return 0.5;
        }

        private static Cursor CameraCropCursor(CameraCropHandle handle)
        {
            switch (handle)
            {
                case CameraCropHandle.NW:
                case CameraCropHandle.SE:
                    return Cursors.SizeNWSE;
                case CameraCropHandle.NE:
                case CameraCropHandle.SW:
                    return Cursors.SizeNESW;
                case CameraCropHandle.N:
                case CameraCropHandle.S:
                    return Cursors.SizeNS;
                default:
                    return Cursors.SizeWE;
            }
        }

        #endregion
    }
}

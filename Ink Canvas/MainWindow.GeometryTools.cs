using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
//XAML 里有个 Name="Canvas" 的元素，同名成员会把 Canvas 类型遮住，所以跟别的文件一样用别名
using WpfCanvas = System.Windows.Controls.Canvas;

namespace InkCanvasPlus
{
    /// <summary>
    /// 几何尺具的共享部分：尺寸常量、主题刻度色、工具栏入口与事件路由、坐标与夹取的静态助手。
    ///
    /// 尺具本身是实例对象（一把尺具一份自己的视觉树和状态），见 MainWindow.GeometryToolInstances.cs。
    /// 这里以前挂着“每样尺具只有一把”的单例：状态字段 + 按元素名写死的约 40 个处理器，
    /// 现在处理器都搬到实例上去了，只剩这些谁都用得着的部分。
    /// </summary>
    public partial class MainWindow
    {
        // ---- 尺具的尺寸与手感。只有这一份定义，实例类从这里取 ----

        //直尺：下限保留（太短了没法用），上限取消（老师要能一直拉长）
        private const double GeometryRulerMinWidth = 360;
        private const double GeometryRulerDefaultWidth = 560;
        private const double GeometryRulerHeight = 120;
        //刻度画布的左/右边距，也就是原来 XAML 里 Grid 的第一、三列宽，合起来 74
        private const double GeometryRulerTickLeftInset = 40;
        private const double GeometryRulerTickRightInset = 34;
        //尺身中段可以拖动、可以贴着边画线的范围（左右各让开一个把手）
        private const double GeometryRulerDragLeftInset = 44;
        private const double GeometryRulerDragRightInset = 36;

        //三角尺：直角边同样只保留下限
        private const double GeometryTriangleMinLeg = 90;
        private const double GeometryTriangleDefaultLegX = 250;
        private const double GeometryTriangleDefaultLegY = 170;
        private const double GeometryTrianglePivotLocal = 20;
        private const double GeometryTriangleTickStartOffset = 25;
        private const double GeometryTriangleTickEndInset = 50;

        //量角器：半径上下限都保留（老师只提了直尺和三角尺）
        private const double GeometryProtractorMinRadius = 110;
        private const double GeometryProtractorMaxRadius = 360;
        private const double GeometryProtractorDefaultRadius = 160;
        private const double GeometryProtractorPadding = 22;
        private const double GeometryProtractorCloseButtonDrop = 44;

        //刻度按可见区裁剪时两头各多留一点，免得边缘那几根因为取整被切掉
        private const double GeometryTickCullMargin = 32;
        //兜底：一次最多画这么多根刻度，防止裁剪万一算错时被一把超长尺子卡死界面。
        //这个数必须比"最宽的窗口看得见的那一段有多少根"还大（10px 一根：8K 屏一屏也才 768 根）。
        //小于可见刻度数时它是从区间开头截断的，砍掉的恰好是可见区靠右的那一段 ——
        //表现就是"有一段刻度和数值都不显示，上下线和中线却还在"。取 2000（两万像素）留足余量
        private const int GeometryTickMaxPerDraw = 2000;

        //尺具之间的层级：从 21 起（覆盖层自己是 20），新开的、刚碰过的排在上面
        private const int GeometryToolZIndexBase = 21;
        //量角器的定点标记单独一层，压在尺具下面、墨迹上面（和以前每个点设 19 是一回事）
        private const int GeometryProtractorMarkZIndex = 19;
        //连点工具栏时同类实例错开落位的步长，绕满一圈回到原位
        private const double GeometryToolCascadeStep = 28;
        private const int GeometryToolCascadeWrap = 6;
        //鼠标离尺边多近才算“要贴着边画线”
        private const double GeometryAssistTolerance = 18;

        //尺具/照片比可见区还大的时候（尺子能拉得比屏幕还长、照片能放得比屏幕还大），
        //至少留这么宽的一段仍然搭在可见区里：任意一端都能被拖进屏幕、把柄够得着，
        //又不会整块丢到屏幕外找不回来。见 ClampAxisKeepingVisible
        private const double OversizedMinVisibleSpan = 140;

        private enum GeometryTriangleEdgeType { AB, AC, BC }

        private struct GeometryTriangleProjection
        {
            public GeometryTriangleEdgeType Edge;
            public double Param;
            public double Distance;

            public GeometryTriangleProjection(GeometryTriangleEdgeType edge, double param, double distance)
            {
                Edge = edge;
                Param = param;
                Distance = distance;
            }
        }

        //助画时那条预览线（还没松手的那条）。全局只有一条，谁在助画谁负责收尾
        private StrokeCollection _geometryPreviewStrokes;

        private void InitializeGeometryTools()
        {
            Panel.SetZIndex(GeometryToolsOverlayCanvas, 20);
        }

        private Brush GeometryToolTickBrush
        {
            get
            {
                return Settings.Canvas.UsingWhiteboard
                    ? new SolidColorBrush(Color.FromRgb(55, 62, 74))
                    : new SolidColorBrush(Color.FromRgb(238, 242, 247));
            }
        }

        private Brush GeometryToolSubtleTickBrush
        {
            get
            {
                return Settings.Canvas.UsingWhiteboard
                    ? new SolidColorBrush(Color.FromRgb(78, 85, 98))
                    : new SolidColorBrush(Color.FromRgb(218, 225, 235));
            }
        }

        /// <summary>黑板/白板切换、主题切换后，所有开着的尺具都重画一遍刻度。</summary>
        private void RefreshGeometryToolTheme()
        {
            foreach (var instance in _geometryToolInstances) instance.RebuildTicks();
        }

        private void GeometryToolsButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BorderClearInDelete.Visibility = Visibility.Collapsed;
            BorderTools.Visibility = Visibility.Collapsed;
            BorderDrawShape.Visibility = Visibility.Collapsed;
            BorderGeometryTools.Visibility = BorderGeometryTools.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            e.Handled = true;
        }

        //点一次就新建一把，不再“有就显示、再点就收起”：画平行线这类场景需要同时摆好几把
        private void RulerToolItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BorderGeometryTools.Visibility = Visibility.Collapsed;
            CreateGeometryTool(GeometryToolKind.Ruler);
            e.Handled = true;
        }

        private void TriangleToolItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BorderGeometryTools.Visibility = Visibility.Collapsed;
            CreateGeometryTool(GeometryToolKind.Triangle);
            e.Handled = true;
        }

        private void ProtractorToolItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BorderGeometryTools.Visibility = Visibility.Collapsed;
            CreateGeometryTool(GeometryToolKind.Protractor);
            e.Handled = true;
        }

        //尺具的助画走的是墨迹画布上的鼠标事件（按下时把捕获交给 inkCanvas），
        //这三个是它在墨迹画布那条链上的入口
        private bool GeometryToolsInkCanvasMouseDown(MouseButtonEventArgs e)
        {
            return TryBeginGeometryAssist(e.GetPosition(inkCanvas), null);
        }

        private bool GeometryToolsInkCanvasMouseMove(MouseEventArgs e)
        {
            if (_assistInstance == null) return false;

            //松手事件丢在窗口外时就走不到 MouseUp，这里顺手收个尾：
            //不然后面每次移动都会被当成“还在助画”吃掉，等于写不上字了
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndGeometryAssist(_assistInstance, true);
                return true;
            }

            _assistInstance.UpdateAssistPreview(e.GetPosition(inkCanvas));
            return true;
        }

        private bool GeometryToolsInkCanvasMouseUp(MouseButtonEventArgs e)
        {
            if (_assistInstance == null) return false;
            EndGeometryAssist(_assistInstance, true);
            return true;
        }

        private bool IsGeometryDrawingAvailable()
        {
            return inkCanvas.Visibility == Visibility.Visible && inkCanvas.EditingMode == InkCanvasEditingMode.Ink && drawingShapeMode == 0;
        }

        //尺具挂在覆盖层上，墨迹挂在 inkCanvas 上，两层在黑板里是同一套坐标（同原点、同位移），
        //所以这两个换算只是写法上分清楚“这个点该按谁的坐标系算”，数值是一样的
        private Point OverlayPointFrom(MouseEventArgs e)
        {
            return e.GetPosition(GeometryToolsOverlayCanvas);
        }

        private Point InkPointFrom(MouseEventArgs e)
        {
            return e.GetPosition(inkCanvas);
        }

        private StrokeCollection CreateLineStrokeCollection(Point start, Point end)
        {
            var attrs = inkCanvas.DefaultDrawingAttributes.Clone();
            attrs.IgnorePressure = true;
            attrs.FitToCurve = false;
            var points = new StylusPointCollection { new StylusPoint(start.X, start.Y), new StylusPoint(end.X, end.Y) };
            return new StrokeCollection { new Stroke(points, attrs) };
        }

        private void ReplaceGeometryPreview(StrokeCollection strokes)
        {
            ClearGeometryPreview();
            _geometryPreviewStrokes = strokes;
            _currentCommitType = CommitReason.CodeInput;
            inkCanvas.Strokes.Add(_geometryPreviewStrokes);
            _currentCommitType = CommitReason.UserInput;
        }

        private void ClearGeometryPreview()
        {
            if (_geometryPreviewStrokes == null || _geometryPreviewStrokes.Count == 0) return;
            _currentCommitType = CommitReason.CodeInput;
            inkCanvas.Strokes.Remove(_geometryPreviewStrokes);
            _currentCommitType = CommitReason.UserInput;
            _geometryPreviewStrokes = null;
        }

        private void CommitGeometryPreview()
        {
            if (_geometryPreviewStrokes == null || _geometryPreviewStrokes.Count == 0) return;
            timeMachine.CommitStrokeUserInputHistory(_geometryPreviewStrokes);
            _geometryPreviewStrokes = null;
        }

        /// <summary>判断事件源是不是某个子件（把柄、关闭按钮），是的话尺身就不该插手。</summary>
        private static bool IsOriginalSourceInside(object source, DependencyObject ancestor)
        {
            var current = source as DependencyObject;
            while (current != null)
            {
                if (ReferenceEquals(current, ancestor)) return true;
                current = VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private static double GetPointerAngleAround(Point center, Point pointer)
        {
            var v = pointer - center;
            return Math.Atan2(v.Y, v.X) * 180 / Math.PI;
        }

        private static double NormalizeAngle(double angle)
        {
            var normalized = angle % 360.0;
            if (normalized < 0) normalized += 360.0;
            return normalized;
        }

        /// <summary>
        /// 计算尺具绕自身旋转中心旋转后，在覆盖层坐标系中相对自身左上角原点 (Left, Top) 的实际占据范围。
        /// </summary>
        /// <param name="width">尺具未旋转时的宽度</param>
        /// <param name="height">尺具未旋转时的高度</param>
        /// <param name="pivotX">旋转中心在尺具内部的 X 坐标</param>
        /// <param name="pivotY">旋转中心在尺具内部的 Y 坐标</param>
        /// <param name="angleDeg">旋转角度</param>
        private static Rect GetRotatedVisualBounds(double width, double height, double pivotX, double pivotY, double angleDeg)
        {
            var rad = angleDeg * Math.PI / 180.0;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            var minX = double.MaxValue;
            var minY = double.MaxValue;
            var maxX = double.MinValue;
            var maxY = double.MinValue;
            foreach (var corner in new[] { new Point(0, 0), new Point(width, 0), new Point(width, height), new Point(0, height) })
            {
                var dx = corner.X - pivotX;
                var dy = corner.Y - pivotY;
                var x = pivotX + (dx * cos) - (dy * sin);
                var y = pivotY + (dx * sin) + (dy * cos);
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        /// <summary>
        /// 按尺具旋转后的实际占据范围约束其位置，使尺具整体保留在“屏幕上看得见的那一块”里。
        /// 夹的是当前可见区而不是整块黑板：黑板比窗口大得多，夹在黑板里就等于允许尺具被拖到屏幕外。
        /// </summary>
        /// <param name="origin">期望的尺具左上角位置</param>
        /// <param name="width">尺具未旋转时的宽度</param>
        /// <param name="height">尺具未旋转时的高度</param>
        /// <param name="pivotX">旋转中心在尺具内部的 X 坐标</param>
        /// <param name="pivotY">旋转中心在尺具内部的 Y 坐标</param>
        /// <param name="angleDeg">旋转角度</param>
        private Point ClampToolOrigin(Point origin, double width, double height, double pivotX, double pivotY, double angleDeg)
        {
            var bounds = GetRotatedVisualBounds(width, height, pivotX, pivotY, angleDeg);
            var boxLeft = ClampAxisKeepingVisible(
                origin.X + bounds.X, bounds.Width, BoardVisibleLeft, BoardVisibleWidth, 0);
            // 纵向再留 80px 给左下角的浮动工具栏，和照片的处理保持一致
            var boxTop = ClampAxisKeepingVisible(
                origin.Y + bounds.Y, bounds.Height, BoardVisibleTop, BoardVisibleHeight, 80);

            return new Point(boxLeft - bounds.X, boxTop - bounds.Y);
        }

        /// <summary>
        /// 沿单个坐标轴约束一整块矩形（尺具/照片旋转后的实际占据范围）的位置，让它留在可见区里。
        /// 装得下就整块留在可见区（和传统的“靠左上”夹法完全一致）；装不下（尺子拉得比屏幕还长、
        /// 照片放得比屏幕还大）就退一步，只要求还有 OversizedMinVisibleSpan 那么宽的一段搭在
        /// 可见区里 —— 否则跨度越大越被顶到可见区的左上角，看着就是“东西自己往左上跑”，
        /// 拖也拖不出来。这个余量以前夹的是“矩形中心还在可见区里”，跨度一大两端就永远落在屏幕外，
        /// 尺子够不着把柄；
        /// 缩尺子时更糟：跨度一变小允许的位置整体右移，一帧就把整把尺子顶过去几千像素，
        /// 把柄直接从指头底下飞走（“一拖尺子右端，整块屏幕跟着跳，尺子右侧就看不见了”）。
        /// 改成只要求搭着边，跳动最多也就是这个余量的量级。
        /// </summary>
        /// <param name="boxLeft">矩形左（上）边缘在可见区坐标系里的位置</param>
        /// <param name="boxSize">矩形在这个轴上的跨度</param>
        /// <param name="visibleLeft">可见区左（上）边缘</param>
        /// <param name="visibleSize">可见区在这个轴上的长度</param>
        /// <param name="reserve">这个方向尾部要空出的余量，例如底部留给浮动工具栏的 80px</param>
        private static double ClampAxisKeepingVisible(double boxLeft, double boxSize, double visibleLeft, double visibleSize, double reserve)
        {
            if (boxSize + reserve <= visibleSize)
            {
                return ClampAxis(boxLeft, visibleLeft, visibleLeft + visibleSize - reserve - boxSize);
            }

            //余量本身比矩形还大（窗口特别小时）就退回“左边缘对齐可见区左边缘”，别把矩形推到屏幕外
            var keep = Math.Min(OversizedMinVisibleSpan, boxSize);
            return ClampAxis(boxLeft, visibleLeft - boxSize + keep, visibleLeft + visibleSize - reserve - keep);
        }

        /// <summary>
        /// 约束单个坐标轴上的位置。装不下时由 ClampAxisKeepingCenterVisible 换算好范围，
        /// 这里只在范围本身不成立（浮点误差）时兜底取靠左上的一侧。
        /// </summary>
        private static double ClampAxis(double value, double min, double max)
        {
            return max < min ? min : Clamp(value, min, max);
        }

        /// <summary>
        /// 按当前旋转角度就地约束尺具位置，使旋转后的尺具仍然完整保留在画布内。
        /// </summary>
        private void KeepToolInsideCanvas(Border tool, double width, double height, double pivotX, double pivotY, double angleDeg)
        {
            var left = WpfCanvas.GetLeft(tool);
            var top = WpfCanvas.GetTop(tool);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            var origin = ClampToolOrigin(new Point(left, top), width, height, pivotX, pivotY, angleDeg);
            WpfCanvas.SetLeft(tool, origin.X);
            WpfCanvas.SetTop(tool, origin.Y);
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}

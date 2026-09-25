using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
//XAML 里有个 Name="Canvas" 的元素，同名成员会把 Canvas 类型遮住，所以跟别的文件一样用别名
using WpfCanvas = System.Windows.Controls.Canvas;

namespace InkCanvasPlus
{
    /// <summary>
    /// 尺具实例：一把尺具 = 一份自己的视觉树 + 一份自己的状态 + 一个自己的容器。
    ///
    /// 以前每样尺具在 XAML 里只有一份定义（RulerBorder / TriangleBorder / ProtractorBorder），
    /// 状态挂在 MainWindow 上，处理器按元素名写死，所以每样只能有一把；画平行线要同时摆两把直尺
    /// 就做不到。现在视觉树改由代码生成（和展台照片、照片手柄的做法一致），外观只有一份定义。
    ///
    /// 每个实例挂在一个 0×0 的 Canvas（Host）里，Host 再挂到 GeometryToolsOverlayCanvas 上：
    /// Canvas 分给子元素的格位就是子元素自己要的尺寸，不会给“比格位还大”的子元素补一个
    /// 跟着走的布局裁切，所以尺子再长也不会被裁掉一块（黑板能整屏书写用的就是同一招）。
    /// Host 摆在 (0,0) 且自己不带变换，于是 Canvas.Left/Top 与 GetPosition(覆盖层) 的坐标含义
    /// 和以前完全一样，旋转中心、夹取、助画的换算一行都不用动。
    ///
    /// 实例之间靠 ZIndex 分层（见 RaiseGeometryTool），不重排 Children：
    /// 正在 CaptureMouse() 的时候重排视觉树，捕获有可能失效。
    /// </summary>
    public partial class MainWindow
    {
        private enum GeometryToolKind { Ruler, Triangle, Protractor }

        //当前开着的尺具。关掉就从这里摘掉，所以“有没有尺具”= 这个表是不是空的
        private readonly List<GeometryToolInstance> _geometryToolInstances = new List<GeometryToolInstance>();
        //置顶计数器：每激活一次加一。涨得太高会重排编号，见 RenumberGeometryToolZOrder
        private int _geometryToolTopZ = GeometryToolZIndexBase - 1;
        //正在沿哪把尺具的边画线。同一时刻只可能有一个（鼠标只有一路）
        private GeometryToolInstance _assistInstance;

        // ---- 原来写在 XAML 里的字面量，现在只有这一份定义 ----
        private static readonly Brush GeometryRulerShellStrokeBrush = FrozenBrush(Color.FromArgb(0xA6, 0x1F, 0x2A, 0x40));
        private static readonly Brush GeometryRulerShellFillBrush = FrozenBrush(Color.FromArgb(0x26, 0xE8, 0xE8, 0xE8));
        private static readonly Brush GeometryRulerBarBrush = FrozenBrush(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A));
        private static readonly Brush GeometryResizeHandleBrush = FrozenBrush(Color.FromArgb(0x55, 0x4A, 0x4A, 0x4A));
        private static readonly Brush GeometryToolHandleBrush = FrozenBrush(Color.FromArgb(0xFF, 0x6D, 0x52, 0xBD));
        private static readonly Brush GeometryCloseButtonForegroundBrush = FrozenBrush(Color.FromArgb(0xFF, 0xD9, 0x35, 0x35));
        private static readonly Brush GeometryCloseButtonBackgroundBrush = FrozenBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        private static readonly Brush GeometryPivotDotFillBrush = FrozenBrush(Color.FromArgb(0xFF, 0xD3, 0x4A, 0x4A));
        private static readonly Brush GeometryPivotDotStrokeBrush = FrozenBrush(Color.FromArgb(0xFF, 0x8A, 0x20, 0x20));
        private static readonly Brush GeometryTriangleFillBrush = FrozenBrush(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));
        private static readonly Brush GeometryToolOutlineBrush = FrozenBrush(Color.FromArgb(0xFF, 0x4E, 0x55, 0x62));
        private static readonly Brush GeometryProtractorFillBrush = FrozenBrush(Color.FromArgb(0x26, 0xD9, 0xE0, 0xEA));
        private static readonly Brush GeometryProtractorMarkBrush = FrozenBrush(Color.FromArgb(0xFF, 0x2A, 0x6A, 0xD9));

        private static SolidColorBrush FrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>
        /// 工具栏点一次就新建一把（老师选的是“每次都新建”），落点按同类已有实例数错开，
        /// 免得连点几次完全重叠看不出来；绕满一圈（6 次）回到原位。
        /// </summary>
        private GeometryToolInstance CreateGeometryTool(GeometryToolKind kind)
        {
            GeometryToolInstance instance;
            switch (kind)
            {
                case GeometryToolKind.Triangle: instance = new TriangleInstance(this); break;
                case GeometryToolKind.Protractor: instance = new ProtractorInstance(this); break;
                default: instance = new RulerInstance(this); break;
            }

            var cascade = CascadeOffsetFor(kind);
            _geometryToolInstances.Add(instance);
            instance.Attach(cascade);
            RaiseGeometryTool(instance);
            return instance;
        }

        private Point CascadeOffsetFor(GeometryToolKind kind)
        {
            var count = 0;
            foreach (var instance in _geometryToolInstances)
            {
                if (instance.Kind == kind) count++;
            }

            var step = (count % GeometryToolCascadeWrap) * GeometryToolCascadeStep;
            return new Point(step, step);
        }

        /// <summary>
        /// 把一把尺具提到最上层。用 ZIndex 而不是把 Host 挪到 Children 末尾：
        /// 尺具与它的把柄可能正拿着鼠标捕获，重排视觉树会让捕获失效。
        /// </summary>
        private void RaiseGeometryTool(GeometryToolInstance instance)
        {
            if (instance == null) return;
            if (_geometryToolTopZ >= GeometryToolZIndexBase + 64) RenumberGeometryToolZOrder();
            _geometryToolTopZ++;
            Panel.SetZIndex(instance.Host, _geometryToolTopZ);
        }

        /// <summary>
        /// 按原来的高低顺序把 ZIndex 重编成连续的一段。只在关掉实例、或编号涨得太高时调用，
        /// 单独置顶走 RaiseGeometryTool，不必每次重排。
        /// </summary>
        private void RenumberGeometryToolZOrder()
        {
            var ordered = new List<GeometryToolInstance>(_geometryToolInstances);
            ordered.Sort((a, b) => Panel.GetZIndex(a.Host).CompareTo(Panel.GetZIndex(b.Host)));
            for (var i = 0; i < ordered.Count; i++)
            {
                Panel.SetZIndex(ordered[i].Host, GeometryToolZIndexBase + i);
            }

            _geometryToolTopZ = GeometryToolZIndexBase + ordered.Count - 1;
        }

        /// <summary>关掉一把尺具：它的容器（连同量角器自己的定点标记）一起摘掉。</summary>
        private void CloseGeometryTool(GeometryToolInstance instance)
        {
            if (instance == null) return;
            //正在沿它画线的话，那条预览线跟着丢掉，别留在板上
            EndGeometryAssist(instance, false);
            instance.Detach();
            _geometryToolInstances.Remove(instance);
            RenumberGeometryToolZOrder();
        }

        /// <summary>
        /// 开始助画（沿尺边画线）。找不到合适的边就返回 false，让事件继续往下走。
        /// only 不为空时只看这一个实例 —— 按在某把尺子上就该沿它画，不能跑去沿另一把画。
        /// </summary>
        private bool TryBeginGeometryAssist(Point inkPoint, GeometryToolInstance only)
        {
            if (!IsGeometryDrawingAvailable()) return false;

            //取离得最近的那条边；一样近的时候取更靠上的那把（后开的那个）
            GeometryToolInstance best = null;
            var bestDistance = double.MaxValue;
            var bestZ = int.MinValue;
            foreach (var instance in _geometryToolInstances)
            {
                if (only != null && !ReferenceEquals(instance, only)) continue;
                var distance = instance.AssistDistance(inkPoint);
                if (distance > GeometryAssistTolerance) continue;
                var z = Panel.GetZIndex(instance.Host);
                if (best == null || distance < bestDistance || (distance == bestDistance && z > bestZ))
                {
                    best = instance;
                    bestDistance = distance;
                    bestZ = z;
                }
            }

            if (best == null || !best.BeginAssist(inkPoint)) return false;

            _assistInstance = best;
            inkCanvas.CaptureMouse();
            return true;
        }

        /// <summary>
        /// 结束助画。commit 为 true 时把预览线留下（成为正式笔迹），否则擦掉。
        /// 预览墨迹是全局共享的一条，所以只由“正在助画的那个实例”来收尾。
        /// </summary>
        private void EndGeometryAssist(GeometryToolInstance instance)
        {
            EndGeometryAssist(instance, true);
        }

        private void EndGeometryAssist(GeometryToolInstance instance, bool commit)
        {
            if (instance != null) instance.CancelAssist();
            if (_assistInstance == null) return;
            if (instance != null && !ReferenceEquals(_assistInstance, instance)) return;

            _assistInstance = null;
            if (commit) CommitGeometryPreview();
            else ClearGeometryPreview();
            inkCanvas.ReleaseMouseCapture();
        }

        /// <summary>清掉所有开着的量角器的定点标记（清屏时用，理由同展台照片：都是这一页的内容）。</summary>
        private void ClearProtractorMarks()
        {
            foreach (var instance in _geometryToolInstances)
            {
                var protractor = instance as ProtractorInstance;
                if (protractor != null) protractor.ClearMarks();
            }
        }

        /// <summary>
        /// 按当前可见区把所有尺具的刻度重画一遍。窗口变大变小、黑板挪了地方都得来一次：
        /// 刻度是按可见区裁出来的（尺子可以不限长），可见区一换位置，裁出来的那一段就作废了。
        /// </summary>
        private void RebuildAllGeometryToolTicks()
        {
            foreach (var instance in _geometryToolInstances) instance.RebuildTicks();
        }

        /// <summary>
        /// 一把尺具的全部状态与行为。旋转中心、坐标换算、夹取、助画都在这里，
        /// 子类只管自己的视觉树和几何。
        /// </summary>
        private abstract class GeometryToolInstance
        {
            protected readonly MainWindow Owner;
            /// <summary>本实例的容器：0×0 的 Canvas。见类头注释，别给它设尺寸、也别换成 Grid。</summary>
            internal readonly WpfCanvas Host;
            /// <summary>尺具本体（原 XAML 里的 xxxBorder）。</summary>
            protected Border Shell;

            internal readonly GeometryToolKind Kind;
            internal double AngleDeg;

            protected bool IsDragging, IsDragPending, IsRotating, IsResizing;
            /// <summary>正在沿这把尺具的边助画。</summary>
            protected bool IsAssisting;
            protected Point DragOffset, LastMousePoint, PressPoint;
            protected DateTime DragPressUtc;

            protected GeometryToolInstance(MainWindow owner, GeometryToolKind kind)
            {
                Owner = owner;
                Kind = kind;
                Host = new WpfCanvas { ClipToBounds = false, Width = 0, Height = 0 };
            }

            /// <summary>旋转中心在尺具本地坐标（相对尺具左上角）里的位置。</summary>
            protected abstract Point PivotLocal { get; }

            /// <summary>造视觉树、定尺寸。事件也在这里挂，别的地方不碰外观。</summary>
            protected abstract void BuildVisualTree();

            /// <summary>重建随主题 / 尺寸 / 可见区变化的刻度（子件位置和尺寸的更新也走这里）。</summary>
            internal abstract void RebuildTicks();

            /// <summary>摆到当前可见区中心。cascade 是同类型实例之间的错位量。</summary>
            internal abstract void PlaceAtVisibleCenter(Point cascade);

            /// <summary>鼠标离“可用于助画的边”有多远，超过容差不参与助画（返回 double.MaxValue）。</summary>
            internal abstract double AssistDistance(Point inkPoint);

            /// <summary>开始助画，顺便画一遍预览。返回 false 表示这个位置其实画不了。</summary>
            internal abstract bool BeginAssist(Point inkPoint);

            internal abstract void UpdateAssistPreview(Point inkPoint);

            internal void CancelAssist() { IsAssisting = false; }

            /// <summary>尺具本体的宽高（未旋转）。三个尺具的宽高都是显式设的，直接取就行。</summary>
            protected Size ShellSize { get { return new Size(Shell.Width, Shell.Height); } }

            /// <summary>是不是有限数（NaN、±Infinity 都算“不可用”）。</summary>
            protected static bool IsFinite(double value)
            {
                return !double.IsNaN(value) && !double.IsInfinity(value);
            }

            protected Point ShellOrigin
            {
                get
                {
                    var left = WpfCanvas.GetLeft(Shell);
                    var top = WpfCanvas.GetTop(Shell);
                    if (double.IsNaN(left)) left = 0;
                    if (double.IsNaN(top)) top = 0;
                    return new Point(left, top);
                }
            }

            /// <summary>旋转中心在覆盖层坐标里的位置。旋转中心是旋转的不动点，所以和角度无关。</summary>
            internal Point PivotWorld
            {
                get
                {
                    var origin = ShellOrigin;
                    var local = PivotLocal;
                    return new Point(origin.X + local.X, origin.Y + local.Y);
                }
            }

            /// <summary>尺身轴线方向（未旋转时指向右）。</summary>
            protected Vector AxisX
            {
                get
                {
                    var rad = AngleDeg * Math.PI / 180.0;
                    return new Vector(Math.Cos(rad), Math.Sin(rad));
                }
            }

            protected Vector AxisY
            {
                get
                {
                    var x = AxisX;
                    return new Vector(-x.Y, x.X);
                }
            }

            /// <summary>覆盖层坐标 → 以旋转中心为原点的尺具本地坐标。</summary>
            internal Point LocalFromWorld(Point world)
            {
                var v = world - PivotWorld;
                var ax = AxisX;
                var ay = AxisY;
                return new Point(v.X * ax.X + v.Y * ax.Y, v.X * ay.X + v.Y * ay.Y);
            }

            /// <summary>以旋转中心为原点的尺具本地坐标 → 覆盖层坐标。</summary>
            internal Point WorldFromLocal(Point local)
            {
                var ax = AxisX;
                var ay = AxisY;
                return PivotWorld + ax * local.X + ay * local.Y;
            }

            protected void ApplyTransform()
            {
                var pivot = PivotLocal;
                Shell.RenderTransform = new RotateTransform(AngleDeg, pivot.X, pivot.Y);
            }

            /// <summary>
            /// 按当前旋转角度就地约束位置，使旋转后的尺具仍留在可见区里。
            /// 返回是否真的挪动过 —— 位置变了刻度就得重算（它按可见区裁剪）。
            /// 注意尺寸取的是 Shell 的当前宽高，所以要先更新尺寸再调这里。
            /// </summary>
            internal bool KeepInside()
            {
                var before = ShellOrigin;
                var size = ShellSize;
                var pivot = PivotLocal;
                Owner.KeepToolInsideCanvas(Shell, size.Width, size.Height, pivot.X, pivot.Y, AngleDeg);
                var after = ShellOrigin;
                return after.X != before.X || after.Y != before.Y;
            }

            /// <summary>把旋转中心挪到指定的覆盖层坐标（带可见区夹取）。</summary>
            internal void MovePivotTo(Point pivotWorld)
            {
                var local = PivotLocal;
                var size = ShellSize;
                var origin = Owner.ClampToolOrigin(new Point(pivotWorld.X - local.X, pivotWorld.Y - local.Y),
                    size.Width, size.Height, local.X, local.Y, AngleDeg);
                WpfCanvas.SetLeft(Shell, origin.X);
                WpfCanvas.SetTop(Shell, origin.Y);
            }

            /// <summary>
            /// 把当前可见区的四角投到尺具本地坐标里，得到可见范围的包围盒。
            /// 是真实可见范围的上界（只会大不会小），拿它裁剪刻度永远不会漏画；
            /// 可见区还不知道时（还没布局）返回 false，调用方就按整把尺子画。
            ///
            /// 只要有一步算出来不是有限数（角度被算成 NaN、可见区还没定下来……），也返回 false。
            /// 这种时候四角的投影全是 NaN，下面那对 min/max 只会停在 ±MaxValue 两个哨兵值上，
            /// 裁出来的区间就是空的 —— 表现正是"上下线和中线都在，刻度和数值一段都不显示"。
            /// 宁可多画一整把尺子的刻度，也不能少画看得见的那一段。
            /// </summary>
            protected bool TryGetVisibleAxisBounds(out Point min, out Point max)
            {
                min = new Point(0, 0);
                max = new Point(0, 0);
                var w = Owner.BoardVisibleWidth;
                var h = Owner.BoardVisibleHeight;
                var left = Owner.BoardVisibleLeft;
                var top = Owner.BoardVisibleTop;
                if (!IsFinite(w) || !IsFinite(h) || w <= 0 || h <= 0) return false;
                if (!IsFinite(left) || !IsFinite(top)) return false;

                var lo = new Point(double.MaxValue, double.MaxValue);
                var hi = new Point(double.MinValue, double.MinValue);
                var corners = new[]
                {
                    new Point(left, top),
                    new Point(left + w, top),
                    new Point(left + w, top + h),
                    new Point(left, top + h),
                };
                foreach (var corner in corners)
                {
                    var local = LocalFromWorld(corner);
                    if (!IsFinite(local.X) || !IsFinite(local.Y)) return false;
                    if (local.X < lo.X) lo.X = local.X;
                    if (local.Y < lo.Y) lo.Y = local.Y;
                    if (local.X > hi.X) hi.X = local.X;
                    if (local.Y > hi.Y) hi.Y = local.Y;
                }

                min = lo;
                max = hi;
                return true;
            }

            /// <summary>
            /// 挂到覆盖层上：造树、进容器、落到可见区中心、画刻度。
            /// 刻度要画两遍是因为它按可见区裁剪，得等落位之后才知道该画哪一段。
            /// </summary>
            internal void Attach(Point cascade)
            {
                BuildVisualTree();
                Host.Children.Add(Shell);
                Owner.GeometryToolsOverlayCanvas.Children.Add(Host);
                PlaceAtVisibleCenter(cascade);
                RebuildTicks();
            }

            /// <summary>从覆盖层上摘掉自己。</summary>
            internal void Detach()
            {
                Shell.ReleaseMouseCapture();
                Host.Children.Clear();
                Owner.GeometryToolsOverlayCanvas.Children.Remove(Host);
                OnDetached();
            }

            /// <summary>子类清理挂在自己身上的额外层（例如量角器的定点标记层）。</summary>
            protected virtual void OnDetached() { }

            /// <summary>把“按在关闭按钮上”之前的拖拽状态收回来。关掉就不用管了。</summary>
            protected void CancelPointerState()
            {
                IsDragging = false;
                IsDragPending = false;
            }

            /// <summary>关闭按钮：三个尺具长得一样，只有 ToolTip 不同。</summary>
            protected Button CreateCloseButton(string tooltip)
            {
                var button = new Button
                {
                    Width = 24,
                    Height = 24,
                    Content = "X",
                    Foreground = GeometryCloseButtonForegroundBrush,
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    ToolTip = tooltip,
                };
                button.Click += (sender, e) => Owner.CloseGeometryTool(this);
                //以前是三个尺具共用一句“把所有人的拖拽状态清零”，现在各管各的
                button.PreviewMouseLeftButtonDown += (sender, e) => CancelPointerState();
                return button;
            }

            /// <summary>旋转把柄：圆形，按住绕旋转中心转。</summary>
            protected Border CreateRotateHandle(double size, double radius, double fontSize)
            {
                var handle = new Border
                {
                    Width = size,
                    Height = size,
                    Background = GeometryToolHandleBrush,
                    CornerRadius = new CornerRadius(radius),
                    Cursor = Cursors.Hand,
                    ToolTip = "按住旋转",
                    Child = new TextBlock
                    {
                        Text = "↻",
                        Foreground = Brushes.White,
                        FontSize = fontSize,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                return handle;
            }

            /// <summary>旋转中心的小红点。</summary>
            protected Ellipse CreatePivotDot()
            {
                return new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = GeometryPivotDotFillBrush,
                    Stroke = GeometryPivotDotStrokeBrush,
                    StrokeThickness = 1.2,
                };
            }
        }

        /// <summary>直尺：拖动中段移动、转把柄旋转、右端把柄拉长（长度不设上限）、滚轮旋转。</summary>
        private sealed class RulerInstance : GeometryToolInstance
        {
            private WpfCanvas _ticks;
            private Button _close;
            private Border _rotateHandle, _resizeHandle;

            internal double Width = GeometryRulerDefaultWidth;

            private double _rotateOffsetDeg;
            private double _assistStartT, _assistOffset;

            internal RulerInstance(MainWindow owner) : base(owner, GeometryToolKind.Ruler) { }

            private double HalfHeight { get { return GeometryRulerHeight / 2.0; } }

            //旋转中心在左中点。注意别再去设 RenderTransformOrigin：两种机制混用，
            //换算出来的圆心会和实际画出来的对不上，拖拽、夹取、助画会整片错位。
            protected override Point PivotLocal { get { return new Point(0, HalfHeight); } }

            protected override void BuildVisualTree()
            {
                Shell = new Border
                {
                    Width = Width,
                    Height = GeometryRulerHeight,
                    Cursor = Cursors.Pen,
                    CornerRadius = new CornerRadius(8),
                    BorderBrush = GeometryRulerShellStrokeBrush,
                    BorderThickness = new Thickness(1.2),
                    Background = GeometryRulerShellFillBrush,
                };
                Shell.MouseLeftButtonDown += Shell_MouseLeftButtonDown;
                Shell.MouseMove += Shell_MouseMove;
                Shell.MouseLeftButtonUp += Shell_MouseLeftButtonUp;
                Shell.MouseWheel += Shell_MouseWheel;

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GeometryRulerTickLeftInset) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(GeometryRulerTickRightInset) });

                _close = CreateCloseButton("取消直尺");
                _close.Margin = new Thickness(8, 0, 0, 0);
                _close.HorizontalAlignment = HorizontalAlignment.Left;
                _close.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(_close, 0);
                grid.Children.Add(_close);

                _ticks = new WpfCanvas { Margin = new Thickness(0, 8, 0, 8) };
                Grid.SetColumn(_ticks, 1);
                grid.Children.Add(_ticks);

                var pivotDot = CreatePivotDot();
                pivotDot.HorizontalAlignment = HorizontalAlignment.Left;
                pivotDot.VerticalAlignment = VerticalAlignment.Center;
                pivotDot.Margin = new Thickness(-5, 0, 0, 0);
                pivotDot.ToolTip = "旋转中心点";
                grid.Children.Add(pivotDot);

                //右端那条深色端条整条都能当“改长度”的把手：原来只有上面那个 18×30 的小方块能拖，
                //手指点上去差个十几像素就落到墨迹画布上，于是变成拖黑板 —— 屏幕跟着手指走、
                //尺子右端（连同把柄）一起滑出屏幕，就再也拖不动长度了。
                //两个把柄各自的事件在前，按到它们还是原本的行为（用 IsOriginalSourceInside 判断）
                var bar = new Border
                {
                    Background = GeometryRulerBarBrush,
                    CornerRadius = new CornerRadius(0, 6, 6, 0),
                    Cursor = Cursors.SizeWE,
                    ToolTip = "按住调整长度",
                };
                bar.MouseLeftButtonDown += ResizeHandle_MouseLeftButtonDown;
                bar.MouseMove += ResizeHandle_MouseMove;
                bar.MouseLeftButtonUp += ResizeHandle_MouseLeftButtonUp;
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };

                _rotateHandle = CreateRotateHandle(22, 11, 13);
                _rotateHandle.Margin = new Thickness(0, 8, 0, 6);
                _rotateHandle.MouseLeftButtonDown += RotateHandle_MouseLeftButtonDown;
                _rotateHandle.MouseMove += RotateHandle_MouseMove;
                _rotateHandle.MouseLeftButtonUp += RotateHandle_MouseLeftButtonUp;
                stack.Children.Add(_rotateHandle);

                _resizeHandle = new Border
                {
                    Width = 18,
                    Height = 30,
                    Margin = new Thickness(0, 4, 0, 8),
                    CornerRadius = new CornerRadius(4),
                    Background = GeometryResizeHandleBrush,
                    Cursor = Cursors.SizeWE,
                    ToolTip = "按住调整长度",
                    Child = new TextBlock
                    {
                        Text = "⋮",
                        Foreground = Brushes.White,
                        FontSize = 14,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                _resizeHandle.MouseLeftButtonDown += ResizeHandle_MouseLeftButtonDown;
                _resizeHandle.MouseMove += ResizeHandle_MouseMove;
                _resizeHandle.MouseLeftButtonUp += ResizeHandle_MouseLeftButtonUp;
                stack.Children.Add(_resizeHandle);

                bar.Child = stack;
                Grid.SetColumn(bar, 2);
                grid.Children.Add(bar);

                Shell.Child = grid;
                RebuildTicks();
            }

            internal override void RebuildTicks()
            {
                if (_ticks == null) return;

                //壳宽是这里唯一的同步点，跟三角尺/量角器一样。只改刻度画布的宽度是不够的：
                //中间那一列的格位还是老宽度，画布整个溢出格位（Canvas 不裁子元素，所以刻度看起来
                //一直长出去），而右端那截深色条和两个把柄留在原地 —— 就是"刻度长出来了、尺子没变长"。
                //顺带一提，Shell.Width 还是 ShellSize 的来源，拖动、夹取、贴边助画都按它算。
                Shell.Width = Width;

                var width = Math.Max(80.0, Width - GeometryRulerTickLeftInset - GeometryRulerTickRightInset);
                var height = Math.Max(40.0, GeometryRulerHeight - 16);
                _ticks.Width = width;
                _ticks.Height = height;
                _ticks.Children.Clear();

                var brush = Owner.GeometryToolTickBrush;
                var centerY = height / 2.0;
                _ticks.Children.Add(new Line { X1 = 0, Y1 = 2, X2 = width, Y2 = 2, Stroke = brush, StrokeThickness = 1 });
                _ticks.Children.Add(new Line { X1 = 0, Y1 = height - 2, X2 = width, Y2 = height - 2, Stroke = brush, StrokeThickness = 1 });
                _ticks.Children.Add(new Line
                {
                    X1 = 0,
                    Y1 = centerY,
                    X2 = width,
                    Y2 = centerY,
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeDashArray = new DoubleCollection { 8, 4 },
                    Opacity = 0.9
                });

                //长度不设上限，一把几万像素的尺子不可能把刻度全画出来，只画看得见的那一段。
                //首尾都吸附回 10px 的格子并夹进 [0, 最右]，所以尺子完整可见时和不裁剪逐字节相同
                var first = 0.0;
                var last = Math.Floor(width / 10.0) * 10.0;
                if (TryGetVisibleAxisBounds(out var visibleMin, out var visibleMax))
                {
                    var lo = visibleMin.X - GeometryRulerTickLeftInset - GeometryTickCullMargin;
                    var hi = visibleMax.X - GeometryRulerTickLeftInset + GeometryTickCullMargin;
                    first = Clamp(Math.Floor(lo / 10.0) * 10.0, 0, last);
                    //下界取 first 而不是 0：可见区万一算到尺子外面去，区间也不会反过来变成空区间
                    //（空区间一根刻度都不画，正是"只显示上下线和中线"的样子）
                    last = Clamp(Math.Ceiling(hi / 10.0) * 10.0, first, last);
                }

                var drawn = 0;
                for (var x = first; x <= last && drawn < GeometryTickMaxPerDraw; x += 10.0, drawn++)
                {
                    var index = (int)Math.Round(x / 10.0);
                    var major = index % 5 == 0;
                    var mid = !major && index % 2 == 0;
                    var len = major ? 24 : (mid ? 17 : 11);
                    _ticks.Children.Add(new Line { X1 = x, Y1 = 2, X2 = x, Y2 = 2 + len, Stroke = brush, StrokeThickness = major ? 1.2 : 1 });
                    _ticks.Children.Add(new Line { X1 = x, Y1 = height - 2, X2 = x, Y2 = height - 2 - len, Stroke = brush, StrokeThickness = major ? 1.2 : 1 });
                    if (!major) continue;
                    var label = new TextBlock { Text = (index / 5).ToString(), FontSize = 11, Foreground = brush };
                    WpfCanvas.SetLeft(label, x + 3);
                    WpfCanvas.SetTop(label, centerY - 8);
                    _ticks.Children.Add(label);
                }
            }

            internal override void PlaceAtVisibleCenter(Point cascade)
            {
                //落在当前可见区正中。黑板画布比窗口大得多，摆到画布正中就摆到屏幕外面去了
                var left = Owner.BoardVisibleLeft + Math.Max(0, (Owner.BoardVisibleWidth - Width) / 2.0) + cascade.X;
                var top = Owner.BoardVisibleTop + Math.Max(0, (Owner.BoardVisibleHeight - GeometryRulerHeight) / 2.0) + cascade.Y;
                AngleDeg = 0;
                var origin = Owner.ClampToolOrigin(new Point(left, top), Width, GeometryRulerHeight, 0, HalfHeight, AngleDeg);
                WpfCanvas.SetLeft(Shell, origin.X);
                WpfCanvas.SetTop(Shell, origin.Y);
                ApplyTransform();
            }

            private bool IsInDragBand(Point overlayPoint)
            {
                var local = LocalFromWorld(overlayPoint);
                return local.X >= GeometryRulerDragLeftInset && local.X <= Width - GeometryRulerDragRightInset &&
                       Math.Abs(local.Y) <= 10;
            }

            private bool IsNearDrawingEdge(Point overlayPoint)
            {
                var local = LocalFromWorld(overlayPoint);
                return local.X >= GeometryRulerDragLeftInset && local.X <= Width - GeometryRulerDragRightInset &&
                       Math.Abs(Math.Abs(local.Y) - HalfHeight) <= GeometryAssistTolerance;
            }

            private double GetPointerAngle(Point pointer)
            {
                return GetPointerAngleAround(PivotWorld, pointer);
            }

            internal override double AssistDistance(Point inkPoint)
            {
                if (!Owner.IsGeometryDrawingAvailable()) return double.MaxValue;
                var local = LocalFromWorld(inkPoint);
                if (local.X < GeometryRulerDragLeftInset || local.X > Width - GeometryRulerDragRightInset) return double.MaxValue;
                return Math.Abs(Math.Abs(local.Y) - HalfHeight);
            }

            internal override bool BeginAssist(Point inkPoint)
            {
                if (AssistDistance(inkPoint) > GeometryAssistTolerance) return false;
                var local = LocalFromWorld(inkPoint);
                IsAssisting = true;
                _assistStartT = Clamp(local.X, GeometryRulerDragLeftInset, Width - GeometryRulerDragRightInset);
                //贴着上边还是下边：取外沿那条边
                _assistOffset = local.Y < 0 ? -HalfHeight : HalfHeight;
                UpdateAssistPreview(inkPoint);
                return true;
            }

            internal override void UpdateAssistPreview(Point inkPoint)
            {
                var local = LocalFromWorld(inkPoint);
                var axis = AxisX;
                var normal = new Vector(-axis.Y, axis.X);
                var pivot = PivotWorld;
                var endT = Clamp(local.X, GeometryRulerDragLeftInset, Width - GeometryRulerDragRightInset);
                Owner.ReplaceGeometryPreview(Owner.CreateLineStrokeCollection(
                    pivot + axis * _assistStartT + normal * _assistOffset,
                    pivot + axis * endT + normal * _assistOffset));
            }

            private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                //碰哪把哪把到最上层（和窗口激活一个道理）。放在最前面，按在把柄上也一样生效
                Owner.RaiseGeometryTool(this);

                if (IsOriginalSourceInside(e.OriginalSource, _close) ||
                    IsOriginalSourceInside(e.OriginalSource, _rotateHandle) ||
                    IsOriginalSourceInside(e.OriginalSource, _resizeHandle))
                {
                    return;
                }
                if (IsRotating || IsResizing) return;

                var overlayPoint = Owner.OverlayPointFrom(e);
                if (IsNearDrawingEdge(overlayPoint) && Owner.TryBeginGeometryAssist(Owner.InkPointFrom(e), this))
                {
                    e.Handled = true;
                    return;
                }

                if (!IsInDragBand(overlayPoint)) return;
                IsDragPending = true;
                DragPressUtc = DateTime.UtcNow;
                IsDragging = false;
                var origin = ShellOrigin;
                DragOffset = new Point(overlayPoint.X - origin.X, overlayPoint.Y - origin.Y);
                Shell.CaptureMouse();
                e.Handled = true;
            }

            private void Shell_MouseMove(object sender, MouseEventArgs e)
            {
                if (IsAssisting)
                {
                    if (e.LeftButton == MouseButtonState.Pressed)
                    {
                        UpdateAssistPreview(Owner.InkPointFrom(e));
                        e.Handled = true;
                    }
                    return;
                }

                if (IsRotating || IsResizing) return;
                if (e.LeftButton != MouseButtonState.Pressed)
                {
                    IsDragPending = false;
                    return;
                }

                //按下之后要停一下才开始拖，免得点一下就把尺子挪偏
                if (IsDragPending && !IsDragging)
                {
                    if ((DateTime.UtcNow - DragPressUtc).TotalMilliseconds < 220) return;
                    IsDragging = true;
                    IsDragPending = false;
                }
                if (!IsDragging) return;

                var p = Owner.OverlayPointFrom(e);
                var origin = Owner.ClampToolOrigin(new Point(p.X - DragOffset.X, p.Y - DragOffset.Y),
                    Width, GeometryRulerHeight, 0, HalfHeight, AngleDeg);
                WpfCanvas.SetLeft(Shell, origin.X);
                WpfCanvas.SetTop(Shell, origin.Y);
                //刻度是"按当前可见区裁出尺身上那一段"再画的，尺子一挪这段就作废了：
                //不重画的话，挪到某个位置就会有整段刻度画不出来，挪回去又自己好了
                RebuildTicks();
                e.Handled = true;
            }

            private void Shell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                if (IsAssisting)
                {
                    Owner.EndGeometryAssist(this, true);
                    e.Handled = true;
                    return;
                }

                IsDragPending = false;
                IsDragging = false;
                Shell.ReleaseMouseCapture();
                e.Handled = true;
            }

            private void Shell_MouseWheel(object sender, MouseWheelEventArgs e)
            {
                //把柄在右端，尺子比屏幕长的时候够不着，滚轮就地转（和量角器同一套手感）
                var step = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 1.0 : 0.1;
                AngleDeg += e.Delta > 0 ? step : -step;
                ApplyTransform();
                KeepInside();
                //转完可见区落在尺身上的那一段就变了，刻度得按新角度重画
                RebuildTicks();
                e.Handled = true;
            }

            private void RotateHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                IsRotating = true;
                LastMousePoint = Owner.OverlayPointFrom(e);
                _rotateOffsetDeg = GetPointerAngle(LastMousePoint) - AngleDeg;
                _rotateHandle.CaptureMouse();
                e.Handled = true;
            }

            private void RotateHandle_MouseMove(object sender, MouseEventArgs e)
            {
                if (!IsRotating || e.LeftButton != MouseButtonState.Pressed) return;
                var pointer = Owner.OverlayPointFrom(e);
                AngleDeg = GetPointerAngle(pointer) - _rotateOffsetDeg;
                ApplyTransform();
                KeepInside();
                //同上：角度变了刻度裁剪的那一段就变了
                RebuildTicks();
                _rotateOffsetDeg = GetPointerAngle(pointer) - AngleDeg;
                e.Handled = true;
            }

            private void RotateHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                IsRotating = false;
                _rotateHandle.ReleaseMouseCapture();
                e.Handled = true;
            }

            private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                IsResizing = true;
                LastMousePoint = Owner.OverlayPointFrom(e);
                //捕获交给按下的那个元素：把柄和整条端条都挂着这三个处理器（见 BuildVisualTree），
                //从哪个上按下去，后面的移动事件就回到哪个上
                (sender as UIElement)?.CaptureMouse();
                e.Handled = true;
            }

            private void ResizeHandle_MouseMove(object sender, MouseEventArgs e)
            {
                if (!IsResizing || e.LeftButton != MouseButtonState.Pressed) return;
                var p = Owner.OverlayPointFrom(e);
                var delta = p - LastMousePoint;
                LastMousePoint = p;
                var axis = AxisX;
                var along = (delta.X * axis.X) + (delta.Y * axis.Y);
                if (Math.Abs(along) < 0.1) return;
                //只保留下限，不设上限：尺子能拉多长就拉多长（刻度按可见区裁剪，长了也不卡）
                Width = Math.Max(GeometryRulerMinWidth, Width + along);
                ApplyTransform();
                RebuildTicks();
                //尺子比屏幕还长的时候旋转中心（左端）多半在屏幕外，变短就是整把尺子朝那边缩，
                //不夹一下会一路缩出屏幕。只在变短时夹：变长时右端跟着鼠标、本来就在屏幕里，
                //夹了反而会跟手感打架。夹的是旋转后的实际范围，尺子装得下时和不夹逐字节相同。
                //（尺子装不下时那种夹法只要求"还有一段搭在可见区里"，位置顶多挪动一二百像素，
                //  不会把右端的把柄从手指底下甩到屏幕外去，见 ClampAxisKeepingVisible）
                //位置真被夹动了就再画一遍刻度：它是按可见区裁出来的一段，位置变了这段就作废了
                if (along < 0 && KeepInside()) RebuildTicks();
                e.Handled = true;
            }

            private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                IsResizing = false;
                //和按下时对称：释放那个真正拿着捕获的元素
                (sender as UIElement)?.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        /// <summary>三角尺：两条直角边都能拉长（不设上限）、转把柄旋转、滚轮旋转、沿边助画。</summary>
        private sealed class TriangleInstance : GeometryToolInstance
        {
            private WpfCanvas _canvas, _ticks;
            private Polygon _polygon;
            private Button _close;
            private Border _rotateHandle, _vertexB, _vertexC;

            internal double LegX = GeometryTriangleDefaultLegX;
            internal double LegY = GeometryTriangleDefaultLegY;

            private double _rotateOffsetDeg;
            private GeometryTriangleEdgeType _assistEdge;
            private double _assistStartParam;

            internal TriangleInstance(MainWindow owner) : base(owner, GeometryToolKind.Triangle) { }

            protected override Point PivotLocal
            {
                get { return new Point(GeometryTrianglePivotLocal, GeometryTrianglePivotLocal); }
            }

            protected override void BuildVisualTree()
            {
                Shell = new Border
                {
                    Width = 330,
                    Height = 240,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Pen,
                };
                Shell.MouseLeftButtonDown += Shell_MouseLeftButtonDown;
                Shell.MouseMove += Shell_MouseMove;
                Shell.MouseLeftButtonUp += Shell_MouseLeftButtonUp;
                Shell.MouseWheel += Shell_MouseWheel;

                _canvas = new WpfCanvas();
                _polygon = new Polygon
                {
                    Fill = GeometryTriangleFillBrush,
                    Stroke = GeometryToolOutlineBrush,
                    StrokeThickness = 2,
                };
                _canvas.Children.Add(_polygon);

                _close = CreateCloseButton("取消三角尺");
                _close.Background = GeometryCloseButtonBackgroundBrush;
                _close.BorderBrush = GeometryCloseButtonBackgroundBrush;
                _close.BorderThickness = new Thickness(1);
                _canvas.Children.Add(_close);

                //刻度只用来显示，别抢鼠标事件
                _ticks = new WpfCanvas { IsHitTestVisible = false };
                _canvas.Children.Add(_ticks);

                _rotateHandle = CreateRotateHandle(24, 12, 13);
                _rotateHandle.MouseLeftButtonDown += RotateHandle_MouseLeftButtonDown;
                _rotateHandle.MouseMove += RotateHandle_MouseMove;
                _rotateHandle.MouseLeftButtonUp += RotateHandle_MouseLeftButtonUp;
                _canvas.Children.Add(_rotateHandle);

                //两个直角边端点：把柄做得比看得见的大一圈，好抓
                _vertexB = CreateVertexHandle(Cursors.SizeWE);
                _vertexB.MouseLeftButtonDown += VertexB_MouseLeftButtonDown;
                _vertexB.MouseMove += VertexB_MouseMove;
                _vertexB.MouseLeftButtonUp += VertexB_MouseLeftButtonUp;
                _canvas.Children.Add(_vertexB);

                _vertexC = CreateVertexHandle(Cursors.SizeNS);
                _vertexC.MouseLeftButtonDown += VertexC_MouseLeftButtonDown;
                _vertexC.MouseMove += VertexC_MouseMove;
                _vertexC.MouseLeftButtonUp += VertexC_MouseLeftButtonUp;
                _canvas.Children.Add(_vertexC);

                Shell.Child = _canvas;
                RebuildTicks();
            }

            private static Border CreateVertexHandle(Cursor cursor)
            {
                return new Border
                {
                    Width = 56,
                    Height = 56,
                    Background = Brushes.Transparent,
                    Cursor = cursor,
                    ToolTip = "拖动改变边长",
                };
            }

            internal override void RebuildTicks()
            {
                if (_canvas == null) return;
                var a = PivotLocal;
                var b = new Point(a.X + LegX, a.Y);
                var c = new Point(a.X, a.Y + LegY);
                Shell.Width = LegX + a.X + 36;
                Shell.Height = LegY + a.Y + 36;
                _canvas.Width = Shell.Width;
                _canvas.Height = Shell.Height;
                _polygon.Points = new PointCollection { a, b, c };
                WpfCanvas.SetLeft(_close, a.X + Math.Max(18, LegX * 0.22));
                WpfCanvas.SetTop(_close, a.Y + Math.Max(12, LegY * 0.18));
                UpdateTicks(a);
                WpfCanvas.SetLeft(_vertexB, b.X - _vertexB.Width * 0.75);
                WpfCanvas.SetTop(_vertexB, b.Y - _vertexB.Height * 0.5);
                WpfCanvas.SetLeft(_vertexC, c.X - _vertexC.Width * 0.5);
                WpfCanvas.SetTop(_vertexC, c.Y - _vertexC.Height * 0.75);
                WpfCanvas.SetLeft(_rotateHandle, a.X + LegX * 0.58 - _rotateHandle.Width / 2.0);
                WpfCanvas.SetTop(_rotateHandle, a.Y + LegY * 0.58 - _rotateHandle.Height / 2.0);
                ApplyTransform();
            }

            private void UpdateTicks(Point a)
            {
                _ticks.Children.Clear();
                _ticks.Width = _canvas.Width;
                _ticks.Height = _canvas.Height;
                var brush = Owner.GeometryToolSubtleTickBrush;
                DrawLegTicks(true, a, brush);
                DrawLegTicks(false, a, brush);
            }

            private void DrawLegTicks(bool horizontal, Point a, Brush brush)
            {
                var legLength = horizontal ? LegX : LegY;
                var last = Math.Max(GeometryTriangleTickStartOffset, legLength - GeometryTriangleTickEndInset);
                var lastIndex = Math.Max(0, (int)Math.Floor((last - GeometryTriangleTickStartOffset) / 10.0 + 0.000001));
                var firstIndex = 0;

                //直角边也不设上限了，只画可见区里的那一段。首尾吸附回 25 + 10k 的格子
                //（刻度是从直角顶点外 25px 起算的），所以直角边完整可见时和不裁剪逐字节相同
                if (TryGetVisibleAxisBounds(out var visibleMin, out var visibleMax))
                {
                    var lo = (horizontal ? visibleMin.X : visibleMin.Y) - GeometryTickCullMargin;
                    var hi = (horizontal ? visibleMax.X : visibleMax.Y) + GeometryTickCullMargin;
                    firstIndex = ClampIndex((int)Math.Ceiling((lo - GeometryTriangleTickStartOffset) / 10.0), 0, lastIndex);
                    lastIndex = ClampIndex((int)Math.Floor((hi - GeometryTriangleTickStartOffset) / 10.0 + 0.000001), firstIndex, lastIndex);
                }

                var drawn = 0;
                for (var index = firstIndex; index <= lastIndex && drawn < GeometryTickMaxPerDraw; index++, drawn++)
                {
                    var pos = GeometryTriangleTickStartOffset + index * 10.0;
                    var major = index % 5 == 0;
                    var mid = !major && index % 2 == 0;
                    var len = major ? 18 : (mid ? 13 : 8);
                    if (horizontal)
                    {
                        var x = a.X + pos;
                        _ticks.Children.Add(new Line { X1 = x, Y1 = a.Y, X2 = x, Y2 = a.Y + len, Stroke = brush, StrokeThickness = 1 });
                        if (major)
                        {
                            var label = new TextBlock { Text = (index / 5).ToString(), FontSize = 11, Foreground = brush };
                            WpfCanvas.SetLeft(label, x - 4);
                            WpfCanvas.SetTop(label, a.Y + len + 1);
                            _ticks.Children.Add(label);
                        }
                    }
                    else
                    {
                        var y = a.Y + pos;
                        _ticks.Children.Add(new Line { X1 = a.X, Y1 = y, X2 = a.X + len, Y2 = y, Stroke = brush, StrokeThickness = 1 });
                        if (major)
                        {
                            var label = new TextBlock { Text = (index / 5).ToString(), FontSize = 11, Foreground = brush };
                            WpfCanvas.SetLeft(label, a.X + len + 2);
                            WpfCanvas.SetTop(label, y - 7);
                            _ticks.Children.Add(label);
                        }
                    }
                }
            }

            private static int ClampIndex(int value, int min, int max)
            {
                if (max < min) return min;
                if (value < min) return min;
                if (value > max) return max;
                return value;
            }

            internal override void PlaceAtVisibleCenter(Point cascade)
            {
                AngleDeg = 0;
                MovePivotTo(new Point(
                    Owner.BoardVisibleLeft + Owner.BoardVisibleWidth * 0.55 + cascade.X,
                    Owner.BoardVisibleTop + Owner.BoardVisibleHeight * 0.45 + cascade.Y));
                ApplyTransform();
            }

            internal override double AssistDistance(Point inkPoint)
            {
                if (!Owner.IsGeometryDrawingAvailable()) return double.MaxValue;
                return GetNearestEdgeProjection(LocalFromWorld(inkPoint), null).Distance;
            }

            internal override bool BeginAssist(Point inkPoint)
            {
                var edge = GetNearestEdgeProjection(LocalFromWorld(inkPoint), null);
                if (edge.Distance > GeometryAssistTolerance) return false;
                IsAssisting = true;
                _assistEdge = edge.Edge;
                _assistStartParam = edge.Param;
                UpdateAssistPreview(inkPoint);
                return true;
            }

            internal override void UpdateAssistPreview(Point inkPoint)
            {
                var edge = GetNearestEdgeProjection(LocalFromWorld(inkPoint), _assistEdge);
                Owner.ReplaceGeometryPreview(Owner.CreateLineStrokeCollection(
                    WorldFromLocal(GetEdgePoint(_assistEdge, _assistStartParam)),
                    WorldFromLocal(GetEdgePoint(_assistEdge, edge.Param))));
            }

            private GeometryTriangleProjection GetNearestEdgeProjection(Point p, GeometryTriangleEdgeType? prefer)
            {
                var best = new GeometryTriangleProjection(GeometryTriangleEdgeType.AB, 0, double.MaxValue);
                CheckEdgeProjection(GeometryTriangleEdgeType.AB, p, prefer, ref best);
                CheckEdgeProjection(GeometryTriangleEdgeType.AC, p, prefer, ref best);
                CheckEdgeProjection(GeometryTriangleEdgeType.BC, p, prefer, ref best);
                return best;
            }

            private void CheckEdgeProjection(GeometryTriangleEdgeType edge, Point p, GeometryTriangleEdgeType? prefer, ref GeometryTriangleProjection best)
            {
                var projection = ProjectToEdge(edge, p);
                //正在沿某条边画的时候，稍微偏向它一点，免得手一抖跳到邻边
                if (prefer.HasValue && edge != prefer.Value) projection.Distance += 0.8;
                if (projection.Distance < best.Distance) best = projection;
            }

            private GeometryTriangleProjection ProjectToEdge(GeometryTriangleEdgeType edge, Point p)
            {
                if (edge == GeometryTriangleEdgeType.AB)
                    return new GeometryTriangleProjection(edge, Clamp(p.X / LegX, 0, 1), Math.Abs(p.Y));
                if (edge == GeometryTriangleEdgeType.AC)
                    return new GeometryTriangleProjection(edge, Clamp(p.Y / LegY, 0, 1), Math.Abs(p.X));
                var segment = ProjectPointToSegment(new Point(LegX, 0), new Point(0, LegY), p);
                return new GeometryTriangleProjection(edge, segment.Param, segment.Distance);
            }

            private Point GetEdgePoint(GeometryTriangleEdgeType edge, double param)
            {
                var t = Clamp(param, 0, 1);
                if (edge == GeometryTriangleEdgeType.AB) return new Point(LegX * t, 0);
                if (edge == GeometryTriangleEdgeType.AC) return new Point(0, LegY * t);
                return new Point(LegX * (1 - t), LegY * t);
            }

            private static GeometryTriangleProjection ProjectPointToSegment(Point a, Point b, Point p)
            {
                var ab = b - a;
                var ap = p - a;
                var denom = ab.X * ab.X + ab.Y * ab.Y;
                if (denom < 0.0001) return new GeometryTriangleProjection(GeometryTriangleEdgeType.BC, 0, (p - a).Length);
                var t = Clamp((ap.X * ab.X + ap.Y * ab.Y) / denom, 0, 1);
                var proj = a + ab * t;
                return new GeometryTriangleProjection(GeometryTriangleEdgeType.BC, t, (p - proj).Length);
            }

            private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                //碰哪把哪把到最上层（和窗口激活一个道理）。放在最前面，按在把柄上也一样生效
                Owner.RaiseGeometryTool(this);

                if (IsOriginalSourceInside(e.OriginalSource, _close) ||
                    IsOriginalSourceInside(e.OriginalSource, _rotateHandle) ||
                    IsOriginalSourceInside(e.OriginalSource, _vertexB) ||
                    IsOriginalSourceInside(e.OriginalSource, _vertexC))
                {
                    return;
                }
                if (IsRotating || IsResizing) return;
                if (Owner.TryBeginGeometryAssist(Owner.InkPointFrom(e), this))
                {
                    e.Handled = true;
                    return;
                }

                IsDragging = true;
                var p = Owner.OverlayPointFrom(e);
                var origin = ShellOrigin;
                DragOffset = new Point(p.X - origin.X, p.Y - origin.Y);
                Shell.CaptureMouse();
                e.Handled = true;
            }

            private void Shell_MouseMove(object sender, MouseEventArgs e)
            {
                if (IsAssisting)
                {
                    if (e.LeftButton == MouseButtonState.Pressed)
                    {
                        UpdateAssistPreview(Owner.InkPointFrom(e));
                        e.Handled = true;
                    }
                    return;
                }

                if (!IsDragging || e.LeftButton != MouseButtonState.Pressed) return;
                var p = Owner.OverlayPointFrom(e);
                MovePivotTo(new Point(p.X - DragOffset.X + GeometryTrianglePivotLocal, p.Y - DragOffset.Y + GeometryTrianglePivotLocal));
                //刻度是按可见区裁出直角边上那一段再画的，三角尺一挪这段就作废了
                RebuildTicks();
                e.Handled = true;
            }

            private void Shell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                if (IsAssisting)
                {
                    Owner.EndGeometryAssist(this, true);
                    e.Handled = true;
                    return;
                }

                IsDragging = false;
                Shell.ReleaseMouseCapture();
                e.Handled = true;
            }

            private void Shell_MouseWheel(object sender, MouseWheelEventArgs e)
            {
                var step = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 1.0 : 0.1;
                AngleDeg += e.Delta > 0 ? step : -step;
                ApplyTransform();
                KeepInside();
                //转完可见区落在直角边上的那一段就变了，刻度得按新角度重画
                RebuildTicks();
                e.Handled = true;
            }

            private void RotateHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                IsRotating = true;
                LastMousePoint = Owner.OverlayPointFrom(e);
                _rotateOffsetDeg = GetPointerAngleAround(PivotWorld, LastMousePoint) - AngleDeg;
                _rotateHandle.CaptureMouse();
                e.Handled = true;
            }

            private void RotateHandle_MouseMove(object sender, MouseEventArgs e)
            {
                if (!IsRotating || e.LeftButton != MouseButtonState.Pressed) return;
                var pointer = Owner.OverlayPointFrom(e);
                AngleDeg = GetPointerAngleAround(PivotWorld, pointer) - _rotateOffsetDeg;
                ApplyTransform();
                KeepInside();
                //同上：角度变了刻度裁剪的那一段就变了
                RebuildTicks();
                _rotateOffsetDeg = GetPointerAngleAround(PivotWorld, pointer) - AngleDeg;
                e.Handled = true;
            }

            private void RotateHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                IsRotating = false;
                _rotateHandle.ReleaseMouseCapture();
                e.Handled = true;
            }

            private void VertexB_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                IsResizing = true;
                _vertexB.CaptureMouse();
                e.Handled = true;
            }

            private void VertexB_MouseMove(object sender, MouseEventArgs e)
            {
                if (!IsResizing || e.LeftButton != MouseButtonState.Pressed) return;
                //只保留下限，不设上限
                var previous = LegX;
                LegX = Math.Max(GeometryTriangleMinLeg, LocalFromWorld(Owner.OverlayPointFrom(e)).X);
                RebuildTicks();
                //直角顶点（旋转中心）在屏幕外时，边变短是整把三角尺朝那边缩，夹一下免得缩出屏幕。
                //只在变短时夹（变长时顶点跟着鼠标、本来就在屏幕里），位置真动了再重画刻度
                if (LegX < previous && KeepInside()) RebuildTicks();
                e.Handled = true;
            }

            private void VertexB_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                IsResizing = false;
                _vertexB.ReleaseMouseCapture();
                e.Handled = true;
            }

            private void VertexC_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                IsResizing = true;
                _vertexC.CaptureMouse();
                e.Handled = true;
            }

            private void VertexC_MouseMove(object sender, MouseEventArgs e)
            {
                if (!IsResizing || e.LeftButton != MouseButtonState.Pressed) return;
                //只保留下限，不设上限
                var previous = LegY;
                LegY = Math.Max(GeometryTriangleMinLeg, LocalFromWorld(Owner.OverlayPointFrom(e)).Y);
                RebuildTicks();
                //同 LegX：只在变短、而且位置真被夹动时重算刻度
                if (LegY < previous && KeepInside()) RebuildTicks();
                e.Handled = true;
            }

            private void VertexC_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                IsResizing = false;
                _vertexC.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        /// <summary>量角器：拖动、旋转（把柄或滚轮）、缩放，点刻度弧留下定点标记，标记跟着它一起消失。</summary>
        private sealed class ProtractorInstance : GeometryToolInstance
        {
            private WpfCanvas _canvas, _ticks, _marks;
            private Path _fill;
            private Ellipse _pivotDot;
            private Button _close;
            private Border _rotateHandle, _resizeHandle;

            internal double Radius = GeometryProtractorDefaultRadius;

            private double _rotateOffsetDeg;

            internal ProtractorInstance(MainWindow owner) : base(owner, GeometryToolKind.Protractor) { }

            protected override Point PivotLocal
            {
                get { return new Point(GeometryProtractorPadding + Radius, GeometryProtractorPadding + Radius); }
            }

            protected override void BuildVisualTree()
            {
                Shell = new Border
                {
                    Width = 380,
                    Height = 250,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    Cursor = Cursors.Hand,
                };
                Shell.MouseLeftButtonDown += Shell_MouseLeftButtonDown;
                Shell.MouseMove += Shell_MouseMove;
                Shell.MouseLeftButtonUp += Shell_MouseLeftButtonUp;
                Shell.MouseWheel += Shell_MouseWheel;

                //半透明的底：既要点得到，又不能挡住底下的墨迹
                _canvas = new WpfCanvas { Background = Brushes.Transparent };
                _fill = new Path
                {
                    Fill = GeometryProtractorFillBrush,
                    Stroke = GeometryToolOutlineBrush,
                    StrokeThickness = 2,
                };
                _canvas.Children.Add(_fill);

                _ticks = new WpfCanvas { IsHitTestVisible = false };
                _canvas.Children.Add(_ticks);

                _pivotDot = CreatePivotDot();
                _canvas.Children.Add(_pivotDot);

                _close = CreateCloseButton("取消量角器");
                _close.Background = GeometryCloseButtonBackgroundBrush;
                _close.BorderBrush = GeometryCloseButtonBackgroundBrush;
                _close.BorderThickness = new Thickness(1);
                _canvas.Children.Add(_close);

                _rotateHandle = CreateRotateHandle(24, 12, 13);
                _rotateHandle.MouseLeftButtonDown += RotateHandle_MouseLeftButtonDown;
                _rotateHandle.MouseMove += RotateHandle_MouseMove;
                _rotateHandle.MouseLeftButtonUp += RotateHandle_MouseLeftButtonUp;
                _rotateHandle.MouseWheel += Shell_MouseWheel;
                _canvas.Children.Add(_rotateHandle);

                _resizeHandle = new Border
                {
                    Width = 22,
                    Height = 22,
                    Background = GeometryResizeHandleBrush,
                    CornerRadius = new CornerRadius(11),
                    Cursor = Cursors.SizeWE,
                    ToolTip = "按住调整大小",
                    Child = new TextBlock
                    {
                        Text = "↔",
                        Foreground = Brushes.White,
                        FontSize = 11,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                };
                _resizeHandle.MouseLeftButtonDown += ResizeHandle_MouseLeftButtonDown;
                _resizeHandle.MouseMove += ResizeHandle_MouseMove;
                _resizeHandle.MouseLeftButtonUp += ResizeHandle_MouseLeftButtonUp;
                _canvas.Children.Add(_resizeHandle);

                Shell.Child = _canvas;

                //定点标记自己有单独一层，直接挂在覆盖层上（不放进 Host）：标记记的是板上的位置，
                //不该跟着量角器一起转、一起挪；但它归这个量角器所有，关掉时一起消失。
                //这就是以前那个“标记关不掉也擦不掉、只能重启”的老毛病。
                _marks = new WpfCanvas { ClipToBounds = false, Width = 0, Height = 0, IsHitTestVisible = false };
                Panel.SetZIndex(_marks, GeometryProtractorMarkZIndex);
                Owner.GeometryToolsOverlayCanvas.Children.Add(_marks);

                RebuildTicks();
            }

            protected override void OnDetached()
            {
                if (_marks == null) return;
                _marks.Children.Clear();
                Owner.GeometryToolsOverlayCanvas.Children.Remove(_marks);
            }

            internal void ClearMarks()
            {
                if (_marks != null) _marks.Children.Clear();
            }

            internal override void RebuildTicks()
            {
                if (_canvas == null) return;
                Radius = Clamp(Radius, GeometryProtractorMinRadius, GeometryProtractorMaxRadius);
                var width = Radius * 2 + GeometryProtractorPadding * 2;
                var height = Radius + GeometryProtractorPadding + 56;
                var center = new Point(GeometryProtractorPadding + Radius, GeometryProtractorPadding + Radius);
                Shell.Width = width;
                Shell.Height = height;
                _canvas.Width = width;
                _canvas.Height = height;
                _ticks.Width = width;
                _ticks.Height = height;

                var start = new Point(center.X - Radius, center.Y);
                var end = new Point(center.X + Radius, center.Y);
                var figure = new PathFigure { StartPoint = start, IsClosed = true, IsFilled = true };
                figure.Segments.Add(new ArcSegment
                {
                    Point = end,
                    Size = new Size(Radius, Radius),
                    SweepDirection = SweepDirection.Clockwise,
                    IsLargeArc = false,
                });
                figure.Segments.Add(new LineSegment(start, true));
                _fill.Data = new PathGeometry(new[] { figure });

                WpfCanvas.SetLeft(_pivotDot, center.X - _pivotDot.Width / 2.0);
                WpfCanvas.SetTop(_pivotDot, center.Y - _pivotDot.Height / 2.0);
                WpfCanvas.SetLeft(_close, center.X - _close.Width / 2.0);
                WpfCanvas.SetTop(_close, Math.Max(4, center.Y - Radius + GeometryProtractorCloseButtonDrop));
                WpfCanvas.SetLeft(_rotateHandle, center.X - _rotateHandle.Width / 2.0);
                WpfCanvas.SetTop(_rotateHandle, center.Y + 14);
                WpfCanvas.SetLeft(_resizeHandle, center.X + Radius - _resizeHandle.Width / 2.0);
                WpfCanvas.SetTop(_resizeHandle, center.Y - _resizeHandle.Height / 2.0);
                UpdateTicks(center, Radius);
                ApplyTransform();
            }

            private void UpdateTicks(Point center, double radius)
            {
                _ticks.Children.Clear();
                var tickBrush = Owner.GeometryToolSubtleTickBrush;
                var textBrush = Owner.GeometryToolTickBrush;
                _ticks.Children.Add(new Line { X1 = center.X - radius, Y1 = center.Y, X2 = center.X + radius, Y2 = center.Y, Stroke = tickBrush, StrokeThickness = 2 });
                for (var deg = 0; deg <= 180; deg++)
                {
                    var radians = Math.PI - deg * Math.PI / 180.0;
                    var major = deg % 10 == 0;
                    var mid = !major && deg % 5 == 0;
                    var len = major ? 16 : (mid ? 11 : 6);
                    var outer = new Point(center.X + Math.Cos(radians) * radius, center.Y - Math.Sin(radians) * radius);
                    var inner = new Point(center.X + Math.Cos(radians) * (radius - len), center.Y - Math.Sin(radians) * (radius - len));
                    _ticks.Children.Add(new Line { X1 = outer.X, Y1 = outer.Y, X2 = inner.X, Y2 = inner.Y, Stroke = tickBrush, StrokeThickness = major ? 1.4 : (mid ? 1.1 : 0.9) });
                    if (!major) continue;
                    AddLabel(center, radius - 28, radians, deg.ToString(), deg == 0 || deg == 90 || deg == 180 ? 12 : 10, textBrush, FontWeights.SemiBold);
                    AddLabel(center, radius - 48, radians, (180 - deg).ToString(), 9, textBrush, FontWeights.Normal);
                }
            }

            private void AddLabel(Point center, double labelRadius, double radians, string text, double fontSize, Brush foreground, FontWeight weight)
            {
                var labelPos = new Point(center.X + Math.Cos(radians) * labelRadius, center.Y - Math.Sin(radians) * labelRadius);
                var label = new TextBlock { Text = text, FontSize = fontSize, FontWeight = weight, Foreground = foreground };
                label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                WpfCanvas.SetLeft(label, labelPos.X - label.DesiredSize.Width / 2.0);
                WpfCanvas.SetTop(label, labelPos.Y - label.DesiredSize.Height / 2.0);
                _ticks.Children.Add(label);
            }

            internal override void PlaceAtVisibleCenter(Point cascade)
            {
                AngleDeg = 0;
                MovePivotTo(new Point(
                    Owner.BoardVisibleLeft + Owner.BoardVisibleWidth * 0.5 + cascade.X,
                    Owner.BoardVisibleTop + Owner.BoardVisibleHeight * 0.52 + cascade.Y));
                ApplyTransform();
            }

            internal override double AssistDistance(Point inkPoint)
            {
                //量角器不参与助画
                return double.MaxValue;
            }

            internal override bool BeginAssist(Point inkPoint)
            {
                return false;
            }

            internal override void UpdateAssistPreview(Point inkPoint) { }

            /// <summary>点在刻度弧附近：吸附到最近整度，留一个定点标记。</summary>
            private bool TryPlaceMark(Point overlayPoint)
            {
                var local = LocalFromWorld(overlayPoint);
                var distance = Math.Sqrt(local.X * local.X + local.Y * local.Y);
                if (local.Y > 10 || distance < Radius - 26 || distance > Radius + 10)
                {
                    return false;
                }

                var rawDegree = Math.Atan2(-local.Y, local.X) * 180.0 / Math.PI;
                var degree = Clamp(Math.Round(180.0 - rawDegree), 0, 180);
                var radians = Math.PI - degree * Math.PI / 180.0;
                var localPoint = new Point(Math.Cos(radians) * Radius, -Math.Sin(radians) * Radius);
                AddMark(WorldFromLocal(localPoint));
                return true;
            }

            private void AddMark(Point worldPoint)
            {
                var dot = new Ellipse
                {
                    Width = 10,
                    Height = 10,
                    Fill = GeometryProtractorMarkBrush,
                    Stroke = Brushes.White,
                    StrokeThickness = 1.4,
                    IsHitTestVisible = false,
                };
                WpfCanvas.SetLeft(dot, worldPoint.X - dot.Width / 2.0);
                WpfCanvas.SetTop(dot, worldPoint.Y - dot.Height / 2.0);
                _marks.Children.Add(dot);
            }

            private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                //碰哪把哪把到最上层（和窗口激活一个道理）。放在最前面，按在把柄上也一样生效
                Owner.RaiseGeometryTool(this);

                if (IsOriginalSourceInside(e.OriginalSource, _close) ||
                    IsOriginalSourceInside(e.OriginalSource, _rotateHandle) ||
                    IsOriginalSourceInside(e.OriginalSource, _resizeHandle))
                {
                    return;
                }
                if (IsRotating || IsResizing) return;

                var overlayPoint = Owner.OverlayPointFrom(e);
                if (TryPlaceMark(overlayPoint))
                {
                    e.Handled = true;
                    return;
                }

                IsDragging = true;
                PressPoint = overlayPoint;
                var origin = ShellOrigin;
                DragOffset = new Point(overlayPoint.X - origin.X, overlayPoint.Y - origin.Y);
                Shell.CaptureMouse();
                e.Handled = true;
            }

            private void Shell_MouseMove(object sender, MouseEventArgs e)
            {
                if (!IsDragging || e.LeftButton != MouseButtonState.Pressed) return;
                var p = Owner.OverlayPointFrom(e);
                //手抖一下不算拖动，省得点一下标记就把量角器带偏
                if (Math.Abs(p.X - PressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(p.Y - PressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
                MovePivotTo(new Point(p.X - DragOffset.X + PivotLocal.X, p.Y - DragOffset.Y + PivotLocal.Y));
                e.Handled = true;
            }

            private void Shell_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                IsDragging = false;
                Shell.ReleaseMouseCapture();
                e.Handled = true;
            }

            private void Shell_MouseWheel(object sender, MouseWheelEventArgs e)
            {
                var step = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 1.0 : 0.1;
                AngleDeg = NormalizeAngle(AngleDeg + (e.Delta > 0 ? step : -step));
                ApplyTransform();
                KeepInside();
                e.Handled = true;
            }

            private void RotateHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                IsRotating = true;
                LastMousePoint = Owner.OverlayPointFrom(e);
                _rotateOffsetDeg = GetPointerAngleAround(PivotWorld, LastMousePoint) - AngleDeg;
                _rotateHandle.CaptureMouse();
                e.Handled = true;
            }

            private void RotateHandle_MouseMove(object sender, MouseEventArgs e)
            {
                if (!IsRotating || e.LeftButton != MouseButtonState.Pressed) return;
                var pointer = Owner.OverlayPointFrom(e);
                AngleDeg = NormalizeAngle(GetPointerAngleAround(PivotWorld, pointer) - _rotateOffsetDeg);
                ApplyTransform();
                KeepInside();
                _rotateOffsetDeg = GetPointerAngleAround(PivotWorld, pointer) - AngleDeg;
                e.Handled = true;
            }

            private void RotateHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                IsRotating = false;
                _rotateHandle.ReleaseMouseCapture();
                e.Handled = true;
            }

            private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            {
                IsResizing = true;
                LastMousePoint = Owner.OverlayPointFrom(e);
                _resizeHandle.CaptureMouse();
                e.Handled = true;
            }

            private void ResizeHandle_MouseMove(object sender, MouseEventArgs e)
            {
                if (!IsResizing || e.LeftButton != MouseButtonState.Pressed) return;
                //缩放要把旋转中心按住不动，所以先记下它在覆盖层里的位置，改完半径再放回去
                var pivot = PivotWorld;
                var local = LocalFromWorld(Owner.OverlayPointFrom(e));
                Radius = Clamp(Math.Sqrt(local.X * local.X + local.Y * local.Y), GeometryProtractorMinRadius, GeometryProtractorMaxRadius);
                RebuildTicks();
                MovePivotTo(pivot);
                e.Handled = true;
            }

            private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
            {
                IsResizing = false;
                _resizeHandle.ReleaseMouseCapture();
                e.Handled = true;
            }
        }
    }
}

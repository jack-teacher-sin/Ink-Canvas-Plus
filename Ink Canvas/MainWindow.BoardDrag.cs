using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
//XAML 里有个 Name="Canvas" 的元素，同名成员会把 Canvas 类型遮住，所以跟别的文件一样用别名
using WpfCanvas = System.Windows.Controls.Canvas;

namespace InkCanvasPlus
{
    /// <summary>
    /// 黑板拖动模式：在“输入状态”和“拖动状态”之间来回切换。
    ///
    /// 拖动挪的是整块黑板上的东西 —— 板书墨迹、展台抓拍的照片（连同它的手柄）、尺具，
    /// 全部挂在同一个位移上一起走，所以它们之间的相对位置始终不变。
    ///
    /// 黑板本身是一张比窗口大得多的画布（每边 k 个屏幕，见 BoardPanScreens），位移只许在 ±k 屏
    /// 之间 —— 窗口于是永远落在画布内部。这是硬要求，因为有两处裁切都会吃掉窗口以外的部分：
    /// InkCanvas 会把自己矩形以外的墨迹连命中测试一起裁掉；而 Grid 会给“比自己的格位还大”的
    /// 子元素补一个布局裁切，那个裁切域记在子元素自己的坐标里，会跟着位移一起挪走 ——
    /// 画布再大也没用，可用范围只剩一块屏幕、还随拖动跑，就是“四周画不上字、光标变箭头”的来源。
    ///
    /// 所以四层不直接挂在 Main_Grid 上，而是统一放进一个和窗口同尺寸的 Canvas（boardHost）：
    /// Canvas 分给子元素的格位就等于子元素自己要的尺寸，不会再套布局裁切，位置改用
    /// Canvas.Left/Top 摆到 (−T,−T)，容器自己不裁子元素 —— 任何位移下窗口都完整落在画布内。
    ///
    /// 做成“整体位移”而不是逐个挪对象，有两个原因：
    /// <list type="number">
    /// <item><description>照片和尺具各自的边界约束都是在画布坐标系里算的，逐个挪会被这些约束
    /// 顶回来；整体位移不碰它们的坐标，约束自然全部满足。</description></item>
    /// <item><description>板书、照片、尺具的屏幕位置由同一个位移保证，不用逐个同步。</description></item>
    /// </list>
    /// </summary>
    public partial class MainWindow : Window
    {
        /// <summary>
        /// 黑板比窗口大多少：每边 k 个屏幕宽/高。拖动范围 = ±k 屏，板书区 = (2k+1) 屏见方。
        /// 想再大一点就改这里，别的地方不用动。
        /// </summary>
        private const double BoardPanScreens = 4.0;

        /// <summary>
        /// 拖动黑板时两次重画尺具刻度的最小间隔（毫秒）。刻度按可见区裁剪，拖动中每帧都得重画，
        /// 一把长尺子有好几百个刻度，跟不上每帧一次，按这个间隔攒着画，见 RequestGeometryTickRebuild。
        /// </summary>
        private const int GeometryTickRebuildIntervalMs = 60;

        //黑板内容的整体位移，同一个值挂到下面几层上（一层一个 Transform 实例，不共用）
        private readonly List<TranslateTransform> boardViewTransforms = new List<TranslateTransform>();
        //参与整体位移的四层。尺寸和摆放方式必须完全一致，它们才共用同一套坐标
        private UIElement[] boardLayers;
        //四层的容器：和窗口同尺寸的 Canvas，四层在它里面被摆到 (−T,−T) 并撑成「窗口 + 2T」。
        //用 Canvas 而不是让四层直接挂在 Main_Grid 上，是为了躲开布局裁切，理由见类头注释
        private WpfCanvas boardHost;
        private double boardViewX;
        private double boardViewY;

        //黑板左上角相对窗口左上角的偏移（T = k × 窗口宽/高）
        private double boardOriginX;
        private double boardOriginY;
        private bool isBoardLaidOut;

        //尺具刻度重画的攒批状态：上次重画的时间、还欠着一次没画的标记、补画用的计时器
        private DispatcherTimer geometryTickRebuildTimer;
        private DateTime geometryTickRebuildLastUtc = DateTime.MinValue;
        private bool geometryTickRebuildPending;

        //是否处于拖动状态（点击左下角的按钮把它翻过来）
        bool isBoardDragMode = false;
        //进入拖动状态前的画笔状态与光标，退出时原样还原
        InkCanvasEditingMode editingModeBeforeBoardDrag = InkCanvasEditingMode.Ink;
        Cursor cursorBeforeBoardDrag = Cursors.Pen;
        //鼠标/触控笔的拖动是否正在进行，以及上一个采样点
        bool isBoardDragging = false;
        Point boardDragLastPoint;

        /// <summary>
        /// 拖动状态是否生效。这里再要求一次黑板可见，是为了在模式切换的中间态里不误伤屏幕模式。
        /// </summary>
        bool IsBoardDragActive
        {
            get { return isBoardDragMode && currentMode == 1; }
        }

        /// <summary>
        /// 板书的可见区（窗口）左上角在黑板坐标里的位置：窗口坐标 = 黑板坐标 − 这个值。
        /// 黑板的下面几层和窗口是同一套单位，只差一个偏移，所以这一个数就够换算了。
        /// 位移只在黑板显示时生效（见 ApplyBoardView），这里也跟着算 0，屏幕/PPT 模式下才对得上。
        /// </summary>
        private double BoardVisibleLeft { get { return boardOriginX - (IsBoardViewVisible ? boardViewX : 0); } }
        private double BoardVisibleTop { get { return boardOriginY - (IsBoardViewVisible ? boardViewY : 0); } }

        /// <summary>可见区宽高。黑板画布和窗口是 1:1，取窗口尺寸即可。</summary>
        private double BoardVisibleWidth { get { return ActualWidth; } }
        private double BoardVisibleHeight { get { return ActualHeight; } }

        /// <summary>黑板是否正显示着。位移只在此时挂上去，收起黑板后另行存放，见 ApplyBoardView。</summary>
        private bool IsBoardViewVisible { get { return GridBackgroundCover.IsVisible; } }

        /// <summary>
        /// 黑板上有没有“整体跳一下会被看出来”的东西：板书、展台照片、尺具，或者已经拖过。
        /// 用来区分两种尺寸变化：换显示器/改缩放（黑板上有东西，位移要补偿）和启动阶段
        /// 窗口先按 XAML 里的尺寸排一遍再最大化（黑板还空着，补了反而一启动就偏到边上、
        /// 有一边还拖不动）。
        /// </summary>
        private bool BoardHasSomethingToKeep
        {
            get
            {
                if (boardViewX != 0 || boardViewY != 0) return true;
                if (inkCanvas.Strokes.Count > 0 || _cameraSnapshots.Count > 0) return true;
                //尺具是实例表，开着一把就算有东西；关掉就从表里摘掉了
                return _geometryToolInstances.Count > 0;
            }
        }

        /// <summary>
        /// 把位移挂到黑板内容的四层上：展台照片、板书墨迹、照片手柄、尺具。
        /// 只设 RenderTransform，不碰 Panel.ZIndex —— 图层顺序仍旧只由声明顺序决定。
        /// 选区的浮动工具条位置是另外算的，见 updateBorderStrokeSelectionControlLocation。
        /// </summary>
        private void InitBoardViewTransforms()
        {
            boardLayers = new UIElement[]
            {
                CameraSnapshotCanvas,       // 展台照片（声明在墨迹之前，墨迹压着它）
                inkCanvas,                  // 板书墨迹
                CameraControlsOverlayCanvas,// 照片的选中框与手柄
                GeometryToolsOverlayCanvas, // 直尺、三角尺、量角器
            };

            //四层要搬进一个和窗口同尺寸的 Canvas 里（理由见类头注释：直接挂在 Grid 上的话，
            //布局系统会给“比格位还大”的子元素补一个跟着位移走的裁切域，画布撑大也没用）。
            //容器插在原来第一层（展台照片）的位置上，四层的声明顺序原样保留，图层顺序不变。
            boardHost = new WpfCanvas { ClipToBounds = false };
            int hostIndex = Main_Grid.Children.IndexOf(CameraSnapshotCanvas);
            if (hostIndex < 0) hostIndex = 0;
            if (hostIndex > Main_Grid.Children.Count) hostIndex = Main_Grid.Children.Count;

            foreach (UIElement layer in boardLayers) Main_Grid.Children.Remove(layer);
            Main_Grid.Children.Insert(hostIndex, boardHost);

            //按声明顺序放回容器，图层关系保持不变：墨迹压着照片，尺具与照片手柄再压着墨迹。
            //（尺具上那个 Panel.ZIndex=20 现在只在容器内部生效，于是它落在选区浮层之下、
            //   和照片手柄的位置关系与原来一致，而选区工具条不再被尺具盖住。）
            boardHost.Children.Add(CameraSnapshotCanvas);
            boardHost.Children.Add(inkCanvas);
            boardHost.Children.Add(GeometryToolsOverlayCanvas);
            boardHost.Children.Add(CameraControlsOverlayCanvas);

            foreach (UIElement layer in boardLayers)
            {
                var transform = new TranslateTransform();
                layer.RenderTransform = transform;
                boardViewTransforms.Add(transform);
            }

            //窗口尺寸定下来（以及以后每次变）都要重排黑板画布。
            //构造函数里这次因为还没有尺寸会直接跳过，等窗口布局好了 SizeChanged 会再来一次
            SizeChanged += (s, e) => LayoutBoardCanvases();
            LayoutBoardCanvases();
        }

        /// <summary>
        /// 按窗口尺寸重排黑板画布：四层一起撑成「窗口 + 2T」，在 boardHost 里摆到 (−T,−T)。
        /// 画布比窗口大是「整屏都能书写、光标不变箭头」的前提（理由见类头注释）；
        /// 四层的尺寸和摆放方式又必须完全一样，它们才共用同一套坐标，跨画布的换算
        /// （照片手柄、尺具、选区工具条）才继续成立。
        /// </summary>
        private void LayoutBoardCanvases()
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (double.IsNaN(w) || double.IsNaN(h) || w <= 0 || h <= 0) return;

            double newOriginX = w * BoardPanScreens;
            double newOriginY = h * BoardPanScreens;

            //黑板变大/变小了（换显示器、改缩放）就把差值补进位移，黑板内容在屏幕上的位置
            //才不会跟着跳。黑板还空着的时候不补，理由见 BoardHasSomethingToKeep
            if (isBoardLaidOut && BoardHasSomethingToKeep)
            {
                boardViewX += newOriginX - boardOriginX;
                boardViewY += newOriginY - boardOriginY;
            }
            isBoardLaidOut = true;
            boardOriginX = newOriginX;
            boardOriginY = newOriginY;

            double boardW = w + newOriginX * 2;
            double boardH = h + newOriginY * 2;
            foreach (UIElement layer in boardLayers)
            {
                var element = layer as FrameworkElement;
                if (element == null) continue;
                element.Width = boardW;
                element.Height = boardH;
                //用 Canvas.Left/Top 摆到 (−T,−T)。容器自己不裁子元素，画布于是完整铺满窗口，
                //位移也只需要跟着走（见 ApplyBoardView）
                WpfCanvas.SetLeft(element, -newOriginX);
                WpfCanvas.SetTop(element, -newOriginY);
            }

            ClampBoardView();
            ApplyBoardView();

            //尺具的刻度是按“当前可见区”裁出来再画的（尺子可以不限长），窗口一变大变小这个范围就作废了，
            //得按新的可见区重画一遍。拖动黑板引起的可见区变化走 ApplyBoardView 里的那条路
            RebuildAllGeometryToolTicks();
        }

        /// <summary>把整块黑板（连同板书、照片、尺具）平移 dx,dy。</summary>
        private void TranslateBoardView(double dx, double dy)
        {
            if (dx == 0 && dy == 0) return;
            boardViewX += dx;
            boardViewY += dy;
            ClampBoardView();
            ApplyBoardView();
        }

        /// <summary>
        /// 位移夹到黑板范围内（±T）。拖不出黑板是必须的：拖过头窗口就会露在画布外面，
        /// 那一块就成了画不上字的死区。Clamp 是几何工具里那个静态助手。
        /// </summary>
        private void ClampBoardView()
        {
            boardViewX = Clamp(boardViewX, -boardOriginX, boardOriginX);
            boardViewY = Clamp(boardViewY, -boardOriginY, boardOriginY);
        }

        /// <summary>
        /// 把位移写到四层的 Transform 上。黑板没显示时（屏幕模式、PPT 放映）一律写 0：
        /// 位移值是黑板自己的状态，留着不动，等黑板再显示时原样恢复 ——
        /// 这样黑板和屏幕来回切不会丢掉板书位置，屏幕模式下的墨迹也不会被整体带偏。
        /// </summary>
        private void ApplyBoardView()
        {
            bool visible = IsBoardViewVisible;
            double x = visible ? boardViewX : 0;
            double y = visible ? boardViewY : 0;

            foreach (TranslateTransform transform in boardViewTransforms)
            {
                transform.X = x;
                transform.Y = y;
            }

            //位移一换，可见区在黑板上的位置就换了地方，尺具那段“按可见区裁出来”的刻度跟着作废，得重画。
            //挂在 ApplyBoardView 而不是 TranslateBoardView 里：黑板显示/隐藏（位移挂上、摘下）也是同一个道理。
            //一次拖动每帧都会走到这儿，所以由 RequestGeometryTickRebuild 攒着画
            RequestGeometryTickRebuild();
        }

        /// <summary>
        /// 请求重画尺具刻度。尺具的刻度是“按当前可见区裁出尺身上那一段”再画的（尺子可以不限长），
        /// 所以可见区一挪地方这段就作废 —— 不重画就是“拖到别处一看，那段尺子上只剩上下线和中线，
        /// 刻度和数值全没有，碰一下尺子又自己出来了”。
        /// 拖动黑板时这个请求每帧都来，而一把长尺子的刻度有好几百个元素，跟不上每帧重画一次，
        /// 于是按时间攒一下：离上次重画够久了就立刻画；不够就先记下，过一会儿补画一次，最后那一下不会漏。
        /// </summary>
        private void RequestGeometryTickRebuild()
        {
            //没有尺具开着就什么都不用做，连计时器都不用起
            if (_geometryToolInstances.Count == 0 || geometryTickRebuildPending) return;

            var now = DateTime.UtcNow;
            if ((now - geometryTickRebuildLastUtc).TotalMilliseconds >= GeometryTickRebuildIntervalMs)
            {
                RebuildAllGeometryToolTicks();
                geometryTickRebuildLastUtc = now;
                return;
            }

            geometryTickRebuildPending = true;
            if (geometryTickRebuildTimer == null) InitGeometryTickRebuildTimer();
            //已经在跑就让它接着跑，别重置：重置等于一直往后推，拖动中就得等松手才画了
            geometryTickRebuildTimer.Start();
        }

        private void InitGeometryTickRebuildTimer()
        {
            //Render 优先级：比输入低一级会被连续不断的 MouseMove 饿着，比渲染高又会让补画插在出画前面
            geometryTickRebuildTimer = new DispatcherTimer(DispatcherPriority.Render, Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(GeometryTickRebuildIntervalMs),
            };
            geometryTickRebuildTimer.Tick += (s, e) =>
            {
                geometryTickRebuildTimer.Stop();
                geometryTickRebuildPending = false;
                RebuildAllGeometryToolTicks();
                geometryTickRebuildLastUtc = DateTime.UtcNow;
            };
        }

        private void BorderBoardDragMode_MouseUp(object sender, MouseButtonEventArgs e)
        {
            SetBoardDragMode(!isBoardDragMode);
            e.Handled = true;
        }

        private void SetBoardDragMode(bool state)
        {
            if (isBoardDragMode == state) return;

            //多指书写和拖动黑板都要接管单指手势，同时开着会互相打架，这里让多指先退出
            if (state && isInMultiTouchMode) BorderMultiTouchMode_MouseUp(null, null);

            isBoardDragMode = state;

            if (state)
            {
                //拖动状态下不再收集墨迹：手指和笔都改成挪黑板。
                //选区留着会浮在挪走的墨迹上，所以一并清掉
                if (inkCanvas.GetSelectedStrokes().Count != 0)
                {
                    GridInkCanvasSelectionCover.Visibility = Visibility.Collapsed;
                    inkCanvas.Select(new StrokeCollection());
                }
                editingModeBeforeBoardDrag = inkCanvas.EditingMode;
                inkCanvas.EditingMode = InkCanvasEditingMode.None;
                cursorBeforeBoardDrag = inkCanvas.Cursor;
                inkCanvas.Cursor = Cursors.SizeAll;
            }
            else
            {
                EndBoardDrag();
                inkCanvas.EditingMode = editingModeBeforeBoardDrag;
                inkCanvas.Cursor = cursorBeforeBoardDrag;
            }

            //激活时把配色反过来，一眼能看出现在是不是拖动状态
            if (state)
            {
                BorderBoardDragMode.SetResourceReference(Border.BackgroundProperty, "FloatBarForeground");
                PathBoardDragModeIcon.SetResourceReference(Shape.StrokeProperty, "FloatBarBackground");
                BorderBoardDragMode.ToolTip = "拖动状态：在屏幕上拖动即可挪动整块黑板；再次点击回到书写状态";
            }
            else
            {
                BorderBoardDragMode.SetResourceReference(Border.BackgroundProperty, "FloatBarBackground");
                PathBoardDragModeIcon.SetResourceReference(Shape.StrokeProperty, "FloatBarForeground");
                BorderBoardDragMode.ToolTip = "点击进入拖动状态：单指或笔可以直接挪动整块黑板";
            }
        }

        /// <summary>
        /// 触摸被提升成鼠标事件后会再走一遍 MouseDown/MouseMove，
        /// 这里把它挡掉，否则一根手指会被平移两次
        /// （触摸走 ManipulationDelta，笔和鼠标走鼠标事件）。
        /// </summary>
        private static bool IsTouchPromotedMouseInput(MouseEventArgs e)
        {
            StylusDevice device = e.StylusDevice;
            return device != null && device.TabletDevice != null
                && device.TabletDevice.Type == TabletDeviceType.Touch;
        }

        private bool TryBeginBoardDrag(MouseButtonEventArgs e)
        {
            if (!IsBoardDragActive || e.ChangedButton != MouseButton.Left) return false;
            if (dec.Count > 0 || IsTouchPromotedMouseInput(e)) return false;

            isBoardDragging = true;
            boardDragLastPoint = e.GetPosition(Main_Grid);
            return true;
        }

        private void ContinueBoardDrag(MouseEventArgs e)
        {
            //松手事件丢在窗口外时会走到这里，顺手把状态收回来，免得拖拽卡住
            if (e.LeftButton == MouseButtonState.Released)
            {
                EndBoardDrag();
                return;
            }

            //取点一律以 Main_Grid 为准，不能拿 inkCanvas 当参照：
            //GetPosition 会把参照元素自己的 RenderTransform 反算掉，而我们要挪的正是这个位移。
            //用 inkCanvas 的话，上一次挪动的量会被从这次的位移里减掉 —— 手指走 20px，黑板只走 10px。
            Point p = e.GetPosition(Main_Grid);
            TranslateBoardView(p.X - boardDragLastPoint.X, p.Y - boardDragLastPoint.Y);
            boardDragLastPoint = p;
        }

        private void EndBoardDrag()
        {
            isBoardDragging = false;
        }
    }
}

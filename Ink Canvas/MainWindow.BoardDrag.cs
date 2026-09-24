using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace InkCanvasPlus
{
    /// <summary>
    /// 黑板拖动模式：在“输入状态”和“拖动状态”之间来回切换。
    ///
    /// 拖动挪的是整块黑板上的东西 —— 板书墨迹、展台抓拍的照片（连同它的手柄）、尺具，
    /// 全部挂在同一个位移上一起走，所以它们之间的相对位置始终不变。
    ///
    /// 做成“整体位移”而不是逐个挪对象，有两个原因：
    /// <list type="number">
    /// <item><description>照片和尺具各自的边界约束都是在画布坐标系里算的（照片放大后会被
    /// 钉在画布左上角、尺具不能拖出画布），逐个挪会被这些约束顶回来；整体位移不碰它们的
    /// 坐标，约束自然全部满足。</description></item>
    /// <item><description>展台照片放大到超过屏幕后就没法再看其余部分了。有了整体位移，
    /// 老师可以先把照片放到最大，再拖动黑板平移到想看的区域 —— 这正是“靠拖动来尽量放大
    /// 抓拍结果”的做法。</description></item>
    /// </list>
    /// </summary>
    public partial class MainWindow : Window
    {
        //黑板内容的整体位移，同一个值挂到下面几层上（一层一个 Transform 实例，不共用）
        private readonly List<TranslateTransform> boardViewTransforms = new List<TranslateTransform>();
        private double boardViewX;
        private double boardViewY;

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
        /// 把位移挂到黑板内容的四层上：展台照片、板书墨迹、照片手柄、尺具。
        /// 只设 RenderTransform，不碰 Panel.ZIndex —— 图层顺序仍旧只由声明顺序决定。
        /// 选区的浮动工具条位置是另外算的，见 updateBorderStrokeSelectionControlLocation。
        /// </summary>
        private void InitBoardViewTransforms()
        {
            UIElement[] boardLayers =
            {
                CameraSnapshotCanvas,       // 展台照片（声明在墨迹之前，墨迹压着它）
                inkCanvas,                  // 板书墨迹
                CameraControlsOverlayCanvas,// 照片的选中框与手柄
                GeometryToolsOverlayCanvas, // 直尺、三角尺、量角器
            };

            foreach (UIElement layer in boardLayers)
            {
                var transform = new TranslateTransform();
                layer.RenderTransform = transform;
                boardViewTransforms.Add(transform);
            }
        }

        /// <summary>把整块黑板（连同板书、照片、尺具）平移 dx,dy。</summary>
        private void TranslateBoardView(double dx, double dy)
        {
            if (dx == 0 && dy == 0) return;
            boardViewX += dx;
            boardViewY += dy;
            ApplyBoardView();
        }

        /// <summary>
        /// 位移归零。黑板收起来时（切屏幕模式、进 PPT 放映、隐藏画板）必须调用：
        /// 这个位移是挂在 inkCanvas 上的，留着会让屏幕/PPT 模式下的墨迹也整体偏出去。
        /// </summary>
        private void ResetBoardView()
        {
            if (boardViewX == 0 && boardViewY == 0) return;
            boardViewX = 0;
            boardViewY = 0;
            ApplyBoardView();
        }

        private void ApplyBoardView()
        {
            foreach (TranslateTransform transform in boardViewTransforms)
            {
                transform.X = boardViewX;
                transform.Y = boardViewY;
            }
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

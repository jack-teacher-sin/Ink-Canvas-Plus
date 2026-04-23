using System;
using System.Windows;
using System.Windows.Controls;
using WpfCanvas = System.Windows.Controls.Canvas;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace InkCanvasPlus
{
    public partial class MainWindow
    {
        private const double GeometryRulerMinWidth = 360;
        private const double GeometryRulerMaxWidth = 1400;
        private const double GeometryTriangleMinLeg = 90;
        private const double GeometryTriangleMaxLeg = 900;
        private const double GeometryTrianglePivotLocal = 20;
        private const double GeometryTriangleTickStartOffset = 25;
        private const double GeometryTriangleTickEndInset = 50;
        private const double GeometryProtractorMinRadius = 110;
        private const double GeometryProtractorMaxRadius = 360;
        private const double GeometryProtractorPadding = 22;
        private const double GeometryProtractorCloseButtonDrop = 44;

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

        private struct GeometryRulerLocal
        {
            public double LocalX;
            public double LocalY;
            public double RulerWidth;
            public double HalfHeight;

            public GeometryRulerLocal(double localX, double localY, double rulerWidth, double halfHeight)
            {
                LocalX = localX;
                LocalY = localY;
                RulerWidth = rulerWidth;
                HalfHeight = halfHeight;
            }
        }

        private bool _isRulerDragging, _isRulerDragPending, _isRulerRotating, _isRulerResizing;
        private bool _isTriangleDragging, _isTriangleRotating, _isTriangleResizeB, _isTriangleResizeC;
        private bool _isProtractorDragging, _isProtractorRotating, _isProtractorResizing;
        private bool _isRulerAssistDrawing, _isTriangleAssistDrawing;
        private Point _rulerDragOffset, _rulerLastMousePoint, _triangleDragOffset, _triangleLastMousePoint;
        private Point _protractorDragOffset, _protractorLastMousePoint, _protractorPressPoint;
        private DateTime _rulerDragPressUtc;
        private double _rulerRotateOffsetDeg, _rulerAngleDeg, _rulerAssistStartT, _rulerAssistOffset;
        private double _triangleRotateOffsetDeg, _triangleAngleDeg, _triangleLegX = 250, _triangleLegY = 170;
        private GeometryTriangleEdgeType _triangleAssistEdge;
        private double _triangleAssistStartParam, _protractorAngleDeg, _protractorRotateOffsetDeg, _protractorRadius = 160;
        private StrokeCollection _geometryPreviewStrokes;

        private void InitializeGeometryTools()
        {
            Panel.SetZIndex(GeometryToolsOverlayCanvas, 20);
            Panel.SetZIndex(RulerBorder, 21);
            Panel.SetZIndex(TriangleBorder, 21);
            Panel.SetZIndex(ProtractorBorder, 21);
            UpdateRulerTicks();
            UpdateTriangleGeometry();
            UpdateProtractorGeometry();
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

        private void RefreshGeometryToolTheme()
        {
            UpdateRulerTicks();
            UpdateTriangleGeometry();
            UpdateProtractorGeometry();
        }

        private void GeometryToolButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isRulerDragging = false;
            _isRulerDragPending = false;
            _isTriangleDragging = false;
            _isProtractorDragging = false;
        }

        private void GeometryToolsButton_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BorderClearInDelete.Visibility = Visibility.Collapsed;
            BorderTools.Visibility = Visibility.Collapsed;
            BorderDrawShape.Visibility = Visibility.Collapsed;
            BorderGeometryTools.Visibility = BorderGeometryTools.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            e.Handled = true;
        }

        private void RulerToolItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BorderGeometryTools.Visibility = Visibility.Collapsed;
            RulerBorder.Visibility = RulerBorder.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (RulerBorder.Visibility == Visibility.Visible) PlaceRulerAtCenter();
            e.Handled = true;
        }

        private void TriangleToolItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BorderGeometryTools.Visibility = Visibility.Collapsed;
            TriangleBorder.Visibility = TriangleBorder.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (TriangleBorder.Visibility == Visibility.Visible) PlaceTriangleAtCenter();
            e.Handled = true;
        }

        private void ProtractorToolItem_MouseUp(object sender, MouseButtonEventArgs e)
        {
            BorderGeometryTools.Visibility = Visibility.Collapsed;
            ProtractorBorder.Visibility = ProtractorBorder.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            if (ProtractorBorder.Visibility == Visibility.Visible) PlaceProtractorAtCenter();
            e.Handled = true;
        }

        private bool GeometryToolsInkCanvasMouseDown(MouseButtonEventArgs e)
        {
            var point = e.GetPosition(inkCanvas);
            return TryBeginTriangleAssist(point) || TryBeginRulerAssist(point);
        }

        private bool GeometryToolsInkCanvasMouseMove(MouseEventArgs e)
        {
            if (_isTriangleAssistDrawing)
            {
                if (e.LeftButton == MouseButtonState.Pressed) UpdateTriangleAssistPreview(e.GetPosition(inkCanvas));
                return true;
            }

            if (_isRulerAssistDrawing)
            {
                if (e.LeftButton == MouseButtonState.Pressed) UpdateRulerAssistPreview(e.GetPosition(inkCanvas));
                return true;
            }

            return false;
        }

        private bool GeometryToolsInkCanvasMouseUp(MouseButtonEventArgs e)
        {
            if (_isTriangleAssistDrawing || _isRulerAssistDrawing)
            {
                _isTriangleAssistDrawing = false;
                _isRulerAssistDrawing = false;
                CommitGeometryPreview();
                inkCanvas.ReleaseMouseCapture();
                return true;
            }

            return false;
        }

        private bool IsGeometryDrawingAvailable()
        {
            return inkCanvas.Visibility == Visibility.Visible && inkCanvas.EditingMode == InkCanvasEditingMode.Ink && drawingShapeMode == 0;
        }

        private void CloseRulerButton_Click(object sender, RoutedEventArgs e)
        {
            RulerBorder.Visibility = Visibility.Collapsed;
            _isRulerAssistDrawing = false;
            ClearGeometryPreview();
        }

        private void CloseTriangleButton_Click(object sender, RoutedEventArgs e)
        {
            TriangleBorder.Visibility = Visibility.Collapsed;
            _isTriangleAssistDrawing = false;
            ClearGeometryPreview();
        }

        private void CloseProtractorButton_Click(object sender, RoutedEventArgs e)
        {
            ProtractorBorder.Visibility = Visibility.Collapsed;
        }

        private void RulerBorder_SizeChanged(object sender, SizeChangedEventArgs e) { UpdateRulerTicks(); }

        private void RulerBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsOriginalSourceInside(e.OriginalSource, CloseRulerButton) ||
                IsOriginalSourceInside(e.OriginalSource, RulerRotateHandle) ||
                IsOriginalSourceInside(e.OriginalSource, RulerResizeHandle))
            {
                return;
            }
            if (_isRulerRotating || _isRulerResizing) return;
            var overlayPoint = e.GetPosition(GeometryToolsOverlayCanvas);
            if (IsNearRulerDrawingEdge(overlayPoint) && TryBeginRulerAssist(e.GetPosition(inkCanvas)))
            {
                e.Handled = true;
                return;
            }

            var p = overlayPoint;
            if (!IsInRulerDragBand(p)) return;
            _isRulerDragPending = true;
            _rulerDragPressUtc = DateTime.UtcNow;
            _isRulerDragging = false;
            _rulerDragOffset = new Point(p.X - WpfCanvas.GetLeft(RulerBorder), p.Y - WpfCanvas.GetTop(RulerBorder));
            RulerBorder.CaptureMouse();
            e.Handled = true;
        }

        private void RulerBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isRulerAssistDrawing)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    UpdateRulerAssistPreview(e.GetPosition(inkCanvas));
                    e.Handled = true;
                }
                return;
            }

            if (_isRulerRotating || _isRulerResizing) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                _isRulerDragPending = false;
                return;
            }

            if (_isRulerDragPending && !_isRulerDragging)
            {
                if ((DateTime.UtcNow - _rulerDragPressUtc).TotalMilliseconds < 220) return;
                _isRulerDragging = true;
                _isRulerDragPending = false;
            }
            if (!_isRulerDragging) return;

            var p = e.GetPosition(GeometryToolsOverlayCanvas);
            WpfCanvas.SetLeft(RulerBorder, Clamp(p.X - _rulerDragOffset.X, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualWidth - RulerBorder.ActualWidth)));
            WpfCanvas.SetTop(RulerBorder, Clamp(p.Y - _rulerDragOffset.Y, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualHeight - RulerBorder.ActualHeight - 80)));
            e.Handled = true;
        }

        private void RulerBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isRulerAssistDrawing)
            {
                _isRulerAssistDrawing = false;
                CommitGeometryPreview();
                inkCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            _isRulerDragPending = false;
            _isRulerDragging = false;
            RulerBorder.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void RulerRotateHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isRulerRotating = true;
            _rulerLastMousePoint = e.GetPosition(GeometryToolsOverlayCanvas);
            _rulerRotateOffsetDeg = GetPointerAngle(_rulerLastMousePoint) - _rulerAngleDeg;
            RulerRotateHandle.CaptureMouse();
            e.Handled = true;
        }

        private void RulerRotateHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isRulerRotating || e.LeftButton != MouseButtonState.Pressed) return;
            _rulerAngleDeg = GetPointerAngle(e.GetPosition(GeometryToolsOverlayCanvas)) - _rulerRotateOffsetDeg;
            ApplyRulerTransform();
            e.Handled = true;
        }

        private void RulerRotateHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isRulerRotating = false;
            RulerRotateHandle.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void RulerResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isRulerResizing = true;
            _rulerLastMousePoint = e.GetPosition(GeometryToolsOverlayCanvas);
            RulerResizeHandle.CaptureMouse();
            e.Handled = true;
        }

        private void RulerResizeHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isRulerResizing || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(GeometryToolsOverlayCanvas);
            var delta = p - _rulerLastMousePoint;
            _rulerLastMousePoint = p;
            var axis = GetRulerAxis();
            var along = (delta.X * axis.X) + (delta.Y * axis.Y);
            if (Math.Abs(along) < 0.1) return;
            RulerBorder.Width = Clamp(RulerBorder.Width + along, GeometryRulerMinWidth, GeometryRulerMaxWidth);
            UpdateRulerTicks();
            ApplyRulerTransform();
            e.Handled = true;
        }

        private void RulerResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isRulerResizing = false;
            RulerResizeHandle.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void TriangleBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsOriginalSourceInside(e.OriginalSource, CloseTriangleButton) ||
                IsOriginalSourceInside(e.OriginalSource, TriangleRotateHandle) ||
                IsOriginalSourceInside(e.OriginalSource, TriangleVertexBHandle) ||
                IsOriginalSourceInside(e.OriginalSource, TriangleVertexCHandle))
            {
                return;
            }
            if (_isTriangleRotating || _isTriangleResizeB || _isTriangleResizeC) return;
            if (TryBeginTriangleAssist(e.GetPosition(inkCanvas)))
            {
                e.Handled = true;
                return;
            }

            _isTriangleDragging = true;
            var p = e.GetPosition(GeometryToolsOverlayCanvas);
            _triangleDragOffset = new Point(p.X - WpfCanvas.GetLeft(TriangleBorder), p.Y - WpfCanvas.GetTop(TriangleBorder));
            TriangleBorder.CaptureMouse();
            e.Handled = true;
        }

        private void TriangleBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isTriangleAssistDrawing)
            {
                if (e.LeftButton == MouseButtonState.Pressed)
                {
                    UpdateTriangleAssistPreview(e.GetPosition(inkCanvas));
                    e.Handled = true;
                }
                return;
            }

            if (!_isTriangleDragging || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(GeometryToolsOverlayCanvas);
            WpfCanvas.SetLeft(TriangleBorder, Clamp(p.X - _triangleDragOffset.X, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualWidth - TriangleBorder.ActualWidth)));
            WpfCanvas.SetTop(TriangleBorder, Clamp(p.Y - _triangleDragOffset.Y, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualHeight - TriangleBorder.ActualHeight - 80)));
            e.Handled = true;
        }

        private void TriangleBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_isTriangleAssistDrawing)
            {
                _isTriangleAssistDrawing = false;
                CommitGeometryPreview();
                inkCanvas.ReleaseMouseCapture();
                e.Handled = true;
                return;
            }

            _isTriangleDragging = false;
            TriangleBorder.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void TriangleRotateHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isTriangleRotating = true;
            _triangleLastMousePoint = e.GetPosition(GeometryToolsOverlayCanvas);
            _triangleRotateOffsetDeg = GetPointerAngleAround(GetTrianglePivotWorld(), _triangleLastMousePoint) - _triangleAngleDeg;
            TriangleRotateHandle.CaptureMouse();
            e.Handled = true;
        }

        private void TriangleRotateHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isTriangleRotating || e.LeftButton != MouseButtonState.Pressed) return;
            _triangleAngleDeg = GetPointerAngleAround(GetTrianglePivotWorld(), e.GetPosition(GeometryToolsOverlayCanvas)) - _triangleRotateOffsetDeg;
            ApplyTriangleTransform();
            e.Handled = true;
        }

        private void TriangleRotateHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isTriangleRotating = false;
            TriangleRotateHandle.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void TriangleVertexBHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isTriangleResizeB = true;
            TriangleVertexBHandle.CaptureMouse();
            e.Handled = true;
        }

        private void TriangleVertexBHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isTriangleResizeB || e.LeftButton != MouseButtonState.Pressed) return;
            _triangleLegX = Clamp(GetTriangleLocalFromWorld(e.GetPosition(GeometryToolsOverlayCanvas)).X, GeometryTriangleMinLeg, GeometryTriangleMaxLeg);
            UpdateTriangleGeometry();
            e.Handled = true;
        }

        private void TriangleVertexBHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isTriangleResizeB = false;
            TriangleVertexBHandle.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void TriangleVertexCHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isTriangleResizeC = true;
            TriangleVertexCHandle.CaptureMouse();
            e.Handled = true;
        }

        private void TriangleVertexCHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isTriangleResizeC || e.LeftButton != MouseButtonState.Pressed) return;
            _triangleLegY = Clamp(GetTriangleLocalFromWorld(e.GetPosition(GeometryToolsOverlayCanvas)).Y, GeometryTriangleMinLeg, GeometryTriangleMaxLeg);
            UpdateTriangleGeometry();
            e.Handled = true;
        }

        private void TriangleVertexCHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isTriangleResizeC = false;
            TriangleVertexCHandle.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ProtractorBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (IsOriginalSourceInside(e.OriginalSource, CloseProtractorButton) ||
                IsOriginalSourceInside(e.OriginalSource, ProtractorRotateHandle) ||
                IsOriginalSourceInside(e.OriginalSource, ProtractorResizeHandle))
            {
                return;
            }
            if (_isProtractorRotating || _isProtractorResizing) return;

            var local = GetProtractorLocalFromWorld(e.GetPosition(GeometryToolsOverlayCanvas));
            if (TryPlaceProtractorMark(local))
            {
                e.Handled = true;
                return;
            }

            _isProtractorDragging = true;
            var p = e.GetPosition(GeometryToolsOverlayCanvas);
            _protractorPressPoint = p;
            _protractorDragOffset = new Point(p.X - WpfCanvas.GetLeft(ProtractorBorder), p.Y - WpfCanvas.GetTop(ProtractorBorder));
            ProtractorBorder.CaptureMouse();
            e.Handled = true;
        }

        private void ProtractorBorder_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isProtractorDragging || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(GeometryToolsOverlayCanvas);
            if (Math.Abs(p.X - _protractorPressPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(p.Y - _protractorPressPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            WpfCanvas.SetLeft(ProtractorBorder, Clamp(p.X - _protractorDragOffset.X, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualWidth - ProtractorBorder.ActualWidth)));
            WpfCanvas.SetTop(ProtractorBorder, Clamp(p.Y - _protractorDragOffset.Y, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualHeight - ProtractorBorder.ActualHeight - 80)));
            e.Handled = true;
        }

        private void ProtractorBorder_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isProtractorDragging = false;
            ProtractorBorder.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ProtractorRotateHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isProtractorRotating = true;
            _protractorLastMousePoint = e.GetPosition(GeometryToolsOverlayCanvas);
            _protractorRotateOffsetDeg = GetPointerAngleAround(GetProtractorPivotWorld(), _protractorLastMousePoint) - _protractorAngleDeg;
            ProtractorRotateHandle.CaptureMouse();
            e.Handled = true;
        }

        private void ProtractorRotateHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isProtractorRotating || e.LeftButton != MouseButtonState.Pressed) return;
            _protractorAngleDeg = NormalizeAngle(GetPointerAngleAround(GetProtractorPivotWorld(), e.GetPosition(GeometryToolsOverlayCanvas)) - _protractorRotateOffsetDeg);
            ApplyProtractorTransform();
            e.Handled = true;
        }

        private void ProtractorRotateHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isProtractorRotating = false;
            ProtractorRotateHandle.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ProtractorResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isProtractorResizing = true;
            _protractorLastMousePoint = e.GetPosition(GeometryToolsOverlayCanvas);
            ProtractorResizeHandle.CaptureMouse();
            e.Handled = true;
        }

        private void ProtractorResizeHandle_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isProtractorResizing || e.LeftButton != MouseButtonState.Pressed) return;
            var pivot = GetProtractorPivotWorld();
            var local = GetProtractorLocalFromWorld(e.GetPosition(GeometryToolsOverlayCanvas));
            _protractorRadius = Clamp(Math.Sqrt(local.X * local.X + local.Y * local.Y), GeometryProtractorMinRadius, GeometryProtractorMaxRadius);
            UpdateProtractorGeometry();
            MoveProtractorPivotTo(pivot);
            e.Handled = true;
        }

        private void ProtractorResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isProtractorResizing = false;
            ProtractorResizeHandle.ReleaseMouseCapture();
            e.Handled = true;
        }

        private void ProtractorBorder_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (ProtractorBorder.Visibility != Visibility.Visible) return;
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? 1.0 : 0.1;
            _protractorAngleDeg = NormalizeAngle(_protractorAngleDeg + (e.Delta > 0 ? step : -step));
            ApplyProtractorTransform();
            e.Handled = true;
        }

        private void PlaceRulerAtCenter()
        {
            WpfCanvas.SetLeft(RulerBorder, Math.Max(0, (GeometryToolsOverlayCanvas.ActualWidth - RulerBorder.Width) / 2.0));
            WpfCanvas.SetTop(RulerBorder, Math.Max(0, (GeometryToolsOverlayCanvas.ActualHeight - RulerBorder.Height) / 2.0));
            _rulerAngleDeg = 0;
            ApplyRulerTransform();
        }

        private void UpdateRulerTicks()
        {
            if (RulerTickCanvas == null) return;
            RulerTickCanvas.Children.Clear();
            var width = Math.Max(80.0, RulerBorder.Width - 74);
            var height = Math.Max(40.0, RulerBorder.Height - 16);
            RulerTickCanvas.Width = width;
            RulerTickCanvas.Height = height;
            var brush = GeometryToolTickBrush;
            var centerY = height / 2.0;
            RulerTickCanvas.Children.Add(new Line { X1 = 0, Y1 = 2, X2 = width, Y2 = 2, Stroke = brush, StrokeThickness = 1 });
            RulerTickCanvas.Children.Add(new Line { X1 = 0, Y1 = height - 2, X2 = width, Y2 = height - 2, Stroke = brush, StrokeThickness = 1 });
            RulerTickCanvas.Children.Add(new Line
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
            for (var x = 0.0; x <= width; x += 10.0)
            {
                var index = (int)Math.Round(x / 10.0);
                var major = index % 5 == 0;
                var mid = !major && index % 2 == 0;
                var len = major ? 24 : (mid ? 17 : 11);
                RulerTickCanvas.Children.Add(new Line { X1 = x, Y1 = 2, X2 = x, Y2 = 2 + len, Stroke = brush, StrokeThickness = major ? 1.2 : 1 });
                RulerTickCanvas.Children.Add(new Line { X1 = x, Y1 = height - 2, X2 = x, Y2 = height - 2 - len, Stroke = brush, StrokeThickness = major ? 1.2 : 1 });
                if (!major) continue;
                var label = new TextBlock { Text = (index / 5).ToString(), FontSize = 11, Foreground = brush };
                WpfCanvas.SetLeft(label, x + 3);
                WpfCanvas.SetTop(label, centerY - 8);
                RulerTickCanvas.Children.Add(label);
            }
        }

        private void ApplyRulerTransform()
        {
            RulerBorder.RenderTransform = new RotateTransform(_rulerAngleDeg);
        }

        private void PlaceTriangleAtCenter()
        {
            MoveTrianglePivotTo(new Point(GeometryToolsOverlayCanvas.ActualWidth * 0.55, GeometryToolsOverlayCanvas.ActualHeight * 0.45));
            _triangleAngleDeg = 0;
            ApplyTriangleTransform();
        }

        private void UpdateTriangleGeometry()
        {
            if (TriangleCanvas == null) return;
            var a = new Point(GeometryTrianglePivotLocal, GeometryTrianglePivotLocal);
            var b = new Point(GeometryTrianglePivotLocal + _triangleLegX, GeometryTrianglePivotLocal);
            var c = new Point(GeometryTrianglePivotLocal, GeometryTrianglePivotLocal + _triangleLegY);
            TriangleBorder.Width = _triangleLegX + GeometryTrianglePivotLocal + 36;
            TriangleBorder.Height = _triangleLegY + GeometryTrianglePivotLocal + 36;
            TriangleCanvas.Width = TriangleBorder.Width;
            TriangleCanvas.Height = TriangleBorder.Height;
            TrianglePolygon.Points = new PointCollection { a, b, c };
            WpfCanvas.SetLeft(CloseTriangleButton, a.X + Math.Max(18, _triangleLegX * 0.22));
            WpfCanvas.SetTop(CloseTriangleButton, a.Y + Math.Max(12, _triangleLegY * 0.18));
            UpdateTriangleTicks(a);
            WpfCanvas.SetLeft(TriangleVertexBHandle, b.X - TriangleVertexBHandle.Width * 0.75);
            WpfCanvas.SetTop(TriangleVertexBHandle, b.Y - TriangleVertexBHandle.Height * 0.5);
            WpfCanvas.SetLeft(TriangleVertexCHandle, c.X - TriangleVertexCHandle.Width * 0.5);
            WpfCanvas.SetTop(TriangleVertexCHandle, c.Y - TriangleVertexCHandle.Height * 0.75);
            WpfCanvas.SetLeft(TriangleRotateHandle, GeometryTrianglePivotLocal + _triangleLegX * 0.58 - TriangleRotateHandle.Width / 2.0);
            WpfCanvas.SetTop(TriangleRotateHandle, GeometryTrianglePivotLocal + _triangleLegY * 0.58 - TriangleRotateHandle.Height / 2.0);
            ApplyTriangleTransform();
        }

        private void UpdateTriangleTicks(Point a)
        {
            TriangleTickCanvas.Children.Clear();
            TriangleTickCanvas.Width = TriangleCanvas.Width;
            TriangleTickCanvas.Height = TriangleCanvas.Height;
            var brush = GeometryToolSubtleTickBrush;
            DrawTriangleLegTicks(true, a, brush);
            DrawTriangleLegTicks(false, a, brush);
        }

        private void DrawTriangleLegTicks(bool horizontal, Point a, Brush brush)
        {
            var legLength = horizontal ? _triangleLegX : _triangleLegY;
            var firstTick = GeometryTriangleTickStartOffset;
            var lastTick = Math.Max(firstTick, legLength - GeometryTriangleTickEndInset);
            for (var pos = firstTick; pos <= lastTick; pos += 10.0)
            {
                var index = (int)Math.Round((pos - firstTick) / 10.0);
                var major = index % 5 == 0;
                var mid = !major && index % 2 == 0;
                var len = major ? 18 : (mid ? 13 : 8);
                if (horizontal)
                {
                    var x = a.X + pos;
                    TriangleTickCanvas.Children.Add(new Line { X1 = x, Y1 = a.Y, X2 = x, Y2 = a.Y + len, Stroke = brush, StrokeThickness = 1 });
                    if (major)
                    {
                        var label = new TextBlock { Text = (index / 5).ToString(), FontSize = 11, Foreground = brush };
                        WpfCanvas.SetLeft(label, x - 4);
                        WpfCanvas.SetTop(label, a.Y + len + 1);
                        TriangleTickCanvas.Children.Add(label);
                    }
                }
                else
                {
                    var y = a.Y + pos;
                    TriangleTickCanvas.Children.Add(new Line { X1 = a.X, Y1 = y, X2 = a.X + len, Y2 = y, Stroke = brush, StrokeThickness = 1 });
                    if (major)
                    {
                        var label = new TextBlock { Text = (index / 5).ToString(), FontSize = 11, Foreground = brush };
                        WpfCanvas.SetLeft(label, a.X + len + 2);
                        WpfCanvas.SetTop(label, y - 7);
                        TriangleTickCanvas.Children.Add(label);
                    }
                }
            }
        }

        private void ApplyTriangleTransform()
        {
            TriangleBorder.RenderTransform = new RotateTransform(_triangleAngleDeg, GeometryTrianglePivotLocal, GeometryTrianglePivotLocal);
        }

        private void MoveTrianglePivotTo(Point pivot)
        {
            WpfCanvas.SetLeft(TriangleBorder, Clamp(pivot.X - GeometryTrianglePivotLocal, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualWidth - TriangleBorder.Width)));
            WpfCanvas.SetTop(TriangleBorder, Clamp(pivot.Y - GeometryTrianglePivotLocal, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualHeight - TriangleBorder.Height - 80)));
        }

        private void UpdateProtractorGeometry()
        {
            if (ProtractorCanvas == null) return;
            _protractorRadius = Clamp(_protractorRadius, GeometryProtractorMinRadius, GeometryProtractorMaxRadius);
            var width = _protractorRadius * 2 + GeometryProtractorPadding * 2;
            var height = _protractorRadius + GeometryProtractorPadding + 56;
            var center = new Point(GeometryProtractorPadding + _protractorRadius, GeometryProtractorPadding + _protractorRadius);
            ProtractorBorder.Width = width;
            ProtractorBorder.Height = height;
            ProtractorCanvas.Width = width;
            ProtractorCanvas.Height = height;
            ProtractorTickCanvas.Width = width;
            ProtractorTickCanvas.Height = height;

            var start = new Point(center.X - _protractorRadius, center.Y);
            var end = new Point(center.X + _protractorRadius, center.Y);
            var figure = new PathFigure { StartPoint = start, IsClosed = true, IsFilled = true };
            figure.Segments.Add(new ArcSegment { Point = end, Size = new Size(_protractorRadius, _protractorRadius), SweepDirection = SweepDirection.Clockwise, IsLargeArc = false });
            figure.Segments.Add(new LineSegment(start, true));
            ProtractorFillPath.Data = new PathGeometry(new[] { figure });

            WpfCanvas.SetLeft(ProtractorPivotDot, center.X - ProtractorPivotDot.Width / 2.0);
            WpfCanvas.SetTop(ProtractorPivotDot, center.Y - ProtractorPivotDot.Height / 2.0);
            WpfCanvas.SetLeft(CloseProtractorButton, center.X - CloseProtractorButton.Width / 2.0);
            WpfCanvas.SetTop(CloseProtractorButton, Math.Max(4, center.Y - _protractorRadius + GeometryProtractorCloseButtonDrop));
            WpfCanvas.SetLeft(ProtractorRotateHandle, center.X - ProtractorRotateHandle.Width / 2.0);
            WpfCanvas.SetTop(ProtractorRotateHandle, center.Y + 14);
            WpfCanvas.SetLeft(ProtractorResizeHandle, center.X + _protractorRadius - ProtractorResizeHandle.Width / 2.0);
            WpfCanvas.SetTop(ProtractorResizeHandle, center.Y - ProtractorResizeHandle.Height / 2.0);
            UpdateProtractorTicks(center, _protractorRadius);
            ApplyProtractorTransform();
        }

        private void UpdateProtractorTicks(Point center, double radius)
        {
            ProtractorTickCanvas.Children.Clear();
            var tickBrush = GeometryToolSubtleTickBrush;
            var textBrush = GeometryToolTickBrush;
            ProtractorTickCanvas.Children.Add(new Line { X1 = center.X - radius, Y1 = center.Y, X2 = center.X + radius, Y2 = center.Y, Stroke = tickBrush, StrokeThickness = 2 });
            for (var deg = 0; deg <= 180; deg++)
            {
                var radians = Math.PI - deg * Math.PI / 180.0;
                var major = deg % 10 == 0;
                var mid = !major && deg % 5 == 0;
                var len = major ? 16 : (mid ? 11 : 6);
                var outer = new Point(center.X + Math.Cos(radians) * radius, center.Y - Math.Sin(radians) * radius);
                var inner = new Point(center.X + Math.Cos(radians) * (radius - len), center.Y - Math.Sin(radians) * (radius - len));
                ProtractorTickCanvas.Children.Add(new Line { X1 = outer.X, Y1 = outer.Y, X2 = inner.X, Y2 = inner.Y, Stroke = tickBrush, StrokeThickness = major ? 1.4 : (mid ? 1.1 : 0.9) });
                if (!major) continue;
                AddProtractorLabel(center, radius - 28, radians, deg.ToString(), deg == 0 || deg == 90 || deg == 180 ? 12 : 10, textBrush, FontWeights.SemiBold);
                AddProtractorLabel(center, radius - 48, radians, (180 - deg).ToString(), 9, textBrush, FontWeights.Normal);
            }
        }

        private void AddProtractorLabel(Point center, double labelRadius, double radians, string text, double fontSize, Brush foreground, FontWeight weight)
        {
            var labelPos = new Point(center.X + Math.Cos(radians) * labelRadius, center.Y - Math.Sin(radians) * labelRadius);
            var label = new TextBlock { Text = text, FontSize = fontSize, FontWeight = weight, Foreground = foreground };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            WpfCanvas.SetLeft(label, labelPos.X - label.DesiredSize.Width / 2.0);
            WpfCanvas.SetTop(label, labelPos.Y - label.DesiredSize.Height / 2.0);
            ProtractorTickCanvas.Children.Add(label);
        }

        private bool TryPlaceProtractorMark(Point local)
        {
            var radius = _protractorRadius;
            var distance = Math.Sqrt(local.X * local.X + local.Y * local.Y);
            if (local.Y > 10 || distance < radius - 26 || distance > radius + 10)
            {
                return false;
            }

            var rawDegree = Math.Atan2(-local.Y, local.X) * 180.0 / Math.PI;
            var degree = Clamp(Math.Round(180.0 - rawDegree), 0, 180);
            var radians = Math.PI - degree * Math.PI / 180.0;
            var localPoint = new Point(Math.Cos(radians) * _protractorRadius, -Math.Sin(radians) * _protractorRadius);
            AddDetachedProtractorMarkDot(GetProtractorWorldFromLocal(localPoint));
            return true;
        }

        private void AddDetachedProtractorMarkDot(Point worldPoint)
        {
            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(Color.FromRgb(42, 106, 217)),
                Stroke = Brushes.White,
                StrokeThickness = 1.4,
                IsHitTestVisible = false,
                Tag = "GeometryProtractorMark"
            };
            WpfCanvas.SetLeft(dot, worldPoint.X - dot.Width / 2.0);
            WpfCanvas.SetTop(dot, worldPoint.Y - dot.Height / 2.0);
            Panel.SetZIndex(dot, 19);
            GeometryToolsOverlayCanvas.Children.Add(dot);
        }

        private void PlaceProtractorAtCenter()
        {
            MoveProtractorPivotTo(new Point(GeometryToolsOverlayCanvas.ActualWidth * 0.5, GeometryToolsOverlayCanvas.ActualHeight * 0.52));
            _protractorAngleDeg = 0;
            ApplyProtractorTransform();
        }

        private void MoveProtractorPivotTo(Point pivot)
        {
            var local = GetProtractorPivotLocal();
            WpfCanvas.SetLeft(ProtractorBorder, Clamp(pivot.X - local.X, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualWidth - ProtractorBorder.Width)));
            WpfCanvas.SetTop(ProtractorBorder, Clamp(pivot.Y - local.Y, 0, Math.Max(0, GeometryToolsOverlayCanvas.ActualHeight - ProtractorBorder.Height - 80)));
        }

        private void ApplyProtractorTransform()
        {
            var pivot = GetProtractorPivotLocal();
            ProtractorBorder.RenderTransform = new RotateTransform(_protractorAngleDeg, pivot.X, pivot.Y);
        }

        private bool TryBeginRulerAssist(Point mouse)
        {
            if (!IsGeometryDrawingAvailable() || RulerBorder.Visibility != Visibility.Visible) return false;
            var local = GetMouseLocalToRuler(mouse);
            var isNearEdge = Math.Abs(Math.Abs(local.LocalY) - local.HalfHeight) <= 18;
            if (local.LocalX < 44 || local.LocalX > local.RulerWidth - 36 || !isNearEdge) return false;
            _isRulerAssistDrawing = true;
            _rulerAssistStartT = Clamp(local.LocalX, 44, local.RulerWidth - 36);
            _rulerAssistOffset = local.LocalY < 0 ? -local.HalfHeight : local.HalfHeight;
            inkCanvas.CaptureMouse();
            UpdateRulerAssistPreview(mouse);
            return true;
        }

        private void UpdateRulerAssistPreview(Point current)
        {
            var local = GetMouseLocalToRuler(current);
            var startT = _rulerAssistStartT;
            var endT = Clamp(local.LocalX, 44, local.RulerWidth - 36);
            var pivot = GetRulerPivot();
            var axis = GetRulerAxis();
            var normal = new Vector(-axis.Y, axis.X);
            ReplaceGeometryPreview(CreateLineStrokeCollection(pivot + axis * startT + normal * _rulerAssistOffset, pivot + axis * endT + normal * _rulerAssistOffset));
        }

        private bool TryBeginTriangleAssist(Point mouse)
        {
            if (!IsGeometryDrawingAvailable() || TriangleBorder.Visibility != Visibility.Visible) return false;
            var edge = GetNearestTriangleEdgeProjection(GetTriangleLocalFromWorld(mouse), null);
            if (edge.Distance > 18.0) return false;
            _isTriangleAssistDrawing = true;
            _triangleAssistEdge = edge.Edge;
            _triangleAssistStartParam = edge.Param;
            inkCanvas.CaptureMouse();
            UpdateTriangleAssistPreview(mouse);
            return true;
        }

        private void UpdateTriangleAssistPreview(Point current)
        {
            var edge = GetNearestTriangleEdgeProjection(GetTriangleLocalFromWorld(current), _triangleAssistEdge);
            ReplaceGeometryPreview(CreateLineStrokeCollection(
                GetTriangleWorldFromLocal(GetTriangleEdgePoint(_triangleAssistEdge, _triangleAssistStartParam)),
                GetTriangleWorldFromLocal(GetTriangleEdgePoint(_triangleAssistEdge, edge.Param))));
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

        private GeometryTriangleProjection GetNearestTriangleEdgeProjection(Point p, GeometryTriangleEdgeType? prefer)
        {
            var best = new GeometryTriangleProjection(GeometryTriangleEdgeType.AB, 0, double.MaxValue);
            CheckTriangleEdgeProjection(GeometryTriangleEdgeType.AB, p, prefer, ref best);
            CheckTriangleEdgeProjection(GeometryTriangleEdgeType.AC, p, prefer, ref best);
            CheckTriangleEdgeProjection(GeometryTriangleEdgeType.BC, p, prefer, ref best);
            return best;
        }

        private void CheckTriangleEdgeProjection(GeometryTriangleEdgeType edge, Point p, GeometryTriangleEdgeType? prefer, ref GeometryTriangleProjection best)
        {
            var projection = ProjectToTriangleEdge(edge, p);
            if (prefer.HasValue && edge != prefer.Value) projection.Distance += 0.8;
            if (projection.Distance < best.Distance) best = projection;
        }

        private GeometryTriangleProjection ProjectToTriangleEdge(GeometryTriangleEdgeType edge, Point p)
        {
            if (edge == GeometryTriangleEdgeType.AB)
                return new GeometryTriangleProjection(edge, Clamp(p.X / _triangleLegX, 0, 1), Math.Abs(p.Y));
            if (edge == GeometryTriangleEdgeType.AC)
                return new GeometryTriangleProjection(edge, Clamp(p.Y / _triangleLegY, 0, 1), Math.Abs(p.X));
            var segment = ProjectPointToSegment(new Point(_triangleLegX, 0), new Point(0, _triangleLegY), p);
            return new GeometryTriangleProjection(edge, segment.Param, segment.Distance);
        }

        private Point GetTriangleEdgePoint(GeometryTriangleEdgeType edge, double param)
        {
            var t = Clamp(param, 0, 1);
            if (edge == GeometryTriangleEdgeType.AB) return new Point(_triangleLegX * t, 0);
            if (edge == GeometryTriangleEdgeType.AC) return new Point(0, _triangleLegY * t);
            return new Point(_triangleLegX * (1 - t), _triangleLegY * t);
        }

        private GeometryTriangleProjection ProjectPointToSegment(Point a, Point b, Point p)
        {
            var ab = b - a;
            var ap = p - a;
            var denom = ab.X * ab.X + ab.Y * ab.Y;
            if (denom < 0.0001) return new GeometryTriangleProjection(GeometryTriangleEdgeType.BC, 0, (p - a).Length);
            var t = Clamp((ap.X * ab.X + ap.Y * ab.Y) / denom, 0, 1);
            var proj = a + ab * t;
            return new GeometryTriangleProjection(GeometryTriangleEdgeType.BC, t, (p - proj).Length);
        }

        private bool IsInRulerDragBand(Point mouse)
        {
            var local = GetMouseLocalToRuler(mouse);
            return local.LocalX >= 44 && local.LocalX <= local.RulerWidth - 36 && Math.Abs(local.LocalY) <= 10;
        }

        private bool IsNearRulerDrawingEdge(Point mouse)
        {
            var local = GetMouseLocalToRuler(mouse);
            return local.LocalX >= 44 && local.LocalX <= local.RulerWidth - 36 &&
                   Math.Abs(Math.Abs(local.LocalY) - local.HalfHeight) <= 18;
        }

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

        private Point GetRulerPivot()
        {
            var left = WpfCanvas.GetLeft(RulerBorder);
            var top = WpfCanvas.GetTop(RulerBorder);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            return new Point(left, top + RulerBorder.Height / 2.0);
        }

        private Vector GetRulerAxis()
        {
            var rad = _rulerAngleDeg * Math.PI / 180.0;
            return new Vector(Math.Cos(rad), Math.Sin(rad));
        }

        private GeometryRulerLocal GetMouseLocalToRuler(Point mouse)
        {
            var pivot = GetRulerPivot();
            var axis = GetRulerAxis();
            var normal = new Vector(-axis.Y, axis.X);
            var v = mouse - pivot;
            return new GeometryRulerLocal(v.X * axis.X + v.Y * axis.Y, v.X * normal.X + v.Y * normal.Y, RulerBorder.Width, RulerBorder.Height / 2.0);
        }

        private Point GetTrianglePivotWorld()
        {
            var left = WpfCanvas.GetLeft(TriangleBorder);
            var top = WpfCanvas.GetTop(TriangleBorder);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            return new Point(left + GeometryTrianglePivotLocal, top + GeometryTrianglePivotLocal);
        }

        private Vector GetTriangleAxisX()
        {
            var rad = _triangleAngleDeg * Math.PI / 180.0;
            return new Vector(Math.Cos(rad), Math.Sin(rad));
        }

        private Vector GetTriangleAxisY()
        {
            var x = GetTriangleAxisX();
            return new Vector(-x.Y, x.X);
        }

        private Point GetTriangleLocalFromWorld(Point world)
        {
            var pivot = GetTrianglePivotWorld();
            var v = world - pivot;
            var ax = GetTriangleAxisX();
            var ay = GetTriangleAxisY();
            return new Point(v.X * ax.X + v.Y * ax.Y, v.X * ay.X + v.Y * ay.Y);
        }

        private Point GetTriangleWorldFromLocal(Point local)
        {
            var pivot = GetTrianglePivotWorld();
            var ax = GetTriangleAxisX();
            var ay = GetTriangleAxisY();
            return pivot + ax * local.X + ay * local.Y;
        }

        private Point GetProtractorPivotLocal()
        {
            return new Point(GeometryProtractorPadding + _protractorRadius, GeometryProtractorPadding + _protractorRadius);
        }

        private Point GetProtractorPivotWorld()
        {
            var left = WpfCanvas.GetLeft(ProtractorBorder);
            var top = WpfCanvas.GetTop(ProtractorBorder);
            if (double.IsNaN(left)) left = 0;
            if (double.IsNaN(top)) top = 0;
            var local = GetProtractorPivotLocal();
            return new Point(left + local.X, top + local.Y);
        }

        private Vector GetProtractorAxisX()
        {
            var rad = _protractorAngleDeg * Math.PI / 180.0;
            return new Vector(Math.Cos(rad), Math.Sin(rad));
        }

        private Vector GetProtractorAxisY()
        {
            var x = GetProtractorAxisX();
            return new Vector(-x.Y, x.X);
        }

        private Point GetProtractorLocalFromWorld(Point world)
        {
            var pivot = GetProtractorPivotWorld();
            var v = world - pivot;
            var ax = GetProtractorAxisX();
            var ay = GetProtractorAxisY();
            return new Point(v.X * ax.X + v.Y * ax.Y, v.X * ay.X + v.Y * ay.Y);
        }

        private Point GetProtractorWorldFromLocal(Point local)
        {
            var pivot = GetProtractorPivotWorld();
            var ax = GetProtractorAxisX();
            var ay = GetProtractorAxisY();
            return pivot + ax * local.X + ay * local.Y;
        }

        private static double GetPointerAngleAround(Point center, Point pointer)
        {
            var v = pointer - center;
            return Math.Atan2(v.Y, v.X) * 180 / Math.PI;
        }

        private double GetPointerAngle(Point pointer)
        {
            var v = pointer - GetRulerPivot();
            return Math.Atan2(v.Y, v.X) * 180 / Math.PI;
        }

        private static double NormalizeAngle(double angle)
        {
            var normalized = angle % 360.0;
            if (normalized < 0) normalized += 360.0;
            return normalized;
        }

        private static double Clamp(double value, double min, double max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}

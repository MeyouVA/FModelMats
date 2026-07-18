using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using FModel.ViewModels;

namespace FModel.Views;

public partial class BlueprintGraphViewer
{
    private const double NodeWidth = 300;
    private const double HeaderHeight = 28;
    private const double BodyTopPad = 6;
    private const double BodyLineHeight = 15;
    private const double PinRowHeight = 20;
    private const double NodeCornerRadius = 8;
    private const double BezierHorizontalOffset = 60;
    private const double PanThreshold = 5.0;
    private const string AllFunctionsLabel = "All Functions";

    private readonly BlueprintGraphViewModel _viewModel;

    private Canvas _canvas;
    private ScaleTransform _scaleTransform;
    private TranslateTransform _translateTransform;

    private readonly Dictionary<BlueprintGraphNode, (double width, double height)> _nodeSizes = new();
    private readonly Dictionary<(BlueprintGraphNode node, string pinName, bool isOutput), Point> _pinPositions = new();
    private readonly Dictionary<BlueprintGraphNode, Border> _nodeBorders = new();
    private readonly Dictionary<BlueprintGraphNode, Border> _nodeShadows = new();
    private readonly Dictionary<BlueprintGraphConnection, Path> _connectionPaths = new();
    /// <summary>Input pins that have an incoming connection this draw pass — wired data pins render filled without a value label.</summary>
    private readonly HashSet<(BlueprintGraphNode node, string pinName)> _wiredInputPins = [];

    private BlueprintGraphNode _selectedNode;
    private readonly HashSet<BlueprintGraphNode> _selectedNodes = [];

    private bool _isPanning;
    private Point _lastMousePos;
    private Point _panStartPos;

    /// <summary>Nodes the user hid with H; cumulative until Alt+H. Hidden nodes and their edges are not drawn.</summary>
    private readonly HashSet<BlueprintGraphNode> _hiddenNodes = [];
    /// <summary>When set, only this function's nodes are shown; null shows every function.</summary>
    private string _isolatedFunction;
    private HashSet<BlueprintGraphNode> _isolatedNodes;
    private bool _suppressFilterEvents;

    private BlueprintGraphNode _draggedNode;
    private bool _isDraggingNode;
    private Point _dragStartCanvasPos;
    private readonly Dictionary<BlueprintGraphNode, Point> _dragOrigins = new();

    private bool _potentialMarquee;
    private bool _isMarqueeSelecting;
    private Point _marqueeStartCanvas;
    private Rectangle _marqueeRect;

    private static readonly BlurEffect _nodeShadowBlur = CreateFrozenBlur(8);

    private static BlurEffect CreateFrozenBlur(double radius)
    {
        var blur = new BlurEffect { Radius = radius };
        blur.Freeze();
        return blur;
    }

    public BlueprintGraphViewer(BlueprintGraphViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        if (!string.IsNullOrEmpty(viewModel.PackageName))
            Title = $"Blueprint Graph Viewer - {viewModel.PackageName}";
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PackageNameText.Text = string.IsNullOrEmpty(_viewModel.ClassName) ? _viewModel.PackageName : _viewModel.ClassName;
        NodeCountText.Text = $"Nodes: {_viewModel.Nodes.Count}";
        ConnectionCountText.Text = $"Connections: {_viewModel.Connections.Count}";
        DetailsButton.IsEnabled = _viewModel.ClassInfo.Count > 0 || _viewModel.Functions.Count > 0;
        if (!string.IsNullOrEmpty(_viewModel.ClassName))
            ClassChainText.Text = string.IsNullOrEmpty(_viewModel.ParentClass)
                ? _viewModel.ClassName
                : $"{_viewModel.ClassName} : {_viewModel.ParentClass}";

        PopulateFunctionCombo();
        UpdateHiddenCount();
        SetupCanvas();
        DrawGraph();
        if (_viewModel.ClassInfo.Count > 0 || _viewModel.Functions.Count > 0)
            ShowDetails();
        Dispatcher.BeginInvoke(FitToView);
    }

    private void SetupCanvas()
    {
        _canvas = new Canvas();
        _scaleTransform = new ScaleTransform(1, 1);
        _translateTransform = new TranslateTransform(0, 0);
        var transformGroup = new TransformGroup();
        transformGroup.Children.Add(_scaleTransform);
        transformGroup.Children.Add(_translateTransform);
        _canvas.RenderTransform = transformGroup;

        CanvasHost.Child = _canvas;
        CanvasHost.MouseWheel += OnMouseWheel;
        CanvasHost.AddHandler(MouseLeftButtonDownEvent, new MouseButtonEventHandler(OnCanvasMouseDown), true);
        CanvasHost.MouseLeftButtonUp += OnCanvasMouseUp;
        CanvasHost.MouseMove += OnCanvasMouseMove;
        CanvasHost.MouseDown += OnPanMouseDown;
        CanvasHost.MouseUp += OnPanMouseUp;
    }

    // ---- Function isolation (view only) ----

    private void PopulateFunctionCombo()
    {
        _suppressFilterEvents = true;
        FunctionCombo.Items.Clear();
        FunctionCombo.Items.Add(AllFunctionsLabel);
        foreach (var fn in _viewModel.Functions)
            FunctionCombo.Items.Add(fn.Name);
        FunctionCombo.SelectedIndex = 0;
        FunctionCombo.IsEnabled = _viewModel.Functions.Count > 1;
        _suppressFilterEvents = false;
    }

    private void OnFunctionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents || _canvas == null) return;
        IsolateFunction(FunctionCombo.SelectedItem as string);
    }

    private void IsolateFunction(string name)
    {
        if (string.IsNullOrEmpty(name) || name == AllFunctionsLabel)
        {
            _isolatedFunction = null;
            _isolatedNodes = null;
        }
        else
        {
            var fn = _viewModel.Functions.FirstOrDefault(f => f.Name == name);
            _isolatedFunction = name;
            _isolatedNodes = fn != null ? new HashSet<BlueprintGraphNode>(fn.Nodes) : null;
            if (FunctionCombo.SelectedItem as string != name)
            {
                _suppressFilterEvents = true;
                FunctionCombo.SelectedItem = name;
                _suppressFilterEvents = false;
            }
        }
        Redraw();
        FitToView();
    }

    // ---- Hide / unhide ----

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // let the combo / text controls keep their own keys
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        if (e.Key == Key.H)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) UnhideAll();
            else HideSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && _selectedNodes.Count > 0)
        {
            CopySelectedNodes();
            e.Handled = true;
        }
    }

    // ---- Copy to clipboard ----

    /// <summary>Copies the selected nodes' full text (title + untruncated statement), in bytecode order.</summary>
    private void CopySelectedNodes()
    {
        var parts = _selectedNodes
            .OrderBy(n => n.FunctionName, StringComparer.Ordinal)
            .ThenBy(n => n.StatementIndex)
            .Select(n => string.IsNullOrWhiteSpace(n.Body) ? n.Title : $"{n.Title}\n{n.Body}");
        TrySetClipboard(string.Join("\n\n", parts));
    }

    /// <summary>Copies every text row currently shown in the properties panel.</summary>
    private void OnCopyPanel(object sender, RoutedEventArgs e)
    {
        var lines = new List<string> { PropertiesTitleText.Text };
        void Walk(DependencyObject element)
        {
            switch (element)
            {
                case TextBlock tb when !string.IsNullOrWhiteSpace(tb.Text): lines.Add(tb.Text); return;
                case TextBox tb when !string.IsNullOrWhiteSpace(tb.Text): lines.Add(tb.Text); return;
                case Button b when b.Content is string s: lines.Add(s); return;
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
                Walk(VisualTreeHelper.GetChild(element, i));
        }
        Walk(PropertiesPanel);
        TrySetClipboard(string.Join("\n", lines));
    }

    private static void TrySetClipboard(string text)
    {
        try
        {
            Clipboard.SetDataObject(text ?? string.Empty, true);
        }
        catch
        {
            // the Win32 clipboard can be transiently locked by another process; nothing useful to do
        }
    }

    private void OnHideSelectedClicked(object sender, RoutedEventArgs e) => HideSelected();
    private void OnUnhideAllClicked(object sender, RoutedEventArgs e) => UnhideAll();

    private void OnShowCodeToggled(object sender, RoutedEventArgs e)
    {
        if (_canvas == null) return;
        _viewModel.SetStatementTextVisible(ShowCodeToggle.IsChecked == true);
        Redraw();
        FitToView();
    }

    private void HideSelected()
    {
        if (_canvas == null || _selectedNodes.Count == 0) return;
        foreach (var node in _selectedNodes) _hiddenNodes.Add(node); // cumulative
        _selectedNodes.Clear();
        _selectedNode = null;
        UpdateHiddenCount();
        UpdateSelectionStatus();
        Redraw();
    }

    private void UnhideAll()
    {
        if (_canvas == null || _hiddenNodes.Count == 0) return;
        _hiddenNodes.Clear();
        UpdateHiddenCount();
        Redraw();
    }

    private void UpdateHiddenCount() =>
        HiddenCountText.Text = _hiddenNodes.Count > 0 ? $"Hidden: {_hiddenNodes.Count}" : string.Empty;

    private void Redraw()
    {
        DrawGraph();
        foreach (var node in _selectedNodes)
            if (IsNodeVisible(node)) ApplySelectionVisual(node, true);
    }

    /// <summary>A node is drawn unless the user hid it (H) or a function isolation is active and the node is in another function.</summary>
    private bool IsNodeVisible(BlueprintGraphNode node) =>
        !_hiddenNodes.Contains(node) && (_isolatedNodes == null || _isolatedNodes.Contains(node));

    // ---- Drawing ----

    private void DrawGraph()
    {
        _canvas.Children.Clear();
        _nodeSizes.Clear();
        _pinPositions.Clear();
        _nodeBorders.Clear();
        _nodeShadows.Clear();
        _connectionPaths.Clear();

        _wiredInputPins.Clear();
        foreach (var connection in _viewModel.Connections)
            if (IsNodeVisible(connection.SourceNode) && IsNodeVisible(connection.TargetNode))
                _wiredInputPins.Add((connection.TargetNode, connection.TargetPinName));

        foreach (var node in _viewModel.Nodes)
            if (IsNodeVisible(node))
                ComputeNodeGeometry(node);

        foreach (var connection in _viewModel.Connections)
            if (IsNodeVisible(connection.SourceNode) && IsNodeVisible(connection.TargetNode))
                DrawConnection(connection);

        foreach (var node in _viewModel.Nodes)
            if (IsNodeVisible(node))
                DrawNode(node);
    }

    private void ComputeNodeGeometry(BlueprintGraphNode node)
    {
        var height = node.Height;
        _nodeSizes[node] = (NodeWidth, height);

        // exec-in and the first exec-out share the first pin row, so flow reads straight across;
        // data pins stack in the rows below on their own side
        var firstPinY = node.NodePosY + HeaderHeight + BodyTopPad + PinRowHeight / 2;
        for (var i = 0; i < node.InputPins.Count; i++)
            _pinPositions[(node, node.InputPins[i].Name, false)] =
                new Point(node.NodePosX, firstPinY + i * PinRowHeight);

        for (var i = 0; i < node.OutputPins.Count; i++)
            _pinPositions[(node, node.OutputPins[i].Name, true)] =
                new Point(node.NodePosX + NodeWidth, firstPinY + i * PinRowHeight);
    }

    private void DrawConnection(BlueprintGraphConnection connection)
    {
        if (!_pinPositions.TryGetValue((connection.SourceNode, connection.SourcePinName, true), out var start) ||
            !_pinPositions.TryGetValue((connection.TargetNode, connection.TargetPinName, false), out var end))
            return;

        var color = GetEdgeColor(connection.Kind);
        var isData = connection.Kind == EBlueprintEdgeKind.Data;
        var path = new Path
        {
            Data = BuildConnectionGeometry(start, end),
            Stroke = new SolidColorBrush(Color.FromArgb(isData ? (byte)185 : (byte)210, color.R, color.G, color.B)),
            StrokeThickness = isData ? 1.7 : 2.4,
            IsHitTestVisible = false
        };
        // back-edges (jumps upward) read better dashed so they don't look like normal flow
        if (connection.Kind is (EBlueprintEdgeKind.Jump or EBlueprintEdgeKind.Push or EBlueprintEdgeKind.Call) && end.Y < start.Y)
            path.StrokeDashArray = new DoubleCollection { 6, 4 };
        Panel.SetZIndex(path, 1);
        _canvas.Children.Add(path);
        _connectionPaths[connection] = path;
    }

    private static PathGeometry BuildConnectionGeometry(Point start, Point end)
    {
        var geometry = new PathGeometry();
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        var offset = Math.Max(Math.Abs(end.X - start.X) / 2, BezierHorizontalOffset);
        figure.Segments.Add(new BezierSegment(
            new Point(start.X + offset, start.Y),
            new Point(end.X - offset, end.Y),
            end, true));
        geometry.Figures.Add(figure);
        geometry.Freeze();
        return geometry;
    }

    private void DrawNode(BlueprintGraphNode node)
    {
        var (width, height) = _nodeSizes[node];

        var shadow = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(NodeCornerRadius),
            Background = Brushes.Black,
            Opacity = 0.45,
            Effect = _nodeShadowBlur
        };
        Canvas.SetLeft(shadow, node.NodePosX + 3);
        Canvas.SetTop(shadow, node.NodePosY + 4);
        Panel.SetZIndex(shadow, 0);
        _canvas.Children.Add(shadow);
        _nodeShadows[node] = shadow;

        // body panel: a subtle vertical gradient like a real Blueprint node
        var bodyBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        bodyBrush.GradientStops.Add(new GradientStop(Color.FromArgb(245, 48, 48, 54), 0));
        bodyBrush.GradientStops.Add(new GradientStop(Color.FromArgb(245, 34, 34, 39), 1));
        bodyBrush.Freeze();

        var border = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(NodeCornerRadius),
            Background = bodyBrush,
            BorderBrush = DefaultBorderBrush(),
            BorderThickness = new Thickness(1),
            SnapsToDevicePixels = true,
            Cursor = Cursors.Hand,
            Tag = node
        };
        border.MouseLeftButtonDown += OnNodeClicked;
        border.MouseMove += OnNodeMouseMove;
        border.MouseLeftButtonUp += OnNodeMouseUp;
        border.LostMouseCapture += OnNodeLostCapture;

        var content = new Canvas { Width = width, Height = height };

        // title bar with a left→dark gradient in the node-kind colour
        var headerColor = GetNodeHeaderColor(node.Kind);
        var headerBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        headerBrush.GradientStops.Add(new GradientStop(headerColor, 0));
        headerBrush.GradientStops.Add(new GradientStop(Color.FromArgb(255,
            (byte)(headerColor.R * 0.45), (byte)(headerColor.G * 0.45), (byte)(headerColor.B * 0.45)), 1));
        headerBrush.Freeze();

        var header = new Border
        {
            Width = width,
            Height = HeaderHeight,
            Background = headerBrush,
            CornerRadius = new CornerRadius(NodeCornerRadius, NodeCornerRadius, 0, 0)
        };
        var headerGrid = new Grid { Margin = new Thickness(10, 0, 8, 0) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = new TextBlock
        {
            Text = node.Title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(title, 0);
        headerGrid.Children.Add(title);
        if (node.StatementIndex >= 0 && node.Kind != EBlueprintNodeKind.VariableGet) // Gets borrow their consumer's index for layout only
        {
            var offset = new TextBlock
            {
                Text = $"@{node.StatementIndex}",
                Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)),
                FontSize = 9,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(offset, 1);
            headerGrid.Children.Add(offset);
        }
        header.Child = headerGrid;
        content.Children.Add(header);

        var pinRows = Math.Max(Math.Max(node.InputPins.Count, node.OutputPins.Count), 1);

        // body text below the pin rows (truncated to the reserved line count; full text lives in the panel)
        if (!string.IsNullOrEmpty(node.Body) && node.BodyLineCount > 0)
        {
            var body = new TextBlock
            {
                Text = node.Body,
                Foreground = new SolidColorBrush(Color.FromRgb(206, 208, 214)),
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Width = width - 26,
                MaxHeight = node.BodyLineCount * BodyLineHeight + 2
            };
            Canvas.SetLeft(body, 14);
            Canvas.SetTop(body, HeaderHeight + BodyTopPad + pinRows * PinRowHeight);
            content.Children.Add(body);
        }

        // exec pins as white triangles, data pins as circles — Blueprint style. When both sides
        // have a pin on the same row, each side's label gets a budget so they can never overlap.
        var firstPinY = HeaderHeight + BodyTopPad + PinRowHeight / 2;
        for (var i = 0; i < node.InputPins.Count; i++)
        {
            var shared = i < node.OutputPins.Count;
            AddNodePin(content, node.InputPins[i], isOutput: false, width, firstPinY + i * PinRowHeight,
                _wiredInputPins.Contains((node, node.InputPins[i].Name)),
                shared ? width * 0.55 - 14 : width - 20);
        }
        for (var i = 0; i < node.OutputPins.Count; i++)
        {
            var shared = i < node.InputPins.Count;
            AddNodePin(content, node.OutputPins[i], isOutput: true, width, firstPinY + i * PinRowHeight, wired: true,
                shared ? width * 0.45 - 14 : width - 20);
        }

        border.Child = content;
        Canvas.SetLeft(border, node.NodePosX);
        Canvas.SetTop(border, node.NodePosY);
        Panel.SetZIndex(border, 2);
        _canvas.Children.Add(border);
        _nodeBorders[node] = border;
    }

    /// <summary>
    /// Draws one pin at the node edge: exec pins as right-pointing triangles, data pins as circles
    /// (filled when wired, hollow when the value is a literal shown in the label). Data pins are
    /// labelled on their own side; unwired data inputs also show the decoded argument.
    /// </summary>
    private static void AddNodePin(Canvas content, BlueprintGraphPin pin, bool isOutput, double width, double cy, bool wired,
        double maxLabelWidth)
    {
        var kindColor = GetEdgeColor(pin.Kind);
        if (pin.IsData)
        {
            var circle = new Ellipse
            {
                Width = 9,
                Height = 9,
                Stroke = new SolidColorBrush(kindColor),
                StrokeThickness = 1.4,
                Fill = wired ? new SolidColorBrush(kindColor) : Brushes.Transparent
            };
            Canvas.SetLeft(circle, isOutput ? width - 4.5 : -4.5);
            Canvas.SetTop(circle, cy - 4.5);
            content.Children.Add(circle);

            var text = pin.Name;
            if (!isOutput && !wired && !string.IsNullOrEmpty(pin.Label) && pin.Label != pin.Name)
                text = $"{pin.Name} = {pin.Label}";
            var dataLabel = new TextBlock
            {
                Text = text,
                Foreground = new SolidColorBrush(Color.FromArgb(230, kindColor.R, kindColor.G, kindColor.B)),
                FontSize = 9,
                MaxWidth = maxLabelWidth,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            dataLabel.Measure(new Size(maxLabelWidth, double.PositiveInfinity));
            Canvas.SetLeft(dataLabel, isOutput ? width - 8 - dataLabel.DesiredSize.Width : 8);
            Canvas.SetTop(dataLabel, cy - dataLabel.DesiredSize.Height / 2);
            content.Children.Add(dataLabel);
            return;
        }

        var tri = new Polygon
        {
            Points = [new Point(0, 0), new Point(9, 5), new Point(0, 10)],
            Fill = new SolidColorBrush(Color.FromRgb(232, 232, 236)),
            Stroke = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
            StrokeThickness = 0.8
        };
        Canvas.SetLeft(tri, isOutput ? width - 5 : -4);
        Canvas.SetTop(tri, cy - 5);
        content.Children.Add(tri);

        // label named branch/flow pins ("True", "False", "Push", "Call"…); plain flow stays unlabelled
        if (isOutput && pin.Name is not ("Then" or "→"))
        {
            var label = new TextBlock
            {
                Text = pin.Name,
                Foreground = new SolidColorBrush(kindColor),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, width - 8 - label.DesiredSize.Width);
            Canvas.SetTop(label, cy - label.DesiredSize.Height / 2);
            content.Children.Add(label);
        }
    }

    // ---- Selection / drag ----

    private void OnNodeClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: BlueprintGraphNode node } border)
            return;

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (_selectedNodes.Remove(node))
            {
                ApplySelectionVisual(node, false);
                if (ReferenceEquals(_selectedNode, node)) _selectedNode = _selectedNodes.LastOrDefault();
            }
            else
            {
                AddToSelection(node);
                _selectedNode = node;
            }
            if (_selectedNode != null) ShowNodeProperties(_selectedNode);
            UpdateSelectionStatus();
            e.Handled = true;
            return;
        }

        if (!_selectedNodes.Contains(node))
        {
            ClearSelection();
            AddToSelection(node);
        }
        _selectedNode = node;
        UpdateSelectionStatus();
        ShowNodeProperties(node);

        _draggedNode = node;
        _isDraggingNode = false;
        _dragStartCanvasPos = e.GetPosition(_canvas);
        _dragOrigins.Clear();
        foreach (var selected in _selectedNodes)
            _dragOrigins[selected] = new Point(selected.NodePosX, selected.NodePosY);
        border.CaptureMouse();
        e.Handled = true;
    }

    private void OnNodeMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggedNode == null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var canvasPos = e.GetPosition(_canvas);
        if (!_isDraggingNode)
        {
            var threshold = PanThreshold / Math.Max(_scaleTransform.ScaleX, 0.01);
            if (Math.Abs(canvasPos.X - _dragStartCanvasPos.X) < threshold &&
                Math.Abs(canvasPos.Y - _dragStartCanvasPos.Y) < threshold)
                return;
            _isDraggingNode = true;
        }

        var delta = canvasPos - _dragStartCanvasPos;
        foreach (var (node, origin) in _dragOrigins)
        {
            node.NodePosX = origin.X + delta.X;
            node.NodePosY = origin.Y + delta.Y;
            MoveNodeVisuals(node);
        }
        e.Handled = true;
    }

    private void OnNodeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_draggedNode == null) return;
        (sender as UIElement)?.ReleaseMouseCapture();
        if (_isDraggingNode) e.Handled = true;
        _draggedNode = null;
        _isDraggingNode = false;
        _dragOrigins.Clear();
    }

    private void OnNodeLostCapture(object sender, MouseEventArgs e)
    {
        _draggedNode = null;
        _isDraggingNode = false;
        _dragOrigins.Clear();
    }

    private void AddToSelection(BlueprintGraphNode node)
    {
        if (_selectedNodes.Add(node))
            ApplySelectionVisual(node, true);
    }

    private void ClearSelection()
    {
        foreach (var node in _selectedNodes)
            ApplySelectionVisual(node, false);
        _selectedNodes.Clear();
        _selectedNode = null;
    }

    private void ApplySelectionVisual(BlueprintGraphNode node, bool selected)
    {
        if (!_nodeBorders.TryGetValue(node, out var border)) return;
        border.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(255, 200, 60)) : DefaultBorderBrush();
        border.BorderThickness = new Thickness(selected ? 2.5 : 1.5);
        Panel.SetZIndex(border, selected ? 3 : 2);
    }

    private static Brush DefaultBorderBrush() => new SolidColorBrush(Color.FromRgb(20, 20, 20));

    private void UpdateSelectionStatus()
    {
        var primary = _selectedNode ?? _selectedNodes.FirstOrDefault();
        SelectedNodeText.Text = _selectedNodes.Count switch
        {
            0 => string.Empty,
            1 when primary != null => $"{primary.Title} — {primary.FunctionName}",
            _ => $"{_selectedNodes.Count} nodes selected"
        };
    }

    private void MoveNodeVisuals(BlueprintGraphNode node)
    {
        if (_nodeBorders.TryGetValue(node, out var border))
        {
            Canvas.SetLeft(border, node.NodePosX);
            Canvas.SetTop(border, node.NodePosY);
        }
        if (_nodeShadows.TryGetValue(node, out var shadow))
        {
            Canvas.SetLeft(shadow, node.NodePosX + 3);
            Canvas.SetTop(shadow, node.NodePosY + 3);
        }

        ComputeNodeGeometry(node);
        foreach (var connection in _viewModel.Connections)
        {
            if (connection.SourceNode != node && connection.TargetNode != node) continue;
            if (!_connectionPaths.TryGetValue(connection, out var path)) continue;
            if (_pinPositions.TryGetValue((connection.SourceNode, connection.SourcePinName, true), out var start) &&
                _pinPositions.TryGetValue((connection.TargetNode, connection.TargetPinName, false), out var end))
                path.Data = BuildConnectionGeometry(start, end);
        }
    }

    // ---- Properties panel ----

    private void ShowNodeProperties(BlueprintGraphNode node)
    {
        PropertiesTitleText.Text = node.Title;
        PropertiesPanel.Children.Clear();

        var explanation = NodeKindExplanation(node);
        if (explanation != null)
            PropertiesPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(38, 120, 200, 140)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 8),
                Child = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.95 }
            });

        foreach (var (key, value) in node.DisplayProperties)
            AddPropertyRow(key, value);
    }

    /// <summary>Plain-language note on what a synthetic/flow node represents, so the graph is legible without knowing the VM.</summary>
    private static string NodeKindExplanation(BlueprintGraphNode node) => node.Kind switch
    {
        EBlueprintNodeKind.FunctionHeader => "Function entry. Execution of this function begins here and flows into the first statement.",
        EBlueprintNodeKind.Branch => "Conditional branch (JumpIfNot). Takes the True pin when the condition holds, otherwise jumps to the False target. Recognized Switch nodes are collapsed compare/branch chains.",
        EBlueprintNodeKind.Cast => "Cast To X. Collapsed from the exact statement sequence the compiler emits for a cast node (cast, bool check, branch); Then = success, Cast Failed = the branch target.",
        EBlueprintNodeKind.Jump => "Unconditional jump. Control transfers to the target statement; this is a compiled goto, not an authored node.",
        EBlueprintNodeKind.Flow => "Execution-flow control. Push/Pop manage the VM's flow stack for latent actions and sequence continuations.",
        EBlueprintNodeKind.Return => "Return. Ends this function's execution.",
        EBlueprintNodeKind.Call => "Function call. A Call pin (if present) dispatches into another function's bytecode, e.g. the event graph's ubergraph.",
        EBlueprintNodeKind.PureCall => "Pure function call. The called function is provably BlueprintPure (see the Pure row for the evidence), so — like in the editor — it has no exec pins; execution passes straight through and only the value wires matter.",
        EBlueprintNodeKind.Assign => "Assignment. Writes the evaluated right-hand expression into the target variable/property.",
        EBlueprintNodeKind.VariableGet => "Variable read. The consuming statement's bytecode argument is this variable token; it is shown as an editor-style Get node feeding the pin.",
        _ => null
    };

    private void OnShowDetails(object sender, RoutedEventArgs e) => ShowDetails();

    private void ShowDetails()
    {
        PropertiesTitleText.Text = "Blueprint Details";
        PropertiesPanel.Children.Clear();

        foreach (var (key, value) in _viewModel.ClassInfo)
            AddPropertyRow(key, value);

        if (_viewModel.Variables.Count > 0)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = $"Variables ({_viewModel.Variables.Count})",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 4)
            });
            foreach (var variable in _viewModel.Variables)
            {
                var value = variable.DefaultValue == null
                    ? variable.Type
                    : $"{variable.Type} = {variable.DefaultValue}";
                AddPropertyRow(variable.Name, value, $"Flags: {variable.Flags}");
            }
        }

        if (_viewModel.Components.Count > 0)
        {
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = $"Components ({_viewModel.Components.Count})",
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 2)
            });
            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = "From the cooked construction script — hierarchy as spawned. Hover a row for the template's serialized properties.",
                FontSize = 10,
                Opacity = 0.6,
                Margin = new Thickness(0, 0, 0, 4),
                TextWrapping = TextWrapping.Wrap
            });

            var scene = _viewModel.Components.Where(c => c.Kind == EBlueprintComponentKind.SceneTree).ToList();
            var runtime = _viewModel.Components.Where(c => c.Kind == EBlueprintComponentKind.Runtime).ToList();
            var timelines = _viewModel.Components.Where(c => c.Kind == EBlueprintComponentKind.Timeline).ToList();

            if (scene.Count > 0)
            {
                var tree = new StackPanel();
                for (var i = 0; i < scene.Count; i++)
                    AddComponentRow(tree, scene, i);
                PropertiesPanel.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(4, 3, 4, 3),
                    Child = tree
                });
            }
            if (runtime.Count > 0)
            {
                AddComponentSectionLabel("Added at runtime (AddComponent nodes)");
                var panel = new StackPanel();
                for (var i = 0; i < runtime.Count; i++)
                    AddComponentRow(panel, runtime, i);
                PropertiesPanel.Children.Add(panel);
            }
            if (timelines.Count > 0)
            {
                AddComponentSectionLabel("Timelines");
                var panel = new StackPanel();
                for (var i = 0; i < timelines.Count; i++)
                    AddComponentRow(panel, timelines, i);
                PropertiesPanel.Children.Add(panel);
            }
        }

        if (_viewModel.Functions.Count == 0) return;

        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Functions",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 10, 0, 4)
        });
        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = "Click a function to show only its graph.",
            FontSize = 10,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 4),
            TextWrapping = TextWrapping.Wrap
        });

        foreach (var fn in _viewModel.Functions)
        {
            var button = new Button
            {
                Content = $"{fn.Name}  ({fn.StatementCount})",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 1, 0, 1),
                Padding = new Thickness(6, 2, 6, 2),
                ToolTip = fn.Signature
            };
            var captured = fn.Name;
            button.Click += (_, _) => IsolateFunction(captured);
            PropertiesPanel.Children.Add(button);
        }
    }

    private void AddComponentSectionLabel(string text) =>
        PropertiesPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.65,
            Margin = new Thickness(0, 8, 0, 2)
        });

    /// <summary>
    /// One row of the Components tree, drawn like the editor's Components panel: tree guide
    /// lines per ancestor level, a per-category icon, "Name  Class", then the asset/attachment
    /// detail line. Template properties stay in the tooltip.
    /// </summary>
    private void AddComponentRow(Panel parent, List<BlueprintComponentInfo> list, int index)
    {
        var component = list[index];
        var guideBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));

        var cells = new StackPanel { Orientation = Orientation.Horizontal };
        // guide line per ancestor level: drawn while that ancestor still has siblings below
        for (var level = 1; level < component.Depth; level++)
        {
            var continues = false;
            for (var j = index + 1; j < list.Count; j++)
            {
                if (list[j].Depth > level) continue;
                continues = list[j].Depth == level;
                break;
            }
            cells.Children.Add(TreeGuideCell(continues ? guideBrush : null));
        }
        if (component.Depth > 0)
        {
            var hasLaterSibling = false;
            for (var j = index + 1; j < list.Count; j++)
            {
                if (list[j].Depth > component.Depth) continue;
                hasLaterSibling = list[j].Depth == component.Depth;
                break;
            }
            cells.Children.Add(TreeElbowCell(guideBrush, hasLaterSibling));
        }
        cells.Children.Add(ComponentIcon(component.Class));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var headerLine = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
        headerLine.Inlines.Add(new System.Windows.Documents.Run(component.Name)
            { FontWeight = FontWeights.SemiBold, FontSize = 11 });
        headerLine.Inlines.Add(new System.Windows.Documents.Run($"  {component.Class}")
            { FontSize = 10, Foreground = new SolidColorBrush(Color.FromArgb(190, 120, 190, 140)) });
        if (component.InheritedFrom != null)
            headerLine.Inlines.Add(new System.Windows.Documents.Run($"  inherited: {component.InheritedFrom}")
                { FontSize = 9, Foreground = new SolidColorBrush(Color.FromArgb(150, 200, 180, 90)) });
        text.Children.Add(headerLine);

        var detail = string.Join("   ", new[]
        {
            component.Asset,
            component.Kind == EBlueprintComponentKind.SceneTree ? component.AttachInfo : null
        }.Where(s => !string.IsNullOrEmpty(s)));
        if (detail.Length > 0)
            text.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 10,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        cells.Children.Add(text);

        var row = new Border
        {
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(3, 1, 3, 1),
            Child = cells
        };
        if (component.TemplateProperties.Count > 0)
            row.ToolTip = string.Join("\n", component.TemplateProperties.Select(p => $"{p.Key} = {p.Value}"));
        row.MouseEnter += (_, _) => row.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        parent.Children.Add(row);
    }

    /// <summary>A 14px indent cell carrying (or not) a full-height vertical guide line.</summary>
    private static UIElement TreeGuideCell(Brush brush)
    {
        var grid = new Grid { Width = 14 };
        if (brush != null)
            grid.Children.Add(new Rectangle { Width = 1, Fill = brush, HorizontalAlignment = HorizontalAlignment.Center });
        return grid;
    }

    /// <summary>The connector cell: ├ when more siblings follow below, └ for the last child.</summary>
    private static UIElement TreeElbowCell(Brush brush, bool continuesDown)
    {
        var grid = new Grid { Width = 14 };
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        var vertical = new Rectangle { Width = 1, Fill = brush, HorizontalAlignment = HorizontalAlignment.Center };
        if (continuesDown) Grid.SetRowSpan(vertical, 2);
        grid.Children.Add(vertical);
        var horizontal = new Rectangle
        {
            Height = 1, Fill = brush,
            Margin = new Thickness(7, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetRowSpan(horizontal, 2);
        grid.Children.Add(horizontal);
        return grid;
    }

    /// <summary>Small vector glyph per component category (purely cosmetic — the class name is always printed beside it).</summary>
    private static UIElement ComponentIcon(string className)
    {
        var (data, color) = ComponentIconData(className ?? string.Empty);
        return new Path
        {
            Data = Geometry.Parse(data),
            Stroke = new SolidColorBrush(color),
            StrokeThickness = 1.1,
            StrokeLineJoin = PenLineJoin.Round,
            Width = 13,
            Height = 13,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(1, 0, 6, 0)
        };
    }

    private static (string Data, Color Color) ComponentIconData(string cls)
    {
        bool Has(string s) => cls.Contains(s, StringComparison.OrdinalIgnoreCase);
        if (Has("SkeletalMesh")) return ("M7,1 A2,2 0 1 1 6.99,1 M7,5 L7,9 M7,6 L3.5,8 M7,6 L10.5,8 M7,9 L4.5,13 M7,9 L9.5,13", Color.FromRgb(232, 150, 170));
        if (Has("StaticMesh") || Has("Mesh")) return ("M7,1 L13,4 L13,10 L7,13 L1,10 L1,4 Z M1,4 L7,7 L13,4 M7,7 L7,13", Color.FromRgb(150, 185, 235));
        if (Has("Audio") || Has("Sound")) return ("M1.5,5 L4,5 L7,2 L7,12 L4,9 L1.5,9 Z M9.5,4.5 A3.5,3.5 0 0 1 9.5,9.5 M11.5,3 A5.5,5.5 0 0 1 11.5,11", Color.FromRgb(90, 200, 200));
        if (Has("Light")) return ("M7,1.5 A3.5,3.5 0 1 1 6.99,1.5 M5.5,10.5 L8.5,10.5 M6,12.5 L8,12.5", Color.FromRgb(240, 215, 110));
        if (Has("Camera")) return ("M1,4 L9,4 L9,10 L1,10 Z M9,6.5 L13,4 L13,10 L9,7.5", Color.FromRgb(180, 150, 230));
        if (Has("Niagara") || Has("Particle") || Has("Emitter")) return ("M7,1 L7,13 M1,7 L13,7 M3.5,3.5 L10.5,10.5 M10.5,3.5 L3.5,10.5", Color.FromRgb(240, 165, 90));
        if (Has("SpringArm")) return ("M2,12.5 L8,6.5 L12,6.5 M12,6.5 A1.3,1.3 0 1 1 11.99,6.5", Color.FromRgb(170, 180, 195));
        if (Has("Capsule")) return ("M4,4.5 A3,3 0 0 1 10,4.5 L10,9.5 A3,3 0 0 1 4,9.5 Z", Color.FromRgb(130, 205, 130));
        if (Has("Sphere")) return ("M7,1.5 A5.5,5.5 0 1 1 6.99,1.5 M1.5,7 L12.5,7", Color.FromRgb(130, 205, 130));
        if (Has("Box") || Has("Collision") || Has("Trigger")) return ("M2,2 L12,2 L12,12 L2,12 Z", Color.FromRgb(130, 205, 130));
        if (Has("Widget")) return ("M1,2.5 L13,2.5 L13,11.5 L1,11.5 Z M1,5.5 L13,5.5", Color.FromRgb(100, 200, 220));
        if (Has("Text")) return ("M2.5,2.5 L11.5,2.5 M7,2.5 L7,12", Color.FromRgb(200, 200, 210));
        if (Has("Spline")) return ("M1,12 C4,1 10,13 13,2", Color.FromRgb(230, 200, 120));
        if (Has("Decal")) return ("M2,2 L12,2 L12,12 L2,12 Z M4.5,4.5 L9.5,4.5 L9.5,9.5 L4.5,9.5 Z", Color.FromRgb(190, 160, 210));
        if (Has("Movement") || Has("Projectile")) return ("M1,7 L11,7 M8,3.5 L11.5,7 L8,10.5", Color.FromRgb(220, 170, 120));
        if (Has("Arrow")) return ("M1,7 L11,7 M8,3.5 L11.5,7 L8,10.5", Color.FromRgb(200, 120, 110));
        if (Has("Billboard") || Has("Sprite")) return ("M1,3 L13,3 L13,11 L1,11 Z M3,9 L6,5.5 L8,7.5 L10,5 L13,9", Color.FromRgb(170, 190, 200));
        if (Has("Timeline")) return ("M7,1.5 A5.5,5.5 0 1 1 6.99,1.5 M7,3.5 L7,7 L10,8.5", Color.FromRgb(150, 200, 160));
        if (Has("Child") || Has("Actor")) return ("M4,2 L12,2 L12,10 L4,10 Z M2,4.5 L4,4.5 M2,4.5 L2,12 L9.5,12 L9.5,10", Color.FromRgb(180, 180, 200));
        if (Has("Scene")) return ("M2,12 L7,7 M7,7 L7,1 M7,7 L13,10", Color.FromRgb(175, 185, 195));
        return ("M7,2.5 A4.5,4.5 0 1 1 6.99,2.5", Color.FromRgb(165, 170, 180));
    }

    private void AddPropertyRow(string key, string value, string toolTip = null)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        if (toolTip != null) panel.ToolTip = toolTip;
        panel.Children.Add(new TextBlock
        {
            Text = key,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Opacity = 0.75
        });
        if (value.Contains('\n') || value.Length > 48)
        {
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(24, 24, 28)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 2, 0, 0),
                Child = new TextBox
                {
                    Text = value,
                    IsReadOnly = true,
                    IsReadOnlyCaretVisible = false,
                    BorderThickness = new Thickness(0),
                    Background = Brushes.Transparent,
                    FontSize = 11,
                    FontFamily = new FontFamily("Consolas"),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            });
        }
        else
        {
            // read-only borderless TextBox so every value in the panel can be selected and copied
            panel.Children.Add(new TextBox
            {
                Text = value,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
        }
        PropertiesPanel.Children.Add(panel);
    }

    // ---- Pan / zoom / marquee / fit ----

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
        var mousePos = e.GetPosition((UIElement)sender);

        var oldScale = _scaleTransform.ScaleX;
        var newScale = Math.Clamp(oldScale * factor, 0.05, 5.0);

        var canvasX = (mousePos.X - _translateTransform.X) / oldScale;
        var canvasY = (mousePos.Y - _translateTransform.Y) / oldScale;

        _scaleTransform.ScaleX = newScale;
        _scaleTransform.ScaleY = newScale;
        _translateTransform.X = mousePos.X - canvasX * newScale;
        _translateTransform.Y = mousePos.Y - canvasY * newScale;

        ZoomText.Text = $"Zoom: {newScale * 100:F0}%";
    }

    private void OnPanMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Right or MouseButton.Middle)) return;
        _isPanning = true;
        _lastMousePos = e.GetPosition((UIElement)sender);
        ((UIElement)sender).CaptureMouse();
        e.Handled = true;
    }

    private void OnPanMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Right or MouseButton.Middle)) return;
        if (!_isPanning) return;
        _isPanning = false;
        ((UIElement)sender).ReleaseMouseCapture();
        e.Handled = true;
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_draggedNode != null || _isPanning) return;
        _potentialMarquee = true;
        _isMarqueeSelecting = false;
        _panStartPos = e.GetPosition((UIElement)sender);
        _marqueeStartCanvas = e.GetPosition(_canvas);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isMarqueeSelecting)
        {
            ((UIElement)sender).ReleaseMouseCapture();
            var marquee = new Rect(
                Canvas.GetLeft(_marqueeRect), Canvas.GetTop(_marqueeRect),
                _marqueeRect.Width, _marqueeRect.Height);
            _canvas.Children.Remove(_marqueeRect);
            _marqueeRect = null;
            _isMarqueeSelecting = false;
            _potentialMarquee = false;

            if (!(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)))
                ClearSelection();
            foreach (var node in _viewModel.Nodes)
            {
                if (!IsNodeVisible(node)) continue;
                var size = _nodeSizes.TryGetValue(node, out var s) ? s : (NodeWidth, 60.0);
                if (marquee.IntersectsWith(new Rect(node.NodePosX, node.NodePosY, size.Item1, size.Item2)))
                    AddToSelection(node);
            }
            _selectedNode = _selectedNodes.LastOrDefault();
            if (_selectedNode != null) ShowNodeProperties(_selectedNode);
            UpdateSelectionStatus();
            return;
        }

        if (_potentialMarquee && _draggedNode == null)
        {
            ClearSelection();
            UpdateSelectionStatus();
            ShowDetails();
        }
        _potentialMarquee = false;
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanning)
        {
            if (e.RightButton != MouseButtonState.Pressed && e.MiddleButton != MouseButtonState.Pressed)
            {
                _isPanning = false;
                ((UIElement)sender).ReleaseMouseCapture();
                return;
            }
            var currentPos = e.GetPosition((UIElement)sender);
            var delta = currentPos - _lastMousePos;
            _translateTransform.X += delta.X;
            _translateTransform.Y += delta.Y;
            _lastMousePos = currentPos;
            return;
        }

        if (!_potentialMarquee || e.LeftButton != MouseButtonState.Pressed)
        {
            _potentialMarquee = false;
            return;
        }

        var screenPos = e.GetPosition((UIElement)sender);
        if (!_isMarqueeSelecting)
        {
            if (Math.Abs(screenPos.X - _panStartPos.X) <= PanThreshold &&
                Math.Abs(screenPos.Y - _panStartPos.Y) <= PanThreshold)
                return;

            _isMarqueeSelecting = true;
            ((UIElement)sender).CaptureMouse();
            _marqueeRect = new Rectangle
            {
                Stroke = new SolidColorBrush(Color.FromArgb(220, 255, 200, 60)),
                StrokeThickness = 1.5 / Math.Max(_scaleTransform.ScaleX, 0.01),
                Fill = new SolidColorBrush(Color.FromArgb(40, 255, 200, 60)),
                IsHitTestVisible = false
            };
            Panel.SetZIndex(_marqueeRect, 10);
            _canvas.Children.Add(_marqueeRect);
        }

        var canvasPos = e.GetPosition(_canvas);
        Canvas.SetLeft(_marqueeRect, Math.Min(canvasPos.X, _marqueeStartCanvas.X));
        Canvas.SetTop(_marqueeRect, Math.Min(canvasPos.Y, _marqueeStartCanvas.Y));
        _marqueeRect.Width = Math.Abs(canvasPos.X - _marqueeStartCanvas.X);
        _marqueeRect.Height = Math.Abs(canvasPos.Y - _marqueeStartCanvas.Y);
    }

    private void OnFitToView(object sender, RoutedEventArgs e) => FitToView();

    private void FitToView()
    {
        var visible = _viewModel.Nodes.Where(IsNodeVisible).ToList();
        if (visible.Count == 0) return;

        var minX = visible.Min(n => n.NodePosX);
        var minY = visible.Min(n => n.NodePosY);
        var maxX = visible.Max(n => n.NodePosX + (_nodeSizes.TryGetValue(n, out var s) ? s.width : NodeWidth));
        var maxY = visible.Max(n => n.NodePosY + (_nodeSizes.TryGetValue(n, out var s) ? s.height : 60));

        var graphWidth = maxX - minX;
        var graphHeight = maxY - minY;
        if (graphWidth < 1 || graphHeight < 1) return;

        var viewWidth = CanvasHost.ActualWidth > 0 ? CanvasHost.ActualWidth : (ActualWidth > 0 ? ActualWidth * 0.7 : 800);
        var viewHeight = CanvasHost.ActualHeight > 0 ? CanvasHost.ActualHeight : (ActualHeight > 0 ? ActualHeight - 120 : 600);

        var scaleX = viewWidth / graphWidth * 0.9;
        var scaleY = viewHeight / graphHeight * 0.9;
        var scale = Math.Min(Math.Min(scaleX, scaleY), 1.5);

        _scaleTransform.ScaleX = scale;
        _scaleTransform.ScaleY = scale;
        _translateTransform.X = -minX * scale + (viewWidth - graphWidth * scale) / 2;
        _translateTransform.Y = -minY * scale + (viewHeight - graphHeight * scale) / 2;

        ZoomText.Text = $"Zoom: {scale * 100:F0}%";
    }

    private void OnResetZoom(object sender, RoutedEventArgs e)
    {
        _scaleTransform.ScaleX = 1;
        _scaleTransform.ScaleY = 1;
        _translateTransform.X = 0;
        _translateTransform.Y = 0;
        ZoomText.Text = "Zoom: 100%";
    }

    // ---- Colours ----

    private static Color GetEdgeColor(EBlueprintEdgeKind kind) => kind switch
    {
        EBlueprintEdgeKind.Entry => Color.FromRgb(100, 200, 120),
        EBlueprintEdgeKind.True => Color.FromRgb(90, 200, 90),
        EBlueprintEdgeKind.False => Color.FromRgb(220, 90, 80),
        EBlueprintEdgeKind.Jump => Color.FromRgb(240, 150, 60),
        EBlueprintEdgeKind.Push => Color.FromRgb(180, 120, 220),
        EBlueprintEdgeKind.Call => Color.FromRgb(90, 150, 230),
        EBlueprintEdgeKind.Data => Color.FromRgb(96, 196, 208),
        _ => Color.FromRgb(205, 205, 205),
    };

    private static Color GetNodeHeaderColor(EBlueprintNodeKind kind) => kind switch
    {
        EBlueprintNodeKind.FunctionHeader => Color.FromRgb(30, 120, 90),
        EBlueprintNodeKind.Branch => Color.FromRgb(180, 120, 40),
        EBlueprintNodeKind.Cast => Color.FromRgb(35, 120, 130),
        EBlueprintNodeKind.Jump => Color.FromRgb(170, 90, 40),
        EBlueprintNodeKind.Flow => Color.FromRgb(110, 70, 160),
        EBlueprintNodeKind.Return => Color.FromRgb(160, 50, 50),
        EBlueprintNodeKind.Call => Color.FromRgb(50, 90, 170),
        EBlueprintNodeKind.PureCall => Color.FromRgb(45, 130, 70),
        EBlueprintNodeKind.Assign => Color.FromRgb(40, 110, 80),
        EBlueprintNodeKind.VariableGet => Color.FromRgb(70, 130, 85),
        _ => Color.FromRgb(72, 72, 82),
    };
}

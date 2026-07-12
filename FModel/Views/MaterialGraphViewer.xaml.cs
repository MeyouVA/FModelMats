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

public partial class MaterialGraphViewer
{
    private const double NodeWidth = 200;
    private const double NodeHeaderHeight = 40;
    private const double PinRowHeight = 22;
    private const double NodeBottomPadding = 8;
    private const double NodeCornerRadius = 6;
    private const double PinCircleRadius = 5;
    private const double PinLabelOffset = 14;
    private const double HeaderGradientDarkenFactor = 0.6;
    private const double BezierHorizontalOffset = 80;
    private const double PanThreshold = 5.0;

    private readonly MaterialGraphViewModel _viewModel;

    private Canvas _canvas;
    private ScaleTransform _scaleTransform;
    private TranslateTransform _translateTransform;

    private readonly Dictionary<MaterialGraphNode, (double width, double height)> _nodeSizes = new();
    private readonly Dictionary<(MaterialGraphNode node, string pinName, bool isOutput), Point> _pinPositions = new();
    private readonly Dictionary<MaterialGraphNode, Border> _nodeBorders = new();
    private readonly Dictionary<MaterialGraphNode, Border> _nodeShadows = new();
    private readonly Dictionary<MaterialGraphConnection, Path> _connectionPaths = new();

    private MaterialGraphNode _selectedNode;
    private readonly HashSet<MaterialGraphNode> _selectedNodes = [];

    private bool _isPanning;
    private Point _panStartPos;
    private Point _lastMousePos;

    /// <summary>View-only: dim engine scaffolding so the user-authored material nodes stand out.</summary>
    private bool _highlightUserGraph;

    private MaterialGraphNode _draggedNode;
    private bool _isDraggingNode;
    private Point _dragStartCanvasPos;
    private readonly Dictionary<MaterialGraphNode, Point> _dragOrigins = new();

    private bool _potentialMarquee;
    private bool _isMarqueeSelecting;
    private Point _marqueeStartCanvas;
    private Rectangle _marqueeRect;

    // shared frozen effect so every node shadow reuses one instance instead of allocating its own
    private static readonly BlurEffect _nodeShadowBlur = CreateFrozenBlur(8);

    private static BlurEffect CreateFrozenBlur(double radius)
    {
        var blur = new BlurEffect { Radius = radius };
        blur.Freeze();
        return blur;
    }

    public MaterialGraphViewer(MaterialGraphViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        if (!string.IsNullOrEmpty(viewModel.PackageName))
            Title = $"Material Graph Viewer - {viewModel.PackageName}";
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PackageNameText.Text = _viewModel.PackageName;
        NodeCountText.Text = $"Nodes: {_viewModel.Nodes.Count}";
        ConnectionCountText.Text = $"Connections: {_viewModel.Connections.Count}";
        if (_viewModel.IsReconstructed)
        {
            ReconstructedBanner.Visibility = Visibility.Visible;
            if (!string.IsNullOrEmpty(_viewModel.ReconstructionNote))
                ReconstructedBannerText.Text = _viewModel.ReconstructionNote;
        }
        if (!string.IsNullOrEmpty(_viewModel.ParentChain))
            ParentChainText.Text = _viewModel.ParentChain;

        MaterialDetailsButton.IsEnabled = _viewModel.MaterialSections.Count > 0;
        ShadersButton.Content = $"Shaders ({_viewModel.Shaders.Count})";
        ShadersButton.IsEnabled = _viewModel.Shaders.Count > 0;
        ExpandMathButton.Visibility = _viewModel.CanExpandPixelShaderMath ? Visibility.Visible : Visibility.Collapsed;
        UpdateUserGraphButtons();

        SetupCanvas();
        DrawGraph();
        // until a node is clicked the side panel shows the whole material
        if (_viewModel.MaterialSections.Count > 0)
            ShowMaterialDetails();
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
        CanvasHost.MouseDown += OnPanMouseDown; // right / middle button panning
        CanvasHost.MouseUp += OnPanMouseUp;
    }

    /// <summary>
    /// Rebuilds the whole graph with "Pixel Shader Math" combiners either kept opaque or
    /// expanded into one node per decoded GPU instruction, then redraws from scratch.
    /// </summary>
    private void OnExpandMathToggled(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || _canvas == null) return;
        _viewModel.ExpandPixelShaderMath = ExpandMathButton.IsChecked == true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _viewModel.Rebuild();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _selectedNode = null;
        _selectedNodes.Clear();
        NodeCountText.Text = $"Nodes: {_viewModel.Nodes.Count}";
        ConnectionCountText.Text = $"Connections: {_viewModel.Connections.Count}";
        if (!string.IsNullOrEmpty(_viewModel.ReconstructionNote))
            ReconstructedBannerText.Text = _viewModel.ReconstructionNote;
        MaterialDetailsButton.IsEnabled = _viewModel.MaterialSections.Count > 0;
        ShadersButton.Content = $"Shaders ({_viewModel.Shaders.Count})";
        ShadersButton.IsEnabled = _viewModel.Shaders.Count > 0;
        UpdateUserGraphButtons();

        DrawGraph();
        if (_viewModel.MaterialSections.Count > 0)
            ShowMaterialDetails();
        FitToView();
    }

    /// <summary>Shows the user-graph highlight/isolation toggles only when there is a mix to separate.</summary>
    private void UpdateUserGraphButtons()
    {
        var visibility = _viewModel.CanSeparateUserGraph ? Visibility.Visible : Visibility.Collapsed;
        HighlightUserGraphButton.Visibility = visibility;
        UserGraphOnlyButton.Visibility = visibility;
    }

    /// <summary>Redraws the graph with the engine scaffolding dimmed (view only, no rebuild).</summary>
    private void OnHighlightUserGraphToggled(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || _canvas == null) return;
        _highlightUserGraph = HighlightUserGraphButton.IsChecked == true;
        DrawGraph();
        // DrawGraph rebuilds every node border, so re-apply the current selection's outline
        foreach (var node in _selectedNodes)
            ApplySelectionVisual(node, true);
    }

    /// <summary>
    /// Rebuilds the graph with the engine shader scaffolding either kept or pruned away, leaving
    /// only the user-authored material nodes wired into the output, then redraws from scratch.
    /// </summary>
    private void OnUserGraphOnlyToggled(object sender, RoutedEventArgs e)
    {
        if (_viewModel == null || _canvas == null) return;
        _viewModel.UserGraphOnly = UserGraphOnlyButton.IsChecked == true;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            _viewModel.Rebuild();
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _selectedNode = null;
        _selectedNodes.Clear();
        NodeCountText.Text = $"Nodes: {_viewModel.Nodes.Count}";
        ConnectionCountText.Text = $"Connections: {_viewModel.Connections.Count}";
        if (!string.IsNullOrEmpty(_viewModel.ReconstructionNote))
            ReconstructedBannerText.Text = _viewModel.ReconstructionNote;
        MaterialDetailsButton.IsEnabled = _viewModel.MaterialSections.Count > 0;
        ShadersButton.Content = $"Shaders ({_viewModel.Shaders.Count})";
        ShadersButton.IsEnabled = _viewModel.Shaders.Count > 0;
        ExpandMathButton.Visibility = _viewModel.CanExpandPixelShaderMath ? Visibility.Visible : Visibility.Collapsed;
        UpdateUserGraphButtons();

        DrawGraph();
        if (_viewModel.MaterialSections.Count > 0)
            ShowMaterialDetails();
        FitToView();
    }

    private void DrawGraph()
    {
        _canvas.Children.Clear();
        _nodeSizes.Clear();
        _pinPositions.Clear();
        _nodeBorders.Clear();
        _nodeShadows.Clear();
        _connectionPaths.Clear();

        // node geometry is deterministic, so pin anchors can be computed
        // up front and connections drawn beneath the nodes
        foreach (var node in _viewModel.Nodes)
            ComputeNodeGeometry(node);

        foreach (var connection in _viewModel.Connections)
            DrawConnection(connection);

        foreach (var node in _viewModel.Nodes)
            DrawNode(node);
    }

    private void ComputeNodeGeometry(MaterialGraphNode node)
    {
        var pinRows = Math.Max(Math.Max(node.InputPins.Count, node.OutputPins.Count), 1);
        var height = NodeHeaderHeight + pinRows * PinRowHeight + NodeBottomPadding;
        _nodeSizes[node] = (NodeWidth, height);

        for (var i = 0; i < node.InputPins.Count; i++)
        {
            _pinPositions[(node, node.InputPins[i].Name, false)] = new Point(
                node.NodePosX,
                node.NodePosY + NodeHeaderHeight + i * PinRowHeight + PinRowHeight / 2);
        }

        for (var i = 0; i < node.OutputPins.Count; i++)
        {
            _pinPositions[(node, node.OutputPins[i].Name, true)] = new Point(
                node.NodePosX + NodeWidth,
                node.NodePosY + NodeHeaderHeight + i * PinRowHeight + PinRowHeight / 2);
        }
    }

    private void DrawConnection(MaterialGraphConnection connection)
    {
        if (!_pinPositions.TryGetValue((connection.SourceNode, connection.SourcePinName, true), out var start) ||
            !_pinPositions.TryGetValue((connection.TargetNode, connection.TargetPinName, false), out var end))
            return;

        var color = GetPinColor(connection.PinType);
        var alpha = connection.IsInferred ? (byte) 140 : (byte) 200;
        // fade edges that touch engine scaffolding so the user graph reads clearly
        if (_highlightUserGraph && (!connection.SourceNode.IsUserMaterialNode || !connection.TargetNode.IsUserMaterialNode))
            alpha = (byte) (alpha * 0.22);
        var path = new Path
        {
            Data = BuildConnectionGeometry(start, end),
            Stroke = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)),
            StrokeThickness = 2.5,
            IsHitTestVisible = false
        };
        if (connection.IsInferred)
            path.StrokeDashArray = new DoubleCollection { 5, 4 };
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

    private void DrawNode(MaterialGraphNode node)
    {
        var (width, height) = _nodeSizes[node];
        // when highlighting the user graph, engine scaffolding is drawn faint
        var dimmed = _highlightUserGraph && !node.IsUserMaterialNode;

        var shadow = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(NodeCornerRadius),
            Background = Brushes.Black,
            Opacity = dimmed ? 0.12 : 0.4,
            Effect = _nodeShadowBlur
        };
        Canvas.SetLeft(shadow, node.NodePosX + 3);
        Canvas.SetTop(shadow, node.NodePosY + 3);
        Panel.SetZIndex(shadow, 0);
        _canvas.Children.Add(shadow);
        _nodeShadows[node] = shadow;

        var border = new Border
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(NodeCornerRadius),
            Background = new SolidColorBrush(Color.FromArgb(230, 42, 42, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
            BorderThickness = new Thickness(1.5),
            SnapsToDevicePixels = true,
            Cursor = Cursors.Hand,
            Opacity = dimmed ? 0.32 : 1.0,
            Tag = node
        };
        border.MouseLeftButtonDown += OnNodeClicked;
        border.MouseMove += OnNodeMouseMove;
        border.MouseLeftButtonUp += OnNodeMouseUp;
        border.LostMouseCapture += OnNodeLostCapture;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(NodeHeaderHeight) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // header with gradient
        var headerColor = GetNodeHeaderColor(node);
        var headerBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        headerBrush.GradientStops.Add(new GradientStop(headerColor, 0.0));
        headerBrush.GradientStops.Add(new GradientStop(
            Color.FromArgb(headerColor.A,
                (byte)(headerColor.R * HeaderGradientDarkenFactor),
                (byte)(headerColor.G * HeaderGradientDarkenFactor),
                (byte)(headerColor.B * HeaderGradientDarkenFactor)), 1.0));
        headerBrush.Freeze();

        var header = new Border
        {
            Background = headerBrush,
            CornerRadius = new CornerRadius(NodeCornerRadius, NodeCornerRadius, 0, 0),
            Padding = new Thickness(8, 3, 8, 3)
        };
        var headerStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        headerStack.Children.Add(new TextBlock
        {
            Text = node.Title,
            Foreground = Brushes.White,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        if (!string.IsNullOrEmpty(node.Subtitle))
        {
            headerStack.Children.Add(new TextBlock
            {
                Text = node.Subtitle,
                Foreground = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
                FontSize = 10,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        header.Child = headerStack;
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        // pin rows
        var pinCanvas = new Canvas();
        Grid.SetRow(pinCanvas, 1);
        grid.Children.Add(pinCanvas);

        for (var i = 0; i < node.InputPins.Count; i++)
            DrawPin(pinCanvas, node.InputPins[i], i, isOutput: false, width);
        for (var i = 0; i < node.OutputPins.Count; i++)
            DrawPin(pinCanvas, node.OutputPins[i], i, isOutput: true, width);

        border.Child = grid;
        Canvas.SetLeft(border, node.NodePosX);
        Canvas.SetTop(border, node.NodePosY);
        Panel.SetZIndex(border, 2);
        _canvas.Children.Add(border);
        _nodeBorders[node] = border;
    }

    private static void DrawPin(Canvas pinCanvas, MaterialGraphPin pin, int index, bool isOutput, double nodeWidth)
    {
        var y = index * PinRowHeight + PinRowHeight / 2;
        var pinColor = GetPinColor(pin.PinType);

        var circle = new Ellipse
        {
            Width = PinCircleRadius * 2,
            Height = PinCircleRadius * 2,
            Fill = new SolidColorBrush(pinColor),
            Stroke = new SolidColorBrush(Color.FromRgb(20, 20, 20)),
            StrokeThickness = 1
        };
        Canvas.SetLeft(circle, isOutput ? nodeWidth - PinCircleRadius : -PinCircleRadius);
        Canvas.SetTop(circle, y - PinCircleRadius);
        pinCanvas.Children.Add(circle);

        var label = new TextBlock
        {
            Text = pin.Name,
            Foreground = new SolidColorBrush(Color.FromRgb(210, 210, 210)),
            FontSize = 10,
            MaxWidth = nodeWidth - PinLabelOffset * 2,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        if (isOutput)
            Canvas.SetLeft(label, nodeWidth - PinLabelOffset - label.DesiredSize.Width);
        else
            Canvas.SetLeft(label, PinLabelOffset);
        Canvas.SetTop(label, y - label.DesiredSize.Height / 2);
        pinCanvas.Children.Add(label);
    }

    private void OnNodeClicked(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: MaterialGraphNode node } border)
            return;

        // shift/ctrl toggles membership without starting a drag
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

        // clicking outside the current selection makes this node the sole selection,
        // clicking inside keeps the group together so it can be dragged as one
        if (!_selectedNodes.Contains(node))
        {
            ClearSelection();
            AddToSelection(node);
        }
        _selectedNode = node;
        UpdateSelectionStatus();
        ShowNodeProperties(node);

        // a drag starts on the same gesture; it only becomes one past the movement threshold
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

        // positions are in canvas space, so the pan threshold shrinks with the zoom level
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

    private void AddToSelection(MaterialGraphNode node)
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

    private void ApplySelectionVisual(MaterialGraphNode node, bool selected)
    {
        if (!_nodeBorders.TryGetValue(node, out var border)) return;
        border.BorderBrush = new SolidColorBrush(selected ? Color.FromRgb(255, 200, 60) : Color.FromRgb(20, 20, 20));
        Panel.SetZIndex(border, selected ? 3 : 2);
    }

    private void UpdateSelectionStatus()
    {
        var primary = _selectedNode ?? _selectedNodes.FirstOrDefault();
        SelectedNodeText.Text = _selectedNodes.Count switch
        {
            0 => string.Empty,
            1 when primary != null => string.IsNullOrEmpty(primary.Subtitle) ? primary.Title : $"{primary.Title} ({primary.Subtitle})",
            _ => $"{_selectedNodes.Count} nodes selected"
        };
    }

    /// <summary>Repositions a node's visuals and re-routes every connection attached to it.</summary>
    private void MoveNodeVisuals(MaterialGraphNode node)
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

        ComputeNodeGeometry(node); // refresh pin anchors at the new position
        foreach (var connection in _viewModel.Connections)
        {
            if (connection.SourceNode != node && connection.TargetNode != node) continue;
            if (!_connectionPaths.TryGetValue(connection, out var path)) continue;
            if (_pinPositions.TryGetValue((connection.SourceNode, connection.SourcePinName, true), out var start) &&
                _pinPositions.TryGetValue((connection.TargetNode, connection.TargetPinName, false), out var end))
                path.Data = BuildConnectionGeometry(start, end);
        }
    }

    private void ShowNodeProperties(MaterialGraphNode node)
    {
        PropertiesTitleText.Text = node.Title;
        PropertiesPanel.Children.Clear();

        AddPropertyRow("Name", node.Name);
        AddPropertyRow("Type", node.ExportType);
        if (!string.IsNullOrEmpty(node.Subtitle))
            AddPropertyRow("Value", node.Subtitle);

        if (node.DisplayProperties.Count > 0)
        {
            PropertiesPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
            foreach (var (key, value) in node.DisplayProperties)
                AddPropertyRow(key, value);
        }
    }

    private void OnShowMaterialDetails(object sender, RoutedEventArgs e) => ShowMaterialDetails();

    /// <summary>Renders every serialized property of the material, chain and shader map.</summary>
    private void ShowMaterialDetails()
    {
        PropertiesTitleText.Text = $"Material Details — {_viewModel.MaterialName}";
        PropertiesPanel.Children.Clear();

        var first = true;
        foreach (var section in _viewModel.MaterialSections)
        {
            if (!first)
                PropertiesPanel.Children.Add(new Separator { Margin = new Thickness(0, 8, 0, 4) });
            first = false;

            PropertiesPanel.Children.Add(new TextBlock
            {
                Text = section.Title,
                FontWeight = FontWeights.SemiBold,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 4)
            });
            foreach (var (key, value) in section.Rows)
                AddPropertyRow(key, value);
        }
    }

    private void OnShowShaders(object sender, RoutedEventArgs e) => ShowShaderList();

    /// <summary>Lists every compiled shader of the map; disassembly renders on expand.</summary>
    private void ShowShaderList()
    {
        PropertiesTitleText.Text = $"Shaders ({_viewModel.Shaders.Count})";
        PropertiesPanel.Children.Clear();

        foreach (var entry in _viewModel.Shaders)
        {
            var header = new StackPanel();
            header.Children.Add(new TextBlock
            {
                Text = entry.TypeName,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap
            });
            var subtitle = entry.Frequency;
            if (!string.IsNullOrEmpty(entry.VertexFactory))
                subtitle += $" • {entry.VertexFactory}";
            if (!string.IsNullOrEmpty(entry.CodeInfo))
                subtitle += $" • {entry.CodeInfo}";
            header.Children.Add(new TextBlock
            {
                Text = subtitle,
                FontSize = 10,
                Opacity = 0.65,
                TextWrapping = TextWrapping.Wrap
            });

            var expander = new Expander
            {
                Header = header,
                Margin = new Thickness(0, 2, 0, 2),
                IsExpanded = false
            };
            var captured = entry; // the disassembly is produced lazily on first expand
            expander.Expanded += (_, _) =>
            {
                if (expander.Content != null) return;
                var content = new StackPanel { Margin = new Thickness(4, 2, 0, 4) };
                if (!string.IsNullOrEmpty(captured.OutputHash))
                    content.Children.Add(CreatePropertyRow("Output Hash", captured.OutputHash));
                content.Children.Add(CreatePropertyRow("Disassembly", captured.Disassembly));
                expander.Content = content;
            };
            PropertiesPanel.Children.Add(expander);
        }
    }

    private void AddPropertyRow(string key, string value) =>
        PropertiesPanel.Children.Add(CreatePropertyRow(key, value));

    private StackPanel CreatePropertyRow(string key, string value)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
        var keyText = new TextBlock
        {
            Text = key,
            FontWeight = FontWeights.SemiBold,
            FontSize = 11,
            Opacity = 0.75,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (value.Contains('\n'))
        {
            // multiline values are code (shader disassembly): monospace, unwrapped — the
            // panel's ScrollViewer already provides horizontal scrolling. The text is kept
            // selectable through a read-only TextBox and a Copy button grabs all of it.
            var header = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 2) };
            DockPanel.SetDock(keyText, Dock.Left);
            header.Children.Add(keyText);

            var copyButton = new Button
            {
                Content = "Copy",
                FontSize = 10,
                Padding = new Thickness(8, 1, 8, 1),
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = $"Copy \"{key}\" to the clipboard"
            };
            copyButton.Click += (_, _) => CopyPropertyValue(copyButton, value);
            DockPanel.SetDock(copyButton, Dock.Right);
            header.Children.Add(copyButton);
            panel.Children.Add(header);

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
                    TextWrapping = TextWrapping.NoWrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            });
        }
        else
        {
            panel.Children.Add(keyText);
            panel.Children.Add(new TextBlock
            {
                Text = value,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap
            });
        }
        return panel;
    }

    private static void CopyPropertyValue(Button source, string value)
    {
        try
        {
            // SetDataObject is more tolerant of the clipboard being briefly locked than SetText
            Clipboard.SetDataObject(value);
        }
        catch
        {
            return; // another process holds the clipboard, leave the button as-is
        }

        var original = source.Content;
        source.Content = "Copied!";
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        timer.Tick += (_, _) =>
        {
            source.Content = original;
            timer.Stop();
        };
        timer.Start();
    }

    private static Color GetNodeHeaderColor(MaterialGraphNode node)
    {
        if (node.IsOutputNode) return Color.FromRgb(160, 50, 50);
        if (node.IsTexture) return Color.FromRgb(0, 130, 130);
        if (node.IsParameter) return Color.FromRgb(20, 130, 70);

        return node.ExportType switch
        {
            _ when node.ExportType.Contains("FunctionCall") => Color.FromRgb(60, 90, 180),
            _ when node.ExportType.Contains("FunctionInput") || node.ExportType.Contains("FunctionOutput") => Color.FromRgb(140, 80, 170),
            _ when node.ExportType.Contains("Comment") => Color.FromRgb(70, 70, 70),
            _ when node.ExportType.Contains("Constant") => Color.FromRgb(80, 120, 60),
            _ when node.ExportType.Contains("Texture") => Color.FromRgb(0, 130, 130),
            _ when node.ExportType.Contains("CompiledShaderStage") => Color.FromRgb(95, 70, 130),
            _ when node.ExportType.Contains("Custom") => Color.FromRgb(170, 100, 40),
            _ => Color.FromRgb(80, 80, 100)
        };
    }

    private static Color GetPinColor(string pinType)
    {
        return pinType switch
        {
            "color" => Color.FromRgb(255, 210, 90),
            "float" => Color.FromRgb(140, 255, 140),
            "vector" => Color.FromRgb(255, 190, 60),
            "vector2" => Color.FromRgb(180, 220, 120),
            "attributes" => Color.FromRgb(120, 160, 255),
            "shadingmodel" => Color.FromRgb(200, 120, 200),
            "bool" => Color.FromRgb(200, 80, 80),
            _ => Color.FromRgb(180, 180, 200)
        };
    }

    // Zoom & Pan
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
        // this handler also sees handled events; a pressed node owns the gesture
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
                var size = _nodeSizes.TryGetValue(node, out var s) ? s : (NodeWidth, 150.0);
                if (marquee.IntersectsWith(new Rect(node.NodePosX, node.NodePosY, size.Item1, size.Item2)))
                    AddToSelection(node);
            }
            _selectedNode = _selectedNodes.LastOrDefault();
            if (_selectedNode != null) ShowNodeProperties(_selectedNode);
            UpdateSelectionStatus();
            return;
        }

        // a plain left-click on empty canvas clears the selection
        if (_potentialMarquee && _draggedNode == null)
        {
            ClearSelection();
            UpdateSelectionStatus();
            if (_viewModel.MaterialSections.Count > 0)
                ShowMaterialDetails();
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
                // the rectangle lives in canvas space, so its stroke is scaled back to ~1.5px
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
        if (_viewModel.Nodes.Count == 0) return;

        var minX = _viewModel.Nodes.Min(n => n.NodePosX);
        var minY = _viewModel.Nodes.Min(n => n.NodePosY);
        var maxX = _viewModel.Nodes.Max(n => n.NodePosX + (_nodeSizes.TryGetValue(n, out var s) ? s.width : NodeWidth));
        var maxY = _viewModel.Nodes.Max(n => n.NodePosY + (_nodeSizes.TryGetValue(n, out var s) ? s.height : 150));

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
}

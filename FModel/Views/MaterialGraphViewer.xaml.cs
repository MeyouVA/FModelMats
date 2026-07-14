using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    /// <summary>Nodes the user hid with H; cumulative until Alt+H. Hidden nodes and their edges are not drawn.</summary>
    private readonly HashSet<MaterialGraphNode> _hiddenNodes = [];
    /// <summary>When set, only nodes feeding this material output pin are shown; null shows all.</summary>
    private string _isolatedPin;
    /// <summary>The set of nodes kept visible under <see cref="_isolatedPin"/>; null when no pin is isolated.</summary>
    private HashSet<MaterialGraphNode> _isolatedNodes;
    /// <summary>View-only: color nodes that belong to a repeated (likely inlined-function) sub-graph pattern.</summary>
    private bool _detectFunctions;
    /// <summary>Per-node pattern color when <see cref="_detectFunctions"/> is on; nodes not in a repeat are absent.</summary>
    private readonly Dictionary<MaterialGraphNode, Color> _patternColorByNode = new();
    /// <summary>Per-node "N identical copies" count for the repeated-pattern properties readout.</summary>
    private readonly Dictionary<MaterialGraphNode, int> _patternCountByNode = new();
    /// <summary>Suppresses the isolate-combo / detect-functions events while their controls are reset programmatically.</summary>
    private bool _suppressFilterEvents;

    private const string AllPinsLabel = "All Pins";

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
        PopulateIsolatePinCombo();
        UpdateHiddenCount();

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
        // the rebuild recreates every node object, so filters holding old references are reset
        ResetViewFilters();
        NodeCountText.Text = $"Nodes: {_viewModel.Nodes.Count}";
        ConnectionCountText.Text = $"Connections: {_viewModel.Connections.Count}";
        if (!string.IsNullOrEmpty(_viewModel.ReconstructionNote))
            ReconstructedBannerText.Text = _viewModel.ReconstructionNote;
        MaterialDetailsButton.IsEnabled = _viewModel.MaterialSections.Count > 0;
        ShadersButton.Content = $"Shaders ({_viewModel.Shaders.Count})";
        ShadersButton.IsEnabled = _viewModel.Shaders.Count > 0;
        UpdateUserGraphButtons();
        PopulateIsolatePinCombo();
        UpdateHiddenCount();

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
        // the rebuild recreates every node object, so filters holding old references are reset
        ResetViewFilters();
        NodeCountText.Text = $"Nodes: {_viewModel.Nodes.Count}";
        ConnectionCountText.Text = $"Connections: {_viewModel.Connections.Count}";
        if (!string.IsNullOrEmpty(_viewModel.ReconstructionNote))
            ReconstructedBannerText.Text = _viewModel.ReconstructionNote;
        MaterialDetailsButton.IsEnabled = _viewModel.MaterialSections.Count > 0;
        ShadersButton.Content = $"Shaders ({_viewModel.Shaders.Count})";
        ShadersButton.IsEnabled = _viewModel.Shaders.Count > 0;
        ExpandMathButton.Visibility = _viewModel.CanExpandPixelShaderMath ? Visibility.Visible : Visibility.Collapsed;
        UpdateUserGraphButtons();
        PopulateIsolatePinCombo();
        UpdateHiddenCount();

        DrawGraph();
        if (_viewModel.MaterialSections.Count > 0)
            ShowMaterialDetails();
        FitToView();
    }

    /// <summary>
    /// Clears the view-only filters (hide set, pin isolation, function detection) and resets their
    /// controls. Called after a rebuild, whose new node objects would leave the old references dangling.
    /// </summary>
    private void ResetViewFilters()
    {
        _suppressFilterEvents = true;
        _hiddenNodes.Clear();
        _isolatedPin = null;
        _isolatedNodes = null;
        _detectFunctions = false;
        _patternColorByNode.Clear();
        _patternCountByNode.Clear();
        DetectFunctionsButton.IsChecked = false;
        _suppressFilterEvents = false;
    }

    /// <summary>Redraws the graph honoring the current view filters, then restores the selection outline.</summary>
    private void Redraw()
    {
        DrawGraph();
        foreach (var node in _selectedNodes)
            if (IsNodeVisible(node)) ApplySelectionVisual(node, true);
    }

    // ---- Output-pin isolation (view only) ----

    /// <summary>Fills the isolate combo with "All Pins" plus every material output pin that has an incoming connection.</summary>
    private void PopulateIsolatePinCombo()
    {
        _suppressFilterEvents = true;
        IsolatePinCombo.Items.Clear();
        IsolatePinCombo.Items.Add(AllPinsLabel);
        var outputNode = _viewModel.Nodes.FirstOrDefault(n => n.IsOutputNode);
        var connected = outputNode == null
            ? new HashSet<string>()
            : new HashSet<string>(_viewModel.Connections.Where(c => c.TargetNode == outputNode).Select(c => c.TargetPinName));
        if (outputNode != null)
            foreach (var pin in outputNode.InputPins.Where(p => !p.IsEngineStagePin && connected.Contains(p.Name)))
                IsolatePinCombo.Items.Add(pin.Name);
        IsolatePinCombo.SelectedIndex = 0;
        IsolatePinCombo.IsEnabled = IsolatePinCombo.Items.Count > 1;
        _suppressFilterEvents = false;
    }

    private void OnIsolatePinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressFilterEvents || _canvas == null) return;
        var pin = IsolatePinCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(pin) || pin == AllPinsLabel)
        {
            _isolatedPin = null;
            _isolatedNodes = null;
        }
        else
        {
            _isolatedPin = pin;
            _isolatedNodes = ComputeUpstreamNodes(pin);
        }
        Redraw();
        FitToView();
    }

    /// <summary>The output node plus every node that feeds one output pin, walking connections backwards.</summary>
    private HashSet<MaterialGraphNode> ComputeUpstreamNodes(string pinName)
    {
        var set = new HashSet<MaterialGraphNode>();
        var outputNode = _viewModel.Nodes.FirstOrDefault(n => n.IsOutputNode);
        if (outputNode == null) return set;
        set.Add(outputNode);

        var incoming = _viewModel.Connections.ToLookup(c => c.TargetNode);
        var stack = new Stack<MaterialGraphNode>();
        foreach (var c in _viewModel.Connections)
            if (c.TargetNode == outputNode && c.TargetPinName == pinName && c.SourceNode != outputNode)
                stack.Push(c.SourceNode);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!set.Add(n)) continue;
            foreach (var c in incoming[n])
                if (c.SourceNode != n) stack.Push(c.SourceNode);
        }
        return set;
    }

    // ---- Detect Functions: repeated sub-graph patterns (view only) ----

    private void OnDetectFunctionsToggled(object sender, RoutedEventArgs e)
    {
        if (_suppressFilterEvents || _canvas == null) return;
        _detectFunctions = DetectFunctionsButton.IsChecked == true;
        if (_detectFunctions) ComputePatterns();
        else { _patternColorByNode.Clear(); _patternCountByNode.Clear(); }
        Redraw();
    }

    /// <summary>
    /// Finds repeated sub-graph patterns and colors each copy of a pattern the same color. A material
    /// function inlined during cooking leaves an identical sub-graph each place it was used (only the
    /// leaf inputs differ), so structurally identical subtrees that occur more than once are flagged.
    /// Each node gets a Merkle signature of its operation kind plus its inputs' signatures; the largest
    /// repeated subtrees win, and their whole body (per copy) is painted one color.
    /// </summary>
    private void ComputePatterns()
    {
        _patternColorByNode.Clear();
        _patternCountByNode.Clear();

        // this node's inputs, ordered by the pin they feed so identical bodies hash identically
        var incoming = new Dictionary<MaterialGraphNode, List<MaterialGraphConnection>>();
        foreach (var c in _viewModel.Connections)
        {
            if (!incoming.TryGetValue(c.TargetNode, out var l)) incoming[c.TargetNode] = l = new List<MaterialGraphConnection>();
            l.Add(c);
        }
        foreach (var l in incoming.Values)
            l.Sort((a, b) => string.CompareOrdinal(a.TargetPinName, b.TargetPinName));

        var sig = new Dictionary<MaterialGraphNode, string>();
        var size = new Dictionary<MaterialGraphNode, int>();

        string Compute(MaterialGraphNode node, HashSet<MaterialGraphNode> path)
        {
            if (sig.TryGetValue(node, out var cached)) return cached;
            if (!path.Add(node)) return "~cycle~"; // back-edge guard; not cached
            var sb = new StringBuilder(CanonicalKind(node));
            var total = 1;
            if (incoming.TryGetValue(node, out var ins))
            {
                sb.Append('(');
                foreach (var c in ins)
                {
                    sb.Append(c.TargetPinName).Append('=').Append(Compute(c.SourceNode, path)).Append(';');
                    total += size.GetValueOrDefault(c.SourceNode, 1);
                }
                sb.Append(')');
            }
            path.Remove(node);
            sig[node] = sb.ToString();
            size[node] = total;
            return sig[node];
        }
        foreach (var node in _viewModel.Nodes) Compute(node, new HashSet<MaterialGraphNode>());

        // group non-trivial subtrees by signature, largest first, and color each repeated group's copies
        var groups = new Dictionary<string, List<MaterialGraphNode>>();
        foreach (var node in _viewModel.Nodes)
        {
            if (size.GetValueOrDefault(node, 1) < 3) continue; // ignore trivial one/two-node fragments
            if (!groups.TryGetValue(sig[node], out var g)) groups[sig[node]] = g = new List<MaterialGraphNode>();
            g.Add(node);
        }

        var claimed = new HashSet<MaterialGraphNode>();
        var palette = 0;
        foreach (var grp in groups.Values.Where(g => g.Count >= 2).OrderByDescending(g => size[g[0]]))
        {
            if (grp.Any(claimed.Contains)) continue; // nested inside an already-colored larger pattern
            var color = PatternColor(palette++);
            foreach (var root in grp)
                ClaimSubtree(root, incoming, claimed, color, grp.Count);
            if (palette >= 24) break; // bound the palette and the work
        }
    }

    private void ClaimSubtree(MaterialGraphNode root,
        Dictionary<MaterialGraphNode, List<MaterialGraphConnection>> incoming,
        HashSet<MaterialGraphNode> claimed, Color color, int copies)
    {
        var stack = new Stack<MaterialGraphNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (!claimed.Add(n)) continue;
            _patternColorByNode[n] = color;
            _patternCountByNode[n] = copies;
            if (incoming.TryGetValue(n, out var ins))
                foreach (var c in ins)
                    if (!claimed.Contains(c.SourceNode)) stack.Push(c.SourceNode);
        }
    }

    /// <summary>
    /// Operation identity for pattern matching: the export type, plus the mnemonic for generic math
    /// ops. Specific values, names and textures are deliberately ignored so the same function used
    /// with different inputs still matches structurally.
    /// </summary>
    private static string CanonicalKind(MaterialGraphNode node)
    {
        var kind = node.ExportType;
        if (kind is "PixelMathOp" or "PreshaderPixelMath") kind += "|" + node.Subtitle;
        return kind;
    }

    private static Color PatternColor(int index)
    {
        var hue = index * 137.508 % 360.0; // golden-angle spacing keeps successive colors far apart
        return HsvToColor(hue, 0.72, 1.0);
    }

    private static Color HsvToColor(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60.0 % 2 - 1));
        var m = v - c;
        double r = 0, g = 0, b = 0;
        switch ((int) (h / 60) % 6)
        {
            case 0: r = c; g = x; break;
            case 1: r = x; g = c; break;
            case 2: g = c; b = x; break;
            case 3: g = x; b = c; break;
            case 4: r = x; b = c; break;
            default: r = c; b = x; break;
        }
        return Color.FromRgb((byte) ((r + m) * 255), (byte) ((g + m) * 255), (byte) ((b + m) * 255));
    }

    // ---- Hide / unhide selected nodes ----

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.H) return;
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return; // don't hijack text editing
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) UnhideAll();
        else HideSelected();
        e.Handled = true;
    }

    private void OnHideSelectedClicked(object sender, RoutedEventArgs e) => HideSelected();
    private void OnUnhideAllClicked(object sender, RoutedEventArgs e) => UnhideAll();

    /// <summary>Hides the current selection. Cumulative — already-hidden nodes stay hidden.</summary>
    private void HideSelected()
    {
        if (_canvas == null || _selectedNodes.Count == 0) return;
        foreach (var n in _selectedNodes) _hiddenNodes.Add(n);
        _selectedNode = null;
        _selectedNodes.Clear();
        UpdateSelectionStatus();
        UpdateHiddenCount();
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
            if (IsNodeVisible(node))
                ComputeNodeGeometry(node);

        foreach (var connection in _viewModel.Connections)
            if (IsNodeVisible(connection.SourceNode) && IsNodeVisible(connection.TargetNode))
                DrawConnection(connection);

        foreach (var node in _viewModel.Nodes)
            if (IsNodeVisible(node))
                DrawNode(node);
    }

    /// <summary>
    /// A node is drawn unless the user hid it (H) or a pin isolation is active and the node does
    /// not feed that pin. Both filters are view-only — the underlying graph is never modified.
    /// </summary>
    private bool IsNodeVisible(MaterialGraphNode node) =>
        !_hiddenNodes.Contains(node) && (_isolatedNodes == null || _isolatedNodes.Contains(node));

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
            BorderBrush = DefaultBorderBrush(node),
            BorderThickness = new Thickness(DefaultBorderThickness(node)),
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
        // Detect Functions: a badge marking this node as part of a repeated (inlined-function) pattern
        if (_detectFunctions && _patternColorByNode.TryGetValue(node, out var badgeColor))
        {
            headerStack.Children.Add(new TextBlock
            {
                Text = $"⧉ ×{_patternCountByNode.GetValueOrDefault(node, 2)} copies",
                Foreground = new SolidColorBrush(badgeColor),
                FontSize = 9,
                FontWeight = FontWeights.SemiBold
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
        border.BorderBrush = selected ? new SolidColorBrush(Color.FromRgb(255, 200, 60)) : DefaultBorderBrush(node);
        border.BorderThickness = new Thickness(selected ? 2.5 : DefaultBorderThickness(node));
        Panel.SetZIndex(border, selected ? 3 : 2);
    }

    /// <summary>Border color of an unselected node: its repeated-pattern color when Detect Functions is on, else the default dark edge.</summary>
    private Brush DefaultBorderBrush(MaterialGraphNode node) =>
        _patternColorByNode.TryGetValue(node, out var c) ? new SolidColorBrush(c) : new SolidColorBrush(Color.FromRgb(20, 20, 20));

    /// <summary>Repeated-pattern nodes get a slightly heavier border so the pattern reads at a glance.</summary>
    private double DefaultBorderThickness(MaterialGraphNode node) =>
        _patternColorByNode.ContainsKey(node) ? 2.5 : 1.5;

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

        // synthetic / engine nodes are not self-explanatory the way an authored node is; explain them
        var explanation = NodeKindExplanation(node);
        if (explanation != null)
            PropertiesPanel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(38, 120, 160, 255)),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 4, 6, 4),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock { Text = explanation, TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.95 }
            });

        AddPropertyRow("Name", node.Name);
        AddPropertyRow("Type", node.ExportType);
        if (!string.IsNullOrEmpty(node.Subtitle))
            AddPropertyRow(node.ExportType == "PixelMathUnresolvedCustom" ? "Raw Instruction" : "Value", node.Subtitle);

        if (_detectFunctions && _patternCountByNode.TryGetValue(node, out var copies))
            AddPropertyRow("Repeated Structure",
                $"part of a sub-graph pattern that appears {copies} times in this material — likely a material function inlined at cook time");

        if (node.DisplayProperties.Count > 0)
        {
            PropertiesPanel.Children.Add(new Separator { Margin = new Thickness(0, 6, 0, 6) });
            foreach (var (key, value) in node.DisplayProperties)
                AddPropertyRow(key, value);
        }
    }

    /// <summary>
    /// A short, honest description of what a non-obvious node represents. Authored parameter/texture
    /// nodes speak for themselves; the synthetic nodes recovered from compiled shader bytecode (an
    /// unresolved instruction, an opaque math combiner, a whole shader stage, a preshader step, …) do
    /// not, so clicking one explains what it is. Returns null for ordinary nodes that need no gloss.
    /// </summary>
    private static string NodeKindExplanation(MaterialGraphNode node)
    {
        if (node.IsOutputNode)
            return "Material output. Each input pin is a material attribute (Base Color, Roughness, …); compiled engine shader stages recovered from the shader map attach here too.";
        switch (node.ExportType)
        {
            case "PixelMathUnresolvedCustom":
                return "Unresolved node — a compiled GPU instruction the decoder could not map to a high-level material operation (dynamic addressing, an unmodeled intrinsic, …). The raw instruction is shown below; it is reported honestly rather than guessed.";
            case "PreshaderPixelMath":
                return "Pixel Shader Math — several values are combined here by arithmetic baked into the compiled shader. The exact operations live only in the instruction stream; turn on \"Expand Shader Math\" to break this into one node per decoded instruction.";
            case "CompiledShaderStage":
                return "A whole compiled shader stage from the material's shader map (vertex, geometry, compute, or a non-base-pass pixel permutation). This is engine scaffolding, not part of the artist's node graph — the properties list the shader types and whether it reads any material parameter.";
            case "PixelMathConstant":
                return "A constant value baked into the compiled shader.";
            case "PixelMathInterpolant":
                return "A vertex interpolant — data the vertex shader passed to the pixel shader (a texture coordinate, vertex color, …), not a material parameter.";
            case "PixelMathEngineConstant":
                return "A value read from an engine constant buffer (View, Primitive, …) rather than the material — driven by the renderer, not the material graph.";
            case "PixelMathTextureSample":
                return "A texture sample decoded from the compiled shader. The Tex input is the bound texture; the Channels property shows which components were read.";
            case "PixelMathAppend":
                return "Combines components written separately into one vector value.";
            case "PixelMathMask":
                return "Selects a subset of a value's components (a swizzle / component mask).";
            case "PixelMathBranch":
                return "A value that differs between the branches of an if in the compiled shader (an SSA phi).";
            case "PixelMathDiscard":
                return "The clip / discard test compiled from the material's Opacity Mask.";
            case "PixelMathOp":
                return "One decoded GPU instruction from the compiled pixel-shader math.";
        }
        if (node.ExportType.StartsWith("Preshader"))
            return "A step of a uniform-expression preshader — CPU-side math the engine evaluates each frame and uploads to the shader's constant buffer (parameters, time, scalar/vector expressions).";
        if (node.IsTexture)
            return "A texture bound to the material; the output pins expose the sampled channels.";
        if (node.IsParameter)
            return "A material parameter — its value can be overridden per material instance.";
        return null;
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
                if (!IsNodeVisible(node)) continue;
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
        var visible = _viewModel.Nodes.Where(IsNodeVisible).ToList();
        if (visible.Count == 0) return;

        var minX = visible.Min(n => n.NodePosX);
        var minY = visible.Min(n => n.NodePosY);
        var maxX = visible.Max(n => n.NodePosX + (_nodeSizes.TryGetValue(n, out var s) ? s.width : NodeWidth));
        var maxY = visible.Max(n => n.NodePosY + (_nodeSizes.TryGetValue(n, out var s) ? s.height : 150));

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

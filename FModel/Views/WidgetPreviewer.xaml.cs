using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IOPath = System.IO.Path;
using CUE4Parse.UE4.Objects.Core.Math;
using FModel.ViewModels;

namespace FModel.Views;

/// <summary>
/// Draws a cooked UMG widget tree the way the editor's designer does: the tree is arranged by the
/// transcribed Slate layout passes in <see cref="WidgetPreviewViewModel"/>, then painted here with
/// the game's own textures and font files. Everything drawn comes from serialized data; anything
/// that could not be resolved is written into the details panel instead of being invented.
/// </summary>
public partial class WidgetPreviewer
{
    private readonly WidgetPreviewViewModel _viewModel;

    private Canvas _canvas;
    private ScaleTransform _scaleTransform;
    private TranslateTransform _translateTransform;

    private UmgWidgetNode _selectedNode;
    private readonly Dictionary<UmgWidgetNode, TreeViewItem> _treeItems = new();
    private readonly Dictionary<UmgWidgetNode, Rectangle> _hitRects = new();
    private Rectangle _selectionOutline;
    private bool _suppressTreeEvents;

    private bool _isPanning;
    private Point _lastMousePos;

    // ---- shared text/typeface plumbing ----
    private static readonly Dictionary<string, Typeface> _typefaceCache = new(StringComparer.Ordinal);
    private static readonly Typeface FallbackTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly string FontScratchDirectory =
        IOPath.Combine(IOPath.GetTempPath(), "FModel", "WidgetPreviewFonts");

    private static readonly (string Label, double Width, double Height)[] Resolutions =
    [
        ("1920 x 1080", 1920, 1080),
        ("1280 x 720", 1280, 720),
        ("2560 x 1440", 2560, 1440),
        ("3840 x 2160", 3840, 2160),
        ("1080 x 1920", 1080, 1920),
        ("800 x 600", 800, 600)
    ];

    static WidgetPreviewer()
    {
        // the layout pass needs text extents, so give the model a measurer backed by the same
        // typeface resolution the draw pass uses — measuring and drawing can never disagree
        WidgetPreviewViewModel.TextMeasurer = MeasureText;
    }

    public WidgetPreviewer(WidgetPreviewViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        if (!string.IsNullOrEmpty(viewModel.ClassName))
            Title = $"Widget Preview - {viewModel.ClassName}";
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WidgetNameText.Text = string.IsNullOrEmpty(_viewModel.ClassName) ? _viewModel.PackageName : _viewModel.ClassName;
        WidgetCountText.Text = $"Widgets: {_viewModel.AllNodes.Count}";
        ProvenanceText.Text =
            "Layout reconstructed from the serialized WidgetTree by the engine's own Slate arrangement rules. " +
            "A cooked widget stores no designer screen size, so the tree is arranged for the resolution chosen above. " +
            WidgetPreviewViewModel.TextMeasurementIsApproximate + ".";

        PopulateResolutions();
        SetupCanvas();

        // the model was arranged once without a text measurer available; redo it now that one is
        _viewModel.Arrange(_viewModel.ScreenWidth, _viewModel.ScreenHeight);

        BuildHierarchy();
        DrawTree();
        ShowDetails();
        Dispatcher.BeginInvoke(FitToView);
    }

    private void PopulateResolutions()
    {
        _suppressTreeEvents = true;
        foreach (var (label, _, _) in Resolutions) ResolutionCombo.Items.Add(label);
        ResolutionCombo.SelectedIndex = 0;
        _suppressTreeEvents = false;
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
        CanvasHost.MouseDown += OnPanMouseDown;
        CanvasHost.MouseUp += OnPanMouseUp;
        CanvasHost.MouseMove += OnCanvasMouseMove;
    }

    // ---------------------------------------------------------------- drawing

    private void DrawTree()
    {
        _canvas.Children.Clear();
        _hitRects.Clear();
        _selectionOutline = null;

        var screenWidth = _viewModel.ScreenWidth;
        var screenHeight = _viewModel.ScreenHeight;

        // the "screen": the area the root widget is arranged into
        _canvas.Children.Add(new Rectangle
        {
            Width = screenWidth,
            Height = screenHeight,
            Fill = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x10)),
            Stroke = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
            StrokeThickness = 1
        });

        if (_viewModel.Root != null) DrawNode(_viewModel.Root, 1.0);

        // hit rectangles sit above the painted content so any widget can be picked
        if (_viewModel.Root != null) AddHitTargets(_viewModel.Root);

        _selectionOutline = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x28)),
            StrokeThickness = 2,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_selectionOutline);
        UpdateSelectionOutline();
    }

    private void DrawNode(UmgWidgetNode node, double inheritedOpacity)
    {
        if (!node.IsArranged) return;

        var showHidden = HiddenToggle.IsChecked == true;
        if (!node.IsPainted && !showHidden) return;

        var opacity = inheritedOpacity * node.RenderOpacity;
        // a Hidden widget still occupies its slot; the designer shows it faintly so the layout reads
        var drawOpacity = node.IsPainted ? opacity : opacity * 0.25;

        DrawContent(node, drawOpacity);

        if (OutlinesToggle.IsChecked == true) DrawOutline(node);
        if (NamesToggle.IsChecked == true) DrawNameLabel(node);

        foreach (var child in node.Children) DrawNode(child, opacity);
    }

    private void DrawContent(UmgWidgetNode node, double opacity)
    {
        var width = Math.Max(0, node.ArrangedWidth);
        var height = Math.Max(0, node.ArrangedHeight);
        if (width <= 0 || height <= 0) return;

        var element = BuildContentElement(node, width, height);
        if (element == null) return;

        element.Opacity = Math.Clamp(opacity, 0, 1);
        element.IsHitTestVisible = false;
        ApplyRenderTransform(node, element, width, height);
        Canvas.SetLeft(element, node.ArrangedX);
        Canvas.SetTop(element, node.ArrangedY);
        _canvas.Children.Add(element);
    }

    private FrameworkElement BuildContentElement(UmgWidgetNode node, double width, double height)
    {
        // ---- text ----
        if (node.Text != null && node.Brush == null)
        {
            var typeface = ResolveTypeface(node.Font);
            var text = new TextBlock
            {
                Text = node.Text,
                FontFamily = typeface.FontFamily,
                FontStyle = typeface.Style,
                FontWeight = typeface.Weight,
                FontStretch = typeface.Stretch,
                FontSize = node.Font?.PixelSize ?? 32,
                Foreground = new SolidColorBrush(ToMediaColor(node.ContentColor)),
                Width = width,
                Height = height,
                TextWrapping = node.AutoWrapText || node.WrapTextAt > 0 ? TextWrapping.Wrap : TextWrapping.NoWrap,
                TextAlignment = node.Justification switch
                {
                    EUmgJustify.Center => TextAlignment.Center,
                    EUmgJustify.Right => TextAlignment.Right,
                    _ => TextAlignment.Left
                }
            };
            return text;
        }

        // ---- brushes ----
        var brush = node.Brush;
        if (brush == null) return null;

        var tint = MultiplyColor(brush.Tint, node.ContentColor);
        if (brush.DrawAs == EUmgBrushDraw.NoDrawType) return null;

        if (brush.HasResource)
        {
            var bitmap = LoadBitmap(brush.TextureBytes, tint);
            if (bitmap != null)
            {
                // Box and Border brushes are 9-slice: the brush Margin names the fixed corners as a
                // fraction of the image (FSlateBrush::Margin, SlateBrush.h)
                if (brush.DrawAs is EUmgBrushDraw.Box or EUmgBrushDraw.Border && !brush.Margin.IsZero)
                    return BuildNineSlice(bitmap, brush.Margin, width, height);

                return new Image
                {
                    Source = bitmap,
                    Width = width,
                    Height = height,
                    Stretch = Stretch.Fill
                };
            }
        }

        // A material or render-target brush is produced by the renderer at run time. Painting its
        // tint as a solid fill would both hide everything beneath it and imply an appearance the
        // asset never specifies, so the area is marked as material-driven instead.
        if (brush.IsUnrenderableResource)
            return new Rectangle
            {
                Width = width,
                Height = height,
                Fill = MaterialPlaceholderBrush,
                Stroke = new SolidColorBrush(Color.FromArgb(0x80, 0xC0, 0x90, 0xE8)),
                StrokeThickness = 1,
                StrokeDashArray = [4, 3]
            };

        // a brush with no resource is a plain tinted quad, which is what Slate draws too
        return new Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(ToMediaColor(tint)),
            RadiusX = brush.DrawAs == EUmgBrushDraw.RoundedBox ? 4 : 0,
            RadiusY = brush.DrawAs == EUmgBrushDraw.RoundedBox ? 4 : 0
        };
    }

    /// <summary>Nine-slice draw for Box/Border brushes: corners keep their pixel size, edges stretch
    /// along one axis and the centre stretches both ways.</summary>
    private static FrameworkElement BuildNineSlice(BitmapSource bitmap, UmgMargin margin, double width, double height)
    {
        var pixelWidth = bitmap.PixelWidth;
        var pixelHeight = bitmap.PixelHeight;
        var left = (int) Math.Round(margin.Left * pixelWidth);
        var top = (int) Math.Round(margin.Top * pixelHeight);
        var right = (int) Math.Round(margin.Right * pixelWidth);
        var bottom = (int) Math.Round(margin.Bottom * pixelHeight);
        if (left + right >= pixelWidth || top + bottom >= pixelHeight)
            return new Image { Source = bitmap, Width = width, Height = height, Stretch = Stretch.Fill };

        var columnsPixels = new[] { left, pixelWidth - left - right, right };
        var rowsPixels = new[] { top, pixelHeight - top - bottom, bottom };
        var columnsTarget = new[] { (double) left, Math.Max(0, width - left - right), right };
        var rowsTarget = new[] { (double) top, Math.Max(0, height - top - bottom), bottom };

        var grid = new Grid { Width = width, Height = height };
        foreach (var column in columnsTarget) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(column, GridUnitType.Pixel) });
        foreach (var row in rowsTarget) grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(row, GridUnitType.Pixel) });

        var offsetY = 0;
        for (var r = 0; r < 3; r++)
        {
            var offsetX = 0;
            for (var c = 0; c < 3; c++)
            {
                if (columnsPixels[c] > 0 && rowsPixels[r] > 0)
                {
                    var slice = new CroppedBitmap(bitmap, new Int32Rect(offsetX, offsetY, columnsPixels[c], rowsPixels[r]));
                    slice.Freeze();
                    var image = new Image { Source = slice, Stretch = Stretch.Fill };
                    Grid.SetColumn(image, c);
                    Grid.SetRow(image, r);
                    grid.Children.Add(image);
                }
                offsetX += columnsPixels[c];
            }
            offsetY += rowsPixels[r];
        }
        return grid;
    }

    /// <summary>FWidgetTransform: translation, then shear/rotation/scale about the pivot
    /// (UWidget::RenderTransformPivot is a fraction of the widget's own size).</summary>
    private static void ApplyRenderTransform(UmgWidgetNode node, FrameworkElement element, double width, double height)
    {
        var hasScale = Math.Abs(node.RenderScale.X - 1) > 1e-4 || Math.Abs(node.RenderScale.Y - 1) > 1e-4;
        var hasShear = Math.Abs(node.RenderShear.X) > 1e-4 || Math.Abs(node.RenderShear.Y) > 1e-4;
        var hasAngle = Math.Abs(node.RenderAngle) > 1e-4;
        var hasTranslation = Math.Abs(node.RenderTranslation.X) > 1e-4 || Math.Abs(node.RenderTranslation.Y) > 1e-4;
        if (!hasScale && !hasShear && !hasAngle && !hasTranslation) return;

        var group = new TransformGroup();
        if (hasShear) group.Children.Add(new SkewTransform(node.RenderShear.X, node.RenderShear.Y));
        if (hasScale) group.Children.Add(new ScaleTransform(node.RenderScale.X, node.RenderScale.Y));
        if (hasAngle) group.Children.Add(new RotateTransform(node.RenderAngle));
        if (hasTranslation) group.Children.Add(new TranslateTransform(node.RenderTranslation.X, node.RenderTranslation.Y));

        element.RenderTransformOrigin = new Point(node.RenderPivot.X, node.RenderPivot.Y);
        element.RenderTransform = group;
        element.Width = width;
        element.Height = height;
    }

    private void DrawOutline(UmgWidgetNode node)
    {
        if (node.ArrangedWidth <= 0 || node.ArrangedHeight <= 0) return;
        var outline = new Rectangle
        {
            Width = node.ArrangedWidth,
            Height = node.ArrangedHeight,
            Stroke = new SolidColorBrush(Color.FromArgb(0x60, 0x60, 0xC0, 0xFF)),
            StrokeThickness = 1,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(outline, node.ArrangedX);
        Canvas.SetTop(outline, node.ArrangedY);
        _canvas.Children.Add(outline);
    }

    private void DrawNameLabel(UmgWidgetNode node)
    {
        if (node.ArrangedWidth <= 0 || node.ArrangedHeight <= 0) return;
        var label = new TextBlock
        {
            Text = node.Name,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromArgb(0xC0, 0x90, 0xD8, 0xFF)),
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, node.ArrangedX + 2);
        Canvas.SetTop(label, node.ArrangedY + 1);
        _canvas.Children.Add(label);
    }

    private void AddHitTargets(UmgWidgetNode node)
    {
        if (node.IsArranged && node.ArrangedWidth > 0 && node.ArrangedHeight > 0 &&
            (node.IsPainted || HiddenToggle.IsChecked == true))
        {
            var hit = new Rectangle
            {
                Width = node.ArrangedWidth,
                Height = node.ArrangedHeight,
                Fill = Brushes.Transparent,
                Tag = node,
                Cursor = Cursors.Hand
            };
            Canvas.SetLeft(hit, node.ArrangedX);
            Canvas.SetTop(hit, node.ArrangedY);
            _canvas.Children.Add(hit);
            _hitRects[node] = hit;
        }

        foreach (var child in node.Children) AddHitTargets(child);
    }

    private void UpdateSelectionOutline()
    {
        if (_selectionOutline == null) return;
        if (_selectedNode is not { IsArranged: true } || _selectedNode.ArrangedWidth <= 0 || _selectedNode.ArrangedHeight <= 0)
        {
            _selectionOutline.Visibility = Visibility.Collapsed;
            return;
        }
        _selectionOutline.Visibility = Visibility.Visible;
        _selectionOutline.Width = _selectedNode.ArrangedWidth;
        _selectionOutline.Height = _selectedNode.ArrangedHeight;
        Canvas.SetLeft(_selectionOutline, _selectedNode.ArrangedX);
        Canvas.SetTop(_selectionOutline, _selectedNode.ArrangedY);
    }

    // ---------------------------------------------------------------- colour and images

    /// <summary>UE keeps widget colours linear and the frame buffer is sRGB, so a linear value has to
    /// be encoded before it can be shown (FLinearColor::ToFColor(true) is the engine's own encode).</summary>
    private static Color ToMediaColor(FLinearColor color)
    {
        var encoded = color.ToFColor(true);
        return Color.FromArgb(encoded.A, encoded.R, encoded.G, encoded.B);
    }

    private static FLinearColor MultiplyColor(FLinearColor a, FLinearColor b)
        => new(a.R * b.R, a.G * b.G, a.B * b.B, a.A * b.A);

    private static readonly Dictionary<(int Hash, uint Tint), BitmapSource> _bitmapCache = new();

    /// <summary>Diagonal hatch used for brushes whose resource the preview cannot rasterise, so the
    /// area reads as "a material draws here" without obscuring the widgets behind it.</summary>
    private static readonly Brush MaterialPlaceholderBrush = CreateMaterialPlaceholderBrush();

    private static Brush CreateMaterialPlaceholderBrush()
    {
        var geometry = new GeometryGroup();
        geometry.Children.Add(new LineGeometry(new Point(0, 8), new Point(8, 0)));
        geometry.Children.Add(new LineGeometry(new Point(-1, 1), new Point(1, -1)));
        geometry.Children.Add(new LineGeometry(new Point(7, 9), new Point(9, 7)));

        var drawing = new GeometryDrawing
        {
            Geometry = geometry,
            Pen = new Pen(new SolidColorBrush(Color.FromArgb(0x55, 0xB0, 0x80, 0xE0)), 1)
        };
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None
        };
        brush.Freeze();
        return brush;
    }

    private static BitmapSource LoadBitmap(byte[] png, FLinearColor tint)
    {
        var tintKey = PackTint(tint);
        var key = (png.Length ^ (png[0] << 8) ^ (png[^1] << 16) ^ png.GetHashCode(), tintKey);
        if (_bitmapCache.TryGetValue(key, out var cached)) return cached;

        try
        {
            using var stream = new MemoryStream(png, false);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            BitmapSource result = image;
            if (tintKey != 0xFFFFFFFF) result = ApplyTint(image, tint);
            result.Freeze();
            _bitmapCache[key] = result;
            return result;
        }
        catch
        {
            return null;
        }
    }

    private static uint PackTint(FLinearColor tint)
    {
        var color = tint.ToFColor(true);
        return (uint) ((color.A << 24) | (color.R << 16) | (color.G << 8) | color.B);
    }

    /// <summary>Slate's shader samples the texture into linear space, multiplies by the linear tint
    /// and writes back to an sRGB target. Reproducing that per channel with a lookup table gives the
    /// same result as the engine rather than an sRGB-space multiply, which would be too dark.</summary>
    private static BitmapSource ApplyTint(BitmapSource source, FLinearColor tint)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var blueLut = BuildChannelLut(tint.B);
        var greenLut = BuildChannelLut(tint.G);
        var redLut = BuildChannelLut(tint.R);
        var alpha = Math.Clamp(tint.A, 0f, 1f);

        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = blueLut[pixels[i]];
            pixels[i + 1] = greenLut[pixels[i + 1]];
            pixels[i + 2] = redLut[pixels[i + 2]];
            pixels[i + 3] = (byte) (pixels[i + 3] * alpha);
        }

        var result = BitmapSource.Create(width, height, converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private static byte[] BuildChannelLut(float tint)
    {
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var linear = SrgbToLinear(i / 255.0);
            lut[i] = (byte) Math.Clamp(Math.Round(LinearToSrgb(linear * tint) * 255.0), 0, 255);
        }
        return lut;
    }

    // the sRGB transfer function pair, as used by FLinearColor::ToFColor / FColor::FromSRGB
    private static double SrgbToLinear(double value)
        => value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);

    private static double LinearToSrgb(double value)
    {
        value = Math.Clamp(value, 0, 1);
        return value <= 0.0031308 ? value * 12.92 : 1.055 * Math.Pow(value, 1.0 / 2.4) - 0.055;
    }

    // ---------------------------------------------------------------- fonts

    /// <summary>Turns the cooked .ufont bytes into a WPF typeface. WPF can only load a face from a
    /// location, so the bytes are written once to a scratch file named by their own hash.</summary>
    private static Typeface ResolveTypeface(UmgFont font)
    {
        if (font?.FaceBytes is not { Length: > 4 }) return FallbackTypeface;

        var hash = Convert.ToHexString(MD5.HashData(font.FaceBytes));
        if (_typefaceCache.TryGetValue(hash, out var cached)) return cached;

        var typeface = FallbackTypeface;
        try
        {
            var directory = IOPath.Combine(FontScratchDirectory, hash);
            Directory.CreateDirectory(directory);
            var path = IOPath.Combine(directory, "face" + FontExtension(font.FaceBytes));
            if (!File.Exists(path)) File.WriteAllBytes(path, font.FaceBytes);

            var family = Fonts.GetFontFamilies(new Uri(path)).FirstOrDefault();
            if (family != null) typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        }
        catch
        {
            // a face WPF cannot open falls back to the substitute; the model already records that
            // the file was found, and the details panel names the font asset either way
        }

        _typefaceCache[hash] = typeface;
        return typeface;
    }

    /// <summary>Cooked .ufont files are the font program verbatim; the sfnt tag says which kind.</summary>
    private static string FontExtension(byte[] data)
    {
        if (data.Length < 4) return ".ttf";
        var tag = (uint) ((data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3]);
        return tag switch
        {
            0x4F54544F => ".otf",   // 'OTTO'
            0x74746366 => ".ttc",   // 'ttcf'
            _ => ".ttf"             // 0x00010000 and 'true'
        };
    }

    private static (double Width, double Height) MeasureText(string text, UmgFont font, double wrapAt)
    {
        if (string.IsNullOrEmpty(text)) return (0, 0);
        var typeface = ResolveTypeface(font);
        var emSize = font?.PixelSize ?? 32;
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            typeface, emSize, Brushes.White, 1.0);
        if (wrapAt > 0) formatted.MaxTextWidth = wrapAt;
        return (formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    // ---------------------------------------------------------------- hierarchy panel

    private void BuildHierarchy()
    {
        HierarchyTree.Items.Clear();
        _treeItems.Clear();
        if (_viewModel.Root == null) return;
        HierarchyTree.Items.Add(BuildTreeItem(_viewModel.Root));
    }

    private TreeViewItem BuildTreeItem(UmgWidgetNode node)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(new TextBlock { Text = node.Name, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "  [" + node.ClassName + "]",
            Opacity = 0.6,
            Margin = new Thickness(2, 0, 0, 0)
        });
        if (node.ExpandedFrom != null)
            header.Children.Add(new TextBlock { Text = "  ↳ expanded", Opacity = 0.5, Margin = new Thickness(4, 0, 0, 0) });
        if (!node.IsPainted)
            header.Children.Add(new TextBlock { Text = "  (" + node.Visibility + ")", Opacity = 0.45, Margin = new Thickness(4, 0, 0, 0) });

        var item = new TreeViewItem { Header = header, Tag = node, IsExpanded = true };
        foreach (var child in node.Children) item.Items.Add(BuildTreeItem(child));
        _treeItems[node] = item;
        return item;
    }

    private void OnHierarchySelectionChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeEvents) return;
        if (e.NewValue is TreeViewItem { Tag: UmgWidgetNode node }) SelectNode(node, fromTree: true);
    }

    private void SelectNode(UmgWidgetNode node, bool fromTree)
    {
        _selectedNode = node;
        UpdateSelectionOutline();
        ShowDetails();
        SelectedWidgetText.Text = node == null
            ? ""
            : $"{node.Name} [{node.ClassName}]  •  {node.ArrangedWidth:0.#} x {node.ArrangedHeight:0.#} at ({node.ArrangedX:0.#}, {node.ArrangedY:0.#})";

        if (fromTree || node == null || !_treeItems.TryGetValue(node, out var item)) return;
        _suppressTreeEvents = true;
        item.IsSelected = true;
        item.BringIntoView();
        _suppressTreeEvents = false;
    }

    // ---------------------------------------------------------------- details panel

    private void OnShowDetails(object sender, RoutedEventArgs e)
    {
        _selectedNode = null;
        UpdateSelectionOutline();
        ShowDetails();
    }

    private void ShowDetails()
    {
        DetailsPanel.Children.Clear();
        if (_selectedNode == null)
        {
            DetailsTitleText.Text = "Widget Details";
            foreach (var (key, value) in _viewModel.ClassInfo) AddDetailRow(key, value);
            if (_viewModel.UnresolvedNotes.Count > 0)
            {
                AddDetailHeader("Not expanded");
                foreach (var note in _viewModel.UnresolvedNotes) AddDetailNote(note);
            }

            var allNotes = _viewModel.AllNodes.SelectMany(n => n.Notes.Select(note => $"{n.Name}: {note}")).Distinct().ToList();
            if (allNotes.Count > 0)
            {
                AddDetailHeader("Unresolved");
                foreach (var note in allNotes) AddDetailNote(note);
            }
            return;
        }

        var node = _selectedNode;
        DetailsTitleText.Text = node.Name;

        AddDetailHeader("Widget");
        AddDetailRow("Class", node.ClassName);
        AddDetailRow("Arranges As", node.Kind.ToString());
        AddDetailRow("Visibility", node.Visibility.ToString());
        if (Math.Abs(node.RenderOpacity - 1) > 1e-4) AddDetailRow("Render Opacity", node.RenderOpacity.ToString("0.###"));
        if (!node.IsEnabled) AddDetailRow("Enabled", "false");
        if (node.ExpandedFrom != null) AddDetailRow("Expanded From", node.ExpandedFrom);

        AddDetailHeader("Layout");
        AddDetailRow("Desired Size", $"{node.DesiredSize.X:0.##} x {node.DesiredSize.Y:0.##}");
        AddDetailRow("Arranged", $"{node.ArrangedWidth:0.##} x {node.ArrangedHeight:0.##} at ({node.ArrangedX:0.##}, {node.ArrangedY:0.##})");
        if (Math.Abs(node.LayoutScale - 1) > 1e-4) AddDetailRow("Layout Scale", node.LayoutScale.ToString("0.###"));

        if (node.SlotClassName.Length > 0)
        {
            AddDetailHeader("Slot (" + node.SlotClassName + ")");
            AddDetailRow("Alignment", $"{node.HAlign} / {node.VAlign}");
            if (!node.Padding.IsZero) AddDetailRow("Padding", node.Padding.ToString());
            if (node.SlotClassName == "CanvasPanelSlot")
            {
                AddDetailRow("Anchors", $"min ({node.AnchorMin.X:0.###}, {node.AnchorMin.Y:0.###})  max ({node.AnchorMax.X:0.###}, {node.AnchorMax.Y:0.###})");
                AddDetailRow("Offsets", node.Offsets.ToString());
                AddDetailRow("Alignment Pivot", $"({node.Alignment.X:0.###}, {node.Alignment.Y:0.###})");
                if (node.AutoSize) AddDetailRow("Auto Size", "true");
                if (node.ZOrder != 0) AddDetailRow("Z Order", node.ZOrder.ToString());
            }
            if (node.SizeRule == EUmgSizeRule.Fill) AddDetailRow("Size", $"Fill {node.SizeValue:0.###}");
            if (node.SlotClassName is "GridSlot" or "UniformGridSlot")
                AddDetailRow("Cell", $"row {node.Row} col {node.Column} (span {node.RowSpan} x {node.ColumnSpan})");
        }

        if (node.Text != null)
        {
            AddDetailHeader("Text");
            AddDetailRow("Value", node.Text);
            if (node.Font != null)
            {
                AddDetailRow("Font", node.Font.FontPath ?? "(class default)");
                AddDetailRow("Typeface", node.Font.FaceName ?? node.Font.TypefaceName);
                AddDetailRow("Size", $"{node.Font.Size} pt ({node.Font.PixelSize:0.#} px at Slate's 96 DPI)");
                AddDetailRow("Face File", node.Font.FaceBytes != null ? $"loaded, {node.Font.FaceBytes.Length:N0} bytes" : "not found");
                if (node.Font.Note != null) AddDetailNote(node.Font.Note);
            }
            AddDetailRow("Color", node.ContentColor.Hex);
        }

        if (node.Brush != null)
        {
            AddDetailHeader("Brush");
            AddDetailRow("Draw As", node.Brush.DrawAs.ToString());
            AddDetailRow("Image Size", $"{node.Brush.ImageWidth:0.##} x {node.Brush.ImageHeight:0.##}");
            AddDetailRow("Tint", node.Brush.Tint.Hex);
            if (node.Brush.ResourcePath != null) AddDetailRow("Resource", $"{node.Brush.ResourceType} {node.Brush.ResourcePath}");
            if (!node.Brush.Margin.IsZero) AddDetailRow("Margin", node.Brush.Margin.ToString());
            if (node.Brush.ResourceNote != null) AddDetailNote(node.Brush.ResourceNote);
        }

        if (node.Notes.Count > 0)
        {
            AddDetailHeader("Unresolved");
            foreach (var note in node.Notes) AddDetailNote(note);
        }

        if (node.Details.Count > 0)
        {
            AddDetailHeader("Serialized Properties");
            foreach (var (key, value) in node.Details) AddDetailRow(key, value);
        }
    }

    private void AddDetailHeader(string text)
    {
        DetailsPanel.Children.Add(new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 3),
            Foreground = new SolidColorBrush(Color.FromRgb(0x7F, 0xD0, 0xE8))
        });
    }

    private void AddDetailRow(string key, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var keyText = new TextBlock { Text = key, Opacity = 0.7, TextWrapping = TextWrapping.Wrap };
        var valueText = new TextBlock { Text = value ?? "", TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(valueText, 1);
        row.Children.Add(keyText);
        row.Children.Add(valueText);
        DetailsPanel.Children.Add(row);
    }

    private void AddDetailNote(string note)
    {
        DetailsPanel.Children.Add(new TextBlock
        {
            Text = "• " + note,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 1, 0, 1),
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x70))
        });
    }

    private void OnCopyPanel(object sender, RoutedEventArgs e)
    {
        var lines = new List<string>();
        foreach (var child in DetailsPanel.Children)
        {
            switch (child)
            {
                case TextBlock block: lines.Add(block.Text); break;
                case Grid grid when grid.Children.Count == 2 && grid.Children[0] is TextBlock key && grid.Children[1] is TextBlock value:
                    lines.Add($"{key.Text}: {value.Text}");
                    break;
            }
        }
        if (lines.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, lines));
    }

    // ---------------------------------------------------------------- interaction

    private void OnResolutionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_canvas == null || ResolutionCombo.SelectedIndex < 0) return;
        var (_, width, height) = Resolutions[ResolutionCombo.SelectedIndex];
        _viewModel.Arrange(width, height);
        DrawTree();
        SelectNode(_selectedNode, fromTree: true);
        FitToView();
    }

    private void OnRedrawToggled(object sender, RoutedEventArgs e)
    {
        if (_canvas == null) return;
        DrawTree();
    }

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(_canvas);
        var picked = PickNode(position);
        if (picked != null) SelectNode(picked, fromTree: false);
    }

    /// <summary>Picks the deepest arranged widget under the cursor, which is what the designer does
    /// when you click through a stack of panels.</summary>
    private UmgWidgetNode PickNode(Point position)
    {
        UmgWidgetNode best = null;
        var bestDepth = -1;
        foreach (var (node, rect) in _hitRects)
        {
            var left = Canvas.GetLeft(rect);
            var top = Canvas.GetTop(rect);
            if (position.X < left || position.Y < top || position.X > left + rect.Width || position.Y > top + rect.Height) continue;
            var depth = Depth(node);
            if (depth <= bestDepth) continue;
            best = node;
            bestDepth = depth;
        }
        return best;
    }

    private static int Depth(UmgWidgetNode node)
    {
        var depth = 0;
        for (var current = node.Parent; current != null; current = current.Parent) depth++;
        return depth;
    }

    private void OnPanMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Right or MouseButton.Middle)) return;
        _isPanning = true;
        _lastMousePos = e.GetPosition(CanvasHost);
        CanvasHost.CaptureMouse();
    }

    private void OnPanMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton is not (MouseButton.Right or MouseButton.Middle)) return;
        _isPanning = false;
        CanvasHost.ReleaseMouseCapture();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning) return;
        var position = e.GetPosition(CanvasHost);
        _translateTransform.X += position.X - _lastMousePos.X;
        _translateTransform.Y += position.Y - _lastMousePos.Y;
        _lastMousePos = position;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var position = e.GetPosition(CanvasHost);
        var factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        var newScale = Math.Clamp(_scaleTransform.ScaleX * factor, 0.02, 8);
        factor = newScale / _scaleTransform.ScaleX;

        _translateTransform.X = position.X - (position.X - _translateTransform.X) * factor;
        _translateTransform.Y = position.Y - (position.Y - _translateTransform.Y) * factor;
        _scaleTransform.ScaleX = _scaleTransform.ScaleY = newScale;
        UpdateZoomText();
        e.Handled = true;
    }

    private void OnFitToView(object sender, RoutedEventArgs e) => FitToView();

    private void FitToView()
    {
        if (_canvas == null) return;
        var availableWidth = CanvasHost.ActualWidth;
        var availableHeight = CanvasHost.ActualHeight;
        if (availableWidth <= 0 || availableHeight <= 0) return;

        var scale = Math.Min(availableWidth / (_viewModel.ScreenWidth + 40), availableHeight / (_viewModel.ScreenHeight + 40));
        scale = Math.Clamp(scale, 0.02, 4);
        _scaleTransform.ScaleX = _scaleTransform.ScaleY = scale;
        _translateTransform.X = (availableWidth - _viewModel.ScreenWidth * scale) / 2;
        _translateTransform.Y = (availableHeight - _viewModel.ScreenHeight * scale) / 2;
        UpdateZoomText();
    }

    private void OnResetZoom(object sender, RoutedEventArgs e)
    {
        _scaleTransform.ScaleX = _scaleTransform.ScaleY = 1;
        _translateTransform.X = _translateTransform.Y = 0;
        UpdateZoomText();
    }

    private void UpdateZoomText() => ZoomText.Text = $"Zoom: {_scaleTransform.ScaleX * 100:0}%";

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) OnCopyPanel(sender, e);
    }
}

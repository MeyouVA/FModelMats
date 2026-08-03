using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.FileProvider;
using CUE4Parse.UE4.Assets;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Engine.Font;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse_Conversion.Textures;

namespace FModel.ViewModels;

// UMG widget preview.
//
// A cooked WidgetBlueprint keeps its whole designer layout in serialized tagged properties:
// WidgetBlueprintGeneratedClass.WidgetTree -> WidgetTree.RootWidget -> panel.Slots[] ->
// slot.Content. Nothing about the layout is lost in the cook, so the tree below is read 1:1
// from those properties; nothing is inferred from names.
//
// Properties equal to their class default are NOT serialized, so every default applied here is
// taken from the engine's own constructors (paths cited at each site, read from the installed
// engine source that matches the game's version). The arrangement code is a transcription of the
// matching Slate SWidget::OnArrangeChildren / ComputeDesiredSize, so the preview places widgets
// the same way the editor's designer does.

#region value types mirrored from Slate

public enum EUmgVisibility { Visible, Collapsed, Hidden, HitTestInvisible, SelfHitTestInvisible }
public enum EUmgHAlign { Fill, Left, Center, Right }
public enum EUmgVAlign { Fill, Top, Center, Bottom }
public enum EUmgSizeRule { Automatic, Fill }
public enum EUmgStretch { None, Fill, ScaleToFit, ScaleToFitX, ScaleToFitY, ScaleToFill, ScaleBySafeZone, UserSpecified, UserSpecifiedWithClipping }
public enum EUmgStretchDirection { Both, DownOnly, UpOnly }
public enum EUmgBrushDraw { NoDrawType, Box, Border, Image, RoundedBox }
public enum EUmgJustify { Left, Center, Right, InvariantLeft }

/// <summary>What a widget does with the children in its Slots array. Chosen from the widget's class
/// (and, for Blueprint widget classes, its serialized parent chain), never from its name.</summary>
public enum EUmgPanelKind
{
    Leaf,
    Canvas,
    Overlay,
    HorizontalBox,
    VerticalBox,
    Grid,
    UniformGrid,
    WrapBox,
    ScrollBox,
    SizeBox,
    ScaleBox,
    Compound,
    Switcher,
    UserWidget
}

public readonly struct UmgMargin(double left, double top, double right, double bottom)
{
    public readonly double Left = left;
    public readonly double Top = top;
    public readonly double Right = right;
    public readonly double Bottom = bottom;

    public double Horizontal => Left + Right;
    public double Vertical => Top + Bottom;
    public bool IsZero => Left == 0 && Top == 0 && Right == 0 && Bottom == 0;
    public override string ToString() => $"L {Left:0.##}  T {Top:0.##}  R {Right:0.##}  B {Bottom:0.##}";
}

/// <summary>A resolved FSlateBrush. <see cref="TextureBytes"/> is a PNG of the decoded resource
/// texture when one could be decoded; a material resource is reported but never rendered.</summary>
public class UmgBrush
{
    public double ImageWidth = 32;   // SlateBrushDefs::DefaultImageSize, SlateBrush.h
    public double ImageHeight = 32;
    public FLinearColor Tint = new(1, 1, 1, 1);   // FSlateBrush::FSlateBrush(), SlateBrush.cpp
    public EUmgBrushDraw DrawAs = EUmgBrushDraw.Image;
    public UmgMargin Margin;
    public string ResourcePath;
    public string ResourceType;
    public byte[] TextureBytes;
    public string ResourceNote;
    public bool HasResource => TextureBytes is { Length: > 0 };
    /// <summary>The brush names a resource the preview cannot rasterise (a material, a render
    /// target). Drawing its tint as a solid fill would hide everything under it and claim an
    /// appearance the asset does not specify, so the view marks the area instead.</summary>
    public bool IsUnrenderableResource;
}

/// <summary>A resolved FSlateFontInfo. <see cref="FaceBytes"/> is the game's own font file, read
/// from the .ufont that the cook writes next to the UFontFace asset.</summary>
public class UmgFont
{
    public string FontPath;
    public string TypefaceName = "Default";
    public int Size = 24;               // UTextBlock::UTextBlock, TextBlock.cpp
    public byte[] FaceBytes;
    public string FaceName;
    public string Note;
    public double OutlineSize;
    public FLinearColor OutlineColor = new(0, 0, 0, 1);

    /// <summary>Slate sizes fonts in points and rasterises at a fixed 96 DPI render target
    /// (FreeTypeConstants::RenderDPI, FontCacheFreeType.h; FreeTypeUtils::ApplySizeAndScale does
    /// <c>size * DPI / 72</c>), so a point size becomes this many device pixels.</summary>
    public double PixelSize => Size * 96.0 / 72.0;
}

#endregion

/// <summary>One widget in the tree, with its slot's layout data and the arranged geometry the
/// layout pass produced.</summary>
public class UmgWidgetNode
{
    public string Name = "";
    public string ClassName = "";
    public string SlotClassName = "";
    public EUmgPanelKind Kind = EUmgPanelKind.Leaf;
    public UmgWidgetNode Parent;
    public readonly List<UmgWidgetNode> Children = [];

    // ---- widget state (UWidget) ----
    public EUmgVisibility Visibility = EUmgVisibility.Visible;   // UWidget::UWidget, Widget.cpp
    public double RenderOpacity = 1.0;                           // UWidget::UWidget, Widget.cpp
    public bool IsEnabled = true;
    public FVector2D RenderTranslation = new(0, 0);
    public FVector2D RenderScale = new(1, 1);
    public FVector2D RenderShear = new(0, 0);
    public double RenderAngle;
    public FVector2D RenderPivot = new(0.5f, 0.5f);              // UWidget::RenderTransformPivot, Widget.h

    // ---- slot layout data ----
    public EUmgHAlign HAlign = EUmgHAlign.Fill;
    public EUmgVAlign VAlign = EUmgVAlign.Fill;
    public UmgMargin Padding;
    /// <summary>Padding this widget puts around its own content (UBorder.Padding, a button style's
    /// NormalPadding). Slate feeds it to the single ChildSlot, so it adds to the child's slot padding.</summary>
    public UmgMargin ContentPadding;
    /// <summary>The slot itself serialized a Padding, so it is the authoritative one.</summary>
    public bool PaddingIsFromSlot;
    public EUmgSizeRule SizeRule = EUmgSizeRule.Automatic;
    public double SizeValue = 1.0;
    public double MaxSize;
    public int Row, Column, RowSpan = 1, ColumnSpan = 1;
    public FVector2D Nudge = new(0, 0);
    public int ZOrder;
    public bool ForceNewLine;

    // canvas slot
    public UmgMargin Offsets = new(0, 0, 100, 30);               // UCanvasPanelSlot::UCanvasPanelSlot, CanvasPanelSlot.cpp
    public FVector2D AnchorMin = new(0, 0);
    public FVector2D AnchorMax = new(0, 0);
    public FVector2D Alignment = new(0, 0);
    public bool AutoSize;

    // ---- per-class content ----
    public UmgBrush Brush;
    public string Text;
    public FLinearColor ContentColor = new(1, 1, 1, 1);
    public UmgFont Font;
    public EUmgJustify Justification = EUmgJustify.Left;
    public bool AutoWrapText;
    public double WrapTextAt;
    public double? WidthOverride, HeightOverride;
    public double? MinDesiredWidth, MinDesiredHeight, MaxDesiredWidth, MaxDesiredHeight;
    public EUmgStretch Stretch = EUmgStretch.None;               // UScaleBox::Stretch, ScaleBox.h
    public EUmgStretchDirection StretchDirection = EUmgStretchDirection.Both;
    public double UserSpecifiedScale = 1.0;
    public int ActiveWidgetIndex;
    public double Percent;
    public FVector2D SpacerSize = new(1, 1);                     // USpacer::Size, Spacer.cpp
    public List<double> ColumnFill = [];
    public List<double> RowFill = [];
    public double WrapSize = 500;                                // UWrapBox::WrapSize, WrapBox.cpp
    public bool ExplicitWrapSize;

    // ---- layout results, in slate units at the chosen screen size ----
    public FVector2D DesiredSize;
    public double ArrangedX, ArrangedY, ArrangedWidth, ArrangedHeight;
    public double LayoutScale = 1.0;
    public bool IsArranged;

    /// <summary>Set when this node is the expanded content of a child widget blueprint; carries the
    /// package it came from so the panel can say where the sub-tree was read.</summary>
    public string ExpandedFrom;
    /// <summary>Anything the reader could not resolve, surfaced instead of being guessed at.</summary>
    public readonly List<string> Notes = [];
    public readonly List<KeyValuePair<string, string>> Details = [];

    public bool IsVisibleForLayout => Visibility != EUmgVisibility.Collapsed;
    public bool IsPainted => Visibility is not (EUmgVisibility.Collapsed or EUmgVisibility.Hidden);

    public IEnumerable<UmgWidgetNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var sub in child.Descendants()) yield return sub;
        }
    }
}

public class WidgetPreviewViewModel
{
    /// <summary>Widget classes whose children come from an entirely different mechanism than a
    /// Slots array, so the tree stops there rather than pretending it is empty.</summary>
    private const int MaxUserWidgetDepth = 6;

    /// <summary>Nested widget blueprints multiply: a retail main menu can expand past four thousand
    /// widgets. Expansion stops there so the window stays usable, and the stop is recorded rather
    /// than the tree silently ending.</summary>
    private const int MaxExpandedNodes = 4000;
    private int _nodeCount;
    private bool _budgetReported;

    public string PackageName { get; private set; } = "";
    public string ClassName { get; private set; } = "";
    public string ParentClass { get; private set; } = "";
    public UmgWidgetNode Root { get; private set; }
    public List<UmgWidgetNode> AllNodes { get; } = [];
    public List<KeyValuePair<string, string>> ClassInfo { get; } = [];
    /// <summary>Widget classes referenced by the tree that could not be expanded, and why.</summary>
    public List<string> UnresolvedNotes { get; } = [];

    /// <summary>Design size the tree was last arranged for. UMG has no serialized designer
    /// resolution in a cooked asset, so this is a viewing choice, not asset data.</summary>
    public double ScreenWidth { get; private set; } = 1920;
    public double ScreenHeight { get; private set; } = 1080;

    private IFileProvider _provider;
    private readonly Dictionary<string, byte[]> _fontCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (byte[] Png, string Note)> _textureCache = new(StringComparer.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- extraction

    /// <summary>Reads the widget tree out of a package's WidgetBlueprintGeneratedClass. Returns null
    /// when the package holds no widget tree.</summary>
    public static WidgetPreviewViewModel ExtractFromPackage(IPackage package, IFileProvider provider)
    {
        var exports = package.GetExports().ToList();
        var tree = exports.FirstOrDefault(e => e.ExportType == "WidgetTree");
        if (tree == null) return null;

        var vm = new WidgetPreviewViewModel { _provider = provider, PackageName = package.Name };

        var generatedClass = exports.FirstOrDefault(e => e.ExportType.EndsWith("WidgetBlueprintGeneratedClass", StringComparison.Ordinal));
        if (generatedClass != null)
        {
            vm.ClassName = generatedClass.Name;
            vm.ParentClass = generatedClass.Super?.Name.Text ?? "";
            // prefer the tree this class points at, in case the package holds more than one
            if (generatedClass.TryGetValue(out UObject ownTree, "WidgetTree") && ownTree != null)
                tree = ownTree;
        }

        var (rootWidget, owningClass) = vm.FindWidgetTreeRoot(generatedClass, tree);
        if (rootWidget == null)
        {
            vm.UnresolvedNotes.Add("no class in this widget's parent chain has a WidgetTree with a RootWidget — the blueprint has an empty designer graph");
            return vm;
        }
        if (owningClass != null)
            vm.ClassInfo.Add(new KeyValuePair<string, string>("Widget Tree From", owningClass));

        vm.Root = vm.ReadWidget(rootWidget, null, 0, []);
        if (owningClass != null) vm.Root.ExpandedFrom = owningClass;
        vm.CollectNodes(vm.Root);

        vm.ClassInfo.Add(new KeyValuePair<string, string>("Package", package.Name));
        if (vm.ClassName.Length > 0) vm.ClassInfo.Add(new KeyValuePair<string, string>("Class", vm.ClassName));
        if (vm.ParentClass.Length > 0) vm.ClassInfo.Add(new KeyValuePair<string, string>("Parent Class", vm.ParentClass));
        vm.ClassInfo.Add(new KeyValuePair<string, string>("Widgets", vm.AllNodes.Count.ToString()));
        if (generatedClass != null && generatedClass.TryGetValue(out UObject[] animations, "Animations"))
            vm.ClassInfo.Add(new KeyValuePair<string, string>("Animations", string.Join(", ", animations.Where(a => a != null).Select(a => a.Name))));

        vm.Arrange(vm.ScreenWidth, vm.ScreenHeight);
        return vm;
    }

    private void CollectNodes(UmgWidgetNode node)
    {
        if (node == null) return;
        AllNodes.Add(node);
        foreach (var child in node.Children) CollectNodes(child);
    }

    private UmgWidgetNode ReadWidget(UObject export, UObject slot, int userWidgetDepth, HashSet<string> expansionStack)
    {
        var node = new UmgWidgetNode
        {
            Name = export.Name,
            ClassName = export.ExportType,
        };
        node.Kind = ClassifyPanel(export, node);
        _nodeCount++;

        ReadWidgetState(export, node);
        if (slot != null) ReadSlot(slot, node);
        ReadContent(export, node);

        // panel children come from the Slots array; each slot names its Content widget
        if (export.TryGetValue(out UObject[] slots, "Slots"))
        {
            foreach (var childSlot in slots)
            {
                if (childSlot == null) continue;
                if (!childSlot.TryGetValue(out UObject content, "Content") || content == null) continue;
                var child = ReadWidget(content, childSlot, userWidgetDepth, expansionStack);
                child.Parent = node;
                node.Children.Add(child);
            }
        }

        // a child widget blueprint keeps its own tree in its own package; the designer shows that
        // content inline, so expand it the same way (guarded against cycles and runaway depth)
        if (node.Kind == EUmgPanelKind.UserWidget && node.Children.Count == 0)
            ExpandUserWidget(export, node, userWidgetDepth, expansionStack);

        return node;
    }

    private void ExpandUserWidget(UObject export, UmgWidgetNode node, int depth, HashSet<string> expansionStack)
    {
        var classPath = PackagePathOf(export.Class);
        if (string.IsNullOrEmpty(classPath))
        {
            node.Notes.Add("the widget class could not be resolved to a package, so its content is not expanded");
            return;
        }
        node.Details.Add(new KeyValuePair<string, string>("Widget Class", classPath));

        if (depth >= MaxUserWidgetDepth)
        {
            node.Notes.Add($"nesting stops at {MaxUserWidgetDepth} levels — content of {classPath} is not expanded");
            return;
        }
        if (_nodeCount >= MaxExpandedNodes)
        {
            node.Notes.Add($"the tree already holds {MaxExpandedNodes:N0} widgets, so {classPath} is not expanded");
            if (!_budgetReported)
            {
                _budgetReported = true;
                UnresolvedNotes.Add($"expansion of nested widget blueprints stopped at {MaxExpandedNodes:N0} widgets to keep the preview responsive — " +
                                    "the widgets that were not expanded are marked in the hierarchy");
            }
            return;
        }
        if (!expansionStack.Add(classPath))
        {
            node.Notes.Add($"{classPath} is already being expanded further up the tree (recursive widget), so it is not expanded again");
            return;
        }

        try
        {
            if (_provider == null || !_provider.TryLoadPackage(classPath, out var childPackage))
            {
                node.Notes.Add($"the package {classPath} is not in the mounted archives, so its content is not expanded");
                if (!UnresolvedNotes.Contains(classPath)) UnresolvedNotes.Add(classPath);
                return;
            }

            var childExports = childPackage.GetExports().ToList();
            var childClass = childExports.FirstOrDefault(e => e.ExportType.EndsWith("WidgetBlueprintGeneratedClass", StringComparison.Ordinal));
            var childTree = childClass != null && childClass.TryGetValue(out UObject named, "WidgetTree") && named != null
                ? named
                : childExports.FirstOrDefault(e => e.ExportType == "WidgetTree");

            // a derived widget blueprint cooks an empty tree and inherits its parent's
            var (childRoot, owningClass) = FindWidgetTreeRoot(childClass, childTree);
            if (childRoot == null)
            {
                node.Notes.Add($"no class in {classPath}'s parent chain has a widget tree with a root widget");
                return;
            }

            var expanded = ReadWidget(childRoot, null, depth + 1, expansionStack);
            expanded.Parent = node;
            expanded.ExpandedFrom = owningClass ?? classPath;
            node.Children.Add(expanded);
        }
        catch (Exception e)
        {
            node.Notes.Add($"failed to expand {classPath}: {e.Message}");
        }
        finally
        {
            expansionStack.Remove(classPath);
        }
    }

    /// <summary>
    /// UWidgetBlueprintGeneratedClass::FindWidgetTreeOwningClass (WidgetBlueprintGeneratedClass.cpp):
    /// a widget blueprint that adds no widgets of its own cooks an empty WidgetTree and inherits its
    /// parent's, so the search walks up the super chain while the tree has no RootWidget. Returns the
    /// root and, when it came from an ancestor, the class that owns it.
    /// </summary>
    private (UObject Root, string OwningClass) FindWidgetTreeRoot(UObject generatedClass, UObject tree)
    {
        if (tree != null && tree.TryGetValue(out UObject ownRoot, "RootWidget") && ownRoot != null)
            return (ownRoot, null);

        var super = generatedClass?.Super;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (super != null)
        {
            var superPackage = PackagePathOf(super);
            if (superPackage == null || !visited.Add(superPackage)) break;
            if (_provider == null || !_provider.TryLoadPackage(superPackage, out var superPkg))
            {
                UnresolvedNotes.Add($"the parent class package {superPackage} is not in the mounted archives, so its widget tree could not be inherited");
                break;
            }

            var superExports = superPkg.GetExports().ToList();
            var superClass = superExports.FirstOrDefault(e => e.ExportType.EndsWith("WidgetBlueprintGeneratedClass", StringComparison.Ordinal));
            var superTree = superClass != null && superClass.TryGetValue(out UObject t, "WidgetTree") && t != null
                ? t
                : superExports.FirstOrDefault(e => e.ExportType == "WidgetTree");

            if (superTree != null && superTree.TryGetValue(out UObject inheritedRoot, "RootWidget") && inheritedRoot != null)
                return (inheritedRoot, superPackage);

            super = superClass?.Super;
        }

        return (null, null);
    }

    /// <summary>Turns a resolved class reference into the package path that holds it.</summary>
    private static string PackagePathOf(ResolvedObject cls)
    {
        if (cls == null) return null;
        var outer = cls;
        while (outer.Outer != null) outer = outer.Outer;
        var path = outer.Name.Text;
        if (string.IsNullOrEmpty(path) || path.StartsWith("/Script/", StringComparison.Ordinal)) return null;
        return path;
    }

    // ---------------------------------------------------------------- property reading

    private static void ReadWidgetState(UObject export, UmgWidgetNode node)
    {
        if (export.TryGetValue(out FName visibility, "Visibility"))
            node.Visibility = ParseEnum(visibility.Text, node.Visibility);
        if (export.TryGetValue(out float renderOpacity, "RenderOpacity"))
            node.RenderOpacity = renderOpacity;
        if (export.TryGetValue(out bool isEnabled, "bIsEnabled"))
            node.IsEnabled = isEnabled;

        if (export.TryGetValue(out FStructFallback transform, "RenderTransform"))
        {
            node.RenderTranslation = Vector2Of(transform, "Translation", new FVector2D(0, 0));
            node.RenderScale = Vector2Of(transform, "Scale", new FVector2D(1, 1));   // FWidgetTransform::Scale, WidgetTransform.h
            node.RenderShear = Vector2Of(transform, "Shear", new FVector2D(0, 0));
            node.RenderAngle = transform.GetOrDefault("Angle", 0f);
        }
        if (export.TryGetValue(out FVector2D pivot, "RenderTransformPivot"))
            node.RenderPivot = pivot;
    }

    /// <summary>
    /// Slot classes do not share a default alignment — UOverlaySlot starts Left/Top while a box slot
    /// starts Fill/Fill, and a Border or Button slot starts with a 4x2 padding. Since a property
    /// equal to its default is never serialized, these have to be applied before the overrides or an
    /// unaligned overlay child would stretch across the whole screen. Every value below is the one
    /// in that slot class's own constructor (UMG/Private/Components/*Slot.cpp).
    /// </summary>
    private static void ApplySlotDefaults(string slotClass, UmgWidgetNode node)
    {
        switch (slotClass)
        {
            case "OverlaySlot":
            case "UniformGridSlot":
                node.HAlign = EUmgHAlign.Left;
                node.VAlign = EUmgVAlign.Top;
                break;
            case "ScaleBoxSlot":
                node.HAlign = EUmgHAlign.Center;
                node.VAlign = EUmgVAlign.Center;
                break;
            case "ButtonSlot":
                node.HAlign = EUmgHAlign.Center;
                node.VAlign = EUmgVAlign.Center;
                node.Padding = new UmgMargin(4, 2, 4, 2);
                break;
            case "BorderSlot":
            case "BackgroundBlurSlot":
                node.HAlign = EUmgHAlign.Fill;
                node.VAlign = EUmgVAlign.Fill;
                node.Padding = new UmgMargin(4, 2, 4, 2);
                break;
            default:
                // HorizontalBoxSlot, VerticalBoxSlot, SizeBoxSlot, GridSlot, ScrollBoxSlot,
                // WrapBoxSlot, WidgetSwitcherSlot and the UPanelSlot base all start filling
                node.HAlign = EUmgHAlign.Fill;
                node.VAlign = EUmgVAlign.Fill;
                break;
        }
    }

    private static void ReadSlot(UObject slot, UmgWidgetNode node)
    {
        node.SlotClassName = slot.ExportType;
        ApplySlotDefaults(slot.ExportType, node);

        // SafeZoneSlot names its alignment members HAlign/VAlign rather than the usual pair
        if (slot.TryGetValue(out FName safeH, "HAlign")) node.HAlign = ParseAlign(safeH.Text, node.HAlign);
        if (slot.TryGetValue(out FName safeV, "VAlign")) node.VAlign = ParseVAlign(safeV.Text, node.VAlign);

        if (slot.TryGetValue(out FName hAlign, "HorizontalAlignment"))
            node.HAlign = ParseAlign(hAlign.Text, node.HAlign);
        if (slot.TryGetValue(out FName vAlign, "VerticalAlignment"))
            node.VAlign = ParseVAlign(vAlign.Text, node.VAlign);
        if (slot.TryGetValue(out FStructFallback padding, "Padding"))
        {
            node.Padding = MarginOf(padding);
            node.PaddingIsFromSlot = true;
        }
        if (slot.TryGetValue(out FStructFallback size, "Size"))
        {
            // FSlateChildSize { Value, SizeRule } — SlateWrapperTypes.h
            node.SizeValue = size.GetOrDefault("Value", 1f);
            if (size.TryGetValue(out FName rule, "SizeRule"))
                node.SizeRule = rule.Text.EndsWith("Fill", StringComparison.Ordinal) ? EUmgSizeRule.Fill : EUmgSizeRule.Automatic;
        }
        if (slot.TryGetValue(out int row, "Row")) node.Row = row;
        if (slot.TryGetValue(out int column, "Column")) node.Column = column;
        if (slot.TryGetValue(out int rowSpan, "RowSpan")) node.RowSpan = Math.Max(1, rowSpan);
        if (slot.TryGetValue(out int columnSpan, "ColumnSpan")) node.ColumnSpan = Math.Max(1, columnSpan);
        if (slot.TryGetValue(out FVector2D nudge, "Nudge")) node.Nudge = nudge;
        if (slot.TryGetValue(out int zOrder, "ZOrder")) node.ZOrder = zOrder;
        if (slot.TryGetValue(out bool forceNewLine, "bForceNewLine")) node.ForceNewLine = forceNewLine;
        if (slot.TryGetValue(out bool autoSize, "bAutoSize")) node.AutoSize = autoSize;

        if (slot.TryGetValue(out FStructFallback layout, "LayoutData"))
        {
            // FAnchorData { Offsets, Anchors, Alignment } — SlateWrapperTypes.h
            if (layout.TryGetValue(out FStructFallback offsets, "Offsets"))
                node.Offsets = MarginOf(offsets, node.Offsets);
            if (layout.TryGetValue(out FStructFallback anchors, "Anchors"))
            {
                node.AnchorMin = Vector2Of(anchors, "Minimum", node.AnchorMin);
                node.AnchorMax = Vector2Of(anchors, "Maximum", node.AnchorMax);
            }
            if (layout.TryGetValue(out FVector2D alignment, "Alignment"))
                node.Alignment = alignment;
        }
    }

    private void ReadContent(UObject export, UmgWidgetNode node)
    {
        // ---- text ----
        if (export.TryGetValue(out FText text, "Text"))
            node.Text = text.Text;
        if (node.Text == null && export.TryGetValue(out string rawText, "Text"))
            node.Text = rawText;
        if (export.TryGetValue(out FStructFallback font, "Font"))
            node.Font = ReadFont(font, node);
        if (export.TryGetValue(out FStructFallback colorAndOpacity, "ColorAndOpacity"))
            node.ContentColor = SlateColorOf(colorAndOpacity, node.ContentColor);
        if (export.TryGetValue(out FName justification, "Justification"))
            node.Justification = ParseEnum(justification.Text, node.Justification);
        if (export.TryGetValue(out bool autoWrap, "AutoWrapText")) node.AutoWrapText = autoWrap;
        if (export.TryGetValue(out float wrapAt, "WrapTextAt")) node.WrapTextAt = wrapAt;

        // ---- brushes ----
        // UImage.Brush / UBorder.Background / UProgressBar fill are all FSlateBrush
        if (export.TryGetValue(out FStructFallback brush, "Brush"))
            node.Brush = ReadBrush(brush, node);
        else if (export.TryGetValue(out FStructFallback background, "Background"))
            node.Brush = ReadBrush(background, node);
        if (export.TryGetValue(out FStructFallback brushColor, "BrushColor"))
            node.ContentColor = LinearColorOf(brushColor, node.ContentColor);

        // ---- size box ----
        if (export.GetOrDefault("bOverride_WidthOverride", false) && export.TryGetValue(out float width, "WidthOverride"))
            node.WidthOverride = width;
        if (export.GetOrDefault("bOverride_HeightOverride", false) && export.TryGetValue(out float height, "HeightOverride"))
            node.HeightOverride = height;
        if (export.GetOrDefault("bOverride_MinDesiredWidth", false) && export.TryGetValue(out float minWidth, "MinDesiredWidth"))
            node.MinDesiredWidth = minWidth;
        if (export.GetOrDefault("bOverride_MinDesiredHeight", false) && export.TryGetValue(out float minHeight, "MinDesiredHeight"))
            node.MinDesiredHeight = minHeight;
        if (export.GetOrDefault("bOverride_MaxDesiredWidth", false) && export.TryGetValue(out float maxWidth, "MaxDesiredWidth"))
            node.MaxDesiredWidth = maxWidth;
        if (export.GetOrDefault("bOverride_MaxDesiredHeight", false) && export.TryGetValue(out float maxHeight, "MaxDesiredHeight"))
            node.MaxDesiredHeight = maxHeight;

        // ---- scale box / switcher / spacer / progress bar ----
        if (export.TryGetValue(out FName stretch, "Stretch"))
            node.Stretch = ParseEnum(stretch.Text, node.Stretch);
        if (export.TryGetValue(out FName stretchDirection, "StretchDirection"))
            node.StretchDirection = ParseEnum(stretchDirection.Text, node.StretchDirection);
        if (export.TryGetValue(out float userScale, "UserSpecifiedScale")) node.UserSpecifiedScale = userScale;
        if (export.TryGetValue(out int activeIndex, "ActiveWidgetIndex")) node.ActiveWidgetIndex = activeIndex;
        if (export.TryGetValue(out float percent, "Percent")) node.Percent = percent;
        if (export.TryGetValue(out FVector2D spacerSize, "Size")) node.SpacerSize = spacerSize;
        if (export.TryGetValue(out FStructFallback fillColor, "FillColorAndOpacity"))
            node.ContentColor = LinearColorOf(fillColor, node.ContentColor);

        // ---- grid fill rules ----
        if (export.TryGetValue(out float[] columnFill, "ColumnFill")) node.ColumnFill = columnFill.Select(f => (double) f).ToList();
        if (export.TryGetValue(out float[] rowFill, "RowFill")) node.RowFill = rowFill.Select(f => (double) f).ToList();

        // ---- wrap box ----
        if (export.TryGetValue(out float wrapSize, "WrapSize")) node.WrapSize = wrapSize;
        if (export.TryGetValue(out bool explicitWrap, "bExplicitWrapSize")) node.ExplicitWrapSize = explicitWrap;

        // UBorder keeps a Padding of its own beside its slot's, but both write the SAME Slate value:
        // UBorder::SynchronizeProperties does MyBorder->SetPadding(Padding) and
        // UBorderSlot::SynchronizeProperties does SetPadding(Padding) (Border.cpp, BorderSlot.cpp).
        // So it is an alternative source for the child's padding, never an addition to it.
        if (node.Kind == EUmgPanelKind.Compound && export.TryGetValue(out FStructFallback contentPadding, "Padding"))
            node.ContentPadding = MarginOf(contentPadding);

        foreach (var property in export.Properties)
            node.Details.Add(new KeyValuePair<string, string>(property.Name.Text, DescribeTag(property)));
    }

    private static string DescribeTag(FPropertyTag tag)
    {
        var value = tag.Tag?.GenericValue;
        return value switch
        {
            null => "",
            FStructFallback structFallback => "{ " + string.Join(", ", structFallback.Properties.Select(p => p.Name.Text)) + " }",
            _ => value.ToString()
        };
    }

    // ---------------------------------------------------------------- brushes and fonts

    private UmgBrush ReadBrush(FStructFallback brush, UmgWidgetNode node)
    {
        var result = new UmgBrush();
        if (brush.TryGetValue(out FVector2D imageSize, "ImageSize"))
        {
            result.ImageWidth = imageSize.X;
            result.ImageHeight = imageSize.Y;
        }
        if (brush.TryGetValue(out FStructFallback tint, "TintColor"))
            result.Tint = SlateColorOf(tint, result.Tint);
        if (brush.TryGetValue(out FName drawAs, "DrawAs"))
            result.DrawAs = ParseEnum(drawAs.Text, result.DrawAs);
        if (brush.TryGetValue(out FStructFallback margin, "Margin"))
            result.Margin = MarginOf(margin);

        if (brush.TryGetValue(out UObject resource, "ResourceObject") && resource != null)
        {
            result.ResourcePath = resource.GetPathName();
            result.ResourceType = resource.ExportType;
            if (resource is UTexture texture)
            {
                var (png, note) = DecodeTexture(texture);
                result.TextureBytes = png;
                result.ResourceNote = note;
            }
            else
            {
                // a material brush is evaluated by the renderer at run time; there is no baked image
                // to show, so the tint is drawn and the material is named rather than faked
                result.IsUnrenderableResource = true;
                result.ResourceNote = $"{resource.ExportType} brushes are drawn by the material renderer — not previewed";
                node.Notes.Add($"brush resource '{resource.Name}' is a {resource.ExportType}, shown as its tint only");
            }
        }
        return result;
    }

    private (byte[] Png, string Note) DecodeTexture(UTexture texture)
    {
        var key = texture.GetPathName();
        if (_textureCache.TryGetValue(key, out var cached)) return cached;

        (byte[] Png, string Note) result;
        try
        {
            // the texture platform is a per-directory user setting; fall back to the default so the
            // model also works outside the app (tests, head-less probes) rather than throwing
            var platform = Settings.UserSettings.Default?.CurrentDir?.TexturePlatform ?? ETexturePlatform.DesktopMobile;
            var decoded = texture.Decode(platform);
            if (decoded == null)
            {
                result = (null, "the texture could not be decoded on this platform");
            }
            else
            {
                using var bitmap = decoded.ToSkBitmap();
                using var data = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                result = (data.ToArray(), null);
            }
        }
        catch (Exception e)
        {
            result = (null, $"the texture could not be decoded: {e.Message}");
        }

        _textureCache[key] = result;
        return result;
    }

    private UmgFont ReadFont(FStructFallback font, UmgWidgetNode node)
    {
        var result = new UmgFont();
        if (font.TryGetValue(out int size, "Size")) result.Size = size;
        if (font.TryGetValue(out FName typeface, "TypefaceFontName")) result.TypefaceName = typeface.Text;
        if (font.TryGetValue(out FStructFallback outline, "OutlineSettings"))
        {
            result.OutlineSize = outline.GetOrDefault("OutlineSize", 0);
            if (outline.TryGetValue(out FStructFallback outlineColor, "OutlineColor"))
                result.OutlineColor = LinearColorOf(outlineColor, result.OutlineColor);
        }

        if (!font.TryGetValue(out UObject fontObject, "FontObject") || fontObject == null)
        {
            // UTextBlock's constructor takes UWidget::GetDefaultFontName() at size 24 with the Bold
            // typeface (TextBlock.cpp, Widget.cpp), and that font ships inside the cook, so the
            // class default resolves to a real face rather than being left blank
            result.TypefaceName = "Bold";
            var (defaultBytes, defaultFace, defaultNote) = ResolveDefaultFont(result.TypefaceName);
            result.FontPath = DefaultFontPackage + " (class default)";
            result.FaceBytes = defaultBytes;
            result.FaceName = defaultFace;
            result.Note = defaultNote ?? "no font is serialized on this text, so the UTextBlock class default (Roboto Bold, 24) is used";
            return result;
        }

        result.FontPath = fontObject.GetPathName();
        var (bytes, faceName, note) = ResolveFontFace(fontObject, result.TypefaceName);
        result.FaceBytes = bytes;
        result.FaceName = faceName;
        result.Note = note;
        if (bytes == null && note != null) node.Notes.Add(note);
        return result;
    }

    /// <summary>UWidget::GetDefaultFontName(), Widget.cpp — the engine font UMG falls back to. It is
    /// cooked into the game, so the class default is resolved from the archives like any other.</summary>
    private const string DefaultFontPackage = "/Engine/EngineFonts/Roboto";

    private (byte[] Bytes, string FaceName, string Note) ResolveDefaultFont(string typefaceName)
    {
        try
        {
            if (_provider != null && _provider.TryLoadPackage(DefaultFontPackage, out var package))
            {
                var fontExport = package.GetExports().FirstOrDefault(e => e.ExportType == "Font");
                if (fontExport != null) return ResolveFontFace(fontExport, typefaceName);
            }
        }
        catch { /* reported through the note below */ }
        return (null, null, $"no font is serialized on this text and the class default {DefaultFontPackage} is not in the mounted archives");
    }

    /// <summary>Walks UFont.CompositeFont to the UFontFace for the requested typeface, then reads the
    /// .ufont the cook wrote beside it (UFontFace::GetCookedFilename, FontFace.cpp) — that file is
    /// the font program itself, so the preview draws text in the game's real font.</summary>
    private (byte[] Bytes, string FaceName, string Note) ResolveFontFace(UObject fontObject, string typefaceName)
    {
        if (!fontObject.TryGetValue(out FStructFallback composite, "CompositeFont"))
            return (null, null, $"'{fontObject.Name}' has no CompositeFont — its faces are not in this package");

        UObject faceAsset = null;
        string matchedName = null;
        if (composite.TryGetValue(out FStructFallback defaultTypeface, "DefaultTypeface") &&
            defaultTypeface.TryGetValue(out FStructFallback[] fonts, "Fonts"))
        {
            foreach (var entry in fonts)
            {
                if (!entry.TryGetValue(out FName entryName, "Name")) continue;
                // FFontData has a custom serializer (FFontData::Serialize, CompositeFont.cpp), so
                // CUE4Parse gives back a typed struct rather than a tagged-property fallback
                if (!entry.TryGetValue(out FFontData faceStruct, "Font")) continue;
                var localFace = faceStruct.LocalFontFaceAsset?.Load();
                if (localFace == null) continue;

                // first entry wins if the requested typeface is not present, matching Slate's
                // FCompositeFontCache fallback to the default typeface's first face
                faceAsset ??= localFace;
                matchedName ??= entryName.Text;
                if (!string.Equals(entryName.Text, typefaceName, StringComparison.OrdinalIgnoreCase)) continue;
                faceAsset = localFace;
                matchedName = entryName.Text;
                break;
            }
        }

        if (faceAsset == null)
            return (null, null, $"'{fontObject.Name}' lists no local font face for typeface '{typefaceName}'");

        // EFontLoadingPolicy::Inline keeps the font program inside the asset itself, and only the
        // other policies cook it out to a side-car .ufont (UFontFace::Serialize, FontFace.cpp), so
        // the inline payload is preferred whenever it is there
        if (faceAsset is UFontFace { FontFaceData.Data: { Length: > 4 } inlineData })
            return (inlineData, matchedName, null);

        var facePath = faceAsset.GetPathName();
        var packagePath = facePath.Contains('.') ? facePath[..facePath.IndexOf('.')] : facePath;
        var cookedPath = packagePath + ".ufont";

        if (_fontCache.TryGetValue(cookedPath, out var cachedBytes))
            return (cachedBytes, matchedName, cachedBytes == null ? $"the cooked font file '{cookedPath}' is not in the mounted archives" : null);

        byte[] bytes = null;
        try
        {
            if (_provider != null && _provider.TrySaveAsset(cookedPath, out var data) && data.Length > 0)
                bytes = data;
        }
        catch { /* falls through to the note below */ }

        _fontCache[cookedPath] = bytes;
        return bytes != null
            ? (bytes, matchedName, null)
            : (null, matchedName, $"the cooked font file '{cookedPath}' is not in the mounted archives — a substitute face is used");
    }

    // ---------------------------------------------------------------- classification

    /// <summary>Decides how a widget arranges its children. Engine classes are matched by their own
    /// class name; a Blueprint widget class (<c>*_C</c>) is a UUserWidget, which always wraps a
    /// single root, so it gets its own kind.</summary>
    private static EUmgPanelKind ClassifyPanel(UObject export, UmgWidgetNode node)
    {
        switch (export.ExportType)
        {
            case "CanvasPanel": return EUmgPanelKind.Canvas;
            case "Overlay": return EUmgPanelKind.Overlay;
            case "HorizontalBox": return EUmgPanelKind.HorizontalBox;
            case "VerticalBox":
            case "StackBox": return EUmgPanelKind.VerticalBox;
            case "GridPanel": return EUmgPanelKind.Grid;
            case "UniformGridPanel": return EUmgPanelKind.UniformGrid;
            case "WrapBox": return EUmgPanelKind.WrapBox;
            case "ScrollBox":
            case "CommonHierarchicalScrollBox": return EUmgPanelKind.ScrollBox;
            case "SizeBox": return EUmgPanelKind.SizeBox;
            case "ScaleBox": return EUmgPanelKind.ScaleBox;
            case "WidgetSwitcher":
            case "CommonAnimatedSwitcher":
            case "CommonActivatableWidgetSwitcher":
            case "CommonActivatableWidgetStack":
            case "CommonActivatableWidgetQueue": return EUmgPanelKind.Switcher;
            case "Border":
            case "CommonBorder":
            case "Button":
            case "CommonButtonBase":
            case "CheckBox":
            case "BackgroundBlur":
            case "RetainerBox":
            case "InvalidationBox":
            case "SafeZone":
            case "NamedSlot":
            case "MenuAnchor":
                return EUmgPanelKind.Compound;
        }

        // a Blueprint widget class ends in _C and is a UUserWidget: exactly one root widget
        if (export.ExportType.EndsWith("_C", StringComparison.Ordinal))
            return EUmgPanelKind.UserWidget;

        // anything that still serialized a Slots array is a panel of some kind; arrange it as an
        // overlay (the neutral stacking behaviour) and say so rather than dropping its children
        if (export.TryGetValue(out UObject[] slots, "Slots") && slots.Length > 0)
        {
            node.Notes.Add($"'{export.ExportType}' is not a known panel type — its {slots.Length} child(ren) are stacked like an Overlay");
            return EUmgPanelKind.Overlay;
        }

        return EUmgPanelKind.Leaf;
    }

    // ---------------------------------------------------------------- layout

    /// <summary>Runs the two Slate layout passes over the tree for a given screen size: desired sizes
    /// bottom-up, then arrangement top-down.</summary>
    public void Arrange(double screenWidth, double screenHeight)
    {
        ScreenWidth = screenWidth;
        ScreenHeight = screenHeight;
        if (Root == null) return;

        ComputeDesiredSize(Root);
        ArrangeNode(Root, 0, 0, screenWidth, screenHeight, 1.0);
    }

    private FVector2D ComputeDesiredSize(UmgWidgetNode node)
    {
        foreach (var child in node.Children) ComputeDesiredSize(child);

        var size = node.Kind switch
        {
            // SConstraintCanvas has no desired size of its own — it fills what it is given
            EUmgPanelKind.Canvas => new FVector2D(0, 0),
            EUmgPanelKind.Overlay => OverlayDesiredSize(node),
            EUmgPanelKind.HorizontalBox => BoxDesiredSize(node, horizontal: true),
            EUmgPanelKind.VerticalBox => BoxDesiredSize(node, horizontal: false),
            EUmgPanelKind.Grid => GridDesiredSize(node),
            EUmgPanelKind.UniformGrid => UniformGridDesiredSize(node),
            EUmgPanelKind.WrapBox => WrapBoxDesiredSize(node),
            EUmgPanelKind.ScrollBox => ScrollBoxDesiredSize(node),
            EUmgPanelKind.SizeBox => SizeBoxDesiredSize(node),
            EUmgPanelKind.ScaleBox => SingleChildDesiredSize(node),
            EUmgPanelKind.Switcher => SwitcherDesiredSize(node),
            EUmgPanelKind.Compound or EUmgPanelKind.UserWidget => SingleChildDesiredSize(node),
            _ => LeafDesiredSize(node)
        };

        node.DesiredSize = size;
        return size;
    }

    /// <summary>SCompoundWidget::ComputeDesiredSize — child desired size plus the slot padding.</summary>
    private static FVector2D SingleChildDesiredSize(UmgWidgetNode node)
    {
        double width = 0, height = 0;
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            var padding = SlotPaddingOf(child);
            width = Math.Max(width, child.DesiredSize.X + padding.Horizontal);
            height = Math.Max(height, child.DesiredSize.Y + padding.Vertical);
        }
        return new FVector2D((float) width, (float) height);
    }

    /// <summary>The padding Slate applies to a compound widget's child. The slot's own value wins
    /// when the cook serialized one; otherwise the parent's Padding property is the value that
    /// reached the same Slate slot.</summary>
    private static UmgMargin SlotPaddingOf(UmgWidgetNode child)
    {
        if (child.PaddingIsFromSlot) return child.Padding;
        var parentPadding = child.Parent?.ContentPadding;
        return parentPadding is { } padding && !padding.IsZero ? padding : child.Padding;
    }

    /// <summary>SOverlay::ComputeDesiredSize — the largest child, padding included.</summary>
    private static FVector2D OverlayDesiredSize(UmgWidgetNode node) => SingleChildDesiredSize(node);

    /// <summary>ComputeDesiredSizeForBox, SBoxPanel.cpp.</summary>
    private static FVector2D BoxDesiredSize(UmgWidgetNode node, bool horizontal)
    {
        double width = 0, height = 0;
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            double along = horizontal ? child.DesiredSize.X : child.DesiredSize.Y;
            if (child.MaxSize > 0) along = Math.Min(child.MaxSize, along);

            if (horizontal)
            {
                width += along + child.Padding.Horizontal;
                height = Math.Max(height, child.DesiredSize.Y + child.Padding.Vertical);
            }
            else
            {
                height += along + child.Padding.Vertical;
                width = Math.Max(width, child.DesiredSize.X + child.Padding.Horizontal);
            }
        }
        return new FVector2D((float) width, (float) height);
    }

    /// <summary>SGridPanel::ComputeDesiredCellSizes — each cell takes the largest desired size in its
    /// column/row, and the panel's desired size is the sum.</summary>
    private static (double[] Columns, double[] Rows) GridCells(UmgWidgetNode node)
    {
        var columnCount = 0;
        var rowCount = 0;
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            columnCount = Math.Max(columnCount, child.Column + child.ColumnSpan);
            rowCount = Math.Max(rowCount, child.Row + child.RowSpan);
        }

        var columns = new double[columnCount];
        var rows = new double[rowCount];
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            var width = child.DesiredSize.X + child.Padding.Horizontal;
            var height = child.DesiredSize.Y + child.Padding.Vertical;
            DistributeCellSize(width / child.ColumnSpan, columns, child.Column, child.Column + child.ColumnSpan);
            DistributeCellSize(height / child.RowSpan, rows, child.Row, child.Row + child.RowSpan);
        }
        return (columns, rows);
    }

    /// <summary>SGridPanel's DistributeSizeContributions: grow the spanned cells only if they do not
    /// already cover the contribution.</summary>
    private static void DistributeCellSize(double contribution, double[] cells, int start, int end)
    {
        if (cells.Length == 0) return;
        start = Math.Clamp(start, 0, cells.Length - 1);
        end = Math.Clamp(end, start + 1, cells.Length);

        var existing = 0.0;
        for (var i = start; i < end; i++) existing += cells[i];
        var total = contribution * (end - start);
        if (existing >= total) return;

        var extra = (total - existing) / (end - start);
        for (var i = start; i < end; i++) cells[i] += extra;
    }

    private static FVector2D GridDesiredSize(UmgWidgetNode node)
    {
        var (columns, rows) = GridCells(node);
        return new FVector2D((float) columns.Sum(), (float) rows.Sum());
    }

    /// <summary>SUniformGridPanel::ComputeDesiredSize — every cell is as big as the largest child.</summary>
    private static FVector2D UniformGridDesiredSize(UmgWidgetNode node)
    {
        double maxWidth = 0, maxHeight = 0;
        int columns = 0, rows = 0;
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            columns = Math.Max(columns, child.Column + 1);
            rows = Math.Max(rows, child.Row + 1);
            maxWidth = Math.Max(maxWidth, child.DesiredSize.X + child.Padding.Horizontal);
            maxHeight = Math.Max(maxHeight, child.DesiredSize.Y + child.Padding.Vertical);
        }
        return new FVector2D((float) (columns * maxWidth), (float) (rows * maxHeight));
    }

    private static FVector2D WrapBoxDesiredSize(UmgWidgetNode node)
    {
        // SWrapBox lays out into a preferred width; without an arrangement pass the desired size is
        // the wrapped extent at that width
        var lines = WrapLines(node, node.ExplicitWrapSize ? node.WrapSize : node.WrapSize);
        double width = 0, height = 0;
        foreach (var line in lines)
        {
            width = Math.Max(width, line.Sum(c => c.DesiredSize.X + c.Padding.Horizontal));
            height += line.Max(c => c.DesiredSize.Y + c.Padding.Vertical);
        }
        return new FVector2D((float) width, (float) height);
    }

    private static List<List<UmgWidgetNode>> WrapLines(UmgWidgetNode node, double available)
    {
        var lines = new List<List<UmgWidgetNode>>();
        var current = new List<UmgWidgetNode>();
        double used = 0;
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            var width = child.DesiredSize.X + child.Padding.Horizontal;
            if (current.Count > 0 && (child.ForceNewLine || used + width > available))
            {
                lines.Add(current);
                current = [];
                used = 0;
            }
            current.Add(child);
            used += width;
        }
        if (current.Count > 0) lines.Add(current);
        return lines;
    }

    /// <summary>A scroll box stacks its children on the scroll axis and never limits its own desired
    /// size on that axis, so the preview shows the full content extent.</summary>
    private static FVector2D ScrollBoxDesiredSize(UmgWidgetNode node) => BoxDesiredSize(node, horizontal: false);

    /// <summary>SBox::ComputeDesiredSize with ComputeDesiredWidth/Height, SBox.cpp.</summary>
    private static FVector2D SizeBoxDesiredSize(UmgWidgetNode node)
    {
        var child = SingleChildDesiredSize(node);
        var width = node.WidthOverride ?? Clamp(child.X, node.MinDesiredWidth, node.MaxDesiredWidth);
        var height = node.HeightOverride ?? Clamp(child.Y, node.MinDesiredHeight, node.MaxDesiredHeight);
        return new FVector2D((float) width, (float) height);
    }

    private static double Clamp(double value, double? min, double? max)
    {
        if (min.HasValue) value = Math.Max(value, min.Value);
        if (max.HasValue) value = Math.Min(value, max.Value);
        return value;
    }

    private static FVector2D SwitcherDesiredSize(UmgWidgetNode node)
    {
        var active = ActiveChild(node);
        return active == null
            ? new FVector2D(0, 0)
            : new FVector2D(active.DesiredSize.X + (float) active.Padding.Horizontal,
                            active.DesiredSize.Y + (float) active.Padding.Vertical);
    }

    private static UmgWidgetNode ActiveChild(UmgWidgetNode node)
    {
        if (node.Children.Count == 0) return null;
        var index = Math.Clamp(node.ActiveWidgetIndex, 0, node.Children.Count - 1);
        return node.Children[index];
    }

    /// <summary>Desired size of a widget with no children. Only the classes whose desired size is
    /// actually derivable from serialized data return one; anything else asks for nothing and is
    /// laid out by its slot, which is what Slate does for a widget with no content.</summary>
    private FVector2D LeafDesiredSize(UmgWidgetNode node)
    {
        // SImage::ComputeDesiredSize returns the brush's ImageSize
        if (node.Brush != null && node.ClassName is "Image" or "CommonLazyImage")
            return new FVector2D((float) node.Brush.ImageWidth, (float) node.Brush.ImageHeight);

        // USpacer builds an SSpacer sized by its Size property
        if (node.ClassName == "Spacer")
            return node.SpacerSize;

        if (node.Text != null)
            return MeasureText(node);

        // Everything else takes its size from a Slate widget style or from data resolved at run time
        // (a CommonUI action icon from the input data, an SSpinBox from its style's font, a slider
        // from its bar style). None of that is in the cooked widget, so the widget asks for nothing
        // and the reason is recorded instead of a made-up size being substituted.
        node.Notes.Add($"'{node.ClassName}' takes its size from a Slate widget style or run-time data, " +
                       "neither of which is stored in the cooked widget — it is arranged with no desired size");
        return new FVector2D(0, 0);
    }

    /// <summary>Text extent. Slate measures with FreeType against the real face; the preview measures
    /// with the same font program through the platform's shaper, so the result is very close but not
    /// guaranteed identical — <see cref="TextMeasurementIsApproximate"/> says so in the UI.</summary>
    private FVector2D MeasureText(UmgWidgetNode node)
    {
        var measure = TextMeasurer;
        if (measure == null || string.IsNullOrEmpty(node.Text)) return new FVector2D(0, 0);
        var wrapAt = node.AutoWrapText || node.WrapTextAt > 0 ? node.WrapTextAt : 0;
        var (width, height) = measure(node.Text, node.Font, wrapAt);
        return new FVector2D((float) width, (float) height);
    }

    /// <summary>Set by the view, which owns the text stack. Kept as a hook so the model stays free of
    /// a UI dependency and can be exercised head-less.</summary>
    public static Func<string, UmgFont, double, (double Width, double Height)> TextMeasurer;
    public const string TextMeasurementIsApproximate =
        "text extents are measured with the game's own font file through the platform text stack, not Slate's FreeType rasteriser, so line breaks can differ by a pixel";

    private void ArrangeNode(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        node.ArrangedX = x;
        node.ArrangedY = y;
        node.ArrangedWidth = width;
        node.ArrangedHeight = height;
        node.LayoutScale = scale;
        node.IsArranged = true;

        switch (node.Kind)
        {
            case EUmgPanelKind.Canvas: ArrangeCanvas(node, x, y, width, height, scale); break;
            case EUmgPanelKind.Overlay: ArrangeOverlay(node, x, y, width, height, scale); break;
            case EUmgPanelKind.HorizontalBox: ArrangeBox(node, x, y, width, height, scale, horizontal: true); break;
            case EUmgPanelKind.VerticalBox: ArrangeBox(node, x, y, width, height, scale, horizontal: false); break;
            case EUmgPanelKind.ScrollBox: ArrangeScrollBox(node, x, y, width, height, scale); break;
            case EUmgPanelKind.Grid: ArrangeGrid(node, x, y, width, height, scale); break;
            case EUmgPanelKind.UniformGrid: ArrangeUniformGrid(node, x, y, width, height, scale); break;
            case EUmgPanelKind.WrapBox: ArrangeWrapBox(node, x, y, width, height, scale); break;
            case EUmgPanelKind.ScaleBox: ArrangeScaleBox(node, x, y, width, height, scale); break;
            case EUmgPanelKind.Switcher: ArrangeSwitcher(node, x, y, width, height, scale); break;
            default: ArrangeSingleChild(node, x, y, width, height, scale); break;
        }
    }

    /// <summary>SConstraintCanvas::ArrangeLayeredChildren, SConstraintCanvas.cpp — the anchor/offset
    /// algorithm the UMG designer uses for canvas slots.</summary>
    private void ArrangeCanvas(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        foreach (var child in node.Children.OrderBy(c => c.ZOrder))
        {
            if (!child.IsVisibleForLayout) continue;

            var anchorLeft = child.AnchorMin.X * width;
            var anchorTop = child.AnchorMin.Y * height;
            var anchorRight = child.AnchorMax.X * width;
            var anchorBottom = child.AnchorMax.Y * height;

            var horizontalStretch = child.AnchorMin.X != child.AnchorMax.X;
            var verticalStretch = child.AnchorMin.Y != child.AnchorMax.Y;

            var size = child.AutoSize
                ? new FVector2D(child.DesiredSize.X, child.DesiredSize.Y)
                : new FVector2D((float) child.Offsets.Right, (float) child.Offsets.Bottom);

            var alignmentOffsetX = size.X * child.Alignment.X;
            var alignmentOffsetY = size.Y * child.Alignment.Y;

            double localX, localY, localWidth, localHeight;
            if (horizontalStretch)
            {
                localX = anchorLeft + child.Offsets.Left;
                localWidth = anchorRight - localX - child.Offsets.Right;
            }
            else
            {
                localX = anchorLeft + child.Offsets.Left - alignmentOffsetX;
                localWidth = size.X;
            }

            if (verticalStretch)
            {
                localY = anchorTop + child.Offsets.Top;
                localHeight = anchorBottom - localY - child.Offsets.Bottom;
            }
            else
            {
                localY = anchorTop + child.Offsets.Top - alignmentOffsetY;
                localHeight = size.Y;
            }

            ArrangeNode(child, x + localX * scale, y + localY * scale, localWidth * scale, localHeight * scale, scale);
        }
    }

    /// <summary>SOverlay::OnArrangeChildren — every child gets the full area, aligned in its slot.</summary>
    private void ArrangeOverlay(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            var horizontal = AlignChild(width / scale, child.DesiredSize.X, child.HAlign, child.Padding.Left, child.Padding.Right);
            var vertical = AlignChild(height / scale, child.DesiredSize.Y, child.VAlign, child.Padding.Top, child.Padding.Bottom);
            ArrangeNode(child, x + horizontal.Offset * scale, y + vertical.Offset * scale,
                horizontal.Size * scale, vertical.Size * scale, scale);
        }
    }

    /// <summary>ArrangeChildrenAlong, SBoxPanel.cpp — fixed children take their desired size, then
    /// the remainder is split between fill children by their size coefficients.</summary>
    private void ArrangeBox(UmgWidgetNode node, double x, double y, double width, double height, double scale, bool horizontal)
    {
        var localWidth = width / scale;
        var localHeight = height / scale;

        double stretchTotal = 0, fixedTotal = 0;
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            fixedTotal += horizontal ? child.Padding.Horizontal : child.Padding.Vertical;
            if (child.SizeRule == EUmgSizeRule.Fill)
            {
                stretchTotal += child.SizeValue;
            }
            else
            {
                var along = horizontal ? child.DesiredSize.X : child.DesiredSize.Y;
                fixedTotal += child.MaxSize > 0 ? Math.Min(child.MaxSize, along) : along;
            }
        }

        var nonFixed = Math.Max(0, (horizontal ? localWidth : localHeight) - fixedTotal);
        double positionSoFar = 0;

        foreach (var child in node.Children)
        {
            double childSize = 0;
            if (child.IsVisibleForLayout)
            {
                if (child.SizeRule == EUmgSizeRule.Fill)
                {
                    if (stretchTotal > 0) childSize = nonFixed * child.SizeValue / stretchTotal;
                }
                else
                {
                    childSize = horizontal ? child.DesiredSize.X : child.DesiredSize.Y;
                }
                if (child.MaxSize > 0) childSize = Math.Min(child.MaxSize, childSize);
            }

            var slotWidth = horizontal ? childSize + child.Padding.Horizontal : localWidth;
            var slotHeight = horizontal ? localHeight : childSize + child.Padding.Vertical;

            var horizontalResult = AlignChild(slotWidth, child.DesiredSize.X, child.HAlign, child.Padding.Left, child.Padding.Right);
            var verticalResult = AlignChild(slotHeight, child.DesiredSize.Y, child.VAlign, child.Padding.Top, child.Padding.Bottom);

            var localX = horizontal ? positionSoFar + horizontalResult.Offset : horizontalResult.Offset;
            var localY = horizontal ? verticalResult.Offset : positionSoFar + verticalResult.Offset;

            if (child.IsVisibleForLayout)
                ArrangeNode(child, x + localX * scale, y + localY * scale,
                    horizontalResult.Size * scale, verticalResult.Size * scale, scale);

            if (child.IsVisibleForLayout)
                positionSoFar += horizontal ? slotWidth : slotHeight;
        }
    }

    /// <summary>A scroll box arranges like a vertical box, but its children keep their desired size
    /// on the scroll axis instead of being squeezed into the visible area.</summary>
    private void ArrangeScrollBox(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        var localWidth = width / scale;
        double positionSoFar = 0;
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            var childHeight = child.DesiredSize.Y;
            var slotHeight = childHeight + child.Padding.Vertical;
            var horizontalResult = AlignChild(localWidth, child.DesiredSize.X, child.HAlign, child.Padding.Left, child.Padding.Right);
            ArrangeNode(child, x + horizontalResult.Offset * scale, y + (positionSoFar + child.Padding.Top) * scale,
                horizontalResult.Size * scale, childHeight * scale, scale);
            positionSoFar += slotHeight;
        }
    }

    /// <summary>SGridPanel::OnArrangeChildren — cells at their desired size, extra space handed to
    /// the columns/rows that carry a fill coefficient.</summary>
    private void ArrangeGrid(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        var (columns, rows) = GridCells(node);
        StretchCells(columns, width / scale, node.ColumnFill);
        StretchCells(rows, height / scale, node.RowFill);

        var columnStarts = PartialSums(columns);
        var rowStarts = PartialSums(rows);

        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            var column = Math.Clamp(child.Column, 0, Math.Max(0, columns.Length - 1));
            var row = Math.Clamp(child.Row, 0, Math.Max(0, rows.Length - 1));
            var cellX = columnStarts[column];
            var cellY = rowStarts[row];
            var cellWidth = columnStarts[Math.Min(column + child.ColumnSpan, columns.Length)] - cellX;
            var cellHeight = rowStarts[Math.Min(row + child.RowSpan, rows.Length)] - cellY;

            var horizontalResult = AlignChild(cellWidth, child.DesiredSize.X, child.HAlign, child.Padding.Left, child.Padding.Right);
            var verticalResult = AlignChild(cellHeight, child.DesiredSize.Y, child.VAlign, child.Padding.Top, child.Padding.Bottom);

            ArrangeNode(child,
                x + (cellX + horizontalResult.Offset + child.Nudge.X) * scale,
                y + (cellY + verticalResult.Offset + child.Nudge.Y) * scale,
                horizontalResult.Size * scale, verticalResult.Size * scale, scale);
        }
    }

    /// <summary>CalculateStretchedCellSizes, SGridPanel.cpp — cells without a coefficient keep their
    /// desired size and the leftover is split between the ones that have one.</summary>
    private static void StretchCells(double[] cells, double available, List<double> coefficients)
    {
        if (cells.Length == 0) return;
        double coefficientTotal = 0, sizeOfCoefficientCells = 0, fixedSize = 0;
        for (var i = 0; i < cells.Length; i++)
        {
            var coefficient = i < coefficients.Count ? coefficients[i] : 0;
            if (coefficient > 0)
            {
                coefficientTotal += coefficient;
                sizeOfCoefficientCells += cells[i];
            }
            else fixedSize += cells[i];
        }
        if (coefficientTotal <= 0) return;

        var spaceForCoefficientCells = Math.Max(available - fixedSize, sizeOfCoefficientCells);
        for (var i = 0; i < cells.Length; i++)
        {
            var coefficient = i < coefficients.Count ? coefficients[i] : 0;
            if (coefficient > 0) cells[i] = spaceForCoefficientCells * coefficient / coefficientTotal;
        }
    }

    private static double[] PartialSums(double[] cells)
    {
        var sums = new double[cells.Length + 1];
        for (var i = 0; i < cells.Length; i++) sums[i + 1] = sums[i] + cells[i];
        return sums;
    }

    /// <summary>SUniformGridPanel::OnArrangeChildren — the area split into equal cells.</summary>
    private void ArrangeUniformGrid(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        int columns = 0, rows = 0;
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            columns = Math.Max(columns, child.Column + 1);
            rows = Math.Max(rows, child.Row + 1);
        }
        if (columns == 0 || rows == 0) return;

        var cellWidth = width / scale / columns;
        var cellHeight = height / scale / rows;
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            var horizontalResult = AlignChild(cellWidth, child.DesiredSize.X, child.HAlign, child.Padding.Left, child.Padding.Right);
            var verticalResult = AlignChild(cellHeight, child.DesiredSize.Y, child.VAlign, child.Padding.Top, child.Padding.Bottom);
            ArrangeNode(child,
                x + (cellWidth * child.Column + horizontalResult.Offset) * scale,
                y + (cellHeight * child.Row + verticalResult.Offset) * scale,
                horizontalResult.Size * scale, verticalResult.Size * scale, scale);
        }
    }

    private void ArrangeWrapBox(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        var available = node.ExplicitWrapSize ? node.WrapSize : width / scale;
        double lineY = 0;
        foreach (var line in WrapLines(node, available))
        {
            double lineX = 0;
            var lineHeight = line.Max(c => c.DesiredSize.Y + c.Padding.Vertical);
            foreach (var child in line)
            {
                var slotWidth = child.DesiredSize.X + child.Padding.Horizontal;
                var horizontalResult = AlignChild(slotWidth, child.DesiredSize.X, child.HAlign, child.Padding.Left, child.Padding.Right);
                var verticalResult = AlignChild(lineHeight, child.DesiredSize.Y, child.VAlign, child.Padding.Top, child.Padding.Bottom);
                ArrangeNode(child, x + (lineX + horizontalResult.Offset) * scale, y + (lineY + verticalResult.Offset) * scale,
                    horizontalResult.Size * scale, verticalResult.Size * scale, scale);
                lineX += slotWidth;
            }
            lineY += lineHeight;
        }
    }

    /// <summary>SScaleBox::OnArrangeChildren with SScaleBox::ComputeContentScale — the child is laid
    /// out at its own size and the whole sub-tree is scaled.</summary>
    private void ArrangeScaleBox(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        var child = node.Children.FirstOrDefault(c => c.IsVisibleForLayout);
        if (child == null) return;

        var areaWidth = width / scale;
        var areaHeight = height / scale;
        var childSize = child.DesiredSize;

        var finalScale = 1.0;
        if (childSize.X != 0 && childSize.Y != 0)
        {
            finalScale = node.Stretch switch
            {
                EUmgStretch.ScaleToFit => Math.Min(areaWidth / childSize.X, areaHeight / childSize.Y),
                EUmgStretch.ScaleToFitX => areaWidth / childSize.X,
                EUmgStretch.ScaleToFitY => areaHeight / childSize.Y,
                EUmgStretch.ScaleToFill => Math.Max(areaWidth / childSize.X, areaHeight / childSize.Y),
                EUmgStretch.UserSpecified or EUmgStretch.UserSpecifiedWithClipping => node.UserSpecifiedScale,
                _ => 1.0
            };
            finalScale = node.StretchDirection switch
            {
                EUmgStretchDirection.DownOnly => Math.Min(finalScale, 1.0),
                EUmgStretchDirection.UpOnly => Math.Max(finalScale, 1.0),
                _ => finalScale
            };
        }

        if (node.Stretch == EUmgStretch.Fill)
        {
            ArrangeNode(child, x, y, width, height, scale);
            return;
        }
        if (Math.Abs(finalScale) < 1e-6) return;

        // AlignChild is run against the scaled area and the offset divided back out, exactly as
        // SScaleBox does, because the child's own geometry is expressed pre-scale
        var horizontalResult = AlignChild(areaWidth, childSize.X * finalScale, child.HAlign, child.Padding.Left, child.Padding.Right, clampToParent: false);
        var verticalResult = AlignChild(areaHeight, childSize.Y * finalScale, child.VAlign, child.Padding.Top, child.Padding.Bottom, clampToParent: false);

        var childWidth = child.HAlign == EUmgHAlign.Fill ? areaWidth / finalScale : childSize.X;
        var childHeight = child.VAlign == EUmgVAlign.Fill ? areaHeight / finalScale : childSize.Y;

        ArrangeNode(child,
            x + horizontalResult.Offset * scale,
            y + verticalResult.Offset * scale,
            childWidth * finalScale * scale,
            childHeight * finalScale * scale,
            scale * finalScale);
    }

    private void ArrangeSwitcher(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        var active = ActiveChild(node);
        foreach (var child in node.Children)
        {
            if (child != active) continue;
            var horizontalResult = AlignChild(width / scale, child.DesiredSize.X, child.HAlign, child.Padding.Left, child.Padding.Right);
            var verticalResult = AlignChild(height / scale, child.DesiredSize.Y, child.VAlign, child.Padding.Top, child.Padding.Bottom);
            ArrangeNode(child, x + horizontalResult.Offset * scale, y + verticalResult.Offset * scale,
                horizontalResult.Size * scale, verticalResult.Size * scale, scale);
        }
    }

    /// <summary>ArrangeSingleChild, LayoutUtils.h — used by every compound widget (Border, Button,
    /// SizeBox, SafeZone, RetainerBox, user widgets).</summary>
    private void ArrangeSingleChild(UmgWidgetNode node, double x, double y, double width, double height, double scale)
    {
        foreach (var child in node.Children)
        {
            if (!child.IsVisibleForLayout) continue;
            var padding = SlotPaddingOf(child);
            var horizontalResult = AlignChild(width / scale, child.DesiredSize.X, child.HAlign, padding.Left, padding.Right);
            var verticalResult = AlignChild(height / scale, child.DesiredSize.Y, child.VAlign, padding.Top, padding.Bottom);
            ArrangeNode(child, x + horizontalResult.Offset * scale, y + verticalResult.Offset * scale,
                horizontalResult.Size * scale, verticalResult.Size * scale, scale);
        }
    }

    /// <summary>AlignChild&lt;Orientation&gt;, LayoutUtils.h — one axis of slot alignment.</summary>
    private static (double Offset, double Size) AlignChild(double allotted, double childDesired, EUmgHAlign align,
        double marginPre, double marginPost, bool clampToParent = true)
        => AlignChild(allotted, childDesired, (int) align, marginPre, marginPost, clampToParent);

    private static (double Offset, double Size) AlignChild(double allotted, double childDesired, EUmgVAlign align,
        double marginPre, double marginPost, bool clampToParent = true)
        => AlignChild(allotted, childDesired, (int) align, marginPre, marginPost, clampToParent);

    private static (double Offset, double Size) AlignChild(double allotted, double childDesired, int align,
        double marginPre, double marginPost, bool clampToParent)
    {
        var totalMargin = marginPre + marginPost;
        // 0 == Fill for both axes (HAlign_Fill / VAlign_Fill are the zero enumerators)
        if (align == 0) return (marginPre, Math.Max(allotted - totalMargin, 0));

        var childSize = Math.Max(clampToParent ? Math.Min(childDesired, allotted - totalMargin) : childDesired, 0);
        return align switch
        {
            1 => (marginPre, childSize),                                             // Left / Top
            2 => ((allotted - childSize) / 2 + marginPre - marginPost, childSize),   // Center
            3 => (allotted - childSize - marginPost, childSize),                     // Right / Bottom
            _ => (marginPre, Math.Max(allotted - totalMargin, 0))
        };
    }

    // ---------------------------------------------------------------- small readers

    private static UmgMargin MarginOf(FStructFallback margin, UmgMargin fallback = default)
        => new(margin.GetOrDefault("Left", (float) fallback.Left),
               margin.GetOrDefault("Top", (float) fallback.Top),
               margin.GetOrDefault("Right", (float) fallback.Right),
               margin.GetOrDefault("Bottom", (float) fallback.Bottom));

    private static FVector2D Vector2Of(FStructFallback holder, string name, FVector2D fallback)
        => holder.TryGetValue(out FVector2D value, name) ? value : fallback;

    /// <summary>FSlateColor: SpecifiedColor is only meaningful when the rule is UseColor_Specified,
    /// which is the struct's default (FSlateColor::FSlateColor, SlateColor.h).</summary>
    private static FLinearColor SlateColorOf(FStructFallback color, FLinearColor fallback)
    {
        if (color.TryGetValue(out FName rule, "ColorUseRule") && !rule.Text.EndsWith("UseColor_Specified", StringComparison.Ordinal))
            return fallback;
        return color.TryGetValue(out FLinearColor specified, "SpecifiedColor") ? specified : fallback;
    }

    private static FLinearColor LinearColorOf(FStructFallback holder, FLinearColor fallback)
    {
        if (holder.TryGetValue(out FLinearColor specified, "SpecifiedColor")) return specified;
        var r = holder.GetOrDefault("R", fallback.R);
        var g = holder.GetOrDefault("G", fallback.G);
        var b = holder.GetOrDefault("B", fallback.B);
        var a = holder.GetOrDefault("A", fallback.A);
        return new FLinearColor(r, g, b, a);
    }

    /// <summary>Reads a UE enum literal such as <c>ESlateVisibility::SelfHitTestInvisible</c> into the
    /// matching managed enumerator; an unknown literal keeps the class default.</summary>
    private static T ParseEnum<T>(string text, T fallback) where T : struct, Enum
    {
        if (string.IsNullOrEmpty(text)) return fallback;
        var name = text.Contains("::") ? text[(text.LastIndexOf(':') + 1)..] : text;
        return Enum.TryParse<T>(name, true, out var value) ? value : fallback;
    }

    private static EUmgHAlign ParseAlign(string text, EUmgHAlign fallback) => text switch
    {
        not null when text.EndsWith("HAlign_Fill", StringComparison.Ordinal) => EUmgHAlign.Fill,
        not null when text.EndsWith("HAlign_Left", StringComparison.Ordinal) => EUmgHAlign.Left,
        not null when text.EndsWith("HAlign_Center", StringComparison.Ordinal) => EUmgHAlign.Center,
        not null when text.EndsWith("HAlign_Right", StringComparison.Ordinal) => EUmgHAlign.Right,
        _ => fallback
    };

    private static EUmgVAlign ParseVAlign(string text, EUmgVAlign fallback) => text switch
    {
        not null when text.EndsWith("VAlign_Fill", StringComparison.Ordinal) => EUmgVAlign.Fill,
        not null when text.EndsWith("VAlign_Top", StringComparison.Ordinal) => EUmgVAlign.Top,
        not null when text.EndsWith("VAlign_Center", StringComparison.Ordinal) => EUmgVAlign.Center,
        not null when text.EndsWith("VAlign_Bottom", StringComparison.Ordinal) => EUmgVAlign.Bottom,
        _ => fallback
    };
}

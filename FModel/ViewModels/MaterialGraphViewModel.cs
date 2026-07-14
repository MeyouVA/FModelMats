using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Assets.Exports.Material.Editor;
using CUE4Parse.UE4.Assets.Exports.Texture;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.FileProvider.Vfs;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.Engine;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Shaders;
using CUE4Parse.UE4.Versions;

namespace FModel.ViewModels;

public class MaterialGraphNode
{
    public string Name { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string ExportType { get; set; } = string.Empty;
    public double NodePosX { get; set; }
    public double NodePosY { get; set; }
    public bool IsOutputNode { get; set; }
    public bool IsParameter { get; set; }
    public bool IsTexture { get; set; }
    /// <summary>
    /// True when this node is part of the user-authored material graph — i.e. it feeds one of the
    /// material's attribute outputs (Base Color, Roughness, …). False marks engine scaffolding
    /// recovered from the shader map (other shader stages, uniforms that reach no material output).
    /// Defaults true so authored graphs, whose every node is user content, need no classification.
    /// </summary>
    public bool IsUserMaterialNode { get; set; } = true;
    public List<MaterialGraphPin> InputPins { get; } = [];
    public List<MaterialGraphPin> OutputPins { get; } = [];
    public List<KeyValuePair<string, string>> DisplayProperties { get; } = [];

    public override string ToString() => $"{ExportType} ({Name})";
}

public class MaterialGraphPin
{
    public string Name { get; set; } = string.Empty;
    public string PinType { get; set; } = string.Empty;
    /// <summary>
    /// True for the extra output-node pins that fan a whole compiled shader stage (vertex,
    /// geometry, …) into the material output. These are engine scaffolding, not one of the
    /// artist's material attribute inputs, so the user-graph classification skips them.
    /// </summary>
    public bool IsEngineStagePin { get; set; }
}

public class MaterialGraphConnection
{
    public MaterialGraphNode SourceNode { get; set; } = null!;
    public string SourcePinName { get; set; } = string.Empty;
    public MaterialGraphNode TargetNode { get; set; } = null!;
    public string TargetPinName { get; set; } = string.Empty;
    public string PinType { get; set; } = string.Empty;
    /// <summary>
    /// True for connections deduced from names/texture metadata rather than read from
    /// serialized data; the viewer draws these dashed to tell them apart.
    /// </summary>
    public bool IsInferred { get; set; }
}

/// <summary>A titled group of key/value rows for the viewer's "Material Details" panel.</summary>
public class MaterialInfoSection
{
    public string Title { get; set; } = string.Empty;
    public List<KeyValuePair<string, string>> Rows { get; } = [];

    public void Add(string key, string value) => Rows.Add(new KeyValuePair<string, string>(key, value));
}

/// <summary>
/// One compiled shader of the material's shader map. The full disassembly is produced
/// lazily on first access so listing dozens of shaders stays cheap.
/// </summary>
public class MaterialShaderEntry
{
    public string TypeName { get; set; } = string.Empty;
    public string Frequency { get; set; } = string.Empty;
    public string VertexFactory { get; set; } = string.Empty;
    public string OutputHash { get; set; } = string.Empty;
    public string CodeInfo { get; set; } = string.Empty;

    private readonly Lazy<string> _disassembly;
    public string Disassembly => _disassembly.Value;

    public MaterialShaderEntry(Func<string> disassembly) =>
        _disassembly = new Lazy<string>(disassembly, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);
}

public class MaterialGraphViewModel
{
    private const int MaxPropertyValueDisplayLength = 120;
    private const double AutoLayoutColumnSpacing = 380;
    private const double AutoLayoutRowSpacing = 48;
    private const double NodeApproxHeaderHeight = 40;
    private const double NodeApproxPinRowHeight = 22;

    public string PackageName { get; set; } = string.Empty;
    public string MaterialName { get; set; } = string.Empty;
    /// <summary>
    /// Instance chain description, e.g. "MI_Child → MI_Base → M_Master", empty for plain materials.
    /// </summary>
    public string ParentChain { get; set; } = string.Empty;
    /// <summary>
    /// True when the original expression graph was stripped during cooking and
    /// the graph shown is an approximate reconstruction from cached parameters.
    /// </summary>
    public bool IsReconstructed { get; private set; }
    /// <summary>
    /// Banner text describing how the reconstruction was produced; empty keeps
    /// the default cached-parameter wording in the viewer.
    /// </summary>
    public string ReconstructionNote { get; private set; } = string.Empty;
    public List<MaterialGraphNode> Nodes { get; } = [];
    public List<MaterialGraphConnection> Connections { get; } = [];
    /// <summary>Every deserialized field of the material, its instance chain and its shader map.</summary>
    public List<MaterialInfoSection> MaterialSections { get; } = [];
    /// <summary>Every compiled shader found in the material's shader map.</summary>
    public List<MaterialShaderEntry> Shaders { get; } = [];
    /// <summary>
    /// When true, opaque "Pixel Shader Math" combiners are replaced by the per-instruction
    /// expression DAG recovered from the compiled pixel shader — one graph node per decoded
    /// DXBC instruction. Off by default; toggle and call <see cref="Rebuild"/>.
    /// </summary>
    public bool ExpandPixelShaderMath { get; set; }
    /// <summary>True when the extraction recovered per-pin expression DAGs to expand into.</summary>
    public bool CanExpandPixelShaderMath { get; private set; }
    /// <summary>
    /// When true, the graph is pruned to only the user-authored material nodes wired straight into
    /// the material output — the way the graph looked in the editor — hiding the engine shader
    /// scaffolding (other shader stages, uniforms that reach no material output). Off by default;
    /// toggle and call <see cref="Rebuild"/>.
    /// </summary>
    public bool UserGraphOnly { get; set; }
    /// <summary>
    /// True when the graph contains a mix of user-authored and engine-generated nodes, so the
    /// user-graph highlight/isolation options have something to act on.
    /// </summary>
    public bool CanSeparateUserGraph { get; private set; }

    private UMaterialInterface _extractedMaterial;
    /// <summary>How many material values reachable only through other shader stages were wired this build.</summary>
    private int _otherStageWiredValues;
    /// <summary>How many compiled shader-stage nodes were added to the graph this build.</summary>
    private int _otherStageNodeCount;
    /// <summary>How many shader-stage nodes were expanded into real instruction nodes this build.</summary>
    private int _otherStageExpandedCount;

    /// <summary>Rebuilds the whole graph from the material it was extracted from, honoring option changes.</summary>
    public void Rebuild()
    {
        if (_extractedMaterial == null) return;
        Nodes.Clear();
        Connections.Clear();
        MaterialSections.Clear();
        Shaders.Clear();
        _nodesByExportName.Clear();
        _pendingLinks.Clear();
        IsReconstructed = false;
        ReconstructionNote = string.Empty;
        ParentChain = string.Empty;
        CanExpandPixelShaderMath = false;
        CanSeparateUserGraph = false;
        Extract(_extractedMaterial);
    }

    private readonly Dictionary<string, MaterialGraphNode> _nodesByExportName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(MaterialGraphNode target, string pinName, string pinType, string sourceName, int outputIndex)> _pendingLinks = [];

    /// <summary>
    /// Builds a material graph from any material interface export. When the package (or the
    /// instance's parent chain) still contains UMaterialExpression exports the true node graph
    /// is rebuilt, otherwise an approximate graph is reconstructed from cached parameters.
    /// </summary>
    public static MaterialGraphViewModel ExtractFromMaterial(UMaterialInterface material)
    {
        var viewModel = new MaterialGraphViewModel
        {
            PackageName = material.Owner?.Name ?? material.Name,
            MaterialName = material.Name,
            _extractedMaterial = material
        };
        viewModel.Extract(material);
        return viewModel;
    }

    /// <summary>
    /// Builds a graph for a material function asset; the function's FunctionOutput
    /// expressions act as the graph roots instead of a material output node.
    /// </summary>
    public static MaterialGraphViewModel ExtractFromFunction(UMaterialFunction function)
    {
        var viewModel = new MaterialGraphViewModel
        {
            PackageName = function.Owner?.Name ?? function.Name,
            MaterialName = function.Name
        };

        var expressions = new List<UObject>();
        if (function.Owner is { } package)
            expressions.AddRange(package.GetExports().Where(e => e is UMaterialExpression));

        foreach (var expression in expressions)
            viewModel.CreateExpressionNode(expression);
        viewModel.ResolvePendingLinks();

        if (viewModel.Nodes.Count > 0 && viewModel.Nodes.All(n => n.NodePosX == 0 && n.NodePosY == 0))
        {
            var targets = viewModel.Connections.Select(c => c.TargetNode).ToHashSet();
            var sources = viewModel.Connections.Select(c => c.SourceNode).ToHashSet();
            var roots = viewModel.Nodes.Where(n => targets.Contains(n) && !sources.Contains(n)).ToList();
            if (roots.Count == 0) roots = viewModel.Nodes.Take(1).ToList();
            viewModel.AutoLayout(roots);
        }

        return viewModel;
    }

    private void Extract(UMaterialInterface material)
    {
        // walk the instance chain down to the root material so expression graphs
        // authored on the master material can be shown for instances too
        var chain = new List<UMaterialInterface> { material };
        var guard = 0;
        while (chain[^1] is UMaterialInstance instance && instance.Parent is UMaterialInterface parent && ++guard < 16)
            chain.Add(parent);

        if (chain.Count > 1)
            ParentChain = string.Join(" → ", chain.Select(m => m.Name));

        // the details/shaders panels are independent of which graph tier renders below
        try { BuildMaterialDetails(chain); } catch { /* additive info only */ }
        try { BuildShaderInventory(chain); } catch { /* additive info only */ }

        var root = chain[^1];
        var expressions = CollectExpressions(root);
        if (expressions.Count > 0)
        {
            BuildExpressionGraph(root, expressions);

            // Some cookers (e.g. Fortnite-era 4.23) keep the expression exports but strip every
            // FExpressionInput property, so the authored topology is gone: a graph whose node
            // count dwarfs its connection count is only stubs. (A healthy authored graph has
            // roughly one connection per interior node — every expression feeds something — while
            // stripped ones keep at most the odd link that survives inside function-call stubs,
            // e.g. CharacterShader_Do_Not_Use_LegacyOnly cooks to 38 nodes with 2 connections.)
            // The compiled shader map still carries the real value trees, so prefer it here.
            if (Nodes.Count > 2 && Connections.Count * 4 < Nodes.Count)
            {
                var stubNodes = Nodes.ToList();
                var stubConnections = Connections.ToList();
                Nodes.Clear();
                Connections.Clear();
                _nodesByExportName.Clear();
                _pendingLinks.Clear();
                IsReconstructed = true;
                if (!TryBuildShaderMapGraph(chain))
                {
                    // no shader map either: restore the stub view, it still shows parameters
                    IsReconstructed = false;
                    Nodes.AddRange(stubNodes);
                    Connections.AddRange(stubConnections);
                    ApplyInstanceOverrides(chain);
                    TryAttachLegacyShaderCode(chain);
                }
                return;
            }

            ApplyInstanceOverrides(chain);
            // pre-4.25 games keep expression exports AND a legacy shader map: the authored
            // graph wins, but the compiled pixel shader still contributes its per-pin code
            TryAttachLegacyShaderCode(chain);
        }
        else
        {
            IsReconstructed = true;
            // richest tier first: decode the compiled uniform expression bytecode kept in
            // the inlined shader map, fall back to cached parameters when there is none
            if (!TryBuildShaderMapGraph(chain))
                BuildReconstructedGraph(chain);
        }
    }

    #region True expression graph

    private static List<UObject> CollectExpressions(UMaterialInterface material)
    {
        var result = new List<UObject>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (material is UMaterial m)
        {
            foreach (var index in m.Expressions)
            {
                if (index.TryLoad(out UMaterialExpression expression) && seen.Add(expression.Name))
                    result.Add(expression);
            }
        }

        // also scan the package exports: some assets keep expression exports even
        // though the Expressions array itself was stripped or failed to resolve
        if (material.Owner is { } package)
        {
            foreach (var export in package.GetExports())
            {
                if (export is UMaterialExpression && seen.Add(export.Name))
                    result.Add(export);
            }
        }

        return result;
    }

    private void BuildExpressionGraph(UMaterialInterface material, List<UObject> expressions)
    {
        foreach (var expression in expressions)
            CreateExpressionNode(expression);

        // material output node: pins come from the material attribute inputs
        // (BaseColor, Metallic, ...) found either on the material export itself (UE4)
        // or on the UMaterialEditorOnlyData export (UE5 uncooked)
        var outputNode = new MaterialGraphNode
        {
            Name = material.Name,
            Title = material.Name,
            Subtitle = "Material Output",
            ExportType = material.ExportType,
            IsOutputNode = true
        };
        Nodes.Add(outputNode);
        _nodesByExportName[material.Name] = outputNode;

        AppendDisplayProperties(outputNode, material.Properties);
        ScanExpressionInputs(outputNode, material.Properties, deepScan: false);

        if (material.Owner is { } package)
        {
            foreach (var export in package.GetExports())
            {
                if (export is not UMaterialInterfaceEditorOnlyData) continue;
                AppendDisplayProperties(outputNode, export.Properties);
                ScanExpressionInputs(outputNode, export.Properties, deepScan: false);
            }
        }

        if (outputNode.InputPins.Count == 0)
            AddStandardOutputPins(outputNode, material);

        outputNode.NodePosX = material.GetOrDefault("EditorX", 0);
        outputNode.NodePosY = material.GetOrDefault("EditorY", 0);

        ResolvePendingLinks();

        // original editor coordinates are kept when the asset still has them,
        // otherwise fall back to a layered auto layout
        if (Nodes.All(n => n.NodePosX == 0 && n.NodePosY == 0))
            AutoLayout([outputNode]);
        else if (outputNode is { NodePosX: 0, NodePosY: 0 } && Nodes.Count > 1)
        {
            outputNode.NodePosX = Nodes.Where(n => !n.IsOutputNode).Max(n => n.NodePosX) + AutoLayoutColumnSpacing;
            outputNode.NodePosY = Nodes.Where(n => !n.IsOutputNode).Average(n => n.NodePosY);
        }
    }

    private void CreateExpressionNode(UObject expression)
    {
        var node = new MaterialGraphNode
        {
            Name = expression.Name,
            ExportType = expression.ExportType,
            Title = CleanExpressionTitle(expression.ExportType),
            NodePosX = expression.GetOrDefault("MaterialExpressionEditorX", 0),
            NodePosY = expression.GetOrDefault("MaterialExpressionEditorY", 0)
        };

        var parameterName = expression.GetOrDefault<FName>("ParameterName");
        if (!parameterName.IsNone && !string.IsNullOrEmpty(parameterName.Text))
        {
            node.IsParameter = true;
            node.Subtitle = parameterName.Text;
        }

        switch (expression)
        {
            case UMaterialExpressionTextureBase { Texture: not null } textureBase:
                node.IsTexture = true;
                if (string.IsNullOrEmpty(node.Subtitle))
                    node.Subtitle = textureBase.Texture.Name;
                break;
            default:
                if (string.IsNullOrEmpty(node.Subtitle))
                    node.Subtitle = GetConstantSubtitle(expression);
                break;
        }

        AppendDisplayProperties(node, expression.Properties);
        BuildOutputPins(node, expression);
        ScanExpressionInputs(node, expression.Properties, deepScan: true);

        Nodes.Add(node);
        _nodesByExportName[expression.Name] = node;
    }

    private static string GetConstantSubtitle(UObject expression)
    {
        return expression.ExportType switch
        {
            "MaterialExpressionConstant" => expression.GetOrDefault("R", 0f).ToString("0.###"),
            "MaterialExpressionConstant2Vector" => $"{expression.GetOrDefault("R", 0f):0.###}, {expression.GetOrDefault("G", 0f):0.###}",
            "MaterialExpressionConstant3Vector" or "MaterialExpressionConstant4Vector"
                when expression.TryGetValue(out FLinearColor color, "Constant") => FormatColor(color),
            "MaterialExpressionMaterialFunctionCall"
                when expression.TryGetValue(out FPackageIndex fn, "MaterialFunction") => fn.Name,
            "MaterialExpressionComment"
                when expression.TryGetValue(out string text, "Text") => text,
            _ => string.Empty
        };
    }

    private static void BuildOutputPins(MaterialGraphNode node, UObject expression)
    {
        if (expression.TryGetValue(out FStructFallback[] outputs, "Outputs") && outputs.Length > 0)
        {
            for (var i = 0; i < outputs.Length; i++)
            {
                var name = outputs[i].GetOrDefault<FName>("OutputName").Text;
                if (string.IsNullOrEmpty(name) || name == "None")
                    name = i == 0 ? "Out" : $"Out {i}";
                // pin names double as lookup keys, keep them unique per node
                while (node.OutputPins.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    name += "'";
                node.OutputPins.Add(new MaterialGraphPin { Name = name, PinType = "default" });
            }
            return;
        }

        node.OutputPins.Add(new MaterialGraphPin { Name = "Out", PinType = "default" });
    }

    /// <summary>
    /// Scans tagged properties for FExpressionInput-shaped values which represent
    /// the node's input pins and their connections to other expressions.
    /// </summary>
    private void ScanExpressionInputs(MaterialGraphNode node, IEnumerable<FPropertyTag> tags, bool deepScan)
    {
        foreach (var tag in tags)
        {
            var pinType = GetPinTypeFromStructName(tag.TagData?.StructType);
            switch (tag.Tag?.GenericValue)
            {
                case FScriptStruct { StructType: FExpressionInput input }:
                    AddInputPin(node, tag.Name.Text, pinType, input);
                    break;
                case FScriptStruct { StructType: FStructFallback fallback }:
                    ScanFallbackForInputs(node, tag.Name.Text, pinType, fallback, deepScan ? 1 : 0);
                    break;
                case UScriptArray array when deepScan:
                    for (var i = 0; i < array.Properties.Count; i++)
                    {
                        switch (array.Properties[i].GenericValue)
                        {
                            case FScriptStruct { StructType: FExpressionInput input }:
                                AddInputPin(node, $"{tag.Name.Text}[{i}]", pinType, input);
                                break;
                            case FScriptStruct { StructType: FStructFallback fallback }:
                                ScanFallbackForInputs(node, $"{tag.Name.Text}[{i}]", pinType, fallback, 1);
                                break;
                        }
                    }
                    break;
            }
        }
    }

    private void ScanFallbackForInputs(MaterialGraphNode node, string pinName, string pinType, FStructFallback fallback, int depth)
    {
        // untyped material inputs (e.g. ShadingModelMaterialInput, SubstrateMaterialInput)
        // still carry the FExpressionInput field layout
        if (fallback.TryGetValue(out FPackageIndex _, "Expression") ||
            (fallback.TryGetValue(out FName _, "ExpressionName") && fallback.TryGetValue(out int _, "OutputIndex")))
        {
            AddInputPin(node, pinName, pinType, new FExpressionInput(fallback));
            return;
        }

        if (depth <= 0) return;

        // nested inputs, e.g. FFunctionExpressionInput.Input on MaterialFunctionCall nodes
        foreach (var tag in fallback.Properties)
        {
            switch (tag.Tag?.GenericValue)
            {
                case FScriptStruct { StructType: FExpressionInput nested }:
                    AddInputPin(node, ResolveNestedPinName(fallback, pinName, tag.Name.Text), pinType, nested);
                    break;
                case FScriptStruct { StructType: FStructFallback nestedFallback }:
                    ScanFallbackForInputs(node, $"{pinName}.{tag.Name.Text}", pinType, nestedFallback, depth - 1);
                    break;
            }
        }
    }

    private static string ResolveNestedPinName(FStructFallback owner, string pinName, string fieldName)
    {
        // FFunctionExpressionInput knows the function input's real name through
        // its ExpressionInput (FunctionInput expression) - keep the array name otherwise
        if (owner.TryGetValue(out FPackageIndex expressionInput, "ExpressionInput") && !expressionInput.IsNull)
        {
            if (expressionInput.TryLoad(out UObject functionInput) &&
                functionInput.TryGetValue(out FName inputName, "InputName") && !inputName.IsNone)
                return inputName.Text;
        }

        return fieldName == "Input" ? pinName : $"{pinName}.{fieldName}";
    }

    private void AddInputPin(MaterialGraphNode node, string pinName, string pinType, FExpressionInput input)
    {
        // pre-native-serialize assets store the fields in a fallback struct
        if (input.FallbackStruct is { } legacy)
            input = new FExpressionInput(legacy);

        if (node.InputPins.Any(p => p.Name.Equals(pinName, StringComparison.OrdinalIgnoreCase)))
            return;

        node.InputPins.Add(new MaterialGraphPin { Name = pinName, PinType = pinType });

        // connection target: package index when present, cooked assets may only keep the name
        var sourceName = input.Expression is { IsNull: false } expr ? expr.Name : input.ExpressionName.Text;
        if (string.IsNullOrEmpty(sourceName) || sourceName == "None")
            return;

        _pendingLinks.Add((node, pinName, pinType, sourceName, input.OutputIndex));
    }

    private void ResolvePendingLinks()
    {
        foreach (var (target, pinName, pinType, sourceName, outputIndex) in _pendingLinks)
        {
            var key = sourceName.Contains('/') ? sourceName[(sourceName.LastIndexOf('/') + 1)..] : sourceName;
            key = key.Contains('.') ? key[(key.LastIndexOf('.') + 1)..] : key;
            if (!_nodesByExportName.TryGetValue(key, out var source))
                continue;

            var sourcePin = source.OutputPins.Count > outputIndex && outputIndex >= 0
                ? source.OutputPins[outputIndex].Name
                : source.OutputPins.FirstOrDefault()?.Name ?? "Out";

            Connections.Add(new MaterialGraphConnection
            {
                SourceNode = source,
                SourcePinName = sourcePin,
                TargetNode = target,
                TargetPinName = pinName,
                PinType = pinType
            });
        }
        _pendingLinks.Clear();
    }

    private void ApplyInstanceOverrides(List<UMaterialInterface> chain)
    {
        // nearest instance in the chain wins, so walk from root-most instance down to the clicked one
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i] is not UMaterialInstance instance) continue;

            ApplyParameterValues(instance, "ScalarParameterValues", value =>
                value.TryGetValue(out float scalar, "ParameterValue") ? scalar.ToString("0.####") : null);
            ApplyParameterValues(instance, "VectorParameterValues", value =>
                value.TryGetValue(out FLinearColor color, "ParameterValue") ? FormatColor(color) : null);
            ApplyParameterValues(instance, "TextureParameterValues", value =>
                value.TryGetValue(out FPackageIndex texture, "ParameterValue") ? texture.Name : null);

            if (instance.StaticParameters is { StaticSwitchParameters: not null })
            {
                foreach (var switchParameter in instance.StaticParameters.StaticSwitchParameters)
                    OverrideParameterNode(switchParameter.Name, switchParameter.Value.ToString(), instance.Name);
            }
        }
    }

    private void ApplyParameterValues(UMaterialInstance instance, string propertyName, Func<FStructFallback, string> formatValue)
    {
        if (!instance.TryGetValue(out FStructFallback[] values, propertyName)) return;

        foreach (var value in values)
        {
            if (!value.TryGetValue(out FStructFallback parameterInfo, "ParameterInfo")) continue;
            var name = parameterInfo.GetOrDefault<FName>("Name").Text;
            if (string.IsNullOrEmpty(name) || name == "None") continue;

            var formatted = formatValue(value);
            if (formatted != null)
                OverrideParameterNode(name, formatted, instance.Name);
        }
    }

    private void OverrideParameterNode(string parameterName, string value, string instanceName)
    {
        foreach (var node in Nodes)
        {
            if (!node.IsParameter || !node.Subtitle.Split(" = ")[0].Equals(parameterName, StringComparison.OrdinalIgnoreCase))
                continue;

            node.Subtitle = $"{parameterName} = {value}";
            node.DisplayProperties.Add(new KeyValuePair<string, string>($"Overridden by {instanceName}", value));
        }
    }

    #endregion

    #region Shader bytecode reconstruction

    /// <summary>
    /// Rebuilds a graph from the compiled shader data that survives cooking: the uniform
    /// expression ("preshader") bytecode, texture parameter binding tables and compilation
    /// output flags of the material's inlined shader map. Returns false when no shader map
    /// was serialized or nothing in it could be decoded.
    /// </summary>
    private bool TryBuildShaderMapGraph(List<UMaterialInterface> chain)
    {
        try
        {
            // the material closest to the one that was clicked owns the shader map compiled
            // for its final parameter permutation (instances can carry their own)
            UMaterialInterface shaderMapOwner = null;
            FMaterialShaderMap shaderMap = null;
            foreach (var material in chain)
            {
                shaderMap = material.LoadedMaterialResources?.FirstOrDefault(r => r.LoadedShaderMap != null)?.LoadedShaderMap;
                if (shaderMap == null) continue;
                shaderMapOwner = material;
                break;
            }

            if (shaderMap == null && TryBuildLegacyShaderMapGraph(chain))
                return true;

            if (shaderMap?.Content is not FMaterialShaderMapContent content ||
                content.MaterialCompilationOutput?.UniformExpressionSet is not { } expressionSet)
                return false;

            var game = shaderMapOwner.Owner?.Provider?.Versions.Game ?? EGame.GAME_UE4_LATEST;
            var decoded = MaterialPreshaderDecoder.Decode(expressionSet, game);
            var hasTextureBindings = expressionSet.UniformTextureParameters?.Any(slot => slot is { Length: > 0 }) == true;
            if (!decoded.AnyDecoded && !hasTextureBindings)
                return false;

            BuildShaderMapGraph(chain, shaderMapOwner, shaderMap, content, expressionSet, decoded, game);
            return true;
        }
        catch
        {
            // never let a bytecode decoding problem take the viewer down, the cached
            // parameter reconstruction still works as the last resort
            Nodes.Clear();
            Connections.Clear();
            _nodesByExportName.Clear();
            _pendingLinks.Clear();
            ReconstructionNote = string.Empty;
            return false;
        }
    }

    private void BuildShaderMapGraph(List<UMaterialInterface> chain, UMaterialInterface shaderMapOwner,
        FMaterialShaderMap shaderMap, FMaterialShaderMapContent content, FUniformExpressionSet expressionSet,
        PreshaderDecodeResult decoded, EGame game)
    {
        var material = chain[0];
        var parameters = new CMaterialParams2();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            try
            {
                chain[i].GetParams(parameters, EMaterialFormat.AllLayers);
            }
            catch
            {
                // display-only data, a partially readable material should not stop the graph
            }
        }

        var outputNode = new MaterialGraphNode
        {
            Name = material.Name,
            Title = material.Name,
            Subtitle = "Material Output (from shader bytecode)",
            ExportType = material.ExportType,
            IsOutputNode = true
        };
        AddStandardOutputPins(outputNode, material, parameters);
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Blend Mode", parameters.BlendMode.ToString()));
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Shading Model", parameters.ShadingModel.ToString()));
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Shader Map", shaderMapOwner.Name));
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Opcode Set", decoded.OpcodeSetUsed));
        foreach (var flag in MaterialPreshaderDecoder.DescribeCompilationOutput(content.MaterialCompilationOutput, game))
            outputNode.DisplayProperties.Add(flag);
        AppendDisplayProperties(outputNode, material.Properties);
        Nodes.Add(outputNode);

        var textureIndices = FindReferencedTextureIndices(chain, shaderMapOwner);
        var referencedTextures = textureIndices.Select(t => t.IsNull ? null : t.Name).ToList();
        var textureObjects = textureIndices.Select(t =>
        {
            try
            {
                return t.IsNull ? null : t.Load<UTexture>();
            }
            catch
            {
                // a missing/unreadable texture package only costs us its metadata
                return null;
            }
        }).ToList();
        var layoutRoots = new List<MaterialGraphNode> { outputNode };

        // texture binding tables: one node per bound texture, typed by its sampler slot
        var textureCount = 0;
        var textureNodesByIndex = new Dictionary<int, MaterialGraphNode>();
        var textureNodesBySlot = new Dictionary<(int Slot, int Index), MaterialGraphNode>();
        var virtualTextureNodes = new List<MaterialGraphNode>();
        for (var slot = 0; slot < (expressionSet.UniformTextureParameters?.Length ?? 0); slot++)
        {
            var slotName = MaterialPreshaderDecoder.GetTextureSlotName(game, slot);
            var indexInSlot = 0;
            foreach (var textureParameter in expressionSet.UniformTextureParameters[slot] ?? [])
            {
                var parameterName = textureParameter.ParameterInfo?.Name.Text ?? textureParameter.ParameterName;
                var isParameter = !string.IsNullOrEmpty(parameterName) && parameterName != "None";
                var textureName = referencedTextures.ElementAtOrDefault(textureParameter.TextureIndex);
                var texture = textureObjects.ElementAtOrDefault(textureParameter.TextureIndex);

                var textureNode = new MaterialGraphNode
                {
                    Name = $"ShaderTexture_{slot}_{textureCount++}",
                    Title = isParameter ? $"Texture Parameter ({slotName})" : $"Texture ({slotName})",
                    Subtitle = isParameter ? parameterName : textureName ?? $"Index {textureParameter.TextureIndex}",
                    ExportType = "PreshaderTextureParameter",
                    IsTexture = true,
                    IsParameter = isParameter
                };
                textureNode.OutputPins.Add(new MaterialGraphPin { Name = "RGB", PinType = "color" });
                textureNode.OutputPins.Add(new MaterialGraphPin { Name = "A", PinType = "float" });
                if (isParameter)
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Parameter Name", parameterName));
                textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Texture Index", textureParameter.TextureIndex.ToString()));
                if (textureName != null)
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Texture", textureName));
                if (texture != null)
                {
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Compression", texture.CompressionSettings.ToString()));
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("sRGB", texture.SRGB.ToString()));
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("LOD Group", texture.LODGroup.ToString()));
                }
                textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Sampler Source", textureParameter.SamplerSource.ToString()));
                if (textureParameter.VirtualTextureLayerIndex != 0)
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("VT Layer", textureParameter.VirtualTextureLayerIndex.ToString()));
                Nodes.Add(textureNode);

                textureNodesByIndex.TryAdd(textureParameter.TextureIndex, textureNode);
                textureNodesBySlot[(slot, indexInSlot++)] = textureNode;
                if (slotName == "Virtual Texture")
                    virtualTextureNodes.Add(textureNode);
            }
        }

        // decoded preshader trees: each is one uniform value the CPU computes and uploads
        // to the material's constant buffer every frame. Parameters are shared between trees
        // like in an authored graph; every shown connection is real, straight from the
        // bytecode. Which output pin consumes a value is compiled into the GPU shader and
        // not serialized, so nothing is wired into the output node.
        var expressionNodes = new Dictionary<PreshaderExpression, MaterialGraphNode>();
        var parameterNodes = new Dictionary<string, MaterialGraphNode>(StringComparer.OrdinalIgnoreCase);
        var uniformRootNodes = new Dictionary<string, MaterialGraphNode>();
        foreach (var uniform in decoded.Uniforms)
        {
            var root = CreatePreshaderExpressionNode(uniform.Expression, expressionNodes, parameterNodes, referencedTextures);
            uniformRootNodes[uniform.Name] = root;

            var slotLabel = uniform.TypeDescription.Length > 0 ? $"{uniform.Name} ({uniform.TypeDescription})" : uniform.Name;
            root.DisplayProperties.Add(new KeyValuePair<string, string>("Uniform Slot", slotLabel));
            if (root.InputPins.Count > 0)
            {
                // operation roots carry the uniform's identity; leaf roots are plain
                // parameter/constant passthroughs and already say what they are
                if (string.IsNullOrEmpty(root.Subtitle))
                    root.Subtitle = $"→ {uniform.Name}";
                var preview = uniform.Expression.ToDisplayString();
                if (preview.Length > MaxPropertyValueDisplayLength)
                    preview = preview[..MaxPropertyValueDisplayLength] + "…";
                root.DisplayProperties.Add(new KeyValuePair<string, string>("Expression", preview));
            }
            if (!layoutRoots.Contains(root))
                layoutRoots.Add(root);
        }

        // bytecode ops that reference a texture (TextureSize, TexelSize, ...) name it by
        // index, so wiring the binding node into them is real data rather than a guess
        foreach (var (expression, expressionNode) in expressionNodes)
        {
            if (expression.TextureIndex < 0 ||
                !textureNodesByIndex.TryGetValue(expression.TextureIndex, out var bindingNode) ||
                expressionNode.InputPins.Any(p => p.Name == "Texture"))
                continue;
            expressionNode.InputPins.Add(new MaterialGraphPin { Name = "Texture", PinType = "default" });
            Connections.Add(new MaterialGraphConnection
            {
                SourceNode = bindingNode, SourcePinName = "RGB",
                TargetNode = expressionNode, TargetPinName = "Texture",
                PinType = "default"
            });
        }

        // parameters the bytecode never referenced still exist in the binding tables
        var referencedParameters = new HashSet<string>(
            expressionNodes.Keys.Where(e => e.IsParameter).Select(e => e.ParameterName),
            StringComparer.OrdinalIgnoreCase);
        var looseRow = 0;
        foreach (var parameter in expressionSet.UniformNumericParameters ?? [])
        {
            var name = parameter.ParameterInfo?.Name.Text;
            if (string.IsNullOrEmpty(name) || name == "None" || !referencedParameters.Add(name)) continue;
            AddLooseParameterNode(GetNumericParameterTitle(parameter.ParameterType), name,
                FormatNumericParameterValue(parameter.Value), GetNumericParameterPinType(parameter.ParameterType), ref looseRow);
        }
        foreach (var parameter in expressionSet.UniformScalarParameters ?? [])
        {
            var name = parameter.ParameterInfo?.Name.Text ?? parameter.ParameterName;
            if (string.IsNullOrEmpty(name) || name == "None" || !referencedParameters.Add(name)) continue;
            AddLooseParameterNode("Scalar Parameter", name, parameter.DefaultValue.ToString("0.####"), "float", ref looseRow);
        }
        foreach (var parameter in expressionSet.UniformVectorParameters ?? [])
        {
            var name = parameter.ParameterInfo?.Name.Text ?? parameter.ParameterName;
            if (string.IsNullOrEmpty(name) || name == "None" || !referencedParameters.Add(name)) continue;
            AddLooseParameterNode("Vector Parameter", name, FormatColor(parameter.DefaultValue), "color", ref looseRow);
        }

        // virtual texture stacks assembled by the cooker
        for (var i = 0; i < (expressionSet.VTStacks?.Length ?? 0); i++)
        {
            var stack = expressionSet.VTStacks[i];
            var stackNode = new MaterialGraphNode
            {
                Name = $"VTStack_{i}",
                Title = "Virtual Texture Stack",
                Subtitle = $"{stack.NumLayers} layer{(stack.NumLayers == 1 ? "" : "s")}",
                ExportType = "PreshaderVirtualTextureStack",
                IsTexture = true
            };
            stackNode.OutputPins.Add(new MaterialGraphPin { Name = "Out", PinType = "default" });
            stackNode.DisplayProperties.Add(new KeyValuePair<string, string>("Preallocated Texture Index", stack.PreallocatedStackTextureIndex.ToString()));
            for (var layer = 0; layer < stack.NumLayers && layer < stack.LayerUniformExpressionIndices.Length; layer++)
            {
                stackNode.DisplayProperties.Add(new KeyValuePair<string, string>($"Layer {layer}", $"Uniform expression {stack.LayerUniformExpressionIndices[layer]}"));

                // layer indices point into the virtual-texture binding table, so these
                // connections are real cooker output too
                var layerExpressionIndex = (int) stack.LayerUniformExpressionIndices[layer];
                if (layerExpressionIndex < 0 || layerExpressionIndex >= virtualTextureNodes.Count) continue;
                var layerPin = $"Layer {layer}";
                stackNode.InputPins.Add(new MaterialGraphPin { Name = layerPin, PinType = "color" });
                Connections.Add(new MaterialGraphConnection
                {
                    SourceNode = virtualTextureNodes[layerExpressionIndex], SourcePinName = "RGB",
                    TargetNode = stackNode, TargetPinName = layerPin,
                    PinType = "color"
                });
            }
            Nodes.Add(stackNode);
        }

        // the base-pass pixel shader compiled into the shader map records exactly which
        // uniform expression and which bound texture reach which render target, and UE4's
        // GBuffer layout ties render targets to material pins — real wiring, no guessing
        PixelShaderWiring wiring = null;
        var wiredPinCount = 0;
        var usesGBufferPins = parameters.BlendMode is EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked;
        if (game < EGame.GAME_UE5_0)
        {
            try
            {
                wiring = MaterialPixelShaderAnalyzer.Analyze(shaderMap, expressionSet, usesGBufferPins);
            }
            catch (Exception e)
            {
                wiring = new PixelShaderWiring { FailureReason = e.Message };
            }
            if (wiring.Success)
                wiredPinCount = ApplyPixelShaderWiring(wiring, outputNode, uniformRootNodes, textureNodesBySlot);
        }
        else
        {
            // UE5: the base-pass pixel shader is SM6 DXIL living in the IoStore shared shader library
            try
            {
                wiring = TryAnalyzeDxil(shaderMap, content, expressionSet, shaderMapOwner, game, usesGBufferPins);
            }
            catch (Exception e)
            {
                wiring = new PixelShaderWiring { FailureReason = e.Message };
            }
            if (wiring is { Success: true })
                wiredPinCount = ApplyPixelShaderWiring(wiring, outputNode, uniformRootNodes, textureNodesBySlot);
        }

        var note = new StringBuilder("Reconstructed from compiled shader bytecode — the editor node graph was stripped when this asset was cooked. ");
        note.Append($"{decoded.Uniforms.Count} uniform expression{(decoded.Uniforms.Count == 1 ? "" : "s")} decoded ({decoded.OpcodeSetUsed} opcode set)");
        if (decoded.FailedCount > 0) note.Append($", {decoded.FailedCount} failed");
        note.Append(". All shown connections are real, decoded 1:1 from serialized data.");
        if (wiredPinCount > 0)
        {
            note.Append($" Output wiring recovered from the compiled base-pass pixel shader ({wiring.ShaderTypeName}).");
            if (ExpandPixelShaderMath && CanExpandPixelShaderMath)
                note.Append(" Pixel shader math expanded: one node per decoded instruction.");
        }
        else if (wiring is { Success: true })
            note.Append($" The compiled base-pass pixel shader ({wiring.ShaderTypeName}) was analyzed, but no serialized value reaches an output pin directly — constant inputs get folded into the shader at compile time.");
        else if (wiring != null)
            note.Append(game >= EGame.GAME_UE5_0
                ? $" Output pins are unwired: decoding the compiled SM6/DXIL base-pass pixel shader failed ({wiring.FailureReason})."
                : $" Output pins are unwired: pixel shader analysis failed ({wiring.FailureReason}).");
        else
            note.Append(" Output pins are unwired: recovering the wiring from the compiled GPU shader is implemented for D3D SM5 (DXBC) and SM6 (DXIL) only.");
        AppendShaderStageNote(note);
        if (UserGraphOnly)
            note.Append(" Showing the user-authored material graph only — the engine shader scaffolding (other shader stages and uniforms that reach no material output) is hidden.");
        ReconstructionNote = note.ToString();
        foreach (var warning in decoded.Warnings.Take(8))
            outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Decode Warning", warning));

        ApplyInstanceOverrides(chain);
        FinalizeReconstructedGraph(layoutRoots);
    }

    /// <summary>
    /// UE5 output-pin wiring. The base-pass pixel shader is SM6 DXIL stored in the IoStore shared
    /// shader library; retrieve it by the shader map's ResourceHash and each candidate's in-map
    /// index, then run the DXIL taint analysis. Candidates are every pixel shader in the map,
    /// richest (most instructions) first — the base-pass permutation writes the whole GBuffer.
    /// </summary>
    private PixelShaderWiring TryAnalyzeDxil(FMaterialShaderMap shaderMap, FMaterialShaderMapContent content,
        FUniformExpressionSet expressionSet, UMaterialInterface shaderMapOwner, EGame game, bool usesGBuffer)
    {
        if (shaderMap.ResourceHash is not { } resourceHash)
            return new PixelShaderWiring { FailureReason = "the shader map does not reference the shared shader library" };
        if (shaderMapOwner.Owner?.Provider is not IVfsFileProvider provider)
            return new PixelShaderWiring { FailureReason = "the material provider does not expose IoStore containers" };

        var candidates = new List<FShader>();
        void Collect(FShader[] shaders)
        {
            foreach (var shader in shaders ?? [])
                if (shader?.Target.Frequency == EShaderFrequency.SF_Pixel)
                    candidates.Add(shader);
        }
        Collect(content.Shaders);
        foreach (var mesh in content.OrderedMeshShaderMaps ?? [])
            Collect(mesh.Shaders);
        if (candidates.Count == 0)
            return new PixelShaderWiring { FailureReason = "the shader map has no pixel shader" };

        PixelShaderWiring best = null;
        var lastError = "no base-pass pixel shader could be analyzed";
        foreach (var shader in candidates.OrderByDescending(s => s.NumInstructions).Take(10))
        {
            var blob = MaterialDxilLibrary.TryGetShaderCode(provider, resourceHash, shader.ResourceIndex, out var error);
            if (blob == null) { lastError = error ?? lastError; continue; }

            PixelShaderWiring candidate;
            try { candidate = MaterialPixelShaderAnalyzer.AnalyzeDxil(blob, expressionSet, game, usesGBuffer); }
            catch (Exception e) { lastError = e.Message; continue; }

            if (!candidate.Success) { lastError = candidate.FailureReason; continue; }
            candidate.ShaderTypeName = HashedNamesProvider.TryGetEntry(shader.Type.Hash, out var tn) && !string.IsNullOrEmpty(tn)
                ? $"{tn}, SM6 DXIL" : "SM6 DXIL";
            if (best == null || candidate.PinSources.Count > best.PinSources.Count) best = candidate;
            if (best.PinSources.Count >= 5) break; // the base-pass PS wires the full GBuffer
        }
        return best ?? new PixelShaderWiring { FailureReason = lastError };
    }

    /// <summary>
    /// Connects preshader roots and texture binding nodes to the output pins the compiled
    /// base-pass pixel shader routes them into. When several values reach the same pin the
    /// shader combined them arithmetically; an explicit intermediate node represents that
    /// math so every pin keeps exactly one incoming connection. Returns how many output
    /// pins got wired.
    /// </summary>
    private int ApplyPixelShaderWiring(PixelShaderWiring wiring, MaterialGraphNode outputNode,
        Dictionary<string, MaterialGraphNode> uniformRootNodes,
        Dictionary<(int Slot, int Index), MaterialGraphNode> textureNodesBySlot)
    {
        CanExpandPixelShaderMath = wiring.PinExpressions.Count > 0 || wiring.ShaderStages.Any(s => s.OutputExpressions.Count > 0);
        _otherStageWiredValues = 0;
        _otherStageNodeCount = 0;
        _otherStageExpandedCount = 0;
        var emitter = ExpandPixelShaderMath && CanExpandPixelShaderMath
            ? new PixelMathEmitter(this, uniformRootNodes, textureNodesBySlot)
            : null;

        var wired = 0;
        foreach (var (pinName, sources) in wiring.PinSources)
        {
            var pin = outputNode.InputPins.FirstOrDefault(p => p.Name == pinName);
            if (pin == null) continue;

            // optional expansion: the real per-instruction math instead of an opaque combiner
            if (emitter != null && wiring.PinExpressions.TryGetValue(pinName, out var expressionRoot) &&
                emitter.TryEmit(expressionRoot, out var expandedNode, out var expandedPin))
            {
                Connections.Add(new MaterialGraphConnection
                {
                    SourceNode = expandedNode, SourcePinName = expandedPin,
                    TargetNode = outputNode, TargetPinName = pinName,
                    PinType = pin.PinType
                });
                if (wiring.PinDisassembly.TryGetValue(pinName, out var expandedCode))
                    outputNode.DisplayProperties.Add(new KeyValuePair<string, string>($"Shader Code → {pinName}", expandedCode));
                wired++;
                continue;
            }

            // resolve each serialized value to the graph node that shows it; uniform slot
            // names here match the ones the preshader decoder assigns pre-UE5
            var endpoints = ResolveValueEndpoints(sources, uniformRootNodes, textureNodesBySlot);
            if (endpoints.Count == 0) continue;

            wiring.PinDisassembly.TryGetValue(pinName, out var shaderCode);

            if (endpoints.Count == 1)
            {
                Connections.Add(new MaterialGraphConnection
                {
                    SourceNode = endpoints[0].Node, SourcePinName = endpoints[0].Pin,
                    TargetNode = outputNode, TargetPinName = pinName,
                    PinType = pin.PinType
                });
                if (shaderCode != null)
                    outputNode.DisplayProperties.Add(new KeyValuePair<string, string>($"Shader Code → {pinName}", shaderCode));
            }
            else
            {
                // the exact combining math lives only in the instruction stream, so it is
                // shown as one opaque node rather than invented arithmetic
                var combiner = new MaterialGraphNode
                {
                    Name = $"PixelShaderMath_{pinName.Replace(" ", string.Empty)}",
                    Title = "Pixel Shader Math",
                    Subtitle = $"→ {pinName}",
                    ExportType = "PreshaderPixelMath"
                };
                if (shaderCode != null)
                    combiner.DisplayProperties.Add(new KeyValuePair<string, string>("Shader Code", shaderCode));
                combiner.OutputPins.Add(new MaterialGraphPin { Name = "Out", PinType = pin.PinType });
                for (var i = 0; i < endpoints.Count; i++)
                {
                    var inputPin = $"In {i}";
                    combiner.InputPins.Add(new MaterialGraphPin { Name = inputPin, PinType = "default" });
                    Connections.Add(new MaterialGraphConnection
                    {
                        SourceNode = endpoints[i].Node, SourcePinName = endpoints[i].Pin,
                        TargetNode = combiner, TargetPinName = inputPin,
                        PinType = "default"
                    });
                }
                Nodes.Add(combiner);
                Connections.Add(new MaterialGraphConnection
                {
                    SourceNode = combiner, SourcePinName = "Out",
                    TargetNode = outputNode, TargetPinName = pinName,
                    PinType = pin.PinType
                });
            }
            wired++;
        }

        // the rest of the shader map: every OTHER compiled shader stage (vertex, geometry,
        // compute, shadow/depth pixel, simplified-shading base-pass permutations) shown as a
        // node wired to the material output, so the whole map is visible — not just the analyzed
        // base-pass pixel shader. Where a stage provably routes material values into its own
        // outputs, the real uniform nodes feed the stage node; where it reads no material
        // parameter at all (e.g. GPU-skinned vertex shaders) the node says so plainly.
        // Skipped entirely in "User Graph Only" mode: engine stages are not part of the artist's
        // graph, and skipping them keeps the shared expansion budget for the material pins.
        if (!UserGraphOnly)
            foreach (var stage in wiring.ShaderStages)
                EmitShaderStageNode(stage, outputNode, uniformRootNodes, textureNodesBySlot, emitter);
        return wired;
    }

    private static readonly string[] StandardMaterialOutputPins =
    [
        "Base Color", "Metallic", "Specular", "Roughness", "Emissive Color", "Opacity", "Opacity Mask",
        "Normal", "World Position Offset", "Ambient Occlusion", "Refraction", "Pixel Depth Offset",
        "Subsurface Color", "Clear Coat", "Clear Coat Roughness", "Anisotropy", "Tangent"
    ];

    /// <summary>Appends the shader-map overview sentence describing the stage nodes added this build.</summary>
    private void AppendShaderStageNote(StringBuilder note)
    {
        // "User Graph Only" hides the stage nodes, so describing them would contradict the view;
        // FinalizeReconstructedGraph adds the isolation sentence instead.
        if (UserGraphOnly || _otherStageNodeCount <= 0) return;
        note.Append($" The rest of the shader map is shown as {_otherStageNodeCount} compiled shader-stage node(s) wired to the output");
        if (_otherStageExpandedCount > 0)
            note.Append($", {_otherStageExpandedCount} of them expanded into their real per-output instruction nodes");
        else if (_otherStageWiredValues > 0)
            note.Append($", {_otherStageWiredValues} of them fed by real material values that reach those stages' outputs");
        note.Append('.');
    }

    /// <summary>
    /// Marks which nodes belong to the user-authored material graph. A node is user content when it
    /// feeds one of the material's attribute outputs (Base Color, Roughness, …) — found by walking
    /// the connections backwards from the output node's material-attribute pins, skipping the extra
    /// pins that fan compiled shader stages into the output. Everything the walk never reaches is
    /// engine scaffolding: other shader stages and uniform values that reach no material output.
    /// This is the exact boundary the material compiler drew — CalcPixelMaterialInputs is the
    /// artist's graph; the surrounding template is the engine's — recovered here from the dataflow,
    /// not guessed. Sets <see cref="CanSeparateUserGraph"/> and returns the engine node count.
    /// </summary>
    private int ClassifyUserGraphNodes()
    {
        var outputNode = Nodes.FirstOrDefault(n => n.IsOutputNode);
        if (outputNode == null)
        {
            foreach (var node in Nodes) node.IsUserMaterialNode = true;
            CanSeparateUserGraph = false;
            return 0;
        }

        var materialPins = new HashSet<string>(
            outputNode.InputPins.Where(p => !p.IsEngineStagePin).Select(p => p.Name),
            StringComparer.Ordinal);
        var incoming = Connections.ToLookup(c => c.TargetNode);

        var user = new HashSet<MaterialGraphNode> { outputNode };
        var stack = new Stack<MaterialGraphNode>();
        foreach (var connection in Connections)
        {
            if (connection.TargetNode == outputNode && connection.SourceNode != outputNode &&
                materialPins.Contains(connection.TargetPinName))
                stack.Push(connection.SourceNode);
        }
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (!user.Add(node)) continue;
            foreach (var connection in incoming[node])
                if (connection.SourceNode != outputNode) stack.Push(connection.SourceNode);
        }

        var engineCount = 0;
        foreach (var node in Nodes)
        {
            node.IsUserMaterialNode = user.Contains(node);
            if (!node.IsUserMaterialNode) engineCount++;
        }
        // the options only make sense when there is genuinely a mix to separate
        CanSeparateUserGraph = engineCount > 0 && user.Count > 1;
        return engineCount;
    }

    /// <summary>
    /// Drops every engine-scaffolding node (and its now-dangling connections and output pins),
    /// leaving only the user-authored material graph wired into the material output — the closest
    /// recoverable form of the graph the artist built in the editor. Returns the layout roots that
    /// survived the prune.
    /// </summary>
    private List<MaterialGraphNode> PruneToUserGraph(List<MaterialGraphNode> layoutRoots)
    {
        var outputNode = Nodes.FirstOrDefault(n => n.IsOutputNode);
        Nodes.RemoveAll(n => !n.IsUserMaterialNode && !n.IsOutputNode);
        var kept = new HashSet<MaterialGraphNode>(Nodes);
        Connections.RemoveAll(c => !kept.Contains(c.SourceNode) || !kept.Contains(c.TargetNode));

        if (outputNode != null)
        {
            var stillConnected = new HashSet<string>(
                Connections.Where(c => c.TargetNode == outputNode).Select(c => c.TargetPinName),
                StringComparer.Ordinal);
            outputNode.InputPins.RemoveAll(p => p.IsEngineStagePin && !stillConnected.Contains(p.Name));
        }

        return layoutRoots.Where(kept.Contains).ToList();
    }

    /// <summary>
    /// Shared tail of the shader-map builders: classify user vs engine nodes, optionally prune to
    /// the user graph, then lay the survivors out. Keeps both reconstruction paths consistent.
    /// </summary>
    private void FinalizeReconstructedGraph(List<MaterialGraphNode> layoutRoots)
    {
        ClassifyUserGraphNodes();
        if (UserGraphOnly)
            layoutRoots = PruneToUserGraph(layoutRoots);
        AutoLayout(layoutRoots);
    }

    /// <summary>
    /// Adds one node representing a compiled shader stage (or a value-routing permutation) and
    /// connects it to the material output node. The node lists the real shader type names and,
    /// when the stage feeds no material value, states that it reads no material parameters — the
    /// honest reason a vertex/geometry/compute stage has nothing wired into it.
    /// </summary>
    private void EmitShaderStageNode(ShaderStageGroup stage, MaterialGraphNode outputNode,
        Dictionary<string, MaterialGraphNode> uniformRootNodes,
        Dictionary<(int Slot, int Index), MaterialGraphNode> textureNodesBySlot,
        PixelMathEmitter emitter)
    {
        var frequencyName = stage.Frequency switch
        {
            0 => "Vertex Shader", 1 => "Hull Shader", 2 => "Domain Shader",
            3 => "Pixel Shader", 4 => "Geometry Shader", 5 => "Compute Shader",
            _ => "Shader"
        };
        var node = new MaterialGraphNode
        {
            Name = $"ShaderStage_{Nodes.Count}",
            Title = stage.Label,
            Subtitle = stage.ShaderCount > 1 ? $"{stage.ShaderCount} compiled shaders" : frequencyName,
            ExportType = "CompiledShaderStage"
        };
        node.OutputPins.Add(new MaterialGraphPin { Name = "Out", PinType = "default" });
        node.DisplayProperties.Add(new KeyValuePair<string, string>("Stage", frequencyName));
        node.DisplayProperties.Add(new KeyValuePair<string, string>("Compiled Shaders", stage.ShaderCount.ToString()));
        if (stage.TypeNames.Count > 0)
            node.DisplayProperties.Add(new KeyValuePair<string, string>("Shader Types", string.Join("\n", stage.TypeNames)));

        // with "Expand Shader Math" on, decode this stage into real instruction nodes — one input
        // pin per output register (SV_Position, TEXCOORD3, …), each fed by the recovered math DAG
        var expandedOutputs = 0;
        if (emitter != null && stage.OutputExpressions.Count > 0)
        {
            foreach (var (semantic, dag) in stage.OutputExpressions)
            {
                if (!emitter.TryEmit(dag, out var exprNode, out var exprPin)) continue;
                var inputPin = semantic;
                while (node.InputPins.Any(p => p.Name == inputPin)) inputPin += " ";
                node.InputPins.Add(new MaterialGraphPin { Name = inputPin, PinType = "default" });
                Connections.Add(new MaterialGraphConnection
                {
                    SourceNode = exprNode, SourcePinName = exprPin,
                    TargetNode = node, TargetPinName = inputPin, PinType = "default"
                });
                expandedOutputs++;
            }
        }

        if (expandedOutputs > 0)
        {
            node.DisplayProperties.Add(new KeyValuePair<string, string>("Expanded From", stage.RepresentativeType));
            node.DisplayProperties.Add(new KeyValuePair<string, string>("Decoded Outputs",
                $"{expandedOutputs} output register(s): {string.Join(", ", stage.OutputExpressions.Keys)}"));
            _otherStageExpandedCount++;
        }
        else
        {
            // not expanded: wire real material values that reach this stage's outputs, if any
            var endpoints = ResolveValueEndpoints(stage.OutputValues, uniformRootNodes, textureNodesBySlot);
            if (endpoints.Count > 0)
            {
                for (var i = 0; i < endpoints.Count; i++)
                {
                    var inputPin = $"In {i}";
                    node.InputPins.Add(new MaterialGraphPin { Name = inputPin, PinType = "default" });
                    Connections.Add(new MaterialGraphConnection
                    {
                        SourceNode = endpoints[i].Node, SourcePinName = endpoints[i].Pin,
                        TargetNode = node, TargetPinName = inputPin, PinType = "default"
                    });
                    _otherStageWiredValues++;
                }
                node.DisplayProperties.Add(new KeyValuePair<string, string>("Material Inputs",
                    $"{endpoints.Count} material value(s) routed to this stage's output"));
            }
            else
            {
                node.DisplayProperties.Add(new KeyValuePair<string, string>("Material Inputs",
                    stage.BindsMaterial
                        ? "reads material parameters, but none provably reaches this stage's output (packed/row-level reads)"
                        : "reads no material parameters — driven by the vertex factory / view / primitive data, not the material graph"));
                if (emitter != null && stage.Frequency == 5)
                    node.DisplayProperties.Add(new KeyValuePair<string, string>("Expand",
                        "compute shaders write to buffers (UAVs), not output registers, so their math cannot be graphed as node outputs"));
            }
        }

        Nodes.Add(node);

        // a distinct pin per stage group on the material output node (labels are already unique
        // by construction; keep them clear of the standard attribute pins and de-dupe defensively)
        var pinName = stage.Label;
        if (StandardMaterialOutputPins.Contains(pinName, StringComparer.OrdinalIgnoreCase))
            pinName = $"{frequencyName}: {pinName}";
        var baseName = pinName;
        var suffix = 2;
        while (outputNode.InputPins.Any(p => p.Name == pinName))
            pinName = $"{baseName} ({suffix++})";
        outputNode.InputPins.Add(new MaterialGraphPin { Name = pinName, PinType = "default", IsEngineStagePin = true });
        Connections.Add(new MaterialGraphConnection
        {
            SourceNode = node, SourcePinName = "Out",
            TargetNode = outputNode, TargetPinName = pinName, PinType = "default"
        });
        _otherStageNodeCount++;
    }

    /// <summary>
    /// Maps serialized shader values to the graph nodes that display them (uniform expression
    /// roots and texture bindings), shared by the material-pin wiring and the per-permutation
    /// output pins.
    /// </summary>
    private static List<(MaterialGraphNode Node, string Pin)> ResolveValueEndpoints(IEnumerable<PixelValueSource> sources,
        Dictionary<string, MaterialGraphNode> uniformRootNodes,
        Dictionary<(int Slot, int Index), MaterialGraphNode> textureNodesBySlot)
    {
        var endpoints = new List<(MaterialGraphNode Node, string Pin)>();
        foreach (var group in sources.GroupBy(s => (s.Kind, s.Index, s.TextureSlot)))
        {
            var sample = group.First();
            switch (sample.Kind)
            {
                case PixelValueKind.VectorExpression:
                    if (uniformRootNodes.TryGetValue($"Vector Expression [{sample.Index}]", out var vectorRoot))
                        endpoints.Add((vectorRoot, "Out"));
                    break;
                case PixelValueKind.ScalarExpression:
                    if (uniformRootNodes.TryGetValue($"Scalar Expression [{sample.Index}]", out var scalarRoot))
                        endpoints.Add((scalarRoot, "Out"));
                    break;
                case PixelValueKind.UniformExpression:
                    if (uniformRootNodes.TryGetValue($"Uniform Expression [{sample.Index}]", out var uniformRoot))
                        endpoints.Add((uniformRoot, "Out"));
                    break;
                case PixelValueKind.Texture:
                    if (!textureNodesBySlot.TryGetValue((sample.TextureSlot, sample.Index), out var textureNode))
                        break;
                    var channels = group.Select(s => s.Channel).Distinct().ToList();
                    if (channels.Contains(-1) || channels.Count(c => c is >= 0 and <= 2) >= 3)
                    {
                        endpoints.Add((textureNode, "RGB"));
                        if (channels.Contains(3)) endpoints.Add((textureNode, "A"));
                    }
                    else
                    {
                        foreach (var channel in channels.OrderBy(c => c))
                            endpoints.Add((textureNode, GetTextureChannelPin(textureNode, channel)));
                    }
                    break;
            }
        }
        return endpoints.Distinct().ToList();
    }

    /// <summary>
    /// Texture binding nodes expose RGB and A by default; a single-channel read gets its own
    /// pin so the graph shows which channel the shader actually consumed.
    /// </summary>
    private static string GetTextureChannelPin(MaterialGraphNode textureNode, int channel)
    {
        if (channel == 3) return "A";
        var name = channel switch { 0 => "R", 1 => "G", 2 => "B", _ => "RGB" };
        if (name != "RGB" && textureNode.OutputPins.All(p => p.Name != name))
        {
            static int ChannelRank(string pinName) => pinName switch { "R" => 0, "G" => 1, "B" => 2, _ => 3 };
            var insertAt = textureNode.OutputPins.FindIndex(p => p.Name == "RGB") + 1;
            while (insertAt < textureNode.OutputPins.Count &&
                   ChannelRank(textureNode.OutputPins[insertAt].Name) < channel)
                insertAt++;
            textureNode.OutputPins.Insert(insertAt, new MaterialGraphPin { Name = name, PinType = "float" });
        }
        return name;
    }

    /// <summary>
    /// Emits a recovered pixel shader expression DAG as graph nodes — one node per decoded
    /// DXBC instruction — wiring the leaves back to the uniform expression roots and texture
    /// binding nodes already in the graph. Shared subexpressions become shared nodes. A node
    /// budget keeps giant masters bounded; a pin over budget keeps its opaque combiner.
    /// </summary>
    private sealed class PixelMathEmitter
    {
        // budget shared across the material output pins AND every expanded shader stage; leaves
        // are hash-consed and op nodes deduped, so shared math across stages does not double-count
        private const int MaxExpandedNodes = 2500;

        private readonly MaterialGraphViewModel _viewModel;
        private readonly Dictionary<string, MaterialGraphNode> _uniformRoots;
        private readonly Dictionary<(int Slot, int Index), MaterialGraphNode> _texturesBySlot;
        private readonly Dictionary<PixelExpressionNode, (MaterialGraphNode Node, string OutPin)> _emitted = new();
        private int _emittedCount;

        public PixelMathEmitter(MaterialGraphViewModel viewModel,
            Dictionary<string, MaterialGraphNode> uniformRoots,
            Dictionary<(int Slot, int Index), MaterialGraphNode> texturesBySlot)
        {
            _viewModel = viewModel;
            _uniformRoots = uniformRoots;
            _texturesBySlot = texturesBySlot;
        }

        public bool TryEmit(PixelExpressionNode root, out MaterialGraphNode node, out string outPin)
        {
            node = null;
            outPin = "Out";
            if (_emittedCount + CountNewNodes(root, []) > MaxExpandedNodes) return false;
            (node, outPin) = Emit(root);
            return node != null;
        }

        private int CountNewNodes(PixelExpressionNode expr, HashSet<PixelExpressionNode> visited)
        {
            if (!visited.Add(expr) || _emitted.ContainsKey(expr)) return 0;
            var count = SharedUniformRootName(expr) != null ? 0 : 1;
            foreach (var arg in expr.Args)
                count += CountNewNodes(arg.Node, visited);
            return count;
        }

        /// <summary>Material cb leaves reuse the uniform expression root node already shown.</summary>
        private string SharedUniformRootName(PixelExpressionNode expr)
        {
            if (expr.Op != "cbrow" || expr.Source is not { } source) return null;
            var name = source.Kind switch
            {
                PixelValueKind.VectorExpression => $"Vector Expression [{source.Index}]",
                PixelValueKind.ScalarExpression => $"Scalar Expression [{source.Index}]",
                PixelValueKind.UniformExpression => $"Uniform Expression [{source.Index}]",
                _ => null
            };
            return name != null && _uniformRoots.ContainsKey(name) ? name : null;
        }

        private (MaterialGraphNode Node, string OutPin) Emit(PixelExpressionNode expr)
        {
            if (_emitted.TryGetValue(expr, out var existing)) return existing;

            if (SharedUniformRootName(expr) is { } rootName)
            {
                var shared = (_uniformRoots[rootName], "Out");
                _emitted[expr] = shared;
                return shared;
            }

            var (title, subtitle, exportType, outPin, pinType) = Describe(expr);
            var node = new MaterialGraphNode
            {
                Name = $"PixelExpr_{_viewModel.Nodes.Count}",
                Title = title,
                Subtitle = subtitle,
                ExportType = exportType,
                IsTexture = expr.Op == "sample"
            };
            node.OutputPins.Add(new MaterialGraphPin { Name = outPin, PinType = pinType });
            if (expr.Saturate)
                node.DisplayProperties.Add(new KeyValuePair<string, string>("Saturate", "result clamped to [0,1] (_sat modifier)"));
            _viewModel.Nodes.Add(node);
            _emitted[expr] = (node, outPin);
            _emittedCount++;

            if (expr.Op == "sample")
            {
                node.DisplayProperties.Add(new KeyValuePair<string, string>("Instruction", expr.Detail));
                if (expr.ChannelMap != null)
                    node.DisplayProperties.Add(new KeyValuePair<string, string>("Channels",
                        string.Concat(expr.ChannelMap.Select(c => "rgba"[Math.Clamp(c, 0, 3)]))));
                if (expr.Source is { Kind: PixelValueKind.Texture } textureSource &&
                    _texturesBySlot.TryGetValue((textureSource.TextureSlot, textureSource.Index), out var binding))
                {
                    if (string.IsNullOrEmpty(node.Subtitle)) node.Subtitle = binding.Subtitle;
                    node.InputPins.Add(new MaterialGraphPin { Name = "Tex", PinType = "color" });
                    _viewModel.Connections.Add(new MaterialGraphConnection
                    {
                        SourceNode = binding, SourcePinName = "RGB",
                        TargetNode = node, TargetPinName = "Tex",
                        PinType = "color"
                    });
                }
            }

            foreach (var arg in expr.Args)
            {
                var (source, sourcePin) = Emit(arg.Node);
                var pinName = ComposePinName(arg);
                node.InputPins.Add(new MaterialGraphPin { Name = pinName, PinType = "default" });
                _viewModel.Connections.Add(new MaterialGraphConnection
                {
                    SourceNode = source, SourcePinName = sourcePin,
                    TargetNode = node, TargetPinName = pinName,
                    PinType = "default"
                });
            }
            return (node, outPin);
        }

        private static (string Title, string Subtitle, string ExportType, string OutPin, string PinType) Describe(PixelExpressionNode expr)
        {
            switch (expr.Op)
            {
                case "imm":
                    return ("Constant", FormatConstants(expr.Constants), "PixelMathConstant", "Out",
                        expr.Constants?.Length > 1 ? "color" : "float");
                case "input":
                    return ("Vertex Interpolant", expr.Detail, "PixelMathInterpolant", "Out", "default");
                case "cbrow":
                    return ("Engine Constant", expr.Detail, "PixelMathEngineConstant", "Out", "default");
                case "sample":
                    return ("Texture Sample", expr.Source == null ? expr.Detail : string.Empty, "PixelMathTextureSample", "RGBA", "color");
                case "append":
                    return ("Append Vector", string.Empty, "PixelMathAppend", "Out", "default");
                case "mask":
                    return ("Component Mask", "." + expr.Detail, "PixelMathMask", "Out", "default");
                case "phi":
                    return ("Branch Result", expr.Detail, "PixelMathBranch", "Out", "default");
                case "discard":
                    return ("Discard (any)", expr.Detail, "PixelMathDiscard", "Out", "default");
                case "opaque":
                    return ("Unresolved", expr.Detail, "PixelMathUnresolvedCustom", "Out", "default");
                case "mov":
                {
                    // a mov that survived aliasing carries a modifier: name it for what it does
                    var arg = expr.Args.Count > 0 ? expr.Args[0] : null;
                    var title = expr.Saturate ? "Saturate"
                        : arg is { Negate: true } ? "Negate"
                        : arg is { Absolute: true } ? "Abs"
                        : "Pass-Through";
                    return (title, "mov" + (expr.Saturate ? "_sat" : string.Empty), "PixelMathOp", "Out", "default");
                }
                default:
                {
                    var title = PixelMathTitles.GetValueOrDefault(expr.Op, expr.Op);
                    return (title, expr.Op + (expr.Saturate ? "_sat" : string.Empty), "PixelMathOp", "Out", "default");
                }
            }
        }

        private static string ComposePinName(PixelExpressionArg arg)
        {
            var name = arg.Name;
            if (arg.Swizzle.Length > 0) name += $" .{arg.Swizzle}";
            if (arg.Absolute) name += " |x|";
            if (arg.Negate) name += " (−)";
            return name;
        }

        private static string FormatConstants(float[] values)
        {
            if (values == null || values.Length == 0) return string.Empty;
            var formatted = values.Select(v => v.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture)).ToList();
            return formatted.Count == 1 ? formatted[0] : $"({string.Join(", ", formatted)})";
        }

        private static readonly Dictionary<string, string> PixelMathTitles = new()
        {
            ["add"] = "Add", ["iadd"] = "Add (int)", ["div"] = "Divide", ["mul"] = "Multiply",
            ["imul"] = "Multiply (int)", ["umul"] = "Multiply (uint)",
            ["mad"] = "Multiply-Add", ["imad"] = "Multiply-Add (int)", ["umad"] = "Multiply-Add (uint)",
            ["min"] = "Min", ["imin"] = "Min (int)", ["umin"] = "Min (uint)",
            ["max"] = "Max", ["imax"] = "Max (int)", ["umax"] = "Max (uint)",
            ["frc"] = "Frac", ["sqrt"] = "Square Root", ["rsq"] = "Inverse Square Root",
            ["exp"] = "Exp2", ["log"] = "Log2", ["rcp"] = "Reciprocal",
            ["dp2"] = "Dot Product (2)", ["dp3"] = "Dot Product (3)", ["dp4"] = "Dot Product (4)",
            ["movc"] = "Select", ["swapc"] = "Swap (conditional)",
            ["round_ni"] = "Floor", ["round_pi"] = "Ceil", ["round_ne"] = "Round", ["round_z"] = "Truncate",
            ["sin"] = "Sine", ["cos"] = "Cosine",
            ["ge"] = "Compare (≥)", ["lt"] = "Compare (<)", ["eq"] = "Compare (=)", ["ne"] = "Compare (≠)",
            ["ige"] = "Compare (≥ int)", ["ilt"] = "Compare (< int)", ["ieq"] = "Compare (= int)", ["ine"] = "Compare (≠ int)",
            ["uge"] = "Compare (≥ uint)", ["ult"] = "Compare (< uint)",
            ["and"] = "And", ["or"] = "Or", ["xor"] = "Xor", ["not"] = "Not", ["ineg"] = "Negate (int)",
            ["ftoi"] = "Float → Int", ["ftou"] = "Float → UInt", ["itof"] = "Int → Float", ["utof"] = "UInt → Float",
            ["deriv_rtx"] = "DDX", ["deriv_rtx_coarse"] = "DDX", ["deriv_rtx_fine"] = "DDX",
            ["deriv_rty"] = "DDY", ["deriv_rty_coarse"] = "DDY", ["deriv_rty_fine"] = "DDY",
            ["ishl"] = "Shift Left", ["ishr"] = "Shift Right", ["ushr"] = "Shift Right (uint)",
            ["f32tof16"] = "Float → Half", ["f16tof32"] = "Half → Float",
            ["imul_hi"] = "Multiply High (int)", ["umul_hi"] = "Multiply High (uint)",
            ["udiv"] = "Divide (uint)", ["udiv_rem"] = "Modulo (uint)"
        };
    }

    /// <summary>
    /// Turns a decoded expression tree into graph nodes. Shared sub-trees become shared nodes
    /// and parameters are deduplicated by name across all trees so each fans out to every
    /// consumer the way it would in an authored graph.
    /// </summary>
    private MaterialGraphNode CreatePreshaderExpressionNode(PreshaderExpression expression,
        Dictionary<PreshaderExpression, MaterialGraphNode> cache,
        Dictionary<string, MaterialGraphNode> parameterNodes,
        List<string> referencedTextures)
    {
        if (cache.TryGetValue(expression, out var existing)) return existing;
        if (expression.IsParameter && expression.ParameterName.Length > 0 &&
            parameterNodes.TryGetValue(expression.ParameterName, out var sharedParameter))
        {
            cache[expression] = sharedParameter;
            return sharedParameter;
        }

        var node = new MaterialGraphNode
        {
            Name = $"Preshader_{Nodes.Count}",
            Title = expression.Title,
            Subtitle = expression.Subtitle,
            ExportType = $"Preshader{expression.Op}",
            IsParameter = expression.IsParameter,
            IsTexture = expression.IsTexture
        };
        node.OutputPins.Add(new MaterialGraphPin { Name = "Out", PinType = "default" });
        if (expression.Detail.Length > 0)
        {
            node.DisplayProperties.Add(new KeyValuePair<string, string>("Detail", expression.Detail));
            if (expression.Op == "ComponentSwizzle" && string.IsNullOrEmpty(node.Subtitle))
                node.Subtitle = expression.Detail;
        }
        if (expression.TextureIndex >= 0)
        {
            node.DisplayProperties.Add(new KeyValuePair<string, string>("Texture Index", expression.TextureIndex.ToString()));
            var textureName = referencedTextures.ElementAtOrDefault(expression.TextureIndex);
            if (textureName != null)
            {
                node.DisplayProperties.Add(new KeyValuePair<string, string>("Texture", textureName));
                if (string.IsNullOrEmpty(node.Subtitle)) node.Subtitle = textureName;
            }
        }
        Nodes.Add(node);
        cache[expression] = node;
        if (expression.IsParameter && expression.ParameterName.Length > 0)
            parameterNodes[expression.ParameterName] = node;

        for (var i = 0; i < expression.Inputs.Count; i++)
        {
            var pinName = i < expression.InputNames.Count ? expression.InputNames[i] : $"In {i}";
            node.InputPins.Add(new MaterialGraphPin { Name = pinName, PinType = "default" });
            var source = CreatePreshaderExpressionNode(expression.Inputs[i], cache, parameterNodes, referencedTextures);
            Connections.Add(new MaterialGraphConnection
            {
                SourceNode = source, SourcePinName = "Out",
                TargetNode = node, TargetPinName = pinName,
                PinType = "default"
            });
        }
        return node;
    }

    /// <summary>
    /// TextureIndex values in the shader map index the referenced-texture list of the material
    /// the map was compiled for, kept in its cached expression data.
    /// </summary>
    private static FPackageIndex[] FindReferencedTextureIndices(List<UMaterialInterface> chain, UMaterialInterface shaderMapOwner)
    {
        var candidates = new List<UMaterialInterface> { shaderMapOwner };
        candidates.AddRange(chain.Where(m => !ReferenceEquals(m, shaderMapOwner)));

        foreach (var material in candidates)
        {
            if (material.CachedExpressionData is { } cachedData &&
                cachedData.TryGetValue(out FPackageIndex[] textures, "ReferencedTextures") &&
                textures.Length > 0)
            {
                return textures;
            }
        }
        return [];
    }

    private static string GetNumericParameterTitle(EMaterialParameterType type) => type switch
    {
        EMaterialParameterType.Scalar => "Scalar Parameter",
        EMaterialParameterType.Vector => "Vector Parameter",
        EMaterialParameterType.DoubleVector => "Double Vector Parameter",
        EMaterialParameterType.StaticSwitch => "Static Switch",
        _ => "Parameter"
    };

    private static string GetNumericParameterPinType(EMaterialParameterType type) => type switch
    {
        EMaterialParameterType.Scalar => "float",
        EMaterialParameterType.Vector or EMaterialParameterType.DoubleVector => "color",
        EMaterialParameterType.StaticSwitch => "bool",
        _ => "default"
    };

    private static string FormatNumericParameterValue(object value) => value switch
    {
        float f => f.ToString("0.####"),
        bool b => b.ToString(),
        FLinearColor color => FormatColor(color),
        FVector4 vector => $"({vector.X:0.####}, {vector.Y:0.####}, {vector.Z:0.####}, {vector.W:0.####})",
        { } other => other.ToString(),
        null => string.Empty
    };

    #endregion

    #region UE 4.23/4.24 legacy shader map graph

    // shared shader code libraries (.ushaderbytecode) parsed once per provider; loaded lazily
    // because only shared-library games (e.g. Fortnite-era cooks) need them, and kept weakly
    // so closing the archive releases the bytecode
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CUE4Parse.FileProvider.IFileProvider, List<FLegacyShaderCodeArchive>> _legacyShaderLibraries = new();

    /// <summary>
    /// Builds a bytecode lookup (shader output hash → code) over the game's pak-cooked shared
    /// shader libraries, or null when the provider has none. The libraries are only actually
    /// read on the first lookup.
    /// </summary>
    private static Func<FSHAHash, byte[]> CreateLegacyShaderCodeResolver(List<UMaterialInterface> chain)
    {
        if (chain[0].Owner?.Provider is not { } provider) return null;
        if (!provider.Files.Keys.Any(k => k.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase)))
            return null;

        return hash =>
        {
            List<FLegacyShaderCodeArchive> libraries;
            lock (_legacyShaderLibraries)
            {
                libraries = _legacyShaderLibraries.GetValue(provider, static p =>
                {
                    var result = new List<FLegacyShaderCodeArchive>();
                    foreach (var (path, file) in p.Files)
                    {
                        if (!path.EndsWith(".ushaderbytecode", StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            var archive = new FShaderCodeArchive(new FByteArchive(path, file.Read(), p.Versions));
                            if (archive.SerializedShaders is FLegacyShaderCodeArchive legacy)
                                result.Add(legacy);
                        }
                        catch
                        {
                            // an unreadable library only disables pixel-shader wiring
                        }
                    }
                    return result;
                });
            }

            foreach (var library in libraries)
            {
                if (library.TryGetCode(hash) is { } code)
                    return code;
            }
            return null;
        };
    }

    private static FMaterialShaderMapLegacy FindLegacyShaderMap(List<UMaterialInterface> chain, out UMaterialInterface owner)
    {
        foreach (var material in chain)
        {
            var map = material.LoadedMaterialResources?.FirstOrDefault(r => r.LoadedShaderMapLegacy != null)?.LoadedShaderMapLegacy;
            if (map == null) continue;
            owner = material;
            return map;
        }
        owner = null;
        return null;
    }

    /// <summary>
    /// Pre-4.25 games keep the authored expression graph in cooked packages, so when the
    /// expression graph rendered, only attach the compiled pixel shader's per-pin assembly
    /// slices to the output node instead of rebuilding anything.
    /// </summary>
    private void TryAttachLegacyShaderCode(List<UMaterialInterface> chain)
    {
        try
        {
            var shaderMap = FindLegacyShaderMap(chain, out _);
            if (shaderMap == null) return;
            var outputNode = Nodes.FirstOrDefault(n => n.IsOutputNode);
            if (outputNode == null) return;

            var parameters = new CMaterialParams2();
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                try { chain[i].GetParams(parameters, EMaterialFormat.AllLayers); }
                catch { /* display-only data */ }
            }
            var usesGBuffer = parameters.BlendMode is EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked;
            var wiring = MaterialPixelShaderAnalyzer.AnalyzeLegacy(shaderMap, usesGBuffer, CreateLegacyShaderCodeResolver(chain));
            if (!wiring.Success) return;

            foreach (var (pinName, code) in wiring.PinDisassembly.OrderBy(p => p.Key))
                outputNode.DisplayProperties.Add(new KeyValuePair<string, string>($"Shader Code → {pinName}", code));
            if (wiring.PinDisassembly.Count > 0)
                outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Shader Code Source", wiring.ShaderTypeName));
        }
        catch
        {
            // purely additive information on top of the authored graph — never fail over it
        }
    }

    private bool TryBuildLegacyShaderMapGraph(List<UMaterialInterface> chain)
    {
        var shaderMap = FindLegacyShaderMap(chain, out var shaderMapOwner);
        if (shaderMap?.MaterialCompilationOutput?.UniformExpressionSet is not { } expressionSet)
            return false;

        BuildLegacyShaderMapGraph(chain, shaderMapOwner, shaderMap, expressionSet);
        return true;
    }

    /// <summary>
    /// Rebuilds a graph from a legacy (&lt; 4.25) cooked shader map. Unlike the preshader
    /// bytecode of 4.25+, the legacy format serializes the uniform expressions as real
    /// polymorphic trees, so every node and connection here is deserialized engine data.
    /// </summary>
    private void BuildLegacyShaderMapGraph(List<UMaterialInterface> chain, UMaterialInterface shaderMapOwner,
        FMaterialShaderMapLegacy shaderMap, FUniformExpressionSetLegacy expressionSet)
    {
        var material = chain[0];
        var parameters = new CMaterialParams2();
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            try { chain[i].GetParams(parameters, EMaterialFormat.AllLayers); }
            catch { /* display-only data, a partially readable material should not stop the graph */ }
        }

        var outputNode = new MaterialGraphNode
        {
            Name = material.Name,
            Title = material.Name,
            Subtitle = "Material Output (from shader map)",
            ExportType = material.ExportType,
            IsOutputNode = true
        };
        AddStandardOutputPins(outputNode, material, parameters);
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Blend Mode", parameters.BlendMode.ToString()));
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Shading Model", parameters.ShadingModel.ToString()));
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Shader Map", shaderMapOwner.Name));
        if (!string.IsNullOrEmpty(shaderMap.FriendlyName))
            outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Friendly Name", shaderMap.FriendlyName));
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Shader Platform", shaderMap.ShaderPlatform.ToString()));
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Quality Level", shaderMap.ShaderMapId.QualityLevel.ToString()));
        outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Feature Level", shaderMap.ShaderMapId.FeatureLevel.ToString()));
        var output = shaderMap.MaterialCompilationOutput;
        if (output.bUsesEyeAdaptation) outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Uses Eye Adaptation", "true"));
        if (output.bModifiesMeshPosition) outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Modifies Mesh Position", "true"));
        if (output.bUsesWorldPositionOffset) outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Uses World Position Offset", "true"));
        if (output.bUsesGlobalDistanceField) outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Uses Global Distance Field", "true"));
        if (output.bUsesPixelDepthOffset) outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Uses Pixel Depth Offset", "true"));
        if (output.bUsesDistanceCullFade) outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Uses Distance Cull Fade", "true"));
        if (output.bHasRuntimeVirtualTextureOutput) outputNode.DisplayProperties.Add(new KeyValuePair<string, string>("Has RVT Output", "true"));
        AppendDisplayProperties(outputNode, material.Properties);
        Nodes.Add(outputNode);

        var layoutRoots = new List<MaterialGraphNode> { outputNode };
        var (referencedTextures, referencedComplete) = ResolveLegacyReferencedTextures(chain);

        // texture binding nodes: the legacy expression arrays are the binding tables, their
        // array order defines the shader's texture register allocation (CreateBufferStruct)
        var textureNodesBySlot = new Dictionary<(int Slot, int Index), MaterialGraphNode>();
        var textureNodesByIndex = new Dictionary<int, MaterialGraphNode>();
        var virtualTextureNodes = new List<MaterialGraphNode>();
        var textureCount = 0;
        void AddTextureBindingNodes(FMaterialUniformExpressionLegacy[] expressions, int slot, string slotName)
        {
            for (var i = 0; i < (expressions?.Length ?? 0); i++)
            {
                var expression = expressions[i];
                var isParameter = !string.IsNullOrEmpty(expression.ParameterName) && expression.ParameterName != "None";
                var textureIndex = expression.TextureIndex;
                var texture = ResolveLegacyTexture(referencedTextures, textureIndex);

                var textureNode = new MaterialGraphNode
                {
                    Name = $"ShaderTexture_{slot}_{textureCount++}",
                    Title = isParameter ? $"Texture Parameter ({slotName})" : $"Texture ({slotName})",
                    Subtitle = isParameter ? expression.ParameterName : texture?.Name ?? $"Index {textureIndex}",
                    ExportType = "PreshaderTextureParameter",
                    IsTexture = true,
                    IsParameter = isParameter
                };
                textureNode.OutputPins.Add(new MaterialGraphPin { Name = "RGB", PinType = "color" });
                textureNode.OutputPins.Add(new MaterialGraphPin { Name = "A", PinType = "float" });
                if (isParameter)
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Parameter Name", expression.ParameterName));
                textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Texture Index", textureIndex.ToString()));
                if (texture != null)
                {
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Texture", texture.Name));
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Compression", texture.CompressionSettings.ToString()));
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("sRGB", texture.SRGB.ToString()));
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("LOD Group", texture.LODGroup.ToString()));
                }
                else if (!referencedComplete && textureIndex >= referencedTextures.Count)
                {
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Texture", $"Index {textureIndex} (list truncated at a material function call)"));
                }
                if (expression.SamplerSource >= 0)
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Sampler Source", ((ESamplerSourceMode) expression.SamplerSource).ToString()));
                if (expression.TextureLayerIndex > 0)
                    textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("VT Layer", expression.TextureLayerIndex.ToString()));
                Nodes.Add(textureNode);

                if (textureIndex >= 0) textureNodesByIndex.TryAdd(textureIndex, textureNode);
                textureNodesBySlot[(slot, i)] = textureNode;
                if (slot == 4) virtualTextureNodes.Add(textureNode);
            }
        }
        // slot ids follow the pixel shader analyzer convention: 0=2D, 1=Cube, 3=Volume, 4=Virtual
        AddTextureBindingNodes(expressionSet.Uniform2DTextureExpressions, 0, "2D");
        AddTextureBindingNodes(expressionSet.UniformCubeTextureExpressions, 1, "Cube");
        AddTextureBindingNodes(expressionSet.UniformVolumeTextureExpressions, 3, "Volume");
        AddTextureBindingNodes(expressionSet.UniformVirtualTextureExpressions, 4, "Virtual Texture");

        // deserialized uniform expression trees: one root per constant buffer value, indices
        // match the constant buffer rows the compiled pixel shader reads
        var expressionNodeCache = new Dictionary<FMaterialUniformExpressionLegacy, MaterialGraphNode>();
        var parameterNodes = new Dictionary<string, MaterialGraphNode>(StringComparer.OrdinalIgnoreCase);
        var uniformRootNodes = new Dictionary<string, MaterialGraphNode>();
        void AddUniformRoots(FMaterialUniformExpressionLegacy[] expressions, string kind)
        {
            for (var i = 0; i < (expressions?.Length ?? 0); i++)
            {
                var root = CreateLegacyExpressionNode(expressions[i], expressionNodeCache, parameterNodes, referencedTextures, textureNodesByIndex);
                var uniformName = $"{kind} [{i}]";
                uniformRootNodes[uniformName] = root;
                root.DisplayProperties.Add(new KeyValuePair<string, string>("Uniform Slot", uniformName));
                if (root.InputPins.Count > 0 && string.IsNullOrEmpty(root.Subtitle))
                    root.Subtitle = $"→ {uniformName}";
                if (!layoutRoots.Contains(root))
                    layoutRoots.Add(root);
            }
        }
        AddUniformRoots(expressionSet.UniformVectorExpressions, "Vector Expression");
        AddUniformRoots(expressionSet.UniformScalarExpressions, "Scalar Expression");

        // virtual texture stacks assembled by the cooker; layer indices point into the
        // virtual texture binding table
        for (var i = 0; i < (expressionSet.VTStacks?.Length ?? 0); i++)
        {
            var stack = expressionSet.VTStacks[i];
            var stackNode = new MaterialGraphNode
            {
                Name = $"VTStack_{i}",
                Title = "Virtual Texture Stack",
                Subtitle = $"{stack.NumLayers} layer{(stack.NumLayers == 1 ? "" : "s")}",
                ExportType = "PreshaderVirtualTextureStack",
                IsTexture = true
            };
            stackNode.OutputPins.Add(new MaterialGraphPin { Name = "Out", PinType = "default" });
            stackNode.DisplayProperties.Add(new KeyValuePair<string, string>("Preallocated Texture Index", stack.PreallocatedStackTextureIndex.ToString()));
            for (var layer = 0; layer < stack.NumLayers && layer < stack.LayerUniformExpressionIndices.Length; layer++)
            {
                var layerExpressionIndex = stack.LayerUniformExpressionIndices[layer];
                stackNode.DisplayProperties.Add(new KeyValuePair<string, string>($"Layer {layer}", $"Uniform expression {layerExpressionIndex}"));
                if (layerExpressionIndex < 0 || layerExpressionIndex >= virtualTextureNodes.Count) continue;
                var layerPin = $"Layer {layer}";
                stackNode.InputPins.Add(new MaterialGraphPin { Name = layerPin, PinType = "color" });
                Connections.Add(new MaterialGraphConnection
                {
                    SourceNode = virtualTextureNodes[layerExpressionIndex], SourcePinName = "RGB",
                    TargetNode = stackNode, TargetPinName = layerPin,
                    PinType = "color"
                });
            }
            Nodes.Add(stackNode);
        }

        // real output wiring from the compiled base-pass pixel shader (D3D SM5)
        PixelShaderWiring wiring = null;
        var wiredPinCount = 0;
        try
        {
            var usesGBuffer = parameters.BlendMode is EBlendMode.BLEND_Opaque or EBlendMode.BLEND_Masked;
            wiring = MaterialPixelShaderAnalyzer.AnalyzeLegacy(shaderMap, usesGBuffer, CreateLegacyShaderCodeResolver(chain));
        }
        catch (Exception e)
        {
            wiring = new PixelShaderWiring { FailureReason = e.Message };
        }
        if (wiring.Success)
            wiredPinCount = ApplyPixelShaderWiring(wiring, outputNode, uniformRootNodes, textureNodesBySlot);

        var uniformCount = (expressionSet.UniformVectorExpressions?.Length ?? 0) + (expressionSet.UniformScalarExpressions?.Length ?? 0);
        var note = new StringBuilder("Reconstructed from the cooked shader map (UE 4.23/4.24 format) — the editor node graph was stripped when this asset was cooked. ");
        note.Append($"{uniformCount} uniform expression tree{(uniformCount == 1 ? "" : "s")} deserialized.");
        note.Append(" All shown connections are real, decoded 1:1 from serialized data.");
        if (wiredPinCount > 0)
        {
            note.Append($" Output wiring recovered from the compiled base-pass pixel shader ({wiring.ShaderTypeName}).");
            if (ExpandPixelShaderMath && CanExpandPixelShaderMath)
                note.Append(" Pixel shader math expanded: one node per decoded instruction.");
        }
        else if (wiring is { Success: true })
            note.Append($" The compiled base-pass pixel shader ({wiring.ShaderTypeName}) was analyzed, but no serialized value reaches an output pin directly — constant inputs get folded into the shader at compile time.");
        else
            note.Append($" Output pins are unwired: pixel shader analysis failed ({wiring.FailureReason}).");
        AppendShaderStageNote(note);
        if (UserGraphOnly)
            note.Append(" Showing the user-authored material graph only — the engine shader scaffolding (other shader stages and uniforms that reach no material output) is hidden.");
        ReconstructionNote = note.ToString();

        ApplyInstanceOverrides(chain);
        FinalizeReconstructedGraph(layoutRoots);
    }

    private static readonly Dictionary<string, string> LegacyOpTitles = new()
    {
        ["Add"] = "Add", ["Sub"] = "Subtract", ["Mul"] = "Multiply", ["Div"] = "Divide",
        ["Dot"] = "Dot Product", ["Cross"] = "Cross Product", ["Min"] = "Min", ["Max"] = "Max",
        ["Clamp"] = "Clamp", ["Saturate"] = "Saturate", ["Floor"] = "Floor", ["Ceil"] = "Ceil",
        ["Round"] = "Round", ["Truncate"] = "Truncate", ["Sign"] = "Sign", ["Frac"] = "Frac",
        ["Fmod"] = "Fmod", ["Abs"] = "Abs", ["Sqrt"] = "Square Root", ["Length"] = "Length",
        ["Log2"] = "Log2", ["Log10"] = "Log10", ["Sin"] = "Sine", ["Cos"] = "Cosine",
        ["Tan"] = "Tangent", ["Asin"] = "Arcsine", ["Acos"] = "Arccosine", ["Atan"] = "Arctangent",
        ["Atan2"] = "Arctangent2", ["Append"] = "Append Vector", ["Swizzle"] = "Component Mask",
        ["TextureProperty"] = "Texture Property"
    };

    /// <summary>
    /// Turns a deserialized legacy uniform expression tree into graph nodes. Shared sub-trees
    /// become shared nodes and parameters are deduplicated by name across all trees, matching
    /// how an authored graph fans a parameter out to every consumer.
    /// </summary>
    private MaterialGraphNode CreateLegacyExpressionNode(FMaterialUniformExpressionLegacy expression,
        Dictionary<FMaterialUniformExpressionLegacy, MaterialGraphNode> cache,
        Dictionary<string, MaterialGraphNode> parameterNodes,
        List<FPackageIndex> referencedTextures,
        Dictionary<int, MaterialGraphNode> textureNodesByIndex)
    {
        if (cache.TryGetValue(expression, out var existing)) return existing;

        var isParameter = !string.IsNullOrEmpty(expression.ParameterName) && expression.ParameterName != "None";
        if (isParameter && parameterNodes.TryGetValue(expression.ParameterName, out var sharedParameter))
        {
            cache[expression] = sharedParameter;
            return sharedParameter;
        }

        // texture-typed operands reuse their binding-table node so the graph keeps one node
        // per bound texture (the indices are shared engine data, not name matching)
        if (expression.TypeName is "FMaterialUniformExpressionTexture" or "FMaterialUniformExpressionTextureParameter" &&
            expression.TextureIndex >= 0 && textureNodesByIndex.TryGetValue(expression.TextureIndex, out var bindingNode))
        {
            cache[expression] = bindingNode;
            return bindingNode;
        }

        var (title, subtitle, pinType) = expression.TypeName switch
        {
            "FMaterialUniformExpressionConstant" => ("Constant", expression.ConstantValue is { } c ? FormatColor(c) : "", "color"),
            "FMaterialUniformExpressionVectorParameter" => ("Vector Parameter", expression.ParameterName ?? "", "color"),
            "FMaterialUniformExpressionScalarParameter" => ("Scalar Parameter", expression.ParameterName ?? "", "float"),
            "FMaterialUniformExpressionTexture" => ("Texture", $"Index {expression.TextureIndex}", "color"),
            "FMaterialUniformExpressionTextureParameter" => ("Texture Parameter", expression.ParameterName ?? "", "color"),
            "FMaterialUniformExpressionRuntimeVirtualTextureParameter" => ("RVT Parameter", $"Index {expression.TextureIndex}", "color"),
            "FMaterialUniformExpressionExternalTexture" or "FMaterialUniformExpressionExternalTextureBase" => ("External Texture", $"Index {expression.TextureIndex}", "color"),
            "FMaterialUniformExpressionExternalTextureParameter" => ("External Texture Parameter", expression.ParameterName ?? "", "color"),
            "FMaterialUniformExpressionExternalTextureCoordinateScaleRotation" => ("External Texture Scale/Rotation", expression.ParameterName ?? "", "color"),
            "FMaterialUniformExpressionExternalTextureCoordinateOffset" => ("External Texture Offset", expression.ParameterName ?? "", "color"),
            _ when expression.OpName != null => (LegacyOpTitles.GetValueOrDefault(expression.OpName, expression.OpName), "", "default"),
            _ => (expression.TypeName.Replace("FMaterialUniformExpression", ""), "", "default")
        };
        if (expression.OpName == "Swizzle")
            subtitle = expression.Values.FirstOrDefault(v => v.Key == "Swizzle").Value ?? subtitle;

        var node = new MaterialGraphNode
        {
            Name = $"LegacyExpr_{Nodes.Count}",
            Title = title,
            Subtitle = subtitle,
            ExportType = $"LegacyUniform{expression.TypeName.Replace("FMaterialUniformExpression", "")}",
            IsParameter = isParameter,
            IsTexture = expression.TextureIndex >= 0
        };
        node.OutputPins.Add(new MaterialGraphPin { Name = "Out", PinType = pinType });
        foreach (var (key, value) in expression.Values)
            node.DisplayProperties.Add(new KeyValuePair<string, string>(key, value));
        if (expression.TextureIndex >= 0 && ResolveLegacyTexture(referencedTextures, expression.TextureIndex) is { } texture)
        {
            node.DisplayProperties.Add(new KeyValuePair<string, string>("Texture", texture.Name));
            if (string.IsNullOrEmpty(node.Subtitle)) node.Subtitle = texture.Name;
        }
        Nodes.Add(node);
        cache[expression] = node;
        if (isParameter)
            parameterNodes[expression.ParameterName] = node;

        foreach (var (slot, operand) in expression.Operands)
        {
            node.InputPins.Add(new MaterialGraphPin { Name = slot, PinType = "default" });
            var source = CreateLegacyExpressionNode(operand, cache, parameterNodes, referencedTextures, textureNodesByIndex);
            Connections.Add(new MaterialGraphConnection
            {
                SourceNode = source, SourcePinName = "Out",
                TargetNode = node, TargetPinName = slot,
                PinType = "default"
            });
        }
        return node;
    }

    private static UTexture ResolveLegacyTexture(List<FPackageIndex> referencedTextures, int index)
    {
        if (index < 0 || index >= referencedTextures.Count) return null;
        try
        {
            return referencedTextures[index].IsNull ? null : referencedTextures[index].Load<UTexture>();
        }
        catch
        {
            return null;
        }
    }

    // UMaterialExpressionTextureBase-derived expression classes in 4.23; these return their
    // Texture property from GetReferencedTexture (CanReferenceTexture == true)
    private static readonly HashSet<string> LegacyTextureBaseExpressionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MaterialExpressionTextureBase",
        "MaterialExpressionTextureObject",
        "MaterialExpressionTextureSample",
        "MaterialExpressionParticleSubUV",
        "MaterialExpressionTextureSampleParameter",
        "MaterialExpressionTextureSampleParameter2D",
        "MaterialExpressionTextureSampleParameterCube",
        "MaterialExpressionTextureSampleParameterVolume",
        "MaterialExpressionTextureSampleParameterSubUV",
        "MaterialExpressionTextureObjectParameter",
        "MaterialExpressionAntialiasedTextureMask"
    };

    /// <summary>
    /// Rebuilds the referenced-texture list a 4.23 game constructs at runtime
    /// (UMaterial::AppendReferencedTextures walks the cooked Expressions array with AddUnique
    /// semantics). Material function calls pull in nested function textures whose exact
    /// traversal cannot be reproduced reliably, so resolution stops there: indices before the
    /// stop point are exact, later ones stay unresolved rather than guessed.
    /// </summary>
    private static (List<FPackageIndex> Textures, bool Complete) ResolveLegacyReferencedTextures(List<UMaterialInterface> chain)
    {
        var textures = new List<FPackageIndex>();
        if (chain[^1] is not UMaterial rootMaterial) return (textures, false);

        void AddUnique(FPackageIndex index)
        {
            if (!textures.Any(t => t.Index == index.Index && Equals(t.Owner, index.Owner)))
                textures.Add(index);
        }

        foreach (var expressionIndex in rootMaterial.Expressions)
        {
            if (!expressionIndex.TryLoad(out UObject expression)) continue;
            var type = expression.ExportType;

            if (LegacyTextureBaseExpressionTypes.Contains(type))
            {
                AddUnique(expression.GetOrDefault("Texture", new FPackageIndex()));
            }
            else if (type.Equals("MaterialExpressionRuntimeVirtualTextureSample", StringComparison.OrdinalIgnoreCase) ||
                     type.Equals("MaterialExpressionRuntimeVirtualTextureSampleParameter", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(expression.GetOrDefault("VirtualTexture", new FPackageIndex()));
            }
            else if (type.Equals("MaterialExpressionCurveAtlasRowParameter", StringComparison.OrdinalIgnoreCase))
            {
                AddUnique(expression.GetOrDefault("Atlas", new FPackageIndex()));
            }
            else if (type.Equals("MaterialExpressionMaterialFunctionCall", StringComparison.OrdinalIgnoreCase) ||
                     type.Equals("MaterialExpressionMaterialAttributeLayers", StringComparison.OrdinalIgnoreCase) ||
                     type.StartsWith("MaterialExpressionFontSample", StringComparison.OrdinalIgnoreCase))
            {
                // nested function / font traversal order is engine-internal; stop resolving
                return (textures, false);
            }
        }

        return (textures, true);
    }

    #endregion

    #region Material details & shader inventory

    /// <summary>
    /// Fills <see cref="MaterialSections"/> with everything deserialized from the material:
    /// every property of every material in the instance chain, the shader map header, the
    /// complete uniform expression trees and the referenced texture list.
    /// </summary>
    private void BuildMaterialDetails(List<UMaterialInterface> chain)
    {
        foreach (var material in chain)
        {
            var section = new MaterialInfoSection { Title = $"{material.ExportType} — {material.Name}" };
            foreach (var tag in material.Properties)
                section.Add(tag.Name.Text, FormatTagValueFull(tag.Tag));
            if (section.Rows.Count == 0)
                section.Add("Properties", "none serialized (all defaults)");
            MaterialSections.Add(section);
        }

        var shaderMap = FindLegacyShaderMap(chain, out var owner);
        if (shaderMap == null) return;

        var mapSection = new MaterialInfoSection { Title = "Shader Map" };
        mapSection.Add("Friendly Name", shaderMap.FriendlyName ?? string.Empty);
        if (owner != null && !ReferenceEquals(owner, chain[0]))
            mapSection.Add("Cooked Into", owner.Name);
        mapSection.Add("Shader Platform", shaderMap.ShaderPlatform.ToString());
        if (shaderMap.ShaderMapId is { } id)
        {
            mapSection.Add("Quality Level", id.QualityLevel.ToString());
            mapSection.Add("Feature Level", id.FeatureLevel.ToString());
            mapSection.Add("Shader Map Id Hash", id.CookedShaderMapIdHash.ToString());
        }
        if (!string.IsNullOrEmpty(shaderMap.DebugDescription))
            mapSection.Add("Debug Description", shaderMap.DebugDescription);
        mapSection.Add("Material Shaders", (shaderMap.Shaders?.Length ?? 0).ToString());
        foreach (var meshMap in shaderMap.MeshShaderMaps ?? [])
            mapSection.Add($"Mesh Shaders — {meshMap.VertexFactoryTypeName}", (meshMap.Shaders?.Length ?? 0).ToString());
        if (shaderMap.MaterialCompilationOutput is { } output)
        {
            if (shaderMap.ParsedProfile == ELegacyShaderMapProfile.PreVirtualTexture)
            {
                // this branch's compilation-output tail is skipped unparsed, so the flag
                // fields hold defaults — showing them would present guesses as data
                mapSection.Add("Compilation Flags", "not decoded on this engine branch (pre-VT tail layout is not public)");
            }
            else
            {
                mapSection.Add("Used Scene Textures", $"0x{output.UsedSceneTextures:X8}");
                mapSection.Add("Uses Eye Adaptation", output.bUsesEyeAdaptation.ToString());
                mapSection.Add("Modifies Mesh Position", output.bModifiesMeshPosition.ToString());
                mapSection.Add("Uses World Position Offset", output.bUsesWorldPositionOffset.ToString());
                mapSection.Add("Uses Global Distance Field", output.bUsesGlobalDistanceField.ToString());
                mapSection.Add("Uses Pixel Depth Offset", output.bUsesPixelDepthOffset.ToString());
                mapSection.Add("Uses Distance Cull Fade", output.bUsesDistanceCullFade.ToString());
                mapSection.Add("Has Runtime VT Output", output.bHasRuntimeVirtualTextureOutput.ToString());
            }
        }
        MaterialSections.Add(mapSection);

        if (shaderMap.MaterialCompilationOutput?.UniformExpressionSet is { } expressionSet)
        {
            var exprSection = new MaterialInfoSection { Title = "Uniform Expressions (compiled value trees)" };
            AddExpressionRows(exprSection, "Vector", expressionSet.UniformVectorExpressions);
            AddExpressionRows(exprSection, "Scalar", expressionSet.UniformScalarExpressions);
            AddExpressionRows(exprSection, "2D Texture", expressionSet.Uniform2DTextureExpressions);
            AddExpressionRows(exprSection, "Cube Texture", expressionSet.UniformCubeTextureExpressions);
            AddExpressionRows(exprSection, "Volume Texture", expressionSet.UniformVolumeTextureExpressions);
            AddExpressionRows(exprSection, "Virtual Texture", expressionSet.UniformVirtualTextureExpressions);
            AddExpressionRows(exprSection, "External Texture", expressionSet.UniformExternalTextureExpressions);
            if (expressionSet.ParameterCollections is { Length: > 0 } collections)
                for (var i = 0; i < collections.Length; i++)
                    exprSection.Add($"Parameter Collection [{i}]", collections[i].ToString());
            if (exprSection.Rows.Count == 0)
                exprSection.Add("Expressions", "none (material has no uniform inputs)");
            MaterialSections.Add(exprSection);
        }

        var (textures, complete) = ResolveLegacyReferencedTextures(chain);
        if (textures.Count > 0 || !complete)
        {
            var textureSection = new MaterialInfoSection { Title = "Referenced Textures (shader map order)" };
            for (var i = 0; i < textures.Count; i++)
                textureSection.Add($"[{i}]", textures[i].Name);
            if (!complete)
                textureSection.Add("Note", "list may be incomplete — the material uses function calls or font samples whose traversal order is engine-internal");
            MaterialSections.Add(textureSection);
        }
    }

    private static void AddExpressionRows(MaterialInfoSection section, string kind, FMaterialUniformExpressionLegacy[] expressions)
    {
        for (var i = 0; i < (expressions?.Length ?? 0); i++)
        {
            var builder = new StringBuilder();
            AppendLegacyExpressionTree(builder, string.Empty, expressions[i], 0);
            section.Add($"{kind} [{i}]", builder.ToString().TrimEnd('\n'));
        }
    }

    private static void AppendLegacyExpressionTree(StringBuilder builder, string slot, FMaterialUniformExpressionLegacy expression, int depth)
    {
        builder.Append(new string(' ', depth * 2));
        if (!string.IsNullOrEmpty(slot))
            builder.Append(slot).Append(": ");
        builder.Append(expression.OpName ?? expression.TypeName.Replace("FMaterialUniformExpression", ""));
        if (expression.Values.Count > 0)
            builder.Append(" (").Append(string.Join(", ", expression.Values.Select(v => $"{v.Key}={v.Value}"))).Append(')');
        builder.Append('\n');
        foreach (var (childSlot, child) in expression.Operands)
            AppendLegacyExpressionTree(builder, childSlot, child, depth + 1);
    }

    /// <summary>
    /// Fills <see cref="Shaders"/> with every shader of the legacy shader map — material
    /// shaders and each vertex factory's mesh shaders — wiring up lazy full disassembly.
    /// </summary>
    private void BuildShaderInventory(List<UMaterialInterface> chain)
    {
        var shaderMap = FindLegacyShaderMap(chain, out _);
        if (shaderMap == null) { BuildModernShaderInventory(chain); return; }
        var expressionSet = shaderMap.MaterialCompilationOutput?.UniformExpressionSet;
        var resolver = CreateLegacyShaderCodeResolver(chain);

        void AddShaders(FShaderLegacy[] shaders, string vertexFactory)
        {
            foreach (var shader in shaders ?? [])
            {
                if (shader == null) continue;
                var resource = shader.Resource;
                var inline = resource?.Code is { Length: > 0 };
                var codeInfo = resource == null
                    ? "no shader resource"
                    : (inline ? $"{resource.Code.Length:N0} bytes inline" : "bytecode in shared shader library") +
                      $", {resource.NumInstructions} instructions";

                Shaders.Add(new MaterialShaderEntry(() =>
                {
                    if (resource == null)
                        return "// shader carries no FShaderResource";
                    var blob = resource.Code is { Length: > 0 } code ? code : resolver?.Invoke(resource.OutputHash);
                    if (blob is not { Length: > 0 })
                        return resolver == null
                            ? "// compiled bytecode lives in the shared shader library (.ushaderbytecode), which was not found in this game"
                            : "// bytecode not found in the shared shader library";
                    return MaterialPixelShaderAnalyzer.DisassembleLegacyShader(shader, blob, expressionSet);
                })
                {
                    TypeName = shader.TypeName,
                    Frequency = ShaderFrequencyName(shader.Target.Frequency),
                    VertexFactory = string.IsNullOrEmpty(vertexFactory) ? shader.VertexFactoryTypeName : vertexFactory,
                    OutputHash = resource?.OutputHash.ToString() ?? string.Empty,
                    CodeInfo = codeInfo
                });
            }
        }

        AddShaders(shaderMap.Shaders, string.Empty);
        foreach (var meshMap in shaderMap.MeshShaderMaps ?? [])
            AddShaders(meshMap.Shaders, meshMap.VertexFactoryTypeName);
    }

    /// <summary>
    /// UE5 shader inventory. The modern (IoStore) shader map stores its shaders as <see cref="FShader"/>
    /// with hashed type names and bytecode in the shared shader library, so the legacy path finds
    /// nothing. Each shader's SM6 DXIL bytecode is pulled from the library on demand and summarized.
    /// </summary>
    private void BuildModernShaderInventory(List<UMaterialInterface> chain)
    {
        UMaterialInterface owner = null;
        FMaterialShaderMap map = null;
        foreach (var material in chain)
        {
            map = material.LoadedMaterialResources?.FirstOrDefault(r => r.LoadedShaderMap != null)?.LoadedShaderMap;
            if (map == null) continue;
            owner = material;
            break;
        }
        if (map?.Content is not FMaterialShaderMapContent content) return;
        var provider = owner.Owner?.Provider as IVfsFileProvider;
        // pre-UE5 games also use the modern shader map, but their bytecode is SM5 DXBC, not SM6 DXIL —
        // only UE5 shaders live in the IoStore library and decode with the DXIL summarizer below
        if ((provider?.Versions.Game ?? EGame.GAME_UE4_LATEST) < EGame.GAME_UE5_0) return;
        var resourceHash = map.ResourceHash;

        static string ResolveName(FHashedName h) =>
            HashedNamesProvider.TryGetEntry(h.Hash, out var n) && !string.IsNullOrEmpty(n) ? n : h.Hash.ToString("X16");
        // a raw hash reads as noise in the vertex-factory column, so leave it blank when unresolved
        static string ResolveNameOrEmpty(FHashedName h) =>
            HashedNamesProvider.TryGetEntry(h.Hash, out var n) && !string.IsNullOrEmpty(n) ? n : string.Empty;

        void AddShaders(FShader[] shaders, string vertexFactory)
        {
            foreach (var shader in shaders ?? [])
            {
                if (shader == null) continue;
                var resIdx = shader.ResourceIndex;
                Shaders.Add(new MaterialShaderEntry(() =>
                {
                    if (provider == null || resourceHash is not { } rh)
                        return "// no IoStore provider or shader-map resource hash to retrieve bytecode";
                    var blob = MaterialDxilLibrary.TryGetShaderCode(provider, rh, resIdx, out var err);
                    if (blob is not { Length: > 0 })
                        return $"// {err ?? "bytecode not found in the shared shader library"}";
                    try { return MaterialDxilLibrary.Describe(blob); }
                    catch (Exception e) { return $"// DXIL summary failed: {e.Message}"; }
                })
                {
                    TypeName = ResolveName(shader.Type),
                    Frequency = shader.Target.Frequency.ToString().Replace("SF_", ""),
                    VertexFactory = string.IsNullOrEmpty(vertexFactory) ? ResolveNameOrEmpty(shader.VFType) : vertexFactory,
                    OutputHash = string.Empty,
                    CodeInfo = $"SM6 DXIL in shared shader library, {shader.NumInstructions} instructions"
                });
            }
        }

        AddShaders(content.Shaders, string.Empty);
        foreach (var meshMap in content.OrderedMeshShaderMaps ?? [])
            AddShaders(meshMap.Shaders, ResolveNameOrEmpty(meshMap.VertexFactoryTypeName));
    }

    /// <summary>EShaderFrequency names per 4.23 RHIDefinitions.h.</summary>
    private static string ShaderFrequencyName(uint frequency) => frequency switch
    {
        0 => "Vertex",
        1 => "Hull",
        2 => "Domain",
        3 => "Pixel",
        4 => "Geometry",
        5 => "Compute",
        _ => $"Frequency {frequency}"
    };

    /// <summary>
    /// Like <see cref="FormatTagValue"/> but expands arrays and struct fallbacks in full
    /// instead of collapsing them — the details panel is meant to show everything.
    /// </summary>
    private static string FormatTagValueFull(FPropertyTagType tag)
    {
        return tag?.GenericValue switch
        {
            UScriptArray array => FormatArrayFull(array),
            FScriptStruct { StructType: FStructFallback fallback } => FormatStructFull(fallback),
            _ => FormatTagValue(tag)
        };
    }

    private static string FormatArrayFull(UScriptArray array)
    {
        if (array.Properties.Count == 0) return "[empty]";
        var items = array.Properties.Select(FormatTagValueFull).ToList();
        if (items.Count <= 4 && items.All(i => i.Length <= 48 && !i.Contains('\n')))
            return $"[{string.Join(", ", items)}]";

        var builder = new StringBuilder();
        for (var i = 0; i < items.Count; i++)
            builder.Append('[').Append(i).Append("] ").Append(items[i].Replace("\n", "\n    ")).Append('\n');
        return builder.ToString().TrimEnd('\n');
    }

    private static string FormatStructFull(FStructFallback fallback)
    {
        if (fallback.Properties.Count == 0) return "{}";
        var parts = fallback.Properties.Select(p => $"{p.Name.Text}={FormatTagValueFull(p.Tag)}").ToList();
        var oneLine = $"{{{string.Join(", ", parts)}}}";
        if (oneLine.Length <= 160 && !oneLine.Contains('\n'))
            return oneLine;
        return string.Join("\n", parts.Select(p => p.Replace("\n", "\n  ")));
    }

    #endregion

    #region Reconstructed parameter graph

    private void BuildReconstructedGraph(List<UMaterialInterface> chain)
    {
        var material = chain[0];
        var parameters = new CMaterialParams2();

        // apply root first so instance overrides land on top
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            try
            {
                chain[i].GetParams(parameters, EMaterialFormat.AllLayers);
            }
            catch
            {
                // a partially readable material should not prevent the graph from showing
            }
        }

        var outputNode = new MaterialGraphNode
        {
            Name = material.Name,
            Title = material.Name,
            Subtitle = "Material Output (reconstructed)",
            ExportType = material.ExportType,
            IsOutputNode = true
        };
        AddStandardOutputPins(outputNode, material, parameters);
        AppendDisplayProperties(outputNode, material.Properties);
        outputNode.DisplayProperties.Insert(0, new KeyValuePair<string, string>("Blend Mode", parameters.BlendMode.ToString()));
        outputNode.DisplayProperties.Insert(1, new KeyValuePair<string, string>("Shading Model", parameters.ShadingModel.ToString()));
        Nodes.Add(outputNode);

        // parent chain, root-most on the far left
        MaterialGraphNode previousParent = null;
        for (var i = chain.Count - 1; i >= 1; i--)
        {
            var parent = chain[i];
            var parentNode = new MaterialGraphNode
            {
                Name = parent.Name,
                Title = parent.ExportType,
                Subtitle = parent.Name,
                ExportType = parent.ExportType,
                NodePosX = -((i + 1) * AutoLayoutColumnSpacing),
                NodePosY = -220
            };
            parentNode.OutputPins.Add(new MaterialGraphPin { Name = "Out", PinType = "attributes" });
            if (previousParent != null)
            {
                parentNode.InputPins.Add(new MaterialGraphPin { Name = "Parent", PinType = "attributes" });
                Connections.Add(new MaterialGraphConnection
                {
                    SourceNode = previousParent, SourcePinName = "Out",
                    TargetNode = parentNode, TargetPinName = "Parent",
                    PinType = "attributes"
                });
            }
            AppendDisplayProperties(parentNode, parent.Properties);
            Nodes.Add(parentNode);
            previousParent = parentNode;
        }

        if (previousParent != null)
        {
            outputNode.InputPins.Insert(0, new MaterialGraphPin { Name = "Parent", PinType = "attributes" });
            Connections.Add(new MaterialGraphConnection
            {
                SourceNode = previousParent, SourcePinName = "Out",
                TargetNode = outputNode, TargetPinName = "Parent",
                PinType = "attributes"
            });
        }

        // texture samples, connected to their most likely material attribute pin
        var row = 0;
        foreach (var (name, texture) in parameters.Textures)
        {
            if (texture is not UTexture tex) continue;

            var textureNode = new MaterialGraphNode
            {
                Name = $"Texture_{name}",
                Title = "Texture Sample",
                Subtitle = name,
                ExportType = "MaterialExpressionTextureSampleParameter2D",
                IsTexture = true,
                IsParameter = true,
                NodePosX = -AutoLayoutColumnSpacing * 2,
                NodePosY = row * 170
            };
            textureNode.InputPins.Add(new MaterialGraphPin { Name = "UVs", PinType = "vector2" });
            textureNode.OutputPins.Add(new MaterialGraphPin { Name = "RGB", PinType = "color" });
            textureNode.OutputPins.Add(new MaterialGraphPin { Name = "A", PinType = "float" });
            textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Parameter Name", name));
            textureNode.DisplayProperties.Add(new KeyValuePair<string, string>("Texture", tex.GetPathName()));
            Nodes.Add(textureNode);
            row++;

            var targetPin = GuessTexturePin(name, tex.Name, outputNode);
            if (targetPin == null) continue;
            Connections.Add(new MaterialGraphConnection
            {
                SourceNode = textureNode, SourcePinName = "RGB",
                TargetNode = outputNode, TargetPinName = targetPin,
                PinType = "color", IsInferred = true
            });
        }

        // scalar / vector / switch parameters, informational (their wiring is not recoverable)
        var parameterRow = 0;
        foreach (var (name, value) in parameters.Scalars)
            AddLooseParameterNode("Scalar Parameter", name, value.ToString("0.####"), "float", ref parameterRow);
        foreach (var (name, value) in parameters.Colors)
            AddLooseParameterNode("Vector Parameter", name, FormatColor(value), "color", ref parameterRow);
        foreach (var (name, value) in parameters.Switches)
            AddLooseParameterNode("Static Switch", name, value.ToString(), "bool", ref parameterRow);

        if (Nodes.Count > 1)
            outputNode.NodePosY = Nodes.Where(n => !n.IsOutputNode).Average(n => n.NodePosY);
    }

    private MaterialGraphNode AddLooseParameterNode(string title, string name, string value, string pinType, ref int row)
    {
        var node = new MaterialGraphNode
        {
            Name = $"{title}_{name}_{row}",
            Title = title,
            Subtitle = $"{name} = {value}",
            ExportType = $"MaterialExpression{title.Replace(" ", "")}",
            IsParameter = true,
            NodePosX = -AutoLayoutColumnSpacing * 3.5,
            NodePosY = row * 110
        };
        node.OutputPins.Add(new MaterialGraphPin { Name = "Out", PinType = pinType });
        node.DisplayProperties.Add(new KeyValuePair<string, string>("Parameter Name", name));
        node.DisplayProperties.Add(new KeyValuePair<string, string>("Value", value));
        Nodes.Add(node);
        row++;
        return node;
    }

    private static string GuessTexturePin(string parameterName, string textureName, MaterialGraphNode outputNode)
    {
        string Match(string candidate) =>
            outputNode.InputPins.FirstOrDefault(p => p.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))?.Name;

        foreach (var name in new[] { parameterName, textureName })
        {
            if (Regex.IsMatch(name, CMaterialParams2.RegexDiffuse, RegexOptions.IgnoreCase))
                return Match("Base Color");
            if (Regex.IsMatch(name, CMaterialParams2.RegexNormals, RegexOptions.IgnoreCase))
                return Match("Normal");
            if (Regex.IsMatch(name, CMaterialParams2.RegexEmissive, RegexOptions.IgnoreCase))
                return Match("Emissive Color");
            if (Regex.IsMatch(name, CMaterialParams2.RegexSpecularMasks, RegexOptions.IgnoreCase))
                return Match("Specular");
        }

        return null;
    }

    #endregion

    #region Shared helpers

    private void AddStandardOutputPins(MaterialGraphNode outputNode, UMaterialInterface material, CMaterialParams2 parameters = null)
    {
        var blendMode = parameters?.BlendMode
                        ?? (material as UMaterial)?.BlendMode
                        ?? EBlendMode.BLEND_Opaque;

        outputNode.InputPins.Add(new MaterialGraphPin { Name = "Base Color", PinType = "color" });
        outputNode.InputPins.Add(new MaterialGraphPin { Name = "Metallic", PinType = "float" });
        outputNode.InputPins.Add(new MaterialGraphPin { Name = "Specular", PinType = "float" });
        outputNode.InputPins.Add(new MaterialGraphPin { Name = "Roughness", PinType = "float" });
        outputNode.InputPins.Add(new MaterialGraphPin { Name = "Emissive Color", PinType = "color" });
        if (blendMode is EBlendMode.BLEND_Translucent or EBlendMode.BLEND_Additive or EBlendMode.BLEND_Modulate or EBlendMode.BLEND_AlphaComposite or EBlendMode.BLEND_AlphaHoldout)
            outputNode.InputPins.Add(new MaterialGraphPin { Name = "Opacity", PinType = "float" });
        if (blendMode is EBlendMode.BLEND_Masked)
            outputNode.InputPins.Add(new MaterialGraphPin { Name = "Opacity Mask", PinType = "float" });
        outputNode.InputPins.Add(new MaterialGraphPin { Name = "Normal", PinType = "vector" });
        outputNode.InputPins.Add(new MaterialGraphPin { Name = "Ambient Occlusion", PinType = "float" });
    }

    /// <summary>
    /// Layered auto layout used when the asset kept no editor coordinates:
    /// nodes are placed in columns by their longest distance to the output node.
    /// </summary>
    private void AutoLayout(List<MaterialGraphNode> roots)
    {
        var depths = new Dictionary<MaterialGraphNode, int>();
        var incoming = Connections.ToLookup(c => c.TargetNode);

        var queue = new Queue<MaterialGraphNode>();
        foreach (var root in roots)
        {
            depths[root] = 0;
            queue.Enqueue(root);
        }
        var iterations = 0;
        while (queue.Count > 0 && iterations++ < Nodes.Count * Connections.Count + Nodes.Count)
        {
            var current = queue.Dequeue();
            var nextDepth = depths[current] + 1;
            foreach (var connection in incoming[current])
            {
                var source = connection.SourceNode;
                if (depths.TryGetValue(source, out var existing) && existing >= nextDepth) continue;
                depths[source] = nextDepth;
                queue.Enqueue(source);
            }
        }

        // unattached nodes go to the far-left column
        var maxDepth = depths.Count > 0 ? depths.Values.Max() : 0;
        foreach (var node in Nodes)
        {
            if (!depths.ContainsKey(node))
                depths[node] = maxDepth + 1;
        }

        var columns = depths
            .GroupBy(kv => kv.Value)
            .OrderBy(g => g.Key)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToList());
        var columnKeys = columns.Keys.ToList();

        // neighbor map across columns, used to pull connected nodes next to each other
        var neighbors = new Dictionary<MaterialGraphNode, List<MaterialGraphNode>>();
        foreach (var connection in Connections)
        {
            if (!neighbors.TryGetValue(connection.SourceNode, out var sourceLinks))
                neighbors[connection.SourceNode] = sourceLinks = [];
            sourceLinks.Add(connection.TargetNode);
            if (!neighbors.TryGetValue(connection.TargetNode, out var targetLinks))
                neighbors[connection.TargetNode] = targetLinks = [];
            targetLinks.Add(connection.SourceNode);
        }

        // normalized position of each node within its column, refined below
        var order = new Dictionary<MaterialGraphNode, double>();
        foreach (var column in columns.Values)
        {
            for (var i = 0; i < column.Count; i++)
                order[column[i]] = column.Count > 1 ? (double) i / (column.Count - 1) : 0.5;
        }

        double Barycenter(MaterialGraphNode node) =>
            neighbors.TryGetValue(node, out var linked) && linked.Count > 0
                ? linked.Average(n => order[n])
                : order[node];

        // a few alternating barycenter sweeps untangle most edge crossings
        for (var sweep = 0; sweep < 4; sweep++)
        {
            foreach (var key in sweep % 2 == 0 ? columnKeys : Enumerable.Reverse(columnKeys))
            {
                var sorted = columns[key].OrderBy(Barycenter).ToList();
                columns[key] = sorted;
                for (var i = 0; i < sorted.Count; i++)
                    order[sorted[i]] = sorted.Count > 1 ? (double) i / (sorted.Count - 1) : 0.5;
            }
        }

        static double NodeHeight(MaterialGraphNode node)
        {
            var pinRows = Math.Max(Math.Max(node.InputPins.Count, node.OutputPins.Count), 1);
            return NodeApproxHeaderHeight + pinRows * NodeApproxPinRowHeight + AutoLayoutRowSpacing;
        }

        var orderedColumns = columns.OrderBy(c => c.Key).Select(c => c.Value).ToList();

        // ---- cluster each node by which material-output pin's subgraph it feeds, so every
        // output/stage becomes its own spatial group instead of one interleaved wall ----
        var outputNode = Nodes.FirstOrDefault(n => n.IsOutputNode);
        var clusterOf = new Dictionary<MaterialGraphNode, string>();
        var pinOrder = new Dictionary<string, int>();
        const string sharedCluster = "«shared»";
        const string looseCluster = "«unattached»";
        if (outputNode != null)
        {
            for (var i = 0; i < outputNode.InputPins.Count; i++)
                pinOrder[outputNode.InputPins[i].Name] = i;

            // walk backwards from each output pin's feeders; a node reached from exactly one pin
            // belongs to that pin's group, one shared by several pins goes to a shared band
            var incomingByTarget = Connections.ToLookup(c => c.TargetNode);
            var reach = new Dictionary<MaterialGraphNode, HashSet<string>>();
            foreach (var seed in Connections.Where(c => c.TargetNode == outputNode))
            {
                if (seed.SourceNode == outputNode) continue;
                var stack = new Stack<MaterialGraphNode>();
                stack.Push(seed.SourceNode);
                var seen = new HashSet<MaterialGraphNode>();
                while (stack.Count > 0)
                {
                    var node = stack.Pop();
                    if (node == outputNode || !seen.Add(node)) continue;
                    if (!reach.TryGetValue(node, out var pins)) reach[node] = pins = [];
                    pins.Add(seed.TargetPinName);
                    foreach (var conn in incomingByTarget[node])
                        if (conn.SourceNode != outputNode) stack.Push(conn.SourceNode);
                }
            }
            foreach (var node in Nodes)
            {
                if (node == outputNode) continue;
                clusterOf[node] = reach.TryGetValue(node, out var pins)
                    ? pins.Count == 1 ? pins.First() : sharedCluster
                    : looseCluster;
            }
        }
        else
        {
            foreach (var node in Nodes) clusterOf[node] = string.Empty;
        }

        double ClusterRank(string cluster) => cluster switch
        {
            sharedCluster => 1e6,
            looseCluster => 1e7,
            _ => pinOrder.TryGetValue(cluster, out var i) ? i : 5e5
        };

        var center = new Dictionary<MaterialGraphNode, double>();

        // Isotonic (pool-adjacent-violators) placement within a band: put each node as close as
        // possible to the average center of its SAME-BAND neighbors, keeping the barycenter order
        // and never overlapping. Restricting to same-band neighbors keeps each group compact.
        void PlaceColumn(List<MaterialGraphNode> column)
        {
            var n = column.Count;
            if (n == 0) return;

            var desired = new double[n];
            for (var i = 0; i < n; i++)
            {
                var node = column[i];
                var cluster = clusterOf[node];
                double sum = 0;
                var count = 0;
                if (neighbors.TryGetValue(node, out var links))
                    foreach (var m in links)
                        if (clusterOf.TryGetValue(m, out var cm) && cm == cluster && center.TryGetValue(m, out var cy))
                        {
                            sum += cy;
                            count++;
                        }
                desired[i] = count > 0 ? sum / count : center[node];
            }

            var offset = new double[n];
            for (var i = 1; i < n; i++)
                offset[i] = offset[i - 1] + (NodeHeight(column[i - 1]) + NodeHeight(column[i])) / 2;

            var blockSum = new List<double>();
            var blockCount = new List<int>();
            for (var i = 0; i < n; i++)
            {
                var sum = desired[i] - offset[i];
                var count = 1;
                while (blockCount.Count > 0 && blockSum[^1] / blockCount[^1] > sum / count)
                {
                    sum += blockSum[^1];
                    count += blockCount[^1];
                    blockSum.RemoveAt(blockSum.Count - 1);
                    blockCount.RemoveAt(blockCount.Count - 1);
                }
                blockSum.Add(sum);
                blockCount.Add(count);
            }

            var index = 0;
            foreach (var (sum, count) in blockSum.Zip(blockCount))
            {
                var mean = sum / count;
                for (var k = 0; k < count; k++, index++)
                    center[column[index]] = mean + offset[index];
            }
        }

        // lay each cluster out as its own vertical band, stacked with a gap between groups
        var bandTop = 0.0;
        const double clusterGap = 220;
        foreach (var band in clusterOf.Keys.GroupBy(n => clusterOf[n]).OrderBy(g => ClusterRank(g.Key)))
        {
            var bandNodes = band.ToHashSet();
            var bandColumns = orderedColumns
                .Select(col => col.Where(bandNodes.Contains).ToList())
                .Where(col => col.Count > 0)
                .ToList();
            if (bandColumns.Count == 0) continue;

            // seed each column stacked from 0, then relax
            foreach (var col in bandColumns)
            {
                var y = 0.0;
                foreach (var node in col)
                {
                    center[node] = y + NodeHeight(node) / 2;
                    y += NodeHeight(node);
                }
            }
            var sweeps = bandNodes.Count > 120 ? 24 : 12;
            for (var iter = 0; iter < sweeps; iter++)
                foreach (var col in iter % 2 == 0 ? bandColumns : Enumerable.Reverse(bandColumns).ToList())
                    PlaceColumn(col);

            var minC = bandNodes.Min(n => center[n] - NodeHeight(n) / 2);
            var maxC = bandNodes.Max(n => center[n] + NodeHeight(n) / 2);
            var shift = bandTop - minC;
            foreach (var node in bandNodes) center[node] += shift;
            bandTop = maxC + shift + clusterGap;
        }

        // the output node spans every band, so centre it on the whole stack
        if (outputNode != null)
            center[outputNode] = (bandTop - clusterGap) / 2;

        foreach (var (depth, column) in columns)
            foreach (var node in column)
            {
                node.NodePosX = -depth * AutoLayoutColumnSpacing;
                node.NodePosY = (center.TryGetValue(node, out var cy) ? cy : 0) - NodeHeight(node) / 2;
            }
    }

    private static string CleanExpressionTitle(string exportType)
    {
        var title = exportType;
        if (title.StartsWith("MaterialExpression", StringComparison.Ordinal))
            title = title["MaterialExpression".Length..];
        if (title.Length == 0)
            return exportType;

        // "TextureSampleParameter2D" -> "Texture Sample Parameter 2D"
        return Regex.Replace(title, "(?<=[a-z0-9])(?=[A-Z])|(?<=[a-zA-Z])(?=[0-9])", " ");
    }

    private static string GetPinTypeFromStructName(string structType)
    {
        return structType switch
        {
            "ColorMaterialInput" => "color",
            "ScalarMaterialInput" => "float",
            "VectorMaterialInput" => "vector",
            "Vector2MaterialInput" => "vector2",
            "MaterialAttributesInput" => "attributes",
            "ShadingModelMaterialInput" => "shadingmodel",
            _ => "default"
        };
    }

    private static void AppendDisplayProperties(MaterialGraphNode node, IEnumerable<FPropertyTag> tags)
    {
        foreach (var tag in tags)
        {
            var value = FormatTagValue(tag.Tag);
            if (value.Length > MaxPropertyValueDisplayLength)
                value = value[..MaxPropertyValueDisplayLength] + "…";
            node.DisplayProperties.Add(new KeyValuePair<string, string>(tag.Name.Text, value));
        }
    }

    private static string FormatTagValue(FPropertyTagType tag)
    {
        return tag?.GenericValue switch
        {
            null => "None",
            bool b => b.ToString(),
            float f => f.ToString("0.####"),
            double d => d.ToString("0.####"),
            FName name => name.Text,
            FText text => text.Text,
            FPackageIndex index => index.Name,
            FScriptStruct { StructType: var structType } => structType switch
            {
                FLinearColor color => FormatColor(color),
                FColor color => $"(R={color.R}, G={color.G}, B={color.B}, A={color.A})",
                FVector vector => $"(X={vector.X:0.###}, Y={vector.Y:0.###}, Z={vector.Z:0.###})",
                FVector2D vector => $"(X={vector.X:0.###}, Y={vector.Y:0.###})",
                FVector4 vector => $"(X={vector.X:0.###}, Y={vector.Y:0.###}, Z={vector.Z:0.###}, W={vector.W:0.###})",
                FGuid guid => guid.ToString(),
                FExpressionInput input => FormatExpressionInput(input),
                FStructFallback fallback => FormatStructFallback(fallback),
                _ => structType?.ToString() ?? "None"
            },
            UScriptArray array => $"[{array.Properties.Count} item{(array.Properties.Count == 1 ? "" : "s")}]",
            { } value => value.ToString() ?? "None"
        };
    }

    private static string FormatExpressionInput(FExpressionInput input)
    {
        if (input.FallbackStruct is { } legacy)
            input = new FExpressionInput(legacy);

        var source = input.Expression is { IsNull: false } expr ? expr.Name : input.ExpressionName.Text;
        if (string.IsNullOrEmpty(source) || source == "None")
            return "not connected";

        var shortName = source.Contains('.') ? source[(source.LastIndexOf('.') + 1)..] : source;
        return input.Mask != 0
            ? $"← {shortName} [{(input.MaskR != 0 ? "R" : "")}{(input.MaskG != 0 ? "G" : "")}{(input.MaskB != 0 ? "B" : "")}{(input.MaskA != 0 ? "A" : "")}]"
            : $"← {shortName}";
    }

    private static string FormatStructFallback(FStructFallback fallback)
    {
        var parts = fallback.Properties.Take(4)
            .Select(p => $"{p.Name.Text}={FormatTagValue(p.Tag)}");
        var suffix = fallback.Properties.Count > 4 ? ", …" : "";
        return $"{{{string.Join(", ", parts)}{suffix}}}";
    }

    private static string FormatColor(FLinearColor color) =>
        $"(R={color.R:0.###}, G={color.G:0.###}, B={color.B:0.###}, A={color.A:0.###})";

    #endregion
}

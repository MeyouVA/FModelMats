using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FModel.ViewModels;

// Porting a reconstructed material back into Unreal.
//
// The Material Graph Viewer recovers a real dataflow graph — nodes, the pins they read, and the
// material outputs they reach — from serialized uniform expressions and compiled shader bytecode.
// This turns that graph into a script the Unreal editor runs to rebuild it as a UMaterial, using
// UMaterialEditingLibrary (Engine/Source/Editor/MaterialEditor/Public/MaterialEditingLibrary.h):
// CreateMaterialExpression / ConnectMaterialExpressions / ConnectMaterialProperty.
//
// Every operation is mapped to the Unreal expression class that does exactly the same thing, and
// each mapping is the class whose Compile() emits that operation. Where Unreal has no node with the
// same semantics, the operation becomes a MaterialExpressionCustom carrying the actual HLSL rather
// than being approximated by a node that would compute something else, and the difference is
// reported. Nothing is invented: an operation with no honest mapping is listed, not guessed at.

/// <summary>What a port produced: the script, and an account of how faithful it is.</summary>
public sealed class MaterialPortResult
{
    public string Script = string.Empty;
    public string MaterialName = string.Empty;
    public bool IsInstancePort;
    public int ExactNodes;
    public int CustomNodes;
    public int SkippedNodes;
    public int PortedConnections;
    public int OutputsConnected;
    public readonly List<string> Notes = [];
    public readonly List<KeyValuePair<string, string>> Summary = [];
}

public static class MaterialUnrealExporter
{
    /// <summary>An operation's Unreal equivalent: the expression class, and the input pin each of
    /// the graph's operands maps to. A null <see cref="UnrealClass"/> means Unreal has no node with
    /// these semantics and the operation is emitted as a Custom node instead.</summary>
    private sealed record NodePlan(string UnrealClass, string[] Inputs, string OutputPin = "", string CustomHlsl = null,
        string[] CustomInputs = null, CompanionPlan Companion = null);

    /// <summary>
    /// A second node the port creates because one shader instruction does the work of two Unreal
    /// nodes. The primary's result feeds <paramref name="PrimaryInput"/> and the operand at
    /// <paramref name="OperandIndex"/> feeds <paramref name="OperandInput"/>; the pair's output is
    /// the companion's. Used for multiply-add, which Unreal has no single node for but which is
    /// exactly a Multiply followed by an Add.
    /// </summary>
    private sealed record CompanionPlan(string UnrealClass, string PrimaryInput, string OperandInput, int OperandIndex);

    // ---------------------------------------------------------------- operation mapping

    /// <summary>
    /// Shader operation to the Unreal expression class that compiles to it. Every entry is the node
    /// whose Compile() emits that exact instruction (Engine/Private/Materials/MaterialExpressions.cpp);
    /// the input names are the FExpressionInput members of the class
    /// (Engine/Classes/Materials/MaterialExpression*.h), which is what ConnectMaterialExpressions
    /// matches on.
    /// </summary>
    private static readonly Dictionary<string, NodePlan> OperationMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // binary arithmetic — A/B on every one of these classes
        ["add"] = new("MaterialExpressionAdd", ["A", "B"]),
        ["iadd"] = new("MaterialExpressionAdd", ["A", "B"]),
        ["mul"] = new("MaterialExpressionMultiply", ["A", "B"]),
        ["imul"] = new("MaterialExpressionMultiply", ["A", "B"]),
        ["umul"] = new("MaterialExpressionMultiply", ["A", "B"]),
        ["div"] = new("MaterialExpressionDivide", ["A", "B"]),
        ["udiv"] = new("MaterialExpressionDivide", ["A", "B"]),
        ["sub"] = new("MaterialExpressionSubtract", ["A", "B"]),
        ["min"] = new("MaterialExpressionMin", ["A", "B"]),
        ["imin"] = new("MaterialExpressionMin", ["A", "B"]),
        ["umin"] = new("MaterialExpressionMin", ["A", "B"]),
        ["max"] = new("MaterialExpressionMax", ["A", "B"]),
        ["imax"] = new("MaterialExpressionMax", ["A", "B"]),
        ["umax"] = new("MaterialExpressionMax", ["A", "B"]),
        // multiply-add: Unreal has no single node, but A*B+C is exactly a Multiply then an Add, so
        // the port emits both rather than dropping to Custom HLSL
        ["mad"] = new("MaterialExpressionMultiply", ["A", "B"],
            Companion: new CompanionPlan("MaterialExpressionAdd", "A", "B", 2)),
        ["imad"] = new("MaterialExpressionMultiply", ["A", "B"],
            Companion: new CompanionPlan("MaterialExpressionAdd", "A", "B", 2)),
        ["umad"] = new("MaterialExpressionMultiply", ["A", "B"],
            Companion: new CompanionPlan("MaterialExpressionAdd", "A", "B", 2)),

        ["dp2"] = new("MaterialExpressionDotProduct", ["A", "B"]),
        ["dp3"] = new("MaterialExpressionDotProduct", ["A", "B"]),
        ["dp4"] = new("MaterialExpressionDotProduct", ["A", "B"]),

        // unary — the graph names a single operand "X"
        ["frc"] = new("MaterialExpressionFrac", ["Input"]),
        ["sqrt"] = new("MaterialExpressionSquareRoot", ["Input"]),
        ["log"] = new("MaterialExpressionLogarithm2", ["X"]),
        ["sin"] = new("MaterialExpressionSine", ["Input"]),
        ["cos"] = new("MaterialExpressionCosine", ["Input"]),
        ["round_ni"] = new("MaterialExpressionFloor", ["Input"]),
        ["round_pi"] = new("MaterialExpressionCeil", ["Input"]),
        ["round_ne"] = new("MaterialExpressionRound", ["Input"]),
        ["round_z"] = new("MaterialExpressionTruncate", ["Input"]),

        // no Unreal node has these semantics, so they carry their own HLSL
        ["rsq"] = new(null, ["X"], CustomHlsl: "rsqrt(A)", CustomInputs: ["A"]),
        ["rcp"] = new(null, ["X"], CustomHlsl: "rcp(A)", CustomInputs: ["A"]),
        ["exp"] = new(null, ["X"], CustomHlsl: "exp2(A)", CustomInputs: ["A"]),
        ["log10"] = new("MaterialExpressionLogarithm10", ["X"]),

        // ---- the uniform-expression (preshader) vocabulary, EPreshaderOpcode ----
        // These run on the CPU each frame to fill the material's uniform buffer, but they are the
        // same arithmetic the graph performed, so each maps to the node that computes it.
        ["Add"] = new("MaterialExpressionAdd", ["A", "B"]),
        ["Sub"] = new("MaterialExpressionSubtract", ["A", "B"]),
        ["Mul"] = new("MaterialExpressionMultiply", ["A", "B"]),
        ["Div"] = new("MaterialExpressionDivide", ["A", "B"]),
        ["Fmod"] = new("MaterialExpressionFmod", ["A", "B"]),
        ["Modulo"] = new("MaterialExpressionFmod", ["A", "B"]),
        ["Min"] = new("MaterialExpressionMin", ["A", "B"]),
        ["Max"] = new("MaterialExpressionMax", ["A", "B"]),
        ["Clamp"] = new("MaterialExpressionClamp", ["Input", "Min", "Max"]),
        ["Sin"] = new("MaterialExpressionSine", ["Input"]),
        ["Cos"] = new("MaterialExpressionCosine", ["Input"]),
        ["Tan"] = new("MaterialExpressionTangent", ["Input"]),
        ["Asin"] = new("MaterialExpressionArcsine", ["Input"]),
        ["Acos"] = new("MaterialExpressionArccosine", ["Input"]),
        ["Atan"] = new("MaterialExpressionArctangent", ["Input"]),
        ["Atan2"] = new("MaterialExpressionArctangent2", ["Y", "X"]),
        ["Dot"] = new("MaterialExpressionDotProduct", ["A", "B"]),
        ["Cross"] = new("MaterialExpressionCrossProduct", ["A", "B"]),
        ["Sqrt"] = new("MaterialExpressionSquareRoot", ["Input"]),
        ["Normalize"] = new("MaterialExpressionNormalize", ["VectorInput"]),
        ["Saturate"] = new("MaterialExpressionSaturate", ["Input"]),
        ["Abs"] = new("MaterialExpressionAbs", ["Input"]),
        ["Floor"] = new("MaterialExpressionFloor", ["Input"]),
        ["Ceil"] = new("MaterialExpressionCeil", ["Input"]),
        ["Round"] = new("MaterialExpressionRound", ["Input"]),
        ["Trunc"] = new("MaterialExpressionTruncate", ["Input"]),
        ["Sign"] = new("MaterialExpressionSign", ["Input"]),
        ["Frac"] = new("MaterialExpressionFrac", ["Input"]),
        ["Fractional"] = new("MaterialExpressionFrac", ["Input"]),
        ["Log2"] = new("MaterialExpressionLogarithm2", ["X"]),
        ["Log10"] = new("MaterialExpressionLogarithm10", ["X"]),
        ["ComponentSwizzle"] = new("MaterialExpressionComponentMask", ["Input"]),
        ["AppendVector"] = new("MaterialExpressionAppendVector", ["A", "B"]),
        // Length has no node of its own; the distance from the origin is the same value
        ["Length"] = new("MaterialExpressionDistance", ["A"]),
        // negation is exactly a multiply by -1, and ConstB supplies the -1 without a second node
        ["Neg"] = new("MaterialExpressionMultiply", ["A"]),
        ["Rcp"] = new(null, ["A"], CustomHlsl: "rcp(A)", CustomInputs: ["A"]),
        ["Exp"] = new(null, ["A"], CustomHlsl: "exp(A)", CustomInputs: ["A"]),
        ["Exp2"] = new(null, ["A"], CustomHlsl: "exp2(A)", CustomInputs: ["A"]),
        ["Log"] = new(null, ["A"], CustomHlsl: "log(A)", CustomInputs: ["A"]),
        ["ScalarParameter"] = new("MaterialExpressionScalarParameter", []),
        ["VectorParameter"] = new("MaterialExpressionVectorParameter", []),
        ["Constant"] = new("MaterialExpressionConstant", []),
    };

    /// <summary>Material output pin to the EMaterialProperty the editor connects it to
    /// (SceneTypes.h). Python spells the enumerators in upper snake case.</summary>
    private static readonly Dictionary<string, string> OutputPropertyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Base Color"] = "MP_BASE_COLOR",
        ["Metallic"] = "MP_METALLIC",
        ["Specular"] = "MP_SPECULAR",
        ["Roughness"] = "MP_ROUGHNESS",
        ["Anisotropy"] = "MP_ANISOTROPY",
        ["Emissive Color"] = "MP_EMISSIVE_COLOR",
        ["Opacity"] = "MP_OPACITY",
        ["Opacity Mask"] = "MP_OPACITY_MASK",
        ["Normal"] = "MP_NORMAL",
        ["Tangent"] = "MP_TANGENT",
        ["World Position Offset"] = "MP_WORLD_POSITION_OFFSET",
        ["Subsurface Color"] = "MP_SUBSURFACE_COLOR",
        ["Custom Data 0"] = "MP_CUSTOM_DATA0",
        ["Custom Data 1"] = "MP_CUSTOM_DATA1",
        ["Clear Coat"] = "MP_CUSTOM_DATA0",
        ["Clear Coat Roughness"] = "MP_CUSTOM_DATA1",
        ["Ambient Occlusion"] = "MP_AMBIENT_OCCLUSION",
        ["Refraction"] = "MP_REFRACTION",
        ["Pixel Depth Offset"] = "MP_PIXEL_DEPTH_OFFSET",
        ["Shading Model"] = "MP_SHADING_MODEL",

        // UE4 still has tessellation: SceneTypes.h names these MP_WorldDisplacement and
        // MP_TessellationMultiplier in 4.27, and only renames them to *_DEPRECATED in UE5. The
        // script resolves the enumerator at run time, so a UE4 target connects them and a UE5
        // target reports that its engine has no such property instead of failing.
        ["World Displacement"] = "MP_WORLD_DISPLACEMENT",
        ["Tessellation Multiplier"] = "MP_TESSELLATION_MULTIPLIER",
    };

    /// <summary>
    /// The viewer builds a loose parameter node's type from its display title, so those names are
    /// labels, not Unreal classes. These are the real classes each one means
    /// (Engine/Classes/Materials/MaterialExpression*.h) — note the parameter forms, since e.g.
    /// UMaterialExpressionStaticSwitch is the A/B switch node, not a named switch parameter.
    /// </summary>
    private static readonly Dictionary<string, string> LooseParameterClasses = new(StringComparer.Ordinal)
    {
        ["MaterialExpressionStaticSwitch"] = "MaterialExpressionStaticSwitchParameter",
        ["MaterialExpressionStaticComponentMask"] = "MaterialExpressionStaticComponentMaskParameter",
        ["MaterialExpressionTextureParameter"] = "MaterialExpressionTextureSampleParameter2D",
        ["MaterialExpressionFontParameter"] = "MaterialExpressionFontSampleParameter",
        ["MaterialExpressionRuntimeVirtualTextureParameter"] = "MaterialExpressionRuntimeVirtualTextureSampleParameter",
        ["MaterialExpressionDoubleVectorParameter"] = "MaterialExpressionVectorParameter",
        // the viewer's fallback title when the decoder could not name the parameter's type
        ["MaterialExpressionParameter"] = "MaterialExpressionScalarParameter",
    };

    // ---------------------------------------------------------------- entry point

    /// <summary>
    /// Builds the Unreal Python script that rebuilds <paramref name="graph"/> as a material under
    /// <paramref name="destinationPath"/>. Only the user-authored part of the graph is ported —
    /// the compiled shader stages the viewer also shows are engine scaffolding, not the artist's
    /// material.
    /// </summary>
    public static MaterialPortResult Build(MaterialGraphViewModel graph, string destinationPath)
    {
        var result = new MaterialPortResult
        {
            MaterialName = SanitizeAssetName(string.IsNullOrEmpty(graph.MaterialName) ? "PortedMaterial" : graph.MaterialName)
        };

        var outputNode = graph.Nodes.FirstOrDefault(n => n.IsOutputNode);
        // an instance carries no graph of its own — its parent does — so it ports as an instance
        result.IsInstancePort = outputNode != null &&
                                outputNode.ExportType.Contains("MaterialInstance", StringComparison.Ordinal);

        var script = new StringBuilder();
        WriteHeader(script, graph, result, destinationPath);

        if (result.IsInstancePort) WriteInstanceBody(script, graph, result, destinationPath);
        else WriteMaterialBody(script, graph, outputNode, result, destinationPath);

        WriteFooter(script);
        result.Script = script.ToString();

        result.Summary.Add(new KeyValuePair<string, string>("Target", result.IsInstancePort
            ? "Material Instance Constant (parent + parameter overrides)"
            : "Material (expression graph)"));
        result.Summary.Add(new KeyValuePair<string, string>("Nodes ported exactly", result.ExactNodes.ToString()));
        result.Summary.Add(new KeyValuePair<string, string>("Nodes ported as Custom HLSL", result.CustomNodes.ToString()));
        result.Summary.Add(new KeyValuePair<string, string>("Nodes not ported", result.SkippedNodes.ToString()));
        result.Summary.Add(new KeyValuePair<string, string>("Connections", result.PortedConnections.ToString()));
        result.Summary.Add(new KeyValuePair<string, string>("Material outputs connected", result.OutputsConnected.ToString()));
        return result;
    }

    // ---------------------------------------------------------------- script sections

    private static void WriteHeader(StringBuilder script, MaterialGraphViewModel graph, MaterialPortResult result, string destinationPath)
    {
        script.AppendLine("# Generated by FModel — Material Graph Viewer, \"Port to Unreal\".");
        script.AppendLine("#");
        script.AppendLine($"# Source package : {graph.PackageName}");
        script.AppendLine($"# Source material: {graph.MaterialName}");
        if (!string.IsNullOrEmpty(graph.ParentChain))
            script.AppendLine($"# Parent chain   : {graph.ParentChain}");
        script.AppendLine($"# Destination    : {destinationPath}/{result.MaterialName}");
        script.AppendLine("#");
        if (graph.IsReconstructed)
        {
            script.AppendLine("# This graph was RECONSTRUCTED from the cooked material's uniform expressions and");
            script.AppendLine("# compiled shader bytecode. A cook does not keep the artist's node graph, so what is");
            script.AppendLine("# rebuilt here is the dataflow the compiler produced, not the original authoring. It");
            script.AppendLine("# computes the same values; it will not look like the source material's node layout.");
        }
        else
        {
            script.AppendLine("# This graph was read from the asset's own serialized material expressions, so the");
            script.AppendLine("# node types and connections below are the authored ones.");
        }
        if (!string.IsNullOrEmpty(graph.ReconstructionNote))
        {
            script.AppendLine("#");
            foreach (var line in WrapComment(graph.ReconstructionNote)) script.AppendLine("# " + line);
        }
        script.AppendLine("#");
        script.AppendLine("# Run from the Unreal editor: Window > Developer Tools > Output Log, switch the command");
        script.AppendLine("# dropdown to Python, then:  exec(open(r\"<this file>\").read())");
        script.AppendLine();
        script.AppendLine("import unreal");
        script.AppendLine();
        script.AppendLine($"DEST_PATH = {Quote(destinationPath)}");
        script.AppendLine($"ASSET_NAME = {Quote(result.MaterialName)}");
        script.AppendLine();
        script.AppendLine("_problems = []");
        script.AppendLine("_connected = 0");
        script.AppendLine("_outputs = 0");
        script.AppendLine();
        script.AppendLine("""
                          def _load(path):
                              "Resolve a source asset by path; a missing one is reported, never faked."
                              asset = unreal.EditorAssetLibrary.load_asset(path) if unreal.EditorAssetLibrary.does_asset_exist(path) else None
                              if asset is None:
                                  _problems.append("asset not found in this project: " + path)
                              return asset


                          def _find(name):
                              # Find an asset by name alone. A cooked shader map records only the texture's
                              # name for some slots, so the path from the source game is not available. This
                              # searches the project the script is run in; if there is not exactly one match
                              # it says so instead of picking one.
                              registry = unreal.AssetRegistryHelpers.get_asset_registry()
                              matches = [a for a in registry.get_assets_by_package_name(name, True)]
                              if not matches:
                                  matches = [a for a in registry.get_all_assets(True) if str(a.asset_name) == name]
                              if len(matches) == 1:
                                  return matches[0].get_asset()
                              if not matches:
                                  _problems.append("no asset named '%s' in this project (the cook recorded only its name)" % name)
                              else:
                                  _problems.append("%d assets are named '%s'; none was used, pick one by hand" % (len(matches), name))
                              return None


                          def _prop(name):
                              "EMaterialProperty by name, so an enum this engine version lacks is reported, not fatal."
                              value = getattr(unreal.MaterialProperty, name, None)
                              if value is None:
                                  _problems.append("this engine version has no MaterialProperty." + name)
                              return value


                          def _set(obj, name, value):
                              try:
                                  obj.set_editor_property(name, value)
                              except Exception as error:
                                  _problems.append("could not set %s: %s" % (name, error))


                          def _conn(source, source_output, target, target_input):
                              # ConnectMaterialExpressions returns False when the named pin does not exist
                              # on that class, so a wiring mistake is reported instead of passing silently.
                              global _connected
                              if source is None or target is None:
                                  return
                              if unreal.MaterialEditingLibrary.connect_material_expressions(source, source_output, target, target_input):
                                  _connected += 1
                              else:
                                  _problems.append("could not connect %s -> %s.%s" % (
                                      source.get_name(), target.get_name(), target_input))


                          def _conn_prop(source, source_output, property_name):
                              global _outputs
                              value = _prop(property_name)
                              if source is None or value is None:
                                  return
                              if unreal.MaterialEditingLibrary.connect_material_property(source, source_output, value):
                                  _outputs += 1
                              else:
                                  _problems.append("could not connect %s -> %s" % (source.get_name(), property_name))
                          """);
        script.AppendLine();
    }

    private static void WriteMaterialBody(StringBuilder script, MaterialGraphViewModel graph, MaterialGraphNode outputNode,
        MaterialPortResult result, string destinationPath)
    {
        script.AppendLine("material = unreal.AssetToolsHelpers.get_asset_tools().create_asset(");
        script.AppendLine("    ASSET_NAME, DEST_PATH, unreal.Material, unreal.MaterialFactoryNew())");
        script.AppendLine("if material is None:");
        script.AppendLine("    raise RuntimeError(\"could not create the material asset\")");
        script.AppendLine();

        WriteMaterialSettings(script, outputNode, result);

        // only the artist's graph is ported; the compiled stage nodes the viewer also shows are the
        // engine's own template, and rebuilding them as material nodes would be meaningless
        var portable = graph.Nodes
            .Where(n => !n.IsOutputNode && n.IsUserMaterialNode && n.ExportType != "CompiledShaderStage")
            .ToList();
        var excluded = graph.Nodes.Count(n => !n.IsOutputNode && !n.IsUserMaterialNode);
        if (excluded > 0)
            result.Notes.Add($"{excluded} node(s) belong to the compiled shader template rather than the material graph and were not ported");

        var plans = new Dictionary<MaterialGraphNode, (string Variable, string CompanionVariable, NodePlan Plan)>();
        var index = 0;

        script.AppendLine("expressions = {}");
        script.AppendLine();

        foreach (var node in portable)
        {
            var plan = PlanFor(node, result);
            if (plan == null)
            {
                result.SkippedNodes++;
                continue;
            }

            var variable = $"n{index++}";
            string companionVariable = null;

            var className = plan.UnrealClass ?? "MaterialExpressionCustom";
            script.AppendLine($"# {node.Title}{(string.IsNullOrEmpty(node.Subtitle) ? "" : " — " + node.Subtitle)}");
            script.AppendLine($"expressions[{Quote(variable)}] = unreal.MaterialEditingLibrary.create_material_expression(");
            script.AppendLine($"    material, unreal.{className}, {(int) node.NodePosX}, {(int) node.NodePosY})");

            foreach (var line in SetupLines(node, plan, variable, result)) script.AppendLine(line);

            if (plan.Companion is { } companion)
            {
                companionVariable = $"n{index++}";
                script.AppendLine($"expressions[{Quote(companionVariable)}] = unreal.MaterialEditingLibrary.create_material_expression(");
                script.AppendLine($"    material, unreal.{companion.UnrealClass}, {(int) node.NodePosX + 160}, {(int) node.NodePosY})");
                script.AppendLine($"_conn(expressions[{Quote(variable)}], \"\", " +
                                  $"expressions[{Quote(companionVariable)}], {Quote(companion.PrimaryInput)})");
                result.ExactNodes++;
                result.PortedConnections++;
            }

            plans[node] = (variable, companionVariable, plan);
            script.AppendLine();

            if (plan.UnrealClass == null) result.CustomNodes++;
            else result.ExactNodes++;
        }

        // expression-to-expression wiring
        script.AppendLine("# --- connections ---");
        foreach (var connection in graph.Connections)
        {
            if (connection.TargetNode == outputNode) continue;
            if (!plans.TryGetValue(connection.SourceNode, out var source)) continue;
            if (!plans.TryGetValue(connection.TargetNode, out var target)) continue;

            var destination = ResolveInput(connection.TargetPinName, connection.TargetNode, target);
            if (destination == null)
            {
                result.Notes.Add($"'{connection.TargetNode.Title}' has no Unreal input matching pin '{connection.TargetPinName}' — that connection was left out");
                continue;
            }

            var outputName = MapOutputPin(connection.SourcePinName, source.Plan);
            script.AppendLine($"_conn(expressions[{Quote(OutputVariableOf(source))}], {Quote(outputName)}, " +
                              $"expressions[{Quote(destination.Value.Variable)}], {Quote(destination.Value.Input)})");
            result.PortedConnections++;
        }
        script.AppendLine();

        // graph-to-material-output wiring
        script.AppendLine("# --- material outputs ---");
        foreach (var connection in graph.Connections)
        {
            if (connection.TargetNode != outputNode) continue;
            if (!plans.TryGetValue(connection.SourceNode, out var source)) continue;

            var pin = connection.TargetPinName;
            if (!OutputPropertyMap.TryGetValue(pin, out var property))
            {
                result.Notes.Add($"no material property matches the output pin '{pin}' — it was not connected");
                continue;
            }

            var outputName = MapOutputPin(connection.SourcePinName, source.Plan);
            script.AppendLine($"_conn_prop(expressions[{Quote(OutputVariableOf(source))}], " +
                              $"{Quote(outputName)}, {Quote(property)})  # {pin}");
            result.OutputsConnected++;
        }
        script.AppendLine();

        script.AppendLine("# The editor's own layout walks back from each connected material output, so it");
        script.AppendLine("# tidies the graph and leaves anything unconnected where it was put.");
        script.AppendLine("unreal.MaterialEditingLibrary.layout_material_expressions(material)");
        script.AppendLine("unreal.MaterialEditingLibrary.recompile_material(material)");
        script.AppendLine("unreal.EditorAssetLibrary.save_loaded_asset(material)");
        script.AppendLine($"unreal.log(\"FModel port: %d expressions, %d/%d connections, %d/%d outputs\" % (");
        script.AppendLine($"    len(expressions), _connected, {result.PortedConnections}, _outputs, {result.OutputsConnected}))");
    }

    private static void WriteMaterialSettings(StringBuilder script, MaterialGraphNode outputNode, MaterialPortResult result)
    {
        if (outputNode == null) return;
        script.AppendLine("# --- material settings, as serialized on the source material ---");

        var properties = outputNode.DisplayProperties;
        string Find(string key) => properties.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

        if (Find("Blend Mode") is { Length: > 0 } blendMode && blendMode.StartsWith("BLEND_", StringComparison.Ordinal))
            script.AppendLine($"_set(material, \"blend_mode\", unreal.BlendMode.{ToPythonEnum(blendMode)})");

        if (Find("Shading Model") is { Length: > 0 } shadingModel && shadingModel.StartsWith("MSM_", StringComparison.Ordinal))
            script.AppendLine($"_set(material, \"shading_model\", unreal.MaterialShadingModel.{ToPythonEnum(shadingModel)})");

        if (Find("MaterialDomain") is { Length: > 0 } domain && domain.StartsWith("MD_", StringComparison.Ordinal))
            script.AppendLine($"_set(material, \"material_domain\", unreal.MaterialDomain.{ToPythonEnum(domain)})");

        if (Find("TwoSided") is { Length: > 0 } twoSided)
            script.AppendLine($"_set(material, \"two_sided\", {(twoSided.Equals("True", StringComparison.OrdinalIgnoreCase) ? "True" : "False")})");

        script.AppendLine();
        result.Notes.Add("material settings come from the source material's serialized properties; anything it left at the engine default is left at the default here too");
    }

    /// <summary>
    /// A material instance stores no graph — only a parent and the parameters it overrides — so the
    /// faithful port is a MaterialInstanceConstant, not a rebuilt node graph.
    /// </summary>
    private static void WriteInstanceBody(StringBuilder script, MaterialGraphViewModel graph, MaterialPortResult result, string destinationPath)
    {
        script.AppendLine("instance = unreal.AssetToolsHelpers.get_asset_tools().create_asset(");
        script.AppendLine("    ASSET_NAME, DEST_PATH, unreal.MaterialInstanceConstant, unreal.MaterialInstanceConstantFactoryNew())");
        script.AppendLine("if instance is None:");
        script.AppendLine("    raise RuntimeError(\"could not create the material instance asset\")");
        script.AppendLine();

        // the parent is the first link of the chain the viewer resolved
        var parent = graph.ParentChain?.Split('→', '>').Select(p => p.Trim()).FirstOrDefault(p => p.Length > 0);
        if (!string.IsNullOrEmpty(parent))
        {
            script.AppendLine($"parent = {AssetLookup(parent, "the parent material", result)}");
            script.AppendLine("if parent is not None:");
            script.AppendLine("    unreal.MaterialEditingLibrary.set_material_instance_parent(instance, parent)");
            script.AppendLine();
        }
        else
        {
            result.Notes.Add("the parent material could not be resolved from the instance, so the port leaves the parent unset");
        }

        script.AppendLine("# --- parameter overrides serialized on this instance ---");
        var wrote = 0;
        foreach (var node in graph.Nodes.Where(n => n.IsParameter))
        {
            var name = DisplayValue(node, "Parameter Name");
            if (string.IsNullOrEmpty(name)) continue;

            if (node.IsTexture)
            {
                var texture = DisplayValue(node, "Texture Path");
                if (string.IsNullOrEmpty(texture)) texture = DisplayValue(node, "Texture");
                if (string.IsNullOrEmpty(texture)) continue;
                script.AppendLine($"_t = {AssetLookup(texture, "a texture parameter", result)}");
                script.AppendLine("if _t is not None:");
                script.AppendLine($"    unreal.MaterialEditingLibrary.set_material_instance_texture_parameter_value(instance, {Quote(name)}, _t)");
                wrote++;
                continue;
            }

            var value = DisplayValue(node, "Value");
            if (string.IsNullOrEmpty(value)) continue;

            if (node.ExportType.Contains("VectorParameter", StringComparison.Ordinal) && TryParseColor(value, out var color))
            {
                script.AppendLine($"unreal.MaterialEditingLibrary.set_material_instance_vector_parameter_value(" +
                                  $"instance, {Quote(name)}, unreal.LinearColor({color}))");
                wrote++;
            }
            else if (node.ExportType.Contains("ScalarParameter", StringComparison.Ordinal) &&
                     float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar))
            {
                script.AppendLine($"unreal.MaterialEditingLibrary.set_material_instance_scalar_parameter_value(" +
                                  $"instance, {Quote(name)}, {scalar.ToString("0.######", CultureInfo.InvariantCulture)})");
                wrote++;
            }
            else if (node.ExportType.Contains("StaticSwitch", StringComparison.Ordinal))
            {
                // static switches live in the static parameter set, which MaterialEditingLibrary
                // does not expose; naming it beats silently dropping it
                script.AppendLine($"# static switch {name} = {value} — set this by hand, the editor scripting");
                script.AppendLine("# library has no setter for static switch parameters");
                result.Notes.Add($"static switch '{name}' = {value} must be set by hand — MaterialEditingLibrary exposes no static-parameter setter");
            }
        }

        result.ExactNodes = wrote;
        if (wrote == 0) result.Notes.Add("this instance overrides no scalar, vector or texture parameter that the viewer could read");

        script.AppendLine();
        script.AppendLine("unreal.EditorAssetLibrary.save_loaded_asset(instance)");
    }

    private static void WriteFooter(StringBuilder script)
    {
        script.AppendLine();
        script.AppendLine("""
                          if _problems:
                              unreal.log_warning("FModel port finished with %d unresolved item(s):" % len(_problems))
                              for problem in _problems:
                                  unreal.log_warning("  - " + problem)
                          else:
                              unreal.log("FModel port finished with everything resolved.")
                          """);
    }

    // ---------------------------------------------------------------- node planning

    /// <summary>Chooses the Unreal expression for a graph node, or null when the node is not
    /// something a material graph can hold.</summary>
    private static NodePlan PlanFor(MaterialGraphNode node, MaterialPortResult result)
    {
        // Loose parameter nodes name their class from the viewer's own title, which is a label
        // rather than an Unreal class name — "Static Switch" is UMaterialExpressionStaticSwitch,
        // a node with A/B inputs, not the named parameter the material actually has. Correct those
        // to the real parameter classes before anything else claims them.
        if (LooseParameterClasses.TryGetValue(node.ExportType, out var corrected))
            return new NodePlan(corrected, []);

        // an authored graph already names real Unreal classes — port them unchanged
        if (node.ExportType.StartsWith("MaterialExpression", StringComparison.Ordinal))
            return new NodePlan(node.ExportType, []);

        switch (node.ExportType)
        {
            case "PixelMathConstant":
                return ConstantPlan(node);

            case "PixelMathTextureSample":
            case "PreshaderTextureParameter":
                return new NodePlan("MaterialExpressionTextureSample", ["Coordinates"], OutputPin: "RGB");

            case "PixelMathAppend":
                return new NodePlan("MaterialExpressionAppendVector", ["A", "B"]);

            case "PixelMathMask":
                return new NodePlan("MaterialExpressionComponentMask", ["Input"]);

            case "PixelMathInterpolant":
                return InterpolantPlan(node, result);

            case "PixelMathOp":
                return OperationPlan(RawOp(node), node, result);

            case "PreshaderPixelMath":
            {
                // Not an operation: the viewer's placeholder for a whole pixel-shader expression
                // that has not been expanded into its instructions yet. Turning on "Expand Shader
                // Math" first replaces each of these with the real node chain, which ports exactly.
                var target = (node.Subtitle ?? string.Empty).TrimStart('→', ' ');
                result.Notes.Add($"the math feeding '{target}' is still collapsed into one node — turn on \"Expand Shader Math\" before porting to get the real node chain instead of a placeholder");
                var arity = Math.Max(1, node.InputPins.Count);
                var inputs = Enumerable.Range(0, arity).Select(i => ((char) ('A' + i)).ToString()).ToArray();
                return new NodePlan(null, inputs,
                    CustomHlsl: $"/* pixel shader math feeding {Escape(target)} — expand the shader math to recover it */ {inputs[0]}",
                    CustomInputs: inputs);
            }

            case "PixelMathEngineConstant":
                // a View-uniform value: real, but not something the material graph owns
                result.Notes.Add($"'{node.Subtitle}' is an engine constant supplied by the renderer, not a material node — ported as a Custom node naming it");
                return new NodePlan(null, [], CustomHlsl: $"/* engine constant: {Escape(node.Subtitle)} */ 0", CustomInputs: []);

            case "PixelMathBranch":
            case "PixelMathDiscard":
            case "PixelMathUnresolvedCustom":
            case "PreshaderVirtualTextureStack":
                result.Notes.Add($"'{node.Title}' ({node.Subtitle}) has no material-graph equivalent — ported as a Custom node so the wiring survives");
                return new NodePlan(null, ["A", "B", "C"], CustomHlsl: $"/* {Escape(node.Title)}: {Escape(node.Subtitle)} */ A", CustomInputs: ["A", "B", "C"]);

            case "CompiledShaderStage":
                return null;
        }

        if (node.ExportType.StartsWith("Preshader", StringComparison.Ordinal))
        {
            var op = node.ExportType["Preshader".Length..];
            // a constant's class follows its component count, and a parameter's follows the type
            // the decoder read for it, so neither needs the operation table
            if (op == "Constant") return ConstantPlan(node);
            if (op is "Parameter" or "ScalarParameter" or "VectorParameter") return ParameterPlan(node, op);
            return OperationPlan(op, node, result);
        }

        if (node.ExportType.StartsWith("LegacyUniform", StringComparison.Ordinal))
            return OperationPlan(node.ExportType["LegacyUniform".Length..], node, result);

        result.Notes.Add($"'{node.ExportType}' is not a kind of node this port knows how to build — it was left out");
        return null;
    }

    private static NodePlan OperationPlan(string op, MaterialGraphNode node, MaterialPortResult result)
    {
        if (string.IsNullOrEmpty(op)) return null;

        if (OperationMap.TryGetValue(op, out var mapped))
            return mapped.UnrealClass != null
                ? mapped
                : new NodePlan(null, mapped.Inputs, CustomHlsl: mapped.CustomHlsl, CustomInputs: mapped.CustomInputs);

        // an operation with no Unreal node of the same meaning keeps its own name in HLSL rather
        // than being approximated by a node that would compute something else
        var arity = Math.Max(1, node.InputPins.Count);
        var inputs = arity == 1 ? new[] { "A" } : Enumerable.Range(0, arity).Select(i => ((char) ('A' + i)).ToString()).ToArray();
        result.Notes.Add($"'{op}' has no Unreal expression with the same semantics — ported as a Custom node containing the operation");
        return new NodePlan(null, inputs, CustomHlsl: $"{Escape(op)}({string.Join(", ", inputs)})", CustomInputs: inputs);
    }

    /// <summary>A vertex interpolant that is a texture coordinate maps to the real UV node; anything
    /// else is a value the vertex shader supplied and the material graph cannot re-derive.</summary>
    private static NodePlan InterpolantPlan(MaterialGraphNode node, MaterialPortResult result)
    {
        var detail = node.Subtitle ?? string.Empty;
        var match = System.Text.RegularExpressions.Regex.Match(detail, @"TEXCOORD\s*(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (match.Success)
            return new NodePlan("MaterialExpressionTextureCoordinate", []);

        if (detail.Contains("COLOR", StringComparison.OrdinalIgnoreCase))
            return new NodePlan("MaterialExpressionVertexColor", []);

        result.Notes.Add($"vertex interpolant '{detail}' is fed by the vertex shader and has no material-graph source — ported as a Custom node naming it");
        return new NodePlan(null, [], CustomHlsl: $"/* vertex interpolant: {Escape(detail)} */ 0", CustomInputs: []);
    }

    /// <summary>
    /// A uniform parameter becomes the matching parameter node. EPreshaderOpcode distinguishes
    /// ScalarParameter from VectorParameter directly; the generic Parameter opcode carries the type
    /// in the value the decoder read, which the graph records as the output pin's type.
    /// </summary>
    private static NodePlan ParameterPlan(MaterialGraphNode node, string op)
    {
        // the decoder's own title says which kind it read, the decoded default's component count
        // confirms it, and the pin type is the last signal — a scalar has exactly one component
        var isVector = op == "VectorParameter"
                       || (node.Title ?? string.Empty).Contains("Vector", StringComparison.OrdinalIgnoreCase)
                       || ParseConstants(ValueOf(node)).Length > 1
                       || node.OutputPins.Any(p => p.PinType is "color" or "vector" or "vector2");
        return new NodePlan(isVector ? "MaterialExpressionVectorParameter" : "MaterialExpressionScalarParameter", []);
    }

    /// <summary>A parameter node names itself either through a "Parameter Name" property or before
    /// the "=" in its subtitle, depending on which builder produced it.</summary>
    private static string ParameterNameOf(MaterialGraphNode node)
    {
        var name = DisplayValue(node, "Parameter Name");
        if (!string.IsNullOrEmpty(name)) return name;
        var subtitle = node.Subtitle ?? string.Empty;
        var equals = subtitle.IndexOf('=');
        return (equals >= 0 ? subtitle[..equals] : subtitle).Trim();
    }

    /// <summary>A parameter node states its default either as a "Value" property or after the "=" in
    /// its subtitle, depending on which builder produced it.</summary>
    private static string ValueOf(MaterialGraphNode node)
    {
        var value = DisplayValue(node, "Value");
        if (!string.IsNullOrEmpty(value)) return value;
        var subtitle = node.Subtitle ?? string.Empty;
        var equals = subtitle.IndexOf('=');
        return equals >= 0 ? subtitle[(equals + 1)..].Trim() : string.Empty;
    }

    /// <summary>Picks the constant class by component count, the way the editor does.</summary>
    private static NodePlan ConstantPlan(MaterialGraphNode node)
    {
        var values = ParseConstants(node.Subtitle);
        return values.Length switch
        {
            >= 4 => new NodePlan("MaterialExpressionConstant4Vector", []),
            3 => new NodePlan("MaterialExpressionConstant3Vector", []),
            2 => new NodePlan("MaterialExpressionConstant2Vector", []),
            _ => new NodePlan("MaterialExpressionConstant", [])
        };
    }

    /// <summary>Property assignments that make the created expression match the source node.</summary>
    private static IEnumerable<string> SetupLines(MaterialGraphNode node, NodePlan plan, string variable, MaterialPortResult result)
    {
        var target = $"expressions[{Quote(variable)}]";

        if (plan.UnrealClass == null)
        {
            yield return $"_set({target}, \"code\", {Quote(plan.CustomHlsl ?? "0")})";
            yield return $"_set({target}, \"description\", {Quote(Truncate(node.Title, 60))})";
            if (plan.CustomInputs is { Length: > 0 })
            {
                var inputs = string.Join(", ", plan.CustomInputs.Select(i => $"unreal.CustomInput(input_name={Quote(i)})"));
                yield return $"_set({target}, \"inputs\", [{inputs}])";
            }
            else
            {
                yield return $"_set({target}, \"inputs\", [])";
            }
            yield break;
        }

        // negation is ported as a multiply, so the -1 has to come from the node's own constant
        if (plan.UnrealClass == "MaterialExpressionMultiply" &&
            node.ExportType.EndsWith("Neg", StringComparison.Ordinal))
            yield return $"_set({target}, \"const_b\", -1.0)";

        switch (plan.UnrealClass)
        {
            case "MaterialExpressionConstant":
            case "MaterialExpressionConstant2Vector":
            case "MaterialExpressionConstant3Vector":
            case "MaterialExpressionConstant4Vector":
            {
                var values = ParseConstants(node.Subtitle);
                if (values.Length == 0) break;
                if (plan.UnrealClass == "MaterialExpressionConstant")
                {
                    yield return $"_set({target}, \"r\", {Fmt(values[0])})";
                    break;
                }
                var names = new[] { "r", "g", "b", "a" };
                for (var i = 0; i < values.Length && i < names.Length; i++)
                    yield return $"_set({target}, {Quote(names[i])}, {Fmt(values[i])})";
                break;
            }

            case "MaterialExpressionComponentMask":
            {
                // the graph records the surviving channels as the node's ".xyzw" subtitle
                var channels = (node.Subtitle ?? string.Empty).TrimStart('.').ToLowerInvariant();
                foreach (var (channel, property) in new[] { ('x', "r"), ('y', "g"), ('z', "b"), ('w', "a") })
                    yield return $"_set({target}, {Quote(property)}, {(channels.Contains(channel) ? "True" : "False")})";
                break;
            }

            case "MaterialExpressionTextureCoordinate":
            {
                var match = System.Text.RegularExpressions.Regex.Match(node.Subtitle ?? "", @"TEXCOORD\s*(\d+)",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success) yield return $"_set({target}, \"coordinate_index\", {match.Groups[1].Value})";
                break;
            }

            case "MaterialExpressionTextureSample":
            {
                var lookup = TextureLookup(node, result);
                if (lookup == null) break;
                yield return $"_t = {lookup}";
                yield return "if _t is not None:";
                yield return $"    _set({target}, \"texture\", _t)";
                break;
            }

            case "MaterialExpressionStaticSwitchParameter":
            case "MaterialExpressionStaticComponentMaskParameter":
            {
                var switchName = ParameterNameOf(node);
                if (!string.IsNullOrEmpty(switchName))
                    yield return $"_set({target}, \"parameter_name\", {Quote(switchName)})";

                var switchValue = ValueOf(node);
                if (plan.UnrealClass.EndsWith("StaticSwitchParameter", StringComparison.Ordinal))
                {
                    // UMaterialExpressionStaticBoolParameter::DefaultValue is the switch's value
                    yield return $"_set({target}, \"default_value\", " +
                                 $"{(switchValue.Equals("True", StringComparison.OrdinalIgnoreCase) ? "True" : "False")})";
                }
                else
                {
                    // the mask node keeps one flag per channel; the graph records the live ones
                    var channels = (switchValue ?? string.Empty).ToUpperInvariant();
                    foreach (var (channel, property) in new[] { ('R', "default_r"), ('G', "default_g"), ('B', "default_b"), ('A', "default_a") })
                        yield return $"_set({target}, {Quote(property)}, {(channels.Contains(channel) ? "True" : "False")})";
                }
                break;
            }

            case "MaterialExpressionScalarParameter":
            case "MaterialExpressionVectorParameter":
            case "MaterialExpressionTextureSampleParameter2D":
            case "MaterialExpressionFontSampleParameter":
            case "MaterialExpressionRuntimeVirtualTextureSampleParameter":
            {
                var name = ParameterNameOf(node);
                if (!string.IsNullOrEmpty(name))
                    yield return $"_set({target}, \"parameter_name\", {Quote(name)})";

                var value = ValueOf(node);
                if (plan.UnrealClass.EndsWith("ScalarParameter", StringComparison.Ordinal) &&
                    float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scalar))
                    yield return $"_set({target}, \"default_value\", {Fmt(scalar)})";
                else if (plan.UnrealClass.EndsWith("VectorParameter", StringComparison.Ordinal) && TryParseColor(value, out var color))
                    yield return $"_set({target}, \"default_value\", unreal.LinearColor({color}))";

                var lookup = TextureLookup(node, null);
                if (lookup != null)
                {
                    yield return $"_t = {lookup}";
                    yield return "if _t is not None:";
                    yield return $"    _set({target}, \"texture\", _t)";
                }
                break;
            }
        }

        // the shader's _sat modifier is a real clamp the port must keep
        if (node.DisplayProperties.Any(p => p.Key.Equals("Saturate", StringComparison.OrdinalIgnoreCase)))
            result.Notes.Add($"'{node.Title}' carries the shader's _sat modifier; add a Saturate after it to match the source exactly");
    }

    // ---------------------------------------------------------------- pin mapping

    /// <summary>Where a node's result comes out: the companion's output when the instruction was
    /// split across two Unreal nodes, otherwise the node's own.</summary>
    private static string OutputVariableOf((string Variable, string CompanionVariable, NodePlan Plan) entry)
        => entry.CompanionVariable ?? entry.Variable;

    /// <summary>Resolves a connection's destination, sending the operand the companion node owns to
    /// the companion instead of the primary.</summary>
    private static (string Variable, string Input)? ResolveInput(string pinName, MaterialGraphNode targetNode,
        (string Variable, string CompanionVariable, NodePlan Plan) target)
    {
        if (target.Plan.Companion is { } companion && target.CompanionVariable != null &&
            OperandIndex(BasePinName(pinName), targetNode) == companion.OperandIndex)
            return (target.CompanionVariable, companion.OperandInput);

        var input = MapInputPin(pinName, targetNode, target.Plan);
        return input == null ? null : (target.Variable, input);
    }

    /// <summary>
    /// Maps a graph input pin onto the Unreal expression's input. The graph decorates operand names
    /// with the swizzle and modifiers it read from the instruction ("A .xyz", "B (−)"), so the base
    /// name is what identifies the operand.
    /// </summary>
    private static string MapInputPin(string pinName, MaterialGraphNode targetNode, NodePlan plan)
    {
        var baseName = BasePinName(pinName);

        // an authored node already uses Unreal's own input names
        if (plan.Inputs.Length == 0)
            return targetNode.ExportType.StartsWith("MaterialExpression", StringComparison.Ordinal) ? baseName : null;

        var index = OperandIndex(baseName, targetNode);
        if (index >= 0 && index < plan.Inputs.Length) return plan.Inputs[index];

        // named operands that already match one of the class's inputs
        return plan.Inputs.FirstOrDefault(i => i.Equals(baseName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Position of an operand in the source instruction, from the names the analyzer gives
    /// them: unary is "X", binary "A"/"B", multiply-add "A"/"B"/"C", a sample's first operand "UVs",
    /// a mask's "In", an append's "X"/"Y"/"Z"/"W".</summary>
    private static int OperandIndex(string baseName, MaterialGraphNode node)
    {
        switch (baseName.ToUpperInvariant())
        {
            case "X" when node.InputPins.Count <= 1: return 0;
            case "IN":
            case "UVS":
            case "A": return 0;
            case "B":
            case "Y": return 1;
            case "C":
            case "Z": return 2;
            case "W": return 3;
        }
        var position = node.InputPins.FindIndex(p => BasePinName(p.Name).Equals(baseName, StringComparison.OrdinalIgnoreCase));
        return position;
    }

    private static string MapOutputPin(string sourcePin, NodePlan plan)
    {
        if (!string.IsNullOrEmpty(plan.OutputPin)) return plan.OutputPin;
        // Unreal treats an empty output name as "the node's default output", which is what every
        // single-output expression has; a texture sample's named channels pass through as-is
        return sourcePin is "RGB" or "RGBA" or "R" or "G" or "B" or "A" ? sourcePin : string.Empty;
    }

    /// <summary>Strips the swizzle and modifier decoration the viewer adds to operand pin names.</summary>
    private static string BasePinName(string pinName)
    {
        if (string.IsNullOrEmpty(pinName)) return string.Empty;
        var cut = pinName.IndexOf(" .", StringComparison.Ordinal);
        if (cut < 0) cut = pinName.IndexOf(" |", StringComparison.Ordinal);
        if (cut < 0) cut = pinName.IndexOf(" (", StringComparison.Ordinal);
        return (cut >= 0 ? pinName[..cut] : pinName).Trim();
    }

    // ---------------------------------------------------------------- small helpers

    private static string RawOp(MaterialGraphNode node)
    {
        var subtitle = node.Subtitle ?? string.Empty;
        var op = subtitle.Split(' ')[0];
        return op.EndsWith("_sat", StringComparison.Ordinal) ? op[..^4] : op;
    }

    /// <summary>
    /// The Python that resolves a node's texture. A full object path is used when the graph resolved
    /// the texture object; a shader map that recorded only the name falls back to searching the
    /// destination project by that name, because the source path simply is not in the cook.
    /// </summary>
    private static string TextureLookup(MaterialGraphNode node, MaterialPortResult result)
    {
        var reference = DisplayValue(node, "Texture Path");
        if (string.IsNullOrEmpty(reference)) reference = DisplayValue(node, "Texture");
        if (!string.IsNullOrEmpty(reference)) return AssetLookup(reference, "a texture", result);

        result?.Notes.Add($"'{node.Title}' names no texture asset, so its sampler is left empty");
        return null;
    }

    /// <summary>
    /// The Python that resolves one asset reference. A full object path can be loaded directly; a
    /// bare name is all some shader-map slots and import entries carry, and there is no path to
    /// recover, so the script searches the destination project for that name instead.
    /// </summary>
    private static string AssetLookup(string reference, string what, MaterialPortResult result)
    {
        var trimmed = (reference ?? string.Empty).Trim();
        if (trimmed.Length == 0) return null;

        if (trimmed.Contains('/'))
            return $"_load({Quote(ToContentPath(trimmed))})";

        result?.Notes.Add($"the cooked asset records only the name '{trimmed}' for {what}, not a path — the script searches the destination project for it");
        return $"_find({Quote(ToContentPath(trimmed))})";
    }

    private static string DisplayValue(MaterialGraphNode node, string key) =>
        node.DisplayProperties.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static float[] ParseConstants(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        return text.Trim('(', ')', ' ')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            // a colour is written "R=0, G=0, B=0, A=1"; a plain vector is just the numbers
            .Select(part => part.Contains('=') ? part[(part.IndexOf('=') + 1)..].Trim() : part)
            .Select(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : (float?) null)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .ToArray();
    }

    private static bool TryParseColor(string text, out string arguments)
    {
        var values = ParseConstants(text);
        if (values.Length == 0) { arguments = null; return false; }
        while (values.Length < 4) values = [..values, values.Length == 3 ? 1f : 0f];
        arguments = string.Join(", ", values.Take(4).Select(Fmt));
        return true;
    }

    /// <summary>A cooked object path is <c>/Game/Path/Asset.Asset</c>; Unreal's asset library takes
    /// the package path, so the trailing object name is dropped.</summary>
    private static string ToContentPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var trimmed = path.Trim();
        var dot = trimmed.LastIndexOf('.');
        if (dot > 0) trimmed = trimmed[..dot];
        return trimmed;
    }

    private static string ToPythonEnum(string enumerator)
    {
        // Unreal's Python bindings spell enumerators in upper snake case: MSM_DefaultLit -> MSM_DEFAULT_LIT
        var builder = new StringBuilder();
        for (var i = 0; i < enumerator.Length; i++)
        {
            var character = enumerator[i];
            if (i > 0 && char.IsUpper(character) && enumerator[i - 1] != '_' && !char.IsUpper(enumerator[i - 1]))
                builder.Append('_');
            builder.Append(char.ToUpperInvariant(character));
        }
        return builder.ToString();
    }

    private static string SanitizeAssetName(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray()).Trim('_');
        return cleaned.Length == 0 ? "PortedMaterial" : cleaned;
    }

    private static string Fmt(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

    private static string Quote(string text) => "\"" + Escape(text) + "\"";

    private static string Escape(string text) =>
        (text ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");

    private static string Truncate(string text, int length) =>
        string.IsNullOrEmpty(text) || text.Length <= length ? text ?? string.Empty : text[..length];

    private static IEnumerable<string> WrapComment(string text, int width = 92)
    {
        var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();
        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + word.Length + 1 > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }
}

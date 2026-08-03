using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FModel.ViewModels;

// Shader-code view of a reconstructed material.
//
// CREDIT: the shader-code listing — printing a cooked material back out as readable shader code —
// is credited to Tectfy (https://github.com/Tectfy), whose work this view's output follows.
//
// The graph and this text are two renderings of the same recovered data: the expression DAG the
// analyzer read out of the compiled base-pass pixel shader, and the uniform-expression trees decoded
// from the preshader bytecode. Nothing here re-derives anything — it walks the same nodes the graph
// draws and prints them as expressions instead of boxes.
//
// A value used more than once is hoisted into a `var _N` so the text shows sharing the way the DAG
// has it, which is also how the compiler emitted it. Anything the decoder could not resolve is
// printed as a comment saying so, never as a plausible-looking expression.

/// <summary>Everything the code view needs, gathered while the graph was built.</summary>
public sealed class MaterialCodeViewSource
{
    public string MaterialName = string.Empty;
    /// <summary>Shader platform / quality / feature level / policy line for the header.</summary>
    public string ShaderDescription = string.Empty;
    public string Provenance = string.Empty;
    public PixelShaderWiring Wiring;
    public PreshaderDecodeResult Preshaders;
    /// <summary>Texture binding (slot, index within slot) → the texture's name, for sample comments.</summary>
    public Dictionary<(int Slot, int Index), string> TextureNames = new();
    /// <summary>Uniform expression index → parameter name, for cb value comments.</summary>
    public Dictionary<int, string> VectorNames = new();
    public Dictionary<int, string> ScalarNames = new();
}

public static class MaterialCodeView
{
    /// <summary>Attribution for the shader-code listing this view produces. Kept in the generated
    /// text as well as the source, so a copied or saved listing carries the credit with it.</summary>
    public const string CreditUrl = "https://github.com/Tectfy";
    public const string Credit = "Shader code view credited to Tectfy — " + CreditUrl;

    public static string Build(MaterialCodeViewSource source)
    {
        var text = new StringBuilder();
        var header = $"// {source.MaterialName}";
        if (source.ShaderDescription.Length > 0) header += $" | {source.ShaderDescription}";
        text.AppendLine(header);
        text.AppendLine("//");
        text.AppendLine($"// Shader code view credited to Tectfy — {CreditUrl}");
        if (source.Provenance.Length > 0)
        {
            text.AppendLine("//");
            foreach (var line in Wrap(source.Provenance, 100)) text.AppendLine("// " + line);
        }
        text.AppendLine();

        WritePixelShaderBody(text, source);
        WriteUniformExpressions(text, source);

        return text.ToString();
    }

    // ---------------------------------------------------------------- pixel shader body

    private static void WritePixelShaderBody(StringBuilder text, MaterialCodeViewSource source)
    {
        var wiring = source.Wiring;
        if (wiring == null || wiring.PinExpressions.Count == 0)
        {
            text.AppendLine("// No compiled pixel-shader math was recovered for this material.");
            if (!string.IsNullOrEmpty(wiring?.FailureReason))
                text.AppendLine($"// {wiring.FailureReason}");
            text.AppendLine();
            return;
        }

        // count how many times each node is reached so shared values can be named once
        var uses = new Dictionary<PixelExpressionNode, int>(ReferenceComparer.Instance);
        foreach (var root in wiring.PinExpressions.Values) CountUses(root, uses, new HashSet<PixelExpressionNode>(ReferenceComparer.Instance));

        var names = new Dictionary<PixelExpressionNode, string>(ReferenceComparer.Instance);
        var declarations = new List<string>();
        var emitted = new HashSet<PixelExpressionNode>(ReferenceComparer.Instance);

        // hoist in dependency order so a shared value is declared before anything that reads it
        foreach (var root in wiring.PinExpressions.Values)
            HoistShared(root, uses, names, declarations, emitted, source);

        if (declarations.Count > 0)
        {
            text.AppendLine("// Shared subexpressions (referenced more than once below)");
            foreach (var declaration in declarations) text.AppendLine(declaration);
            text.AppendLine();
        }

        foreach (var (pin, expression) in wiring.PinExpressions)
        {
            var note = wiring.StagePinNotes.TryGetValue(pin, out var stageNote) ? $"  // {stageNote}" : string.Empty;
            text.AppendLine($"{Identifier(pin)} = {Format(expression, names, source, top: true)};{note}");
        }
        text.AppendLine();
    }

    private static void CountUses(PixelExpressionNode node, Dictionary<PixelExpressionNode, int> uses,
        HashSet<PixelExpressionNode> path)
    {
        if (node == null) return;
        uses[node] = uses.GetValueOrDefault(node) + 1;
        if (uses[node] > 1) return;      // already walked through this node once
        if (!path.Add(node)) return;     // cycles cannot happen in a DAG, but never loop forever
        foreach (var argument in node.Args) CountUses(argument.Node, uses, path);
        path.Remove(node);
    }

    private static void HoistShared(PixelExpressionNode node, Dictionary<PixelExpressionNode, int> uses,
        Dictionary<PixelExpressionNode, string> names, List<string> declarations,
        HashSet<PixelExpressionNode> emitted, MaterialCodeViewSource source)
    {
        if (node == null || !emitted.Add(node)) return;
        foreach (var argument in node.Args) HoistShared(argument.Node, uses, names, declarations, emitted, source);

        // a leaf is no cheaper to name than to repeat, so only interior shared values get a name
        if (uses.GetValueOrDefault(node) <= 1 || node.Args.Count == 0) return;
        var name = "_" + names.Count;
        declarations.Add($"var {name} = {Format(node, names, source, top: true)};");
        names[node] = name;
    }

    /// <summary>Prints one node. <paramref name="top"/> suppresses the outer parentheses a nested
    /// expression would need.</summary>
    private static string Format(PixelExpressionNode node, Dictionary<PixelExpressionNode, string> names,
        MaterialCodeViewSource source, bool top = false)
    {
        if (node == null) return "0";
        if (!top && names.TryGetValue(node, out var shared)) return shared;

        var body = FormatBody(node, names, source);
        if (node.Saturate) body = $"saturate({body})";
        return body;
    }

    private static string FormatBody(PixelExpressionNode node, Dictionary<PixelExpressionNode, string> names,
        MaterialCodeViewSource source)
    {
        string Arg(int index) => index < node.Args.Count ? FormatArg(node.Args[index], names, source) : "0";

        switch (node.Op)
        {
            case "imm":
                return FormatConstant(node.Constants);

            case "input":
                // the semantic when the decoder kept one ("TEXCOORD0 (v2)"), otherwise a token
                // saying only what is actually known: that a vertex interpolant supplied the value
                return node.Detail.Length > 0 ? Token(node.Detail) : "VertexInterpolant";

            case "cbrow":
                return FormatCbValue(node, source);

            case "sample":
                return FormatSample(node, names, source);

            case "opaque":
                return $"/* opaque: {node.Detail} */";

            case "discard":
                return $"discard({Arg(0)})";

            case "mask":
                // the surviving channels are the node's own swizzle
                return node.Args.Count > 0 ? Arg(0) : "0";

            case "append":
                return $"float{node.Args.Count}({string.Join(", ", Enumerable.Range(0, node.Args.Count).Select(Arg))})";

            case "phi":
                return node.Args.Count >= 2
                    ? $"/* differs between branches */ ({Arg(0)} : {Arg(1)})"
                    : $"/* {node.Detail} */";

            case "mov":
                return Arg(0);

            case "movc":
                return node.Args.Count >= 3 ? $"({Arg(0)} ? {Arg(1)} : {Arg(2)})" : "0";

            case "mad" or "imad" or "umad":
                return node.Args.Count >= 3 ? $"({Arg(0)} * {Arg(1)} + {Arg(2)})" : "0";

            case "add" or "iadd": return $"({Arg(0)} + {Arg(1)})";
            case "sub": return $"({Arg(0)} - {Arg(1)})";
            case "mul" or "imul" or "umul": return $"({Arg(0)} * {Arg(1)})";
            case "div" or "udiv": return $"({Arg(0)} / {Arg(1)})";

            case "dp2": return $"dot2({Arg(0)}, {Arg(1)})";
            case "dp3": return $"dot3({Arg(0)}, {Arg(1)})";
            case "dp4": return $"dot4({Arg(0)}, {Arg(1)})";

            case "lt": return $"({Arg(0)} < {Arg(1)})";
            case "ge": return $"({Arg(0)} >= {Arg(1)})";
            case "eq": return $"({Arg(0)} == {Arg(1)})";
            case "ne": return $"({Arg(0)} != {Arg(1)})";
            case "and": return $"({Arg(0)} & {Arg(1)})";
            case "or": return $"({Arg(0)} | {Arg(1)})";
            case "xor": return $"({Arg(0)} ^ {Arg(1)})";

            default:
            {
                // every other decoded instruction prints as a call named by its mnemonic, which is
                // exactly what the shader did — no invented friendly name
                var name = FunctionNames.GetValueOrDefault(node.Op, node.Op);
                var arguments = string.Join(", ", Enumerable.Range(0, node.Args.Count).Select(Arg));
                return node.Args.Count == 0 ? name : $"{name}({arguments})";
            }
        }
    }

    /// <summary>Mnemonics whose HLSL spelling differs from the DXBC name.</summary>
    private static readonly Dictionary<string, string> FunctionNames = new(StringComparer.Ordinal)
    {
        ["frc"] = "frac", ["rsq"] = "rsqrt", ["exp"] = "exp2", ["log"] = "log2",
        ["round_ni"] = "floor", ["round_pi"] = "ceil", ["round_ne"] = "round", ["round_z"] = "trunc",
        ["sincos"] = "sincos", ["deriv_rtx"] = "ddx", ["deriv_rty"] = "ddy",
        ["deriv_rtx_coarse"] = "ddx_coarse", ["deriv_rty_coarse"] = "ddy_coarse",
        ["deriv_rtx_fine"] = "ddx_fine", ["deriv_rty_fine"] = "ddy_fine",
    };

    private static string FormatArg(PixelExpressionArg argument, Dictionary<PixelExpressionNode, string> names,
        MaterialCodeViewSource source)
    {
        var value = Format(argument.Node, names, source);
        if (argument.Swizzle.Length > 0) value += "." + argument.Swizzle;
        if (argument.Absolute) value = $"abs({value})";
        if (argument.Negate) value = $"-{value}";
        return value;
    }

    private static string FormatConstant(float[] constants)
    {
        if (constants == null || constants.Length == 0) return "0";
        var values = constants.Select(v => v.ToString("0.####", CultureInfo.InvariantCulture));
        return constants.Length == 1 ? values.First() : $"Const({string.Join(", ", values)})";
    }

    /// <summary>A constant-buffer read: a material uniform gets the parameter's name when the
    /// binding tables name it, and an engine buffer read is labelled as engine data.</summary>
    private static string FormatCbValue(PixelExpressionNode node, MaterialCodeViewSource source)
    {
        if (node.Source is not { } value)
            return node.Detail.Length > 0 ? $"/* {node.Detail} */ 0" : "0";

        switch (value.Kind)
        {
            case PixelValueKind.VectorExpression:
                return source.VectorNames.TryGetValue(value.Index, out var vectorName)
                    ? $"{vectorName} /* UniformVector{value.Index} */"
                    : $"UniformVector{value.Index}";
            case PixelValueKind.ScalarExpression:
                return source.ScalarNames.TryGetValue(value.Index, out var scalarName)
                    ? $"{scalarName} /* UniformScalar{value.Index} */"
                    : $"UniformScalar{value.Index}";
            case PixelValueKind.UniformExpression:
                return $"UniformExpression{value.Index}";
            default:
                return node.Detail.Length > 0 ? $"/* {node.Detail} */ 0" : "0";
        }
    }

    private static string FormatSample(PixelExpressionNode node, Dictionary<PixelExpressionNode, string> names,
        MaterialCodeViewSource source)
    {
        var call = node.Detail.Length > 0 ? node.Detail.Split(' ')[0] : "sample";

        string textureName = null;
        var label = string.Empty;
        if (node.Source is { Kind: PixelValueKind.Texture } texture)
        {
            source.TextureNames.TryGetValue((texture.TextureSlot, texture.Index), out textureName);
            if (textureName != null) label = $" /* [Texture {texture.Index}] */";
        }

        // the coordinate arguments survive only when the decoder kept the UV chain; when pruning
        // collapsed it, naming the texture beats printing an empty argument list
        var arguments = node.Args.Count > 0
            ? string.Join(", ", node.Args.Select(a => FormatArg(a, names, source)))
            : textureName ?? string.Empty;

        var channels = node.ChannelMap is { Length: > 0 }
            ? "." + string.Concat(node.ChannelMap.Select(c => "rgba"[Math.Clamp(c, 0, 3)]))
            : string.Empty;

        return $"{call}({arguments}){label}{channels}";
    }

    // ---------------------------------------------------------------- uniform expressions

    private static void WriteUniformExpressions(StringBuilder text, MaterialCodeViewSource source)
    {
        var preshaders = source.Preshaders;
        if (preshaders is not { AnyDecoded: true }) return;

        text.AppendLine($"// Uniform Expressions — computed on the CPU each frame ({preshaders.OpcodeSetUsed})");
        foreach (var uniform in preshaders.Uniforms)
        {
            var expression = uniform.Expression?.ToDisplayString(12) ?? "0";
            text.AppendLine($"{uniform.TypeDescription} {uniform.Name} = {expression};");
        }

        if (preshaders.FailedCount > 0)
            text.AppendLine($"// {preshaders.FailedCount} uniform expression(s) could not be decoded and are not shown.");
        foreach (var warning in preshaders.Warnings.Distinct())
            text.AppendLine($"// {warning}");
        text.AppendLine();
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Keeps a decoder label readable as a code token without changing what it says.</summary>
    private static string Token(string detail) =>
        detail.Contains(' ') && !detail.Contains('(') ? detail.Replace(' ', '_') : detail;

    /// <summary>Material pin names become identifiers the way the engine writes them.</summary>
    private static string Identifier(string pin) =>
        string.Concat(pin.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == ' ')).Trim().Replace(' ', '_');

    private static IEnumerable<string> Wrap(string text, int width)
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

    /// <summary>Expression nodes are compared by identity — the DAG deliberately reuses a node to
    /// mean "the same value", which is exactly the sharing the text hoists.</summary>
    private sealed class ReferenceComparer : IEqualityComparer<PixelExpressionNode>
    {
        public static readonly ReferenceComparer Instance = new();
        public bool Equals(PixelExpressionNode x, PixelExpressionNode y) => ReferenceEquals(x, y);
        public int GetHashCode(PixelExpressionNode obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using CUE4Parse.UE4.Assets.Exports.Material;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Versions;

namespace FModel.ViewModels;

/// <summary>
/// A single node of a decoded uniform expression ("preshader") tree.
/// Leaves are constants, parameters or texture references, inner nodes are operations.
/// </summary>
public class PreshaderExpression
{
    public string Op { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public List<PreshaderExpression> Inputs { get; } = [];
    public List<string> InputNames { get; } = [];
    public bool IsParameter { get; set; }
    public bool IsConstant { get; set; }
    public bool IsTexture { get; set; }
    public string ParameterName { get; set; } = string.Empty;
    public int TextureIndex { get; set; } = -1;
    public string Detail { get; set; } = string.Empty;

    public void AddInput(string name, PreshaderExpression expression)
    {
        InputNames.Add(name);
        Inputs.Add(expression);
    }

    /// <summary>Compact "Saturate(Mul(Param, 2))"-style preview, depth-limited for shared sub-trees.</summary>
    public string ToDisplayString(int depth = 6)
    {
        if (Inputs.Count == 0)
        {
            if (IsConstant) return Subtitle.Length > 0 ? Subtitle : "0";
            if (IsParameter) return ParameterName.Length > 0 ? ParameterName : Title;
            return Subtitle.Length > 0 ? $"{Title}({Subtitle})" : Title;
        }

        if (depth <= 0) return $"{Title}(…)";
        var arguments = string.Join(", ", Inputs.Select(i => i.ToDisplayString(depth - 1)));
        var detail = Detail.Length > 0 ? $"[{Detail}]" : string.Empty;
        return $"{Title}{detail}({arguments})";
    }

    public override string ToString() => ToDisplayString();
}

/// <summary>One uniform value produced by the shader map, together with its decoded expression tree.</summary>
public class DecodedPreshaderUniform
{
    public string Name { get; set; } = string.Empty;
    public string TypeDescription { get; set; } = string.Empty;
    public PreshaderExpression Expression { get; set; } = null!;
}

public class PreshaderDecodeResult
{
    public List<DecodedPreshaderUniform> Uniforms { get; } = [];
    public List<string> Warnings { get; } = [];
    public string OpcodeSetUsed { get; set; } = string.Empty;
    public int FailedCount { get; set; }
    public bool AnyDecoded => Uniforms.Count > 0;
}

/// <summary>
/// Decodes the compiled uniform expression ("preshader") bytecode kept in a cooked material's
/// inline shader map back into symbolic expression trees. The virtual machine is a byte-packed
/// stack machine whose opcode numbering changed across engine versions, so three opcode tables
/// are kept (4.25-4.27, 5.0-5.2, 5.3-5.7) and the decoder falls back to the neighbouring UE5
/// table when a window fails to validate against the version-preferred one.
/// </summary>
public static class MaterialPreshaderDecoder
{
    #region Canonical opcodes and version tables

    private enum EOp
    {
        Nop, ConstantZero, Constant, ScalarParameter, VectorParameter, Parameter,
        Add, Sub, Mul, Div, Fmod, Modulo, Min, Max, Clamp,
        Sin, Cos, Tan, Asin, Acos, Atan, Atan2, Dot, Cross,
        Sqrt, Rcp, Length, Normalize, Saturate, Abs, Floor, Ceil, Round, Trunc,
        Sign, Frac, Fractional, Log2, Log10, Exp, Exp2, Log,
        ComponentSwizzle, AppendVector, TextureSize, TexelSize,
        ExternalTextureCoordinateScaleRotation, ExternalTextureCoordinateOffset,
        RuntimeVirtualTextureUniform, SparseVolumeTextureUniform,
        GetField, SetField, Neg, Jump, JumpIfFalse, PushValue,
        Less, Assign, Greater, LessEqual, GreaterEqual
    }

    // EMaterialPreshaderOpcode, UE 4.25-4.27 (MaterialShared.h)
    private static readonly EOp[] TablePre5 =
    [
        EOp.Nop, EOp.ConstantZero, EOp.Constant, EOp.ScalarParameter, EOp.VectorParameter,
        EOp.Add, EOp.Sub, EOp.Mul, EOp.Div, EOp.Fmod, EOp.Min, EOp.Max, EOp.Clamp,
        EOp.Sin, EOp.Cos, EOp.Tan, EOp.Asin, EOp.Acos, EOp.Atan, EOp.Atan2, EOp.Dot, EOp.Cross,
        EOp.Sqrt, EOp.Length, EOp.Saturate, EOp.Abs, EOp.Floor, EOp.Ceil, EOp.Round, EOp.Trunc,
        EOp.Sign, EOp.Frac, EOp.Fractional, EOp.Log2, EOp.Log10,
        EOp.ComponentSwizzle, EOp.AppendVector, EOp.TextureSize, EOp.TexelSize,
        EOp.ExternalTextureCoordinateScaleRotation, EOp.ExternalTextureCoordinateOffset,
        EOp.RuntimeVirtualTextureUniform
    ];

    // UE::Shader::EPreshaderOpcode, UE 5.0-5.2 (Preshader.h)
    private static readonly EOp[] TableUE50 =
    [
        EOp.Nop, EOp.ConstantZero, EOp.Constant, EOp.Parameter,
        EOp.Add, EOp.Sub, EOp.Mul, EOp.Div, EOp.Fmod, EOp.Min, EOp.Max, EOp.Clamp,
        EOp.Sin, EOp.Cos, EOp.Tan, EOp.Asin, EOp.Acos, EOp.Atan, EOp.Atan2, EOp.Dot, EOp.Cross,
        EOp.Sqrt, EOp.Rcp, EOp.Length, EOp.Normalize, EOp.Saturate, EOp.Abs, EOp.Floor, EOp.Ceil,
        EOp.Round, EOp.Trunc, EOp.Sign, EOp.Frac, EOp.Fractional, EOp.Log2, EOp.Log10,
        EOp.ComponentSwizzle, EOp.AppendVector, EOp.TextureSize, EOp.TexelSize,
        EOp.ExternalTextureCoordinateScaleRotation, EOp.ExternalTextureCoordinateOffset,
        EOp.RuntimeVirtualTextureUniform,
        EOp.GetField, EOp.SetField, EOp.Neg, EOp.Jump, EOp.JumpIfFalse, EOp.PushValue,
        EOp.Less, EOp.Assign, EOp.Greater, EOp.LessEqual, EOp.GreaterEqual
    ];

    // UE::Shader::EPreshaderOpcode, UE 5.3-5.7: Modulo and SparseVolumeTextureUniform inserted,
    // Exp/Exp2/Log appended (verified identical in 5.5 and 5.7 Preshader.h)
    private static readonly EOp[] TableUE53 =
    [
        EOp.Nop, EOp.ConstantZero, EOp.Constant, EOp.Parameter,
        EOp.Add, EOp.Sub, EOp.Mul, EOp.Div, EOp.Fmod, EOp.Modulo, EOp.Min, EOp.Max, EOp.Clamp,
        EOp.Sin, EOp.Cos, EOp.Tan, EOp.Asin, EOp.Acos, EOp.Atan, EOp.Atan2, EOp.Dot, EOp.Cross,
        EOp.Sqrt, EOp.Rcp, EOp.Length, EOp.Normalize, EOp.Saturate, EOp.Abs, EOp.Floor, EOp.Ceil,
        EOp.Round, EOp.Trunc, EOp.Sign, EOp.Frac, EOp.Fractional, EOp.Log2, EOp.Log10,
        EOp.ComponentSwizzle, EOp.AppendVector, EOp.TextureSize, EOp.TexelSize,
        EOp.ExternalTextureCoordinateScaleRotation, EOp.ExternalTextureCoordinateOffset,
        EOp.RuntimeVirtualTextureUniform, EOp.SparseVolumeTextureUniform,
        EOp.GetField, EOp.SetField, EOp.Neg, EOp.Jump, EOp.JumpIfFalse, EOp.PushValue,
        EOp.Less, EOp.Assign, EOp.Greater, EOp.LessEqual, EOp.GreaterEqual,
        EOp.Exp, EOp.Exp2, EOp.Log
    ];

    #endregion

    #region Public API

    public static PreshaderDecodeResult Decode(FUniformExpressionSet expressionSet, EGame game)
    {
        var result = new PreshaderDecodeResult();
        var preshaderData = expressionSet?.UniformPreshaderData;
        if (preshaderData?.Data is not { Length: > 0 })
        {
            result.Warnings.Add("The shader map contains no preshader bytecode.");
            return result;
        }

        if (preshaderData.bPreshader2)
        {
            result.Warnings.Add("This material uses the UE 5.8+ Preshader2 bytecode which is not supported yet.");
            return result;
        }

        var context = new DecodeContext
        {
            Data = preshaderData.Data,
            Names = preshaderData.Names,
            StructTypes = preshaderData.StructTypes,
            StructComponentTypes = preshaderData.StructComponentTypes,
            ScalarParameters = expressionSet.UniformScalarParameters,
            VectorParameters = expressionSet.UniformVectorParameters,
            NumericParameters = expressionSet.UniformNumericParameters,
            IsUE5 = game >= EGame.GAME_UE5_0
        };

        var windows = CollectWindows(expressionSet, game);
        if (windows.Count == 0)
        {
            result.Warnings.Add("The shader map declares no uniform expressions.");
            return result;
        }

        if (!context.IsUE5)
        {
            RunTable(result, windows, context, TablePre5, "UE4 (4.25-4.27)");
            return result;
        }

        var preferFive3 = game >= EGame.GAME_UE5_3;
        var preferred = preferFive3 ? TableUE53 : TableUE50;
        var preferredName = preferFive3 ? "UE 5.3-5.7" : "UE 5.0-5.2";
        var alternate = preferFive3 ? TableUE50 : TableUE53;
        var alternateName = preferFive3 ? "UE 5.0-5.2" : "UE 5.3-5.7";

        RunTable(result, windows, context, preferred, preferredName);
        if (result.FailedCount == 0) return result;

        // opcode numbering shifted somewhere between 5.2 and 5.5 with no local source to pin it
        // down exactly, so when the version-preferred table fails, the neighbouring table is tried
        var retry = new PreshaderDecodeResult();
        RunTable(retry, windows, context, alternate, alternateName);
        return retry.FailedCount < result.FailedCount ? retry : result;
    }

    /// <summary>Slot names for FUniformExpressionSet.UniformTextureParameters, per version (EMaterialTextureParameterType).</summary>
    public static string GetTextureSlotName(EGame game, int slotIndex)
    {
        string[] slots = game switch
        {
            >= EGame.GAME_UE5_3 => ["Texture 2D", "Texture Cube", "Texture 2D Array", "Texture Cube Array", "Volume Texture", "Virtual Texture", "Sparse Volume Texture", "Texture"],
            >= EGame.GAME_UE5_0 => ["Texture 2D", "Texture Cube", "Texture 2D Array", "Texture Cube Array", "Volume Texture", "Virtual Texture"],
            _ => ["Texture 2D", "Texture Cube", "Texture 2D Array", "Volume Texture", "Virtual Texture"]
        };
        return slotIndex >= 0 && slotIndex < slots.Length ? slots[slotIndex] : "Texture";
    }

    /// <summary>
    /// Human readable material flags recovered from FMaterialCompilationOutput's packed bitfields.
    /// Only flags that are set are returned. Bit positions follow the engine's LAYOUT_BITFIELD
    /// packing, which gained extra bits in 5.3/5.4/5.5.
    /// </summary>
    public static List<KeyValuePair<string, string>> DescribeCompilationOutput(FMaterialCompilationOutput output, EGame game)
    {
        var properties = new List<KeyValuePair<string, string>>();
        if (output == null) return properties;

        var flagNames = new List<string> { "Needs Scene Textures" };
        if (game >= EGame.GAME_UE5_3) flagNames.Add("Uses DBuffer Texture Lookup");
        flagNames.AddRange([
            "Uses Eye Adaptation", "Modifies Mesh Position", "Uses World Position Offset",
            "Uses Global Distance Field", "Uses Pixel Depth Offset", "Uses Distance Cull Fade",
            "Uses Per-Instance Custom Data", "Uses Per-Instance Random", "Uses Vertex Interpolator",
            "Has Runtime Virtual Texture Output", "Uses Anisotropy"
        ]);
        if (game >= EGame.GAME_UE5_3) flagNames.Add("Uses Displacement");
        if (game >= EGame.GAME_UE5_4) flagNames.Add("Used With Neural Networks");
        if (game >= EGame.GAME_UE5_5) flagNames.Add("Uses Customized UVs");

        var packed = output.b1 | (uint) output.b2 << 8;
        for (var bit = 0; bit < flagNames.Count && bit < 16; bit++)
        {
            if ((packed & 1u << bit) != 0)
                properties.Add(new KeyValuePair<string, string>(flagNames[bit], "true"));
        }

        if (output.UsedSceneTextures != 0)
            properties.Add(new KeyValuePair<string, string>("Used Scene Textures", $"0x{output.UsedSceneTextures:X}"));

        return properties;
    }

    #endregion

    #region Preshader window enumeration

    private readonly record struct PreshaderWindow(string Name, string TypeDescription, int Start, int End);

    private static List<PreshaderWindow> CollectWindows(FUniformExpressionSet set, EGame game)
    {
        var windows = new List<PreshaderWindow>();

        void Add(string name, string typeDescription, FMaterialUniformPreshaderHeader header) =>
            windows.Add(new PreshaderWindow(name, typeDescription, (int) header.OpcodeOffset, (int) (header.OpcodeOffset + header.OpcodeSize)));

        if (game < EGame.GAME_UE5_0)
        {
            for (var i = 0; i < set.UniformVectorPreshaders.Length; i++)
                Add($"Vector Expression [{i}]", "Float4", set.UniformVectorPreshaders[i]);
            for (var i = 0; i < set.UniformScalarPreshaders.Length; i++)
                Add($"Scalar Expression [{i}]", "Float1", set.UniformScalarPreshaders[i]);
            return windows;
        }

        for (var i = 0; i < set.UniformPreshaders.Length; i++)
        {
            var header = set.UniformPreshaders[i];
            var typeDescription = header switch
            {
                FMaterialUniformPreshaderHeader_5_8 h58 => $"{h58.Type} @ buffer[{h58.BufferOffset}]",
                FMaterialUniformPreshaderHeader_5_1 h51 => DescribeFields(set.UniformPreshaderFields, h51),
                FMaterialUniformPreshaderHeader_5_0 h50 => $"{h50.ComponentType}{h50.NumComponents} @ buffer[{h50.BufferOffset}]",
                _ => string.Empty
            };
            Add($"Uniform Expression [{i}]", typeDescription, header);
        }
        return windows;
    }

    private static string DescribeFields(FMaterialUniformPreshaderField[] fields, FMaterialUniformPreshaderHeader_5_1 header)
    {
        if (fields == null || header.FieldIndex + header.NumFields > (uint) fields.Length)
            return string.Empty;

        var parts = new List<string>();
        for (var i = 0u; i < header.NumFields; i++)
        {
            var field = fields[header.FieldIndex + i];
            parts.Add($"{field.Type} @ buffer[{field.BufferOffset}]");
        }
        return string.Join(", ", parts);
    }

    private static void RunTable(PreshaderDecodeResult result, List<PreshaderWindow> windows, DecodeContext context, EOp[] table, string tableName)
    {
        result.OpcodeSetUsed = tableName;
        foreach (var window in windows)
        {
            if (window.Start < 0 || window.End > context.Data.Length || window.End <= window.Start)
            {
                if (window.End != window.Start)
                {
                    result.FailedCount++;
                    result.Warnings.Add($"{window.Name}: bytecode window [{window.Start}..{window.End}] is out of range.");
                }
                continue;
            }

            try
            {
                var expression = DecodeWindow(context, table, window.Start, window.End);
                result.Uniforms.Add(new DecodedPreshaderUniform
                {
                    Name = window.Name,
                    TypeDescription = window.TypeDescription,
                    Expression = expression
                });
            }
            catch (PreshaderDecodeException e)
            {
                result.FailedCount++;
                result.Warnings.Add($"{window.Name}: {e.Message}");
            }
        }
    }

    #endregion

    #region Stack machine decoding

    private sealed class DecodeContext
    {
        public byte[] Data;
        public FName[] Names;
        public FPreshaderStructType[] StructTypes;
        public EValueComponentType[] StructComponentTypes;
        public FMaterialScalarParameterInfo[] ScalarParameters;
        public FMaterialVectorParameterInfo[] VectorParameters;
        public FMaterialNumericParameterInfo[] NumericParameters;
        public bool IsUE5;
    }

    private sealed class PreshaderDecodeException(string message) : Exception(message);

    private sealed class ByteReader(byte[] data, int start, int end)
    {
        public int Pos = start;
        public readonly int End = end;

        private void Ensure(int count)
        {
            if (Pos + count > End) throw new PreshaderDecodeException("bytecode window ended in the middle of an instruction");
        }

        public byte U8() { Ensure(1); return data[Pos++]; }
        public ushort U16() { Ensure(2); var v = BitConverter.ToUInt16(data, Pos); Pos += 2; return v; }
        public int I32() { Ensure(4); var v = BitConverter.ToInt32(data, Pos); Pos += 4; return v; }
        public float F32() { Ensure(4); var v = BitConverter.ToSingle(data, Pos); Pos += 4; return v; }
        public double F64() { Ensure(8); var v = BitConverter.ToDouble(data, Pos); Pos += 8; return v; }
        public void Skip(int count) { Ensure(count); Pos += count; }
    }

    private sealed class Branch
    {
        public PreshaderExpression Condition;
        public int ElseTarget;
        public int JoinTarget = -1;
        public PreshaderExpression ThenValue;
    }

    private static PreshaderExpression DecodeWindow(DecodeContext context, EOp[] table, int start, int end)
    {
        var reader = new ByteReader(context.Data, start, end);
        var stack = new List<PreshaderExpression>();
        var branches = new List<Branch>();

        PreshaderExpression Pop()
        {
            if (stack.Count == 0) throw new PreshaderDecodeException("stack underflow");
            var value = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            return value;
        }

        void FinalizeJoins()
        {
            while (branches.Count > 0 && branches[^1].JoinTarget == reader.Pos && branches[^1].ThenValue != null)
            {
                var branch = branches[^1];
                branches.RemoveAt(branches.Count - 1);
                var select = new PreshaderExpression { Op = "If", Title = "If" };
                select.AddInput("Condition", branch.Condition);
                select.AddInput("True", branch.ThenValue);
                select.AddInput("False", Pop());
                stack.Add(select);
            }
        }

        while (reader.Pos < reader.End)
        {
            FinalizeJoins();
            if (reader.Pos >= reader.End) break;

            var opcodeByte = reader.U8();
            if (opcodeByte >= table.Length)
                throw new PreshaderDecodeException($"unknown opcode 0x{opcodeByte:X2}");

            var op = table[opcodeByte];
            switch (op)
            {
                case EOp.Nop:
                    break;

                case EOp.ConstantZero:
                    stack.Add(DecodeConstantZero(context, reader));
                    break;
                case EOp.Constant:
                    stack.Add(DecodeConstant(context, reader));
                    break;
                case EOp.ScalarParameter:
                    stack.Add(DecodeLegacyParameter(context, reader, isVector: false));
                    break;
                case EOp.VectorParameter:
                    stack.Add(DecodeLegacyParameter(context, reader, isVector: true));
                    break;
                case EOp.Parameter:
                    stack.Add(DecodeNumericParameter(context, reader));
                    break;

                case EOp.Add or EOp.Sub or EOp.Mul or EOp.Div or EOp.Fmod or EOp.Modulo or EOp.Min or EOp.Max
                    or EOp.Atan2 or EOp.Dot or EOp.Cross or EOp.AppendVector
                    or EOp.Less or EOp.Greater or EOp.LessEqual or EOp.GreaterEqual:
                {
                    // UE4 serializes an extra operand byte for these: the MCT value type for
                    // Dot/Cross, the component count of the first vector for AppendVector
                    if (!context.IsUE5 && op is EOp.Dot or EOp.Cross or EOp.AppendVector)
                        reader.Skip(1);

                    var node = new PreshaderExpression { Op = op.ToString(), Title = GetOpTitle(op) };
                    var b = Pop();
                    var a = Pop();
                    node.AddInput("A", a);
                    node.AddInput("B", b);
                    stack.Add(node);
                    break;
                }

                case EOp.Sin or EOp.Cos or EOp.Tan or EOp.Asin or EOp.Acos or EOp.Atan or EOp.Sqrt or EOp.Rcp
                    or EOp.Length or EOp.Normalize or EOp.Saturate or EOp.Abs or EOp.Floor or EOp.Ceil
                    or EOp.Round or EOp.Trunc or EOp.Sign or EOp.Frac or EOp.Fractional or EOp.Log2
                    or EOp.Log10 or EOp.Exp or EOp.Exp2 or EOp.Log or EOp.Neg:
                {
                    var node = new PreshaderExpression { Op = op.ToString(), Title = GetOpTitle(op) };
                    node.AddInput("In", Pop());
                    stack.Add(node);
                    break;
                }

                case EOp.Clamp:
                {
                    var node = new PreshaderExpression { Op = "Clamp", Title = "Clamp" };
                    var max = Pop();
                    var min = Pop();
                    var value = Pop();
                    node.AddInput("Value", value);
                    node.AddInput("Min", min);
                    node.AddInput("Max", max);
                    stack.Add(node);
                    break;
                }

                case EOp.ComponentSwizzle:
                {
                    var numElements = reader.U8();
                    Span<byte> indices = [reader.U8(), reader.U8(), reader.U8(), reader.U8()];
                    var mask = new StringBuilder();
                    for (var i = 0; i < Math.Min((int) numElements, 4); i++)
                        mask.Append(indices[i] switch { 0 => 'R', 1 => 'G', 2 => 'B', 3 => 'A', _ => '?' });
                    var node = new PreshaderExpression { Op = "ComponentSwizzle", Title = "Component Mask", Detail = mask.ToString() };
                    node.AddInput("In", Pop());
                    stack.Add(node);
                    break;
                }

                case EOp.TextureSize or EOp.TexelSize:
                {
                    var parameterName = ReadParameterInfoName(context, reader);
                    var textureIndex = reader.I32();
                    stack.Add(new PreshaderExpression
                    {
                        Op = op.ToString(),
                        Title = op == EOp.TextureSize ? "Texture Size" : "Texel Size",
                        Subtitle = parameterName,
                        ParameterName = parameterName,
                        IsTexture = true,
                        TextureIndex = textureIndex
                    });
                    break;
                }

                case EOp.ExternalTextureCoordinateScaleRotation or EOp.ExternalTextureCoordinateOffset:
                {
                    var nameIndex = reader.U16();
                    reader.Skip(16); // external texture FGuid
                    var sourceTextureIndex = reader.I32();
                    stack.Add(new PreshaderExpression
                    {
                        Op = op.ToString(),
                        Title = op == EOp.ExternalTextureCoordinateScaleRotation ? "External Texture Scale/Rotation" : "External Texture Offset",
                        Subtitle = ResolveName(context, nameIndex),
                        IsTexture = true,
                        TextureIndex = sourceTextureIndex
                    });
                    break;
                }

                case EOp.RuntimeVirtualTextureUniform or EOp.SparseVolumeTextureUniform:
                {
                    var parameterName = ReadParameterInfoName(context, reader);
                    var textureIndex = reader.I32();
                    var vectorIndex = reader.I32();
                    stack.Add(new PreshaderExpression
                    {
                        Op = op.ToString(),
                        Title = op == EOp.RuntimeVirtualTextureUniform ? "Runtime Virtual Texture Uniform" : "Sparse Volume Texture Uniform",
                        Subtitle = parameterName,
                        ParameterName = parameterName,
                        IsTexture = true,
                        TextureIndex = textureIndex,
                        Detail = $"Vector[{vectorIndex}]"
                    });
                    break;
                }

                case EOp.GetField:
                {
                    var (type, _) = ReadPreshaderType(context, reader);
                    var componentIndex = reader.I32();
                    var node = new PreshaderExpression
                    {
                        Op = "GetField", Title = "Get Field",
                        Detail = $"{type} @ [{componentIndex}]"
                    };
                    node.AddInput("Struct", Pop());
                    stack.Add(node);
                    break;
                }

                case EOp.SetField:
                {
                    var componentIndex = reader.I32();
                    var componentNum = reader.I32();
                    var value = Pop();
                    if (stack.Count == 0) throw new PreshaderDecodeException("stack underflow");
                    var node = new PreshaderExpression
                    {
                        Op = "SetField", Title = "Set Field",
                        Detail = $"[{componentIndex}..{componentIndex + Math.Max(componentNum - 1, 0)}]"
                    };
                    node.AddInput("Struct", stack[^1]);
                    node.AddInput("Value", value);
                    stack[^1] = node; // the engine modifies the struct value in place
                    break;
                }

                case EOp.PushValue:
                {
                    var stackOffset = reader.U16();
                    var index = stack.Count - 1 - stackOffset;
                    if (index < 0 || index >= stack.Count) throw new PreshaderDecodeException("PushValue offset out of range");
                    stack.Add(stack[index]);
                    break;
                }

                case EOp.Assign:
                {
                    var value = Pop();
                    Pop(); // replaced value
                    stack.Add(value);
                    break;
                }

                case EOp.JumpIfFalse:
                {
                    var offset = reader.I32();
                    branches.Add(new Branch { Condition = Pop(), ElseTarget = reader.Pos + offset });
                    break;
                }

                case EOp.Jump:
                {
                    var offset = reader.I32();
                    // the only jump the material translator emits is the then-branch terminator of
                    // an if/else pair; anything else is control flow this decoder cannot represent
                    if (branches.Count == 0 || branches[^1].ThenValue != null || branches[^1].ElseTarget != reader.Pos)
                        throw new PreshaderDecodeException("unsupported control flow (unpaired jump)");
                    branches[^1].JoinTarget = reader.Pos + offset;
                    branches[^1].ThenValue = Pop();
                    break;
                }

                default:
                    throw new PreshaderDecodeException($"unhandled opcode {op}");
            }
        }

        FinalizeJoins();

        if (reader.Pos != reader.End) throw new PreshaderDecodeException("bytecode window was not fully consumed");
        if (branches.Count > 0) throw new PreshaderDecodeException("unterminated conditional branch");
        if (stack.Count != 1) throw new PreshaderDecodeException($"expected 1 result value, got {stack.Count}");
        return stack[0];
    }

    #endregion

    #region Operand decoding helpers

    private static PreshaderExpression DecodeConstantZero(DecodeContext context, ByteReader reader)
    {
        var detail = string.Empty;
        if (context.IsUE5)
        {
            var (type, _) = ReadPreshaderType(context, reader);
            detail = type.ToString();
        }
        return new PreshaderExpression { Op = "Constant", Title = "Constant", Subtitle = "0", Detail = detail, IsConstant = true };
    }

    private static PreshaderExpression DecodeConstant(DecodeContext context, ByteReader reader)
    {
        if (!context.IsUE5)
        {
            // UE4 constants are always a full FLinearColor
            var r = reader.F32();
            var g = reader.F32();
            var b = reader.F32();
            var a = reader.F32();
            var display = r == g && g == b && b == a
                ? r.ToString("0.####")
                : $"({r:0.####}, {g:0.####}, {b:0.####}, {a:0.####})";
            return new PreshaderExpression { Op = "Constant", Title = "Constant", Subtitle = display, IsConstant = true };
        }

        var (type, structIndex) = ReadPreshaderType(context, reader);
        var components = new List<string>();

        if (type == EShaderValueType.Struct)
        {
            if (context.StructTypes == null || structIndex < 0 || structIndex >= context.StructTypes.Length)
                throw new PreshaderDecodeException("constant references an unknown struct type");

            var structType = context.StructTypes[structIndex];
            for (var i = 0; i < structType.NumComponents; i++)
            {
                var componentTypeIndex = structType.ComponentTypeIndex + i;
                if (context.StructComponentTypes == null || componentTypeIndex < 0 || componentTypeIndex >= context.StructComponentTypes.Length)
                    throw new PreshaderDecodeException("constant references an unknown struct component type");
                components.Add(ReadComponent(reader, context.StructComponentTypes[componentTypeIndex]));
            }

            return new PreshaderExpression
            {
                Op = "Constant", Title = "Constant",
                Subtitle = components.Count <= 4 ? $"({string.Join(", ", components)})" : $"Struct ({components.Count} components)",
                Detail = "Struct",
                IsConstant = true
            };
        }

        var componentType = GetComponentType(type);
        var count = GetNumComponents(type);
        if (count == 0) throw new PreshaderDecodeException($"constant of non-numeric type {type}");
        for (var i = 0; i < count; i++)
            components.Add(ReadComponent(reader, componentType));

        return new PreshaderExpression
        {
            Op = "Constant", Title = "Constant",
            Subtitle = components.Count == 1 ? components[0] : $"({string.Join(", ", components)})",
            Detail = type.ToString(),
            IsConstant = true
        };
    }

    private static string ReadComponent(ByteReader reader, EValueComponentType componentType) => componentType switch
    {
        EValueComponentType.Float => reader.F32().ToString("0.####"),
        EValueComponentType.Double or EValueComponentType.Numeric => reader.F64().ToString("0.####"),
        EValueComponentType.Int => reader.I32().ToString(),
        EValueComponentType.Bool => (reader.U8() != 0).ToString(),
        _ => throw new PreshaderDecodeException($"constant with component type {componentType}")
    };

    private static PreshaderExpression DecodeLegacyParameter(DecodeContext context, ByteReader reader, bool isVector)
    {
        var index = reader.U16();
        var parameters = isVector ? (FMaterialBaseParameterInfo[]) context.VectorParameters : context.ScalarParameters;
        if (parameters == null || index >= parameters.Length)
            throw new PreshaderDecodeException($"{(isVector ? "vector" : "scalar")} parameter index {index} out of range");

        var parameter = parameters[index];
        var name = parameter.ParameterInfo?.Name.Text ?? parameter.ParameterName ?? $"#{index}";
        var defaultValue = parameter switch
        {
            FMaterialScalarParameterInfo scalar => scalar.DefaultValue.ToString("0.####"),
            FMaterialVectorParameterInfo vector => FormatColor(vector.DefaultValue),
            _ => string.Empty
        };

        return new PreshaderExpression
        {
            Op = isVector ? "VectorParameter" : "ScalarParameter",
            Title = isVector ? "Vector Parameter" : "Scalar Parameter",
            Subtitle = defaultValue.Length > 0 ? $"{name} = {defaultValue}" : name,
            ParameterName = name,
            IsParameter = true
        };
    }

    private static PreshaderExpression DecodeNumericParameter(DecodeContext context, ByteReader reader)
    {
        var index = reader.U16();
        if (context.NumericParameters == null || index >= context.NumericParameters.Length)
            throw new PreshaderDecodeException($"numeric parameter index {index} out of range");

        var parameter = context.NumericParameters[index];
        var name = parameter.ParameterInfo?.Name.Text ?? $"#{index}";
        var defaultValue = parameter.Value switch
        {
            float f => f.ToString("0.####"),
            bool flag => flag.ToString(),
            FLinearColor color => FormatColor(color),
            FVector4 vector => $"({vector.X:0.####}, {vector.Y:0.####}, {vector.Z:0.####}, {vector.W:0.####})",
            { } value => value.ToString(),
            null => string.Empty
        };

        return new PreshaderExpression
        {
            Op = "Parameter",
            Title = parameter.ParameterType switch
            {
                EMaterialParameterType.Scalar => "Scalar Parameter",
                EMaterialParameterType.Vector => "Vector Parameter",
                EMaterialParameterType.DoubleVector => "Double Vector Parameter",
                EMaterialParameterType.StaticSwitch => "Static Switch",
                _ => "Parameter"
            },
            Subtitle = defaultValue.Length > 0 ? $"{name} = {defaultValue}" : name,
            ParameterName = name,
            IsParameter = true
        };
    }

    private static (EShaderValueType type, int structIndex) ReadPreshaderType(DecodeContext context, ByteReader reader)
    {
        var type = (EShaderValueType) reader.U8();
        if (type >= EShaderValueType.Num) throw new PreshaderDecodeException($"invalid value type 0x{(byte) type:X2}");
        var structIndex = type == EShaderValueType.Struct ? reader.U16() : -1;
        return (type, structIndex);
    }

    /// <summary>FHashedMaterialParameterInfo as written into preshader data: name index, Index, Association.</summary>
    private static string ReadParameterInfoName(DecodeContext context, ByteReader reader)
    {
        var nameIndex = reader.U16();
        reader.I32(); // ParameterInfo.Index
        reader.U8();  // ParameterInfo.Association
        return ResolveName(context, nameIndex);
    }

    private static string ResolveName(DecodeContext context, ushort nameIndex) =>
        context.Names != null && nameIndex < context.Names.Length ? context.Names[nameIndex].Text : $"#{nameIndex}";

    private static int GetNumComponents(EShaderValueType type) => type switch
    {
        EShaderValueType.Float1 or EShaderValueType.Double1 or EShaderValueType.Int1 or EShaderValueType.Bool1 or EShaderValueType.Numeric1 => 1,
        EShaderValueType.Float2 or EShaderValueType.Double2 or EShaderValueType.Int2 or EShaderValueType.Bool2 or EShaderValueType.Numeric2 => 2,
        EShaderValueType.Float3 or EShaderValueType.Double3 or EShaderValueType.Int3 or EShaderValueType.Bool3 or EShaderValueType.Numeric3 => 3,
        EShaderValueType.Float4 or EShaderValueType.Double4 or EShaderValueType.Int4 or EShaderValueType.Bool4 or EShaderValueType.Numeric4 => 4,
        EShaderValueType.Float4x4 or EShaderValueType.Double4x4 or EShaderValueType.DoubleInverse4x4 or EShaderValueType.Numeric4x4 => 16,
        _ => 0
    };

    private static EValueComponentType GetComponentType(EShaderValueType type) => type switch
    {
        EShaderValueType.Float1 or EShaderValueType.Float2 or EShaderValueType.Float3 or EShaderValueType.Float4 or EShaderValueType.Float4x4 => EValueComponentType.Float,
        EShaderValueType.Double1 or EShaderValueType.Double2 or EShaderValueType.Double3 or EShaderValueType.Double4
            or EShaderValueType.Double4x4 or EShaderValueType.DoubleInverse4x4 => EValueComponentType.Double,
        EShaderValueType.Int1 or EShaderValueType.Int2 or EShaderValueType.Int3 or EShaderValueType.Int4 => EValueComponentType.Int,
        EShaderValueType.Bool1 or EShaderValueType.Bool2 or EShaderValueType.Bool3 or EShaderValueType.Bool4 => EValueComponentType.Bool,
        EShaderValueType.Numeric1 or EShaderValueType.Numeric2 or EShaderValueType.Numeric3 or EShaderValueType.Numeric4
            or EShaderValueType.Numeric4x4 => EValueComponentType.Numeric,
        _ => EValueComponentType.Void
    };

    private static string GetOpTitle(EOp op) => op switch
    {
        EOp.Add => "Add",
        EOp.Sub => "Subtract",
        EOp.Mul => "Multiply",
        EOp.Div => "Divide",
        EOp.Fmod or EOp.Modulo => "Fmod",
        EOp.Min => "Min",
        EOp.Max => "Max",
        EOp.Atan2 => "Atan2",
        EOp.Dot => "Dot Product",
        EOp.Cross => "Cross Product",
        EOp.AppendVector => "Append Vector",
        EOp.Neg => "Negate",
        EOp.Frac or EOp.Fractional => "Frac",
        EOp.Rcp => "Reciprocal",
        EOp.Less => "Less",
        EOp.Greater => "Greater",
        EOp.LessEqual => "Less Equal",
        EOp.GreaterEqual => "Greater Equal",
        _ => op.ToString()
    };

    private static string FormatColor(FLinearColor color) =>
        $"(R={color.R:0.###}, G={color.G:0.###}, B={color.B:0.###}, A={color.A:0.###})";

    #endregion
}

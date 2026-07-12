using System;
using System.Collections.Generic;
using CUE4Parse.Compression;
using CUE4Parse.UE4.Objects.Core.Math;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using CUE4Parse.UE4.Readers;
using CUE4Parse.UE4.Versions;
using Newtonsoft.Json;
using Serilog;

namespace CUE4Parse.UE4.Assets.Exports.Material;

// =====================================================================================================
// UE 4.23/4.24 (pre-FMemoryImage) cooked inline shader map format.
// All layouts decoded 1:1 from the UE 4.23 engine source (E:\EpicGames\UE_4.23):
//  - FMaterial::SerializeInlineShaderMap            Engine/Private/Materials/MaterialShared.cpp
//  - FMaterialShaderMap::Serialize                  Engine/Private/Materials/MaterialShader.cpp
//  - FMaterialShaderMapId::Serialize (cooked)       Engine/Private/Materials/MaterialShader.cpp
//  - FMaterialCompilationOutput::Serialize          Engine/Private/Materials/MaterialShared.cpp
//  - FUniformExpressionSet::Serialize + expressions Engine/Private/Materials/MaterialUniformExpressions.*
//  - TShaderMap::SerializeInline                    RenderCore/Public/Shader.h
//  - FShader::SerializeBase                         RenderCore/Private/Shader.cpp
//  - FShaderResource::Serialize                     RenderCore/Private/Shader.cpp
//  - FMaterialShader/FMeshMaterialShader::Serialize Renderer/Private/ShaderBaseClasses.cpp
//  - TBasePassPixelShaderPolicyParamType::Serialize Renderer/Private/BasePassRendering.h
//  - FVertexFactoryParameterRef operator<<          RenderCore/Private/VertexFactory.cpp
// NOTE: all Seek/Tell offsets written by the cooker (shader end offsets, vertex factory parameter
// skip offsets) are RELATIVE to FMaterialResourceProxyReader::OffsetToFirstResource.
// =====================================================================================================

/// <summary>FMaterialShaderMap serialized with the legacy (pre-FMemoryImage, &lt; 4.25) format.</summary>
public class FMaterialShaderMapLegacy
{
    public FMaterialShaderMapIdLegacy ShaderMapId;
    public EShaderPlatform ShaderPlatform;
    public string FriendlyName;
    public FMaterialCompilationOutputLegacy MaterialCompilationOutput;
    public string DebugDescription;
    /// <summary>Material (non-mesh) shaders of the map.</summary>
    public FShaderLegacy[] Shaders = [];
    /// <summary>Mesh material shader maps, keyed by vertex factory type name.</summary>
    public FMeshMaterialShaderMapLegacy[] MeshShaderMaps = [];
    /// <summary>
    /// Byte-layout profile the map was parsed with. On PreVirtualTexture branches the
    /// FMaterialCompilationOutput flag fields are skipped, so they keep their defaults.
    /// </summary>
    public ELegacyShaderMapProfile ParsedProfile;

    /// <summary>
    /// Deserializes the map and returns whether the stream stayed aligned all the way to the
    /// trailing bCooked flag — the caller uses this to detect a wrong byte-layout profile.
    /// </summary>
    public bool Deserialize(FMaterialResourceProxyReader Ar)
    {
        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
        {
            var mapPos = Ar.Position;
            var peek = Ar.ReadBytes((int) Math.Min(112, Ar.Length - Ar.Position));
            Log.Verbose("LegacyShaderMap: map start at {0} ({1}): {2}", mapPos, Ar.LegacyProfile, Convert.ToHexString(peek));
            Ar.Position = mapPos;
        }

        ParsedProfile = Ar.LegacyProfile;
        ShaderMapId = new FMaterialShaderMapIdLegacy(Ar);

        ShaderPlatform = (EShaderPlatform) Ar.Read<int>();
        FriendlyName = Ar.ReadFString(false); // raw FString, not name-map based
        var compilationOutputPos = Ar.Position;
        Log.Verbose("LegacyShaderMap '{0}': compilation output at {1}", FriendlyName, Ar.Position);
        MaterialCompilationOutput = new FMaterialCompilationOutputLegacy(Ar, FriendlyName);
        Log.Verbose("LegacyShaderMap: debug description at {0}", Ar.Position);
        DebugDescription = Ar.ReadFString(false);

        // TShaderMap<FMaterialShaderType>::SerializeInline(Ar, true, false, bLoadedByCookedMaterial)
        Log.Verbose("LegacyShaderMap: material shaders at {0}", Ar.Position);
        Shaders = SerializeInline(Ar);

        // Mesh material shaders: count + (VFType name + inline map) per entry
        Log.Verbose("LegacyShaderMap: mesh shader maps at {0}", Ar.Position);
        var numMeshShaderMaps = Ar.Read<int>();
        MeshShaderMaps = new FMeshMaterialShaderMapLegacy[numMeshShaderMaps];
        for (var i = 0; i < numMeshShaderMaps; i++)
        {
            MeshShaderMaps[i] = new FMeshMaterialShaderMapLegacy
            {
                VertexFactoryTypeName = Ar.ReadFName().Text,
                Shaders = SerializeInline(Ar)
            };
        }

        var bCookedPos = Ar.Position;
        var bCooked = Ar.ReadBoolean();
        // cooked data always writes bCooked=true here; reading anything else means the
        // stream drifted and the byte-layout profile does not match this branch's format
        if (!bCooked && Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
        {
            Ar.Position = compilationOutputPos;
            var context = Ar.ReadBytes((int) Math.Min(bCookedPos + 96 - compilationOutputPos, Ar.Length - Ar.Position));
            Log.Verbose("LegacyShaderMap: compilation output bytes ({0}..{1}): {2}", compilationOutputPos, bCookedPos, Convert.ToHexString(context));
            Ar.Position = bCookedPos + 4;
        }
        return bCooked;
    }

    /// <summary>TShaderMap::SerializeInline load path: shaders + shader pipelines (pipelines are consumed but skipped).</summary>
    internal static FShaderLegacy[] SerializeInline(FMaterialResourceProxyReader Ar)
    {
        var numShaders = Ar.Read<int>();
        Log.Verbose("LegacyShaderMap: SerializeInline {0} shaders at {1}", numShaders, Ar.Position);
        var shaders = new List<FShaderLegacy>(numShaders);
        for (var i = 0; i < numShaders; i++)
        {
            var shader = FShaderLegacy.Read(Ar);
            if (shader != null) shaders.Add(shader);
        }

        var numPipelines = Ar.Read<int>();
        for (var p = 0; p < numPipelines; p++)
        {
            Ar.ReadFName(); // FShaderPipelineType name
            var numStages = Ar.Read<int>();
            for (var s = 0; s < numStages; s++)
            {
                var shader = FShaderLegacy.Read(Ar);
                if (shader != null) shaders.Add(shader);
            }
        }

        return shaders.ToArray();
    }
}

public class FMeshMaterialShaderMapLegacy
{
    public string VertexFactoryTypeName;
    public FShaderLegacy[] Shaders = [];
}

/// <summary>Cooked FMaterialShaderMapId (&lt; 4.25): quality + feature level + id hash, no layout params.</summary>
public class FMaterialShaderMapIdLegacy
{
    public EMaterialQualityLevel QualityLevel;
    public ERHIFeatureLevel FeatureLevel;
    public FSHAHash CookedShaderMapIdHash;

    public FMaterialShaderMapIdLegacy(FArchive Ar)
    {
        QualityLevel = (EMaterialQualityLevel) Ar.Read<int>();
        FeatureLevel = (ERHIFeatureLevel) Ar.Read<int>();
        CookedShaderMapIdHash = new FSHAHash(Ar);
    }
}

/// <summary>FMaterialCompilationOutput (&lt; 4.25).</summary>
public class FMaterialCompilationOutputLegacy
{
    public FUniformExpressionSetLegacy UniformExpressionSet;
    public uint UsedSceneTextures;
    public bool bUsesEyeAdaptation;
    public bool bModifiesMeshPosition;
    public bool bUsesWorldPositionOffset;
    public bool bUsesGlobalDistanceField;
    public bool bUsesPixelDepthOffset;
    public bool bUsesDistanceCullFade;
    public bool bHasRuntimeVirtualTextureOutput;

    public FMaterialCompilationOutputLegacy(FMaterialResourceProxyReader Ar, string friendlyName)
    {
        UniformExpressionSet = new FUniformExpressionSetLegacy(Ar);

        if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
        {
            var tailPos = Ar.Position;
            var peek = Ar.ReadBytes((int) Math.Min(120, Ar.Length - Ar.Position));
            Log.Verbose("LegacyShaderMap: compilation output tail at {0} ({1}): {2}", tailPos, Ar.LegacyProfile, Convert.ToHexString(peek));
            Ar.Position = tailPos;
        }

        if (Ar.LegacyProfile == ELegacyShaderMapProfile.PreVirtualTexture)
        {
            // Pre-VT branch tail: scene-texture/estimate/flag fields whose exact field map is
            // not public source, so they are skipped rather than misattributed. The size is NOT
            // constant: 62 bytes on most Fortnite Season X materials, but longer on some — e.g.
            // M_FN_Character_MASTER measures 78, exactly one 16-byte GUID more, consistent with
            // its material parameter collection reference. Instead of a fixed skip, anchor on
            // the DebugDescription FString that always follows the tail: int32 length +
            // "Compiling <FriendlyName>: ". The trailing bCooked flag still validates the
            // whole map parse.
            SkipToDebugDescription(Ar, friendlyName);
            return;
        }

        UsedSceneTextures = Ar.Read<uint>();
        // cooked (non-editor) estimates: 3x uint16 + 2x uint8
        Ar.Position += 3 * sizeof(ushort) + 2 * sizeof(byte);
        var packedFlags = Ar.Read<byte>();
        bUsesEyeAdaptation              = ((packedFlags >> 0) & 1) != 0;
        bModifiesMeshPosition           = ((packedFlags >> 1) & 1) != 0;
        bUsesWorldPositionOffset        = ((packedFlags >> 2) & 1) != 0;
        bUsesGlobalDistanceField        = ((packedFlags >> 3) & 1) != 0;
        bUsesPixelDepthOffset           = ((packedFlags >> 4) & 1) != 0;
        bUsesDistanceCullFade           = ((packedFlags >> 5) & 1) != 0;
        bHasRuntimeVirtualTextureOutput = ((packedFlags >> 6) & 1) != 0;
    }

    /// <summary>
    /// Positions the reader on the DebugDescription FString ("Compiling &lt;FriendlyName&gt;: …")
    /// that terminates the compilation-output tail. The match requires a plausible FString
    /// length immediately followed by the exact ASCII prefix, so a false hit inside the
    /// sub-1KB tail is practically impossible — and the map's trailing bCooked flag would
    /// still reject one. Throws when no anchor is found so the profile retry can react.
    /// </summary>
    private static void SkipToDebugDescription(FMaterialResourceProxyReader Ar, string friendlyName)
    {
        var start = Ar.Position;
        var prefix = System.Text.Encoding.ASCII.GetBytes($"Compiling {friendlyName}: ");
        var window = (int) Math.Min(1024 + prefix.Length + 4, Ar.Length - start);
        var bytes = Ar.ReadBytes(window);

        for (var offset = 0; offset + 4 + prefix.Length <= window; offset++)
        {
            // ANSI FString length incl. null terminator; instance shader maps can carry very
            // long DebugDescriptions (static permutation dumps, 10k+ chars), so the only upper
            // bound is that the string must fit in the remaining stream
            var length = BitConverter.ToInt32(bytes, offset);
            if (length <= prefix.Length || start + offset + 4 + length > Ar.Length) continue;

            var matches = true;
            for (var b = 0; b < prefix.Length; b++)
            {
                if (bytes[offset + 4 + b] == prefix[b]) continue;
                matches = false;
                break;
            }
            if (!matches) continue;

            Log.Verbose("LegacyShaderMap: compilation output tail is {0} bytes (DebugDescription anchor)", offset);
            Ar.Position = start + offset; // the caller reads the FString itself
            return;
        }

        throw new InvalidOperationException(
            $"DebugDescription anchor 'Compiling {friendlyName}: ' not found after the uniform expression set");
    }
}

/// <summary>
/// FUniformExpressionSet (&lt; 4.25): real serialized FMaterialUniformExpression trees
/// (polymorphic by registered type name), not preshader bytecode.
/// </summary>
public class FUniformExpressionSetLegacy
{
    public FMaterialUniformExpressionLegacy[] UniformVectorExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformScalarExpressions = [];
    public FMaterialUniformExpressionLegacy[] Uniform2DTextureExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformCubeTextureExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformVolumeTextureExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformVirtualTextureExpressions = [];
    public FMaterialUniformExpressionLegacy[] UniformExternalTextureExpressions = [];
    public FMaterialVirtualTextureStackLegacy[] VTStacks = [];
    public FGuid[] ParameterCollections = [];

    public FUniformExpressionSetLegacy(FMaterialResourceProxyReader Ar)
    {
        UniformVectorExpressions = ReadExpressionArray(Ar);
        UniformScalarExpressions = ReadExpressionArray(Ar);
        Uniform2DTextureExpressions = ReadExpressionArray(Ar);
        UniformCubeTextureExpressions = ReadExpressionArray(Ar);
        UniformVolumeTextureExpressions = ReadExpressionArray(Ar);
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.PreVirtualTexture)
        {
            // pre-VT branches: no virtual texture arrays/stacks, no reserved 2D array slot
            UniformExternalTextureExpressions = ReadExpressionArray(Ar);
            ParameterCollections = Ar.ReadArray<FGuid>();
            return;
        }
        UniformVirtualTextureExpressions = ReadExpressionArray(Ar);
        UniformExternalTextureExpressions = ReadExpressionArray(Ar);
        VTStacks = Ar.ReadArray(() => new FMaterialVirtualTextureStackLegacy(Ar));
        ReadExpressionArray(Ar); // Uniform2DTextureArrayExpressions - reserved, always empty in 4.23
        ParameterCollections = Ar.ReadArray<FGuid>();
    }

    private static FMaterialUniformExpressionLegacy[] ReadExpressionArray(FMaterialResourceProxyReader Ar)
    {
        var num = Ar.Read<int>();
        var result = new FMaterialUniformExpressionLegacy[num];
        for (var i = 0; i < num; i++)
        {
            result[i] = FMaterialUniformExpressionLegacy.Read(Ar);
        }
        return result;
    }
}

/// <summary>FMaterialVirtualTextureStack (&lt; 4.25 layout: layer count + indices + preallocated index).</summary>
public class FMaterialVirtualTextureStackLegacy
{
    public uint NumLayers;
    public int[] LayerUniformExpressionIndices = [];
    public int PreallocatedStackTextureIndex;

    public FMaterialVirtualTextureStackLegacy(FArchive Ar)
    {
        NumLayers = Ar.Read<uint>();
        LayerUniformExpressionIndices = Ar.ReadArray<int>((int) NumLayers);
        PreallocatedStackTextureIndex = Ar.Read<int>();
    }
}

/// <summary>
/// A deserialized FMaterialUniformExpression tree node. TypeName is the engine class name
/// (e.g. FMaterialUniformExpressionScalarParameter); Operands are the nested expressions.
/// </summary>
public class FMaterialUniformExpressionLegacy
{
    public string TypeName;
    /// <summary>Nested sub-expressions with their slot names ("X", "A", "B", "Input", "Min", "Max", "Texture").</summary>
    public List<KeyValuePair<string, FMaterialUniformExpressionLegacy>> Operands = [];
    /// <summary>Scalar display values (op names, indices, defaults...) keyed by field name.</summary>
    public List<KeyValuePair<string, string>> Values = [];

    // Typed fields used by graph building (populated per type where applicable)
    public string? ParameterName;
    public int ParameterAssociation = -1;
    public int ParameterIndex;
    public FLinearColor? ConstantValue;
    public float? ScalarDefault;
    public FLinearColor? VectorDefault;
    public int TextureIndex = -1;
    public int TextureLayerIndex = -1;
    public int SamplerSource = -1;
    public bool bVirtualTexture;
    public string? OpName;

    private static readonly string[] FoldedMathOps = ["Add", "Sub", "Mul", "Div", "Dot", "Cross"];
    private static readonly string[] TrigMathOps = ["Sin", "Cos", "Tan", "Asin", "Acos", "Atan", "Atan2"];

    public static FMaterialUniformExpressionLegacy Read(FMaterialResourceProxyReader Ar)
    {
        var typeNamePos = Ar.Position;
        var typeName = Ar.ReadFName().Text;
        var expr = new FMaterialUniformExpressionLegacy { TypeName = typeName };
        try
        {
            expr.Deserialize(Ar);
        }
        catch (NotSupportedException) when (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
        {
            var resume = Ar.Position;
            Ar.Position = Math.Max(0, typeNamePos - 96);
            var context = Ar.ReadBytes((int) Math.Min(160, Ar.Length - Ar.Position));
            Log.Verbose("LegacyShaderMap: unknown expression type '{0}' at {1}, bytes from {2}: {3}",
                typeName, typeNamePos, Math.Max(0, typeNamePos - 96), Convert.ToHexString(context));
            Ar.Position = resume;
            throw;
        }
        return expr;
    }

    private void ReadParameterInfo(FMaterialResourceProxyReader Ar)
    {
        // FMaterialParameterInfo: FName Name, TEnumAsByte Association, int32 Index
        ParameterName = Ar.ReadFName().Text;
        ParameterAssociation = Ar.Read<byte>();
        ParameterIndex = Ar.Read<int>();
        Values.Add(new("Parameter", ParameterName));
    }

    private void ReadTextureBase(FMaterialResourceProxyReader Ar)
    {
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.PreVirtualTexture)
        {
            // pre-VT FMaterialUniformExpressionTexture::Serialize: int32 TextureIndex, int32 SamplerSource
            TextureIndex = Ar.Read<int>();
            SamplerSource = Ar.Read<int>();
            Values.Add(new("Texture Index", TextureIndex.ToString()));
            return;
        }

        // FMaterialUniformExpressionTexture::Serialize: int32 TextureIndex, int32 LayerIndex, int32 SamplerSource, bool bVirtualTexture
        TextureIndex = Ar.Read<int>();
        TextureLayerIndex = Ar.Read<int>();
        SamplerSource = Ar.Read<int>();
        bVirtualTexture = Ar.ReadBoolean();
        Values.Add(new("Texture Index", TextureIndex.ToString()));
        if (bVirtualTexture) Values.Add(new("Virtual Texture", "true"));
    }

    private void ReadChild(FMaterialResourceProxyReader Ar, string slot)
        => Operands.Add(new(slot, Read(Ar)));

    private void Deserialize(FMaterialResourceProxyReader Ar)
    {
        switch (TypeName)
        {
            case "FMaterialUniformExpressionConstant":
            {
                ConstantValue = Ar.Read<FLinearColor>();
                var valueType = Ar.Read<byte>();
                Values.Add(new("Value", ConstantValue.Value.ToString()));
                Values.Add(new("Value Type", valueType.ToString()));
                break;
            }
            case "FMaterialUniformExpressionVectorParameter":
                ReadParameterInfo(Ar);
                VectorDefault = Ar.Read<FLinearColor>();
                Values.Add(new("Default", VectorDefault.Value.ToString()));
                break;
            case "FMaterialUniformExpressionScalarParameter":
                ReadParameterInfo(Ar);
                ScalarDefault = Ar.Read<float>();
                Values.Add(new("Default", ScalarDefault.Value.ToString()));
                break;
            case "FMaterialUniformExpressionTexture":
            case "FMaterialUniformExpressionFlipBookTextureParameter": // no extra fields over the texture base
                ReadTextureBase(Ar);
                break;
            case "FMaterialUniformExpressionTextureParameter":
                ReadParameterInfo(Ar);
                ReadTextureBase(Ar);
                break;
            case "FMaterialUniformExpressionExternalTextureBase":
            case "FMaterialUniformExpressionExternalTexture":
            {
                TextureIndex = Ar.Read<int>(); // SourceTextureIndex
                var guid = Ar.Read<FGuid>();
                Values.Add(new("Source Texture Index", TextureIndex.ToString()));
                Values.Add(new("External Texture Guid", guid.ToString()));
                break;
            }
            case "FMaterialUniformExpressionExternalTextureParameter":
            {
                ParameterName = Ar.ReadFName().Text;
                Values.Add(new("Parameter", ParameterName));
                goto case "FMaterialUniformExpressionExternalTextureBase";
            }
            case "FMaterialUniformExpressionExternalTextureCoordinateScaleRotation":
            case "FMaterialUniformExpressionExternalTextureCoordinateOffset":
            {
                // TOptional<FName> parameter name: bool (uint32) + FName when set
                if (Ar.ReadBoolean())
                {
                    ParameterName = Ar.ReadFName().Text;
                    Values.Add(new("Parameter", ParameterName));
                }
                goto case "FMaterialUniformExpressionExternalTextureBase";
            }
            case "FMaterialUniformExpressionRuntimeVirtualTextureParameter":
            {
                TextureIndex = Ar.Read<int>();
                var paramIndex = Ar.Read<int>();
                Values.Add(new("Texture Index", TextureIndex.ToString()));
                Values.Add(new("Param Index", paramIndex.ToString()));
                break;
            }
            case "FMaterialUniformExpressionSine":
            {
                ReadChild(Ar, "X");
                var bIsCosine = Ar.ReadBoolean();
                OpName = bIsCosine ? "Cos" : "Sin";
                Values.Add(new("Op", OpName));
                break;
            }
            case "FMaterialUniformExpressionTrigMath":
            {
                ReadChild(Ar, "X");
                ReadChild(Ar, "Y");
                var op = Ar.Read<byte>();
                OpName = op < TrigMathOps.Length ? TrigMathOps[op] : $"Trig{op}";
                Values.Add(new("Op", OpName));
                break;
            }
            case "FMaterialUniformExpressionSquareRoot":
                OpName = "Sqrt";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionLength":
            {
                OpName = "Length";
                ReadChild(Ar, "X");
                if (FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.TypeHandlingForMaterialSqrtNodes)
                    Ar.Position += 4; // uint32 ValueType
                break;
            }
            case "FMaterialUniformExpressionLogarithm2":
                OpName = "Log2";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionLogarithm10":
                OpName = "Log10";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionFoldedMath":
            {
                ReadChild(Ar, "A");
                ReadChild(Ar, "B");
                var op = Ar.Read<byte>();
                OpName = op < FoldedMathOps.Length ? FoldedMathOps[op] : $"Math{op}";
                Values.Add(new("Op", OpName));
                if (FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.TypeHandlingForMaterialSqrtNodes)
                    Ar.Position += 4; // uint32 ValueType
                break;
            }
            case "FMaterialUniformExpressionPeriodic":
                OpName = "Frac"; // periodic wraps its input into [0,1)
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionAppendVector":
            {
                OpName = "Append";
                ReadChild(Ar, "A");
                ReadChild(Ar, "B");
                var numComponentsA = Ar.Read<uint>();
                Values.Add(new("NumComponentsA", numComponentsA.ToString()));
                break;
            }
            case "FMaterialUniformExpressionMin":
                OpName = "Min";
                ReadChild(Ar, "A");
                ReadChild(Ar, "B");
                break;
            case "FMaterialUniformExpressionMax":
                OpName = "Max";
                ReadChild(Ar, "A");
                ReadChild(Ar, "B");
                break;
            case "FMaterialUniformExpressionClamp":
                OpName = "Clamp";
                ReadChild(Ar, "Input");
                ReadChild(Ar, "Min");
                ReadChild(Ar, "Max");
                break;
            case "FMaterialUniformExpressionSaturate":
                OpName = "Saturate";
                ReadChild(Ar, "Input");
                break;
            case "FMaterialUniformExpressionComponentSwizzle":
            {
                OpName = "Swizzle";
                ReadChild(Ar, "X");
                var r = Ar.Read<sbyte>();
                var g = Ar.Read<sbyte>();
                var b = Ar.Read<sbyte>();
                var a = Ar.Read<sbyte>();
                Ar.Position += 1; // int8 NumElements (derived from the indices)
                Values.Add(new("Swizzle", FormatSwizzle(r, g, b, a)));
                break;
            }
            case "FMaterialUniformExpressionFloor":
                OpName = "Floor";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionCeil":
                OpName = "Ceil";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionRound":
                OpName = "Round";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionTruncate":
                OpName = "Truncate";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionSign":
                OpName = "Sign";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionFrac":
                OpName = "Frac";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionFmod":
                OpName = "Fmod";
                ReadChild(Ar, "A");
                ReadChild(Ar, "B");
                break;
            case "FMaterialUniformExpressionAbs":
                OpName = "Abs";
                ReadChild(Ar, "X");
                break;
            case "FMaterialUniformExpressionTextureProperty":
            {
                OpName = "TextureProperty";
                ReadChild(Ar, "Texture");
                var property = Ar.Read<sbyte>(); // TMTM_TextureSize=0 / TMTM_TexelSize=1
                Values.Add(new("Property", property == 0 ? "TextureSize" : property == 1 ? "TexelSize" : property.ToString()));
                break;
            }
            default:
                // Unknown expression type: the stream cannot be advanced safely past it.
                throw new NotSupportedException($"Unknown FMaterialUniformExpression type '{TypeName}' in legacy shader map");
        }
    }

    private static string FormatSwizzle(sbyte r, sbyte g, sbyte b, sbyte a)
    {
        const string comps = "rgba";
        var result = "";
        Span<sbyte> idx = [r, g, b, a];
        foreach (var i in idx)
        {
            if (i is >= 0 and < 4) result += comps[i];
        }
        return result;
    }

    public override string ToString() => OpName ?? ParameterName ?? TypeName;
}

/// <summary>FShaderParameterMapInfo serialized with plain FArchive semantics (&lt; 4.25).</summary>
public class FShaderParameterMapInfoLegacy
{
    public FShaderParameterInfoLegacy[] UniformBuffers = [];
    public FShaderParameterInfoLegacy[] TextureSamplers = [];
    public FShaderParameterInfoLegacy[] SRVs = [];
    public FShaderLooseParameterBufferInfoLegacy[] LooseParameterBuffers = [];

    public FShaderParameterMapInfoLegacy(FArchive Ar)
    {
        UniformBuffers = Ar.ReadArray<FShaderParameterInfoLegacy>();
        TextureSamplers = Ar.ReadArray<FShaderParameterInfoLegacy>();
        SRVs = Ar.ReadArray<FShaderParameterInfoLegacy>();
        LooseParameterBuffers = Ar.ReadArray(() => new FShaderLooseParameterBufferInfoLegacy(Ar));
    }
}

public struct FShaderParameterInfoLegacy
{
    public ushort BaseIndex;
    public ushort Size;
}

public class FShaderLooseParameterBufferInfoLegacy
{
    public ushort BufferIndex;
    public ushort BufferSize;
    public FShaderParameterInfoLegacy[] Parameters;

    public FShaderLooseParameterBufferInfoLegacy(FArchive Ar)
    {
        BufferIndex = Ar.Read<ushort>();
        BufferSize = Ar.Read<ushort>();
        Parameters = Ar.ReadArray<FShaderParameterInfoLegacy>();
    }
}

/// <summary>FShaderUniformBufferParameter: uint16 BaseIndex + serialized bool bIsBound.</summary>
public struct FShaderUniformBufferParameterLegacy
{
    public ushort BaseIndex;
    public bool bIsBound;

    public FShaderUniformBufferParameterLegacy(FArchive Ar)
    {
        BaseIndex = Ar.Read<ushort>();
        bIsBound = Ar.ReadBoolean();
    }
}

/// <summary>
/// The FMaterialShader/FMeshMaterialShader parameter block parsed out of the type-specific
/// (virtual Serialize) part of a base pass pixel shader. MaterialUniformBuffer.BaseIndex is
/// the material constant buffer slot.
/// </summary>
public class FMaterialShaderParametersLegacy
{
    public FShaderUniformBufferParameterLegacy SceneTexturesUniformBuffer;
    public FShaderUniformBufferParameterLegacy MobileSceneTexturesUniformBuffer;
    public FShaderUniformBufferParameterLegacy MaterialUniformBuffer;
    public FShaderUniformBufferParameterLegacy[] ParameterCollectionUniformBuffers = [];
    // FDebugUniformExpressionSet: expression counts recorded at cook time
    public int NumVectorExpressions;
    public int NumScalarExpressions;
    public int Num2DTextureExpressions;
    public int NumCubeTextureExpressions;
    public int NumVolumeTextureExpressions;
    public int NumVirtualTextureExpressions;
    public string DebugDescription = "";
    // FMeshMaterialShader
    public FShaderUniformBufferParameterLegacy PassUniformBuffer;
    public string VertexFactoryTypeName = "";
    // TBasePassPixelShaderPolicyParamType
    public FShaderUniformBufferParameterLegacy[] LightMapPolicyParameters = [];
    public FShaderUniformBufferParameterLegacy ReflectionCaptureBuffer;
}

/// <summary>
/// A single legacy FShader. The type-specific parameter block is decoded only for known layouts
/// (base pass pixel shaders). For every other type the FShader::SerializeBase tail — hashes,
/// target, uniform buffer parameter names and the FShaderResource with the compiled bytecode —
/// is recovered by a self-validating scan (see ReadUnknownTypeFromTail); only when that fails
/// is the shader skipped via the serialized end offset, like the engine skips unknown types.
/// </summary>
public class FShaderLegacy
{
    public string TypeName;
    /// <summary>Only set for parseable types (base pass pixel shaders).</summary>
    public FMaterialShaderParametersLegacy? MaterialParameters;
    public FSHAHash OutputHash;
    public FSHAHash MaterialShaderMapHash;
    public string ShaderPipelineName = "";
    public string VertexFactoryTypeName = "";
    public FShaderTargetLegacy Target;
    public int PermutationId;
    /// <summary>Uniform buffer struct names in slot order (name, BaseIndex) as serialized in the shader tail.</summary>
    public (string Name, FShaderUniformBufferParameterLegacy Parameter)[] UniformBufferParameters = [];
    public FShaderResourceLegacy? Resource;

    /// <summary>
    /// Light map policy name -> number of FShaderUniformBufferParameter entries its PixelParametersType serializes.
    /// From LightMapRendering.h: FUniformLightMapPolicyShaderParametersType = 3 (PrecomputedLightingBuffer,
    /// IndirectLightingCache, LightmapResourceCluster); FSelfShadowedTranslucencyPolicy = 1 (TranslucentSelfShadow);
    /// self-shadowed indirect policies = 3 + 1.
    /// </summary>
    private static readonly Dictionary<string, int> BasePassPixelPolicyParamCounts = new()
    {
        ["FNoLightMapPolicy"] = 3,
        ["FPrecomputedVolumetricLightmapLightingPolicy"] = 3,
        ["FCachedVolumeIndirectLightingPolicy"] = 3,
        ["FCachedPointIndirectLightingPolicy"] = 3,
        ["FSimpleNoLightmapLightingPolicy"] = 3,
        ["FSimpleLightmapOnlyLightingPolicy"] = 3,
        ["FSimpleDirectionalLightLightingPolicy"] = 3,
        ["FSimpleStationaryLightPrecomputedShadowsLightingPolicy"] = 3,
        ["FSimpleStationaryLightSingleSampleShadowsLightingPolicy"] = 3,
        ["FSimpleStationaryLightVolumetricLightmapShadowsLightingPolicy"] = 3,
        ["TLightMapPolicyLQ"] = 3,
        ["TLightMapPolicyHQ"] = 3,
        ["TDistanceFieldShadowsAndLightMapPolicyHQ"] = 3,
        ["FSelfShadowedTranslucencyPolicy"] = 1,
        ["FSelfShadowedCachedPointIndirectLightingPolicy"] = 4,
        ["FSelfShadowedVolumetricLightmapPolicy"] = 4,
    };

    /// <summary>Returns the policy parameter count if the type is a parseable base pass pixel shader.</summary>
    private static int? GetBasePassPixelPolicyParamCount(string typeName)
    {
        if (!typeName.StartsWith("TBasePassPS", StringComparison.Ordinal)) return null;
        var policy = typeName["TBasePassPS".Length..];
        if (policy.EndsWith("Skylight", StringComparison.Ordinal)) policy = policy[..^"Skylight".Length];
        return BasePassPixelPolicyParamCounts.TryGetValue(policy, out var count) ? count : null;
    }

    /// <summary>Reads one shader from TShaderMap::SerializeInline (type name + end-offset framed blob).</summary>
    public static FShaderLegacy? Read(FMaterialResourceProxyReader Ar)
    {
        var typeNamePosition = Ar.Position;
        var typeName = Ar.ReadFName().Text;
        var endOffset = Ar.Read<long>(); // relative to OffsetToFirstResource
        var endPosition = Ar.OffsetToFirstResource + endOffset;

        var policyParamCount = GetBasePassPixelPolicyParamCount(typeName);
        if (policyParamCount == null)
        {
            // Unknown type-specific parameter layout: the front of the frame cannot be parsed,
            // but the FShader::SerializeBase tail (hashes, target, uniform buffer names and the
            // FShaderResource with the bytecode) can still be recovered by anchoring on the
            // shader's own type FName — see ReadUnknownTypeFromTail. Failure skips the shader,
            // exactly like the engine skips shader types it does not know.
            var frameStart = Ar.Position;
            Ar.Position = typeNamePosition;
            var typeNameBytes = Ar.ReadBytes(8); // FName on disk: int32 name index + int32 number
            Ar.Position = frameStart;
            var recovered = ReadUnknownTypeFromTail(Ar, typeName, typeNameBytes, frameStart, endPosition);
            Ar.Position = endPosition;
            return recovered;
        }

        var startPosition = Ar.Position;
        try
        {
            var shader = new FShaderLegacy { TypeName = typeName };
            shader.Deserialize(Ar, policyParamCount.Value);
            if (Ar.Position != endPosition)
                throw new InvalidOperationException($"Legacy shader '{typeName}' parsed {Ar.Position - startPosition} bytes, expected {endPosition - startPosition}");
            return shader;
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to parse legacy shader '{0}', skipping", typeName);
            if (Log.IsEnabled(Serilog.Events.LogEventLevel.Verbose))
            {
                var failPos = Ar.Position;
                Ar.Position = startPosition;
                var context = Ar.ReadBytes((int) Math.Min(Math.Min(480, endPosition - startPosition), Ar.Length - Ar.Position));
                Log.Verbose("LegacyShaderMap: shader '{0}' frame {1}..{2} (failed at {3}): {4}",
                    typeName, startPosition, endPosition, failPos, Convert.ToHexString(context));
            }
            Ar.Position = endPosition;
            return null;
        }
    }

    /// <summary>
    /// Recovers a shader whose type-specific parameter layout is unknown by locating the
    /// FShader::SerializeBase tail inside the end-offset frame. The tail contains the shader's
    /// own type FName — byte-identical to the FName the frame started with — at a fixed 76-byte
    /// distance past the tail start (OutputHash 20 + MaterialShaderMapHash 20 + ShaderPipelineName
    /// FName 8 + VertexFactoryTypeName FName 8 + VFSourceHash 20). The frame is scanned for those
    /// 8 bytes and a candidate is accepted only when the entire remainder (SerializeBase tail +
    /// inline FShaderResource + parameter bindings) deserializes cleanly and lands exactly on the
    /// serialized end offset. A misplaced anchor cannot survive that validation; ambiguity
    /// (more than one surviving candidate) rejects the shader instead of picking one.
    /// </summary>
    private static FShaderLegacy? ReadUnknownTypeFromTail(FMaterialResourceProxyReader Ar,
        string typeName, byte[] typeNameBytes, long frameStart, long endPosition)
    {
        const int tailBytesBeforeTypeName = 76;

        var frameLength = (int) (endPosition - frameStart);
        if (frameLength < tailBytesBeforeTypeName + 8 || frameStart + frameLength > Ar.Length)
            return null;
        Ar.Position = frameStart;
        var frame = Ar.ReadBytes(frameLength);

        FShaderLegacy? found = null;
        for (var offset = tailBytesBeforeTypeName; offset + 8 <= frameLength; offset++)
        {
            if (frame[offset] != typeNameBytes[0]) continue;
            var matches = true;
            for (var b = 1; b < 8; b++)
            {
                if (frame[offset + b] == typeNameBytes[b]) continue;
                matches = false;
                break;
            }
            if (!matches) continue;

            try
            {
                Ar.Position = frameStart + offset - tailBytesBeforeTypeName;
                var candidate = new FShaderLegacy { TypeName = typeName };
                candidate.DeserializeBaseTail(Ar);
                if (Ar.Position != endPosition) continue;
                if (candidate.Target.Frequency >= 10) continue; // SF_NumFrequencies (RHIDefinitions.h)
                if (!string.Equals(candidate.TypeName, typeName, StringComparison.Ordinal)) continue;
                if (found != null)
                {
                    Log.Verbose("LegacyShaderMap: shader '{0}' tail anchor is ambiguous, skipping", typeName);
                    return null;
                }
                found = candidate;
            }
            catch
            {
                // candidate did not validate — keep scanning
            }
        }
        return found;
    }

    private void Deserialize(FMaterialResourceProxyReader Ar, int policyParamCount)
    {
        // ---- virtual Serialize: FMaterialShader::Serialize (ShaderBaseClasses.cpp) ----
        var p = new FMaterialShaderParametersLegacy
        {
            SceneTexturesUniformBuffer = new FShaderUniformBufferParameterLegacy(Ar),
            MobileSceneTexturesUniformBuffer = new FShaderUniformBufferParameterLegacy(Ar),
            MaterialUniformBuffer = new FShaderUniformBufferParameterLegacy(Ar),
            ParameterCollectionUniformBuffers = Ar.ReadArray(() => new FShaderUniformBufferParameterLegacy(Ar)),
            NumVectorExpressions = Ar.Read<int>(),
            NumScalarExpressions = Ar.Read<int>(),
            Num2DTextureExpressions = Ar.Read<int>(),
            NumCubeTextureExpressions = Ar.Read<int>(),
            NumVolumeTextureExpressions = Ar.Read<int>()
        };
        if (Ar.LegacyProfile != ELegacyShaderMapProfile.PreVirtualTexture)
            p.NumVirtualTextureExpressions = Ar.Read<int>();
        Ar.ReadFName(); // DebugUniformExpressionUBLayout name
        Ar.Position += 4; // uint32 ConstantBufferSize
        Ar.ReadArray<ushort>(); // ResourceOffsets
        Ar.ReadArray<byte>(); // ResourceTypes
        p.DebugDescription = Ar.ReadFString(false);
        if (Ar.LegacyProfile == ELegacyShaderMapProfile.PreVirtualTexture)
        {
            // pre-VT branch: 20 bytes of parameters here instead of the 4-byte VTFeedbackBuffer
            // (measured against the VF reference + policy params + SerializeBase tail alignment
            // on Fortnite Season X cooks; the end-offset check catches any cook that differs)
            Ar.Position += 20;
        }
        else
        {
            Ar.Position += 2 * sizeof(ushort); // FShaderResourceParameter VTFeedbackBuffer (BaseIndex, NumResources)
        }

        // ---- FMeshMaterialShader::Serialize ----
        p.PassUniformBuffer = new FShaderUniformBufferParameterLegacy(Ar);
        // FVertexFactoryParameterRef: VF type + freq + platform + hash + self-framed parameter blob
        p.VertexFactoryTypeName = Ar.ReadFName().Text;
        Ar.Position += 2; // uint8 ShaderFrequency, uint8 ShaderPlatform
        Ar.Position += 20; // FSHAHash VFHash
        var vfSkipOffset = Ar.Read<long>(); // relative to OffsetToFirstResource
        Ar.Position = Ar.OffsetToFirstResource + vfSkipOffset; // VF parameter layouts are per-VF-type: skip exactly

        // ---- TBasePassPixelShaderPolicyParamType::Serialize ----
        p.LightMapPolicyParameters = new FShaderUniformBufferParameterLegacy[policyParamCount];
        for (var i = 0; i < policyParamCount; i++)
            p.LightMapPolicyParameters[i] = new FShaderUniformBufferParameterLegacy(Ar);
        p.ReflectionCaptureBuffer = new FShaderUniformBufferParameterLegacy(Ar);
        MaterialParameters = p;

        DeserializeBaseTail(Ar);
    }

    /// <summary>
    /// FShader::SerializeBase tail (Shader.cpp) + inline FShaderResource + parameter bindings.
    /// This part is common to every shader type, so it is shared between the full parse of
    /// known layouts and the tail-anchored recovery of unknown ones.
    /// </summary>
    private void DeserializeBaseTail(FMaterialResourceProxyReader Ar)
    {
        OutputHash = new FSHAHash(Ar);
        MaterialShaderMapHash = new FSHAHash(Ar);
        ShaderPipelineName = Ar.ReadFName().Text;
        VertexFactoryTypeName = Ar.ReadFName().Text;
        Ar.Position += 20; // VFSourceHash (default hash when cooked)
        TypeName = Ar.ReadFName().Text; // authoritative type name (matches the outer one)
        if (FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.ShaderPermutationId)
            PermutationId = Ar.Read<int>();
        Ar.Position += 20; // SourceHash (default hash when cooked)
        Target = Ar.Read<FShaderTargetLegacy>(); // 2x uint32 (frequency, platform)

        var numUniformParameters = Ar.Read<int>();
        if (numUniformParameters is < 0 or > 256) // largest observed counts are well below this
            throw new InvalidOperationException($"implausible uniform buffer parameter count {numUniformParameters}");
        UniformBufferParameters = new (string, FShaderUniformBufferParameterLegacy)[numUniformParameters];
        var useStructFName = FFortniteMainBranchObjectVersion.Get(Ar) >= FFortniteMainBranchObjectVersion.Type.MaterialInstanceSerializeOptimization_ShaderFName;
        for (var i = 0; i < numUniformParameters; i++)
        {
            var structName = useStructFName ? Ar.ReadFName().Text : Ar.ReadFString(false);
            UniformBufferParameters[i] = (structName, new FShaderUniformBufferParameterLegacy(Ar));
        }

        // inline FShaderResource (bShadersInline == true for cooked material shader maps)
        Resource = new FShaderResourceLegacy(Ar);

        // FShaderParameterBindings (Shader.h): 9 arrays + uint16 RootParameterBufferIndex
        // (pre-VT branches have one array fewer between Parameters and ParameterReferences)
        Ar.ReadArray<ulong>(); // Parameters (4x uint16)
        var bindingArrayCount = Ar.LegacyProfile == ELegacyShaderMapProfile.PreVirtualTexture ? 6 : 7;
        for (var i = 0; i < bindingArrayCount; i++) Ar.ReadArray<uint>(); // Textures..GraphUAVs (2x uint16 each)
        Ar.ReadArray<uint>(); // ParameterReferences (2x uint16)
        Ar.Position += 2; // RootParameterBufferIndex
    }
}

/// <summary>FShaderTarget (&lt; 4.25 stream form): serialized as two uint32s (frequency, platform).</summary>
public struct FShaderTargetLegacy
{
    public uint Frequency;
    public uint Platform;
}

/// <summary>Inline FShaderResource (&lt; 4.25) carrying the compiled shader bytecode.</summary>
public class FShaderResourceLegacy
{
    public string SpecificTypeName = "";
    public int SpecificPermutationId;
    public FShaderTargetLegacy Target;
    public FSHAHash OutputHash;
    public uint NumInstructions;
    public FShaderParameterMapInfoLegacy ParameterMapInfo;
    /// <summary>True when the bytecode lives in a shared shader code library instead of the package.</summary>
    public bool bCodeInSharedLocation;
    /// <summary>Decompressed shader bytecode (FShaderCode layout: bytecode + optional data), empty if shared.</summary>
    [JsonIgnore] public byte[] Code = [];

    public FShaderResourceLegacy(FMaterialResourceProxyReader Ar)
    {
        SpecificTypeName = Ar.ReadFName().Text;
        if (FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.ShaderPermutationId)
            SpecificPermutationId = Ar.Read<int>();
        Target = Ar.Read<FShaderTargetLegacy>();
        if (FRenderingObjectVersion.Get(Ar) < FRenderingObjectVersion.Type.ShaderResourceCodeSharing)
        {
            Code = Ar.ReadArray<byte>();
        }
        OutputHash = new FSHAHash(Ar);
        NumInstructions = Ar.Read<uint>();
        // NumTextureSamplers is editor-only and not cooked
        ParameterMapInfo = new FShaderParameterMapInfoLegacy(Ar);

        var uncompressedCodeSize = Ar.Read<int>(); // VER_UE4_COMPRESSED_SHADER_RESOURCES
        if (FRenderingObjectVersion.Get(Ar) >= FRenderingObjectVersion.Type.ShaderResourceCodeSharing)
        {
            bCodeInSharedLocation = Ar.ReadBoolean();
            if (!bCodeInSharedLocation)
            {
                Code = Ar.ReadArray<byte>();
            }
        }

        // FShaderResource::UncompressCode: data is Zlib-compressed when the stored size differs
        if (Code.Length > 0 && uncompressedCodeSize > 0 && Code.Length != uncompressedCodeSize)
        {
            Code = Compression.Compression.Decompress(Code, uncompressedCodeSize, CompressionMethod.Zlib);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using CUE4Parse.UE4.Assets.Exports;
using CUE4Parse.UE4.Assets.Exports.Animation;
using CUE4Parse.UE4.Assets.Objects;
using CUE4Parse.UE4.Assets.Objects.Properties;
using CUE4Parse.UE4.Assets.Readers;
using CUE4Parse.UE4.Assets.Utils;
using CUE4Parse.UE4.Objects.Core.i18N;
using CUE4Parse.UE4.Objects.Core.Misc;
using CUE4Parse.UE4.Objects.UObject;
using Newtonsoft.Json;
using UObjectExport = CUE4Parse.UE4.Assets.Exports.UObject;

namespace CUE4Parse.UE4.Objects.Engine.Animation;

public class UAnimBlueprintGeneratedClass : UBlueprintGeneratedClass
{
	public FStructFallback[] BakedStateMachines = [];
	public FPackageIndex TargetSkeleton = new();
	public FAnimNotifyEvent[] AnimNotifies = [];
	public FName[] SyncGroupNames = [];
	public UScriptMap? OrderedSavedPoseIndicesMap;
	public UScriptMap? GraphAssetPlayerInformation;
	public UScriptMap? GraphBlendOptions;
	public FStructFallback[] AnimNodeData = [];
	public UScriptMap? NodeTypeMap;

	[JsonIgnore] private FAnimNodePropertyData[]? _animNodePropertyData;
	[JsonIgnore] private FAnimNodeData[]? _resolvedAnimNodeData;
	[JsonIgnore] private FAnimBlueprintFunction[]? _animBlueprintFunctions;
	[JsonIgnore] private Dictionary<string, int>? _orderedSavedPoseIndices;
	[JsonIgnore] private Dictionary<string, FStructFallback>? _orderedSavedPoseIndexData;
	[JsonIgnore] private Dictionary<string, FStructFallback>? _graphAssetPlayerInformationData;
	[JsonIgnore] private Dictionary<string, FStructFallback>? _graphBlendOptionsData;
	[JsonIgnore] private Dictionary<string, FAnimNodeStructData>? _nodeTypeData;
	[JsonIgnore] private Dictionary<string, FStructFallback>? _nodeTypeFallbackData;
	[JsonIgnore] private Dictionary<string, FAnimNodeStructData>? _nodeTypeDataAliases;
	[JsonIgnore] private Dictionary<string, FStructFallback>? _nodeTypeFallbackAliases;
	[JsonIgnore] private FPropertyTag[]? _constantNodeValueProperties;
	[JsonIgnore] private FPropertyTag[]? _mutableNodeValueProperties;
	[JsonIgnore] private FStructProperty? _mutableNodeDataProperty;

	[JsonIgnore]
	public FAnimNodePropertyData[] AnimNodePropertyData => _animNodePropertyData ??= BuildAnimNodePropertyData();

	[JsonIgnore]
	public FAnimNodeData[] ResolvedAnimNodeData => _resolvedAnimNodeData ??= BuildAnimNodeData();

	[JsonIgnore]
	public FStructProperty[] AnimNodeProperties => [.. AnimNodePropertyData.Select(data => data.Property)];

	[JsonIgnore]
	public FStructProperty[] LinkedAnimGraphNodeProperties =>
		[.. AnimNodePropertyData.Where(data => data.IsLinkedAnimGraphNode).Select(data => data.Property)];

	[JsonIgnore]
	public FStructProperty[] LinkedAnimLayerNodeProperties =>
		[.. AnimNodePropertyData.Where(data => data.IsLinkedAnimLayerNode).Select(data => data.Property)];

	[JsonIgnore]
	public FStructProperty[] StateMachineNodeProperties =>
		[.. AnimNodePropertyData.Where(data => data.IsStateMachineNode).Select(data => data.Property)];

	[JsonIgnore]
	public FAnimBlueprintFunction[] AnimBlueprintFunctions => _animBlueprintFunctions ??= GenerateAnimationBlueprintFunctions();

	[JsonIgnore]
	public IReadOnlyDictionary<string, int> OrderedSavedPoseNodeIndices =>
		_orderedSavedPoseIndices ??= BuildOrderedSavedPoseNodeIndices();

	[JsonIgnore]
	public IReadOnlyDictionary<string, FStructFallback> OrderedSavedPoseIndexData =>
		_orderedSavedPoseIndexData ??= BuildStructFallbackMap(OrderedSavedPoseIndicesMap);

	[JsonIgnore]
	public IReadOnlyDictionary<string, FStructFallback> GraphAssetPlayerInformationData =>
		_graphAssetPlayerInformationData ??= BuildStructFallbackMap(GraphAssetPlayerInformation);

	[JsonIgnore]
	public IReadOnlyDictionary<string, FStructFallback> GraphBlendOptionsData =>
		_graphBlendOptionsData ??= BuildStructFallbackMap(GraphBlendOptions);

	[JsonIgnore]
	public IReadOnlyDictionary<string, FAnimNodeStructData> NodeTypeData =>
		_nodeTypeData ??= BuildNodeTypeData();

	[JsonIgnore]
	public IReadOnlyDictionary<string, FStructFallback> NodeTypeFallbackData =>
		_nodeTypeFallbackData ??= BuildStructFallbackMap(NodeTypeMap);

	[JsonIgnore]
	private IReadOnlyDictionary<string, FAnimNodeStructData> NodeTypeDataAliases =>
		_nodeTypeDataAliases ??= BuildAliasMap(NodeTypeData);

	[JsonIgnore]
	private IReadOnlyDictionary<string, FStructFallback> NodeTypeFallbackAliases =>
		_nodeTypeFallbackAliases ??= BuildAliasMap(NodeTypeFallbackData);

	[JsonIgnore]
	public FStructProperty[] PreUpdateNodeProperties { get; private set; } = [];

	[JsonIgnore]
	public FStructProperty[] DynamicResetNodeProperties { get; private set; } = [];

	[JsonIgnore]
	public FStructProperty[] InitializationNodeProperties { get; private set; } = [];

	[JsonIgnore]
	public int RootAnimNodeIndex => ResolveRootAnimNodeIndex();

	[JsonIgnore]
	public FStructProperty? RootAnimNodeProperty =>
		RootAnimNodeIndex >= 0 && RootAnimNodeIndex < AnimNodeProperties.Length ? AnimNodeProperties[RootAnimNodeIndex] : null;

	[JsonIgnore]
	public IReadOnlyList<FPropertyTag> ConstantNodeValueProperties =>
		_constantNodeValueProperties ??= BuildConstantNodeValueProperties();

	[JsonIgnore]
	public IReadOnlyList<FPropertyTag> MutableNodeValueProperties =>
		_mutableNodeValueProperties ??= BuildMutableNodeValueProperties();

	[JsonIgnore]
	public FStructProperty? MutableNodeDataProperty => _mutableNodeDataProperty ??= ResolveMutableNodeDataProperty();

	public override void Deserialize(FAssetArchive Ar, long validPos)
	{
		base.Deserialize(Ar, validPos);

		BakedStateMachines = GetOrDefault(nameof(BakedStateMachines), Array.Empty<FStructFallback>());
		TargetSkeleton = GetOrDefault(nameof(TargetSkeleton), TargetSkeleton);
		AnimNotifies = GetOrDefault(nameof(AnimNotifies), Array.Empty<FAnimNotifyEvent>());
		SyncGroupNames = GetOrDefault(nameof(SyncGroupNames), Array.Empty<FName>());
		OrderedSavedPoseIndicesMap = GetOrDefault<UScriptMap?>(nameof(OrderedSavedPoseIndicesMap));
		GraphAssetPlayerInformation = GetOrDefault<UScriptMap?>(nameof(GraphAssetPlayerInformation));
		GraphBlendOptions = GetOrDefault<UScriptMap?>(nameof(GraphBlendOptions));
		AnimNodeData = GetOrDefault(nameof(AnimNodeData), Array.Empty<FStructFallback>());
		NodeTypeMap = GetOrDefault<UScriptMap?>(nameof(NodeTypeMap));

		InvalidateCaches();
	}

	public bool TryGetAnimBlueprintFunction(string functionName, out FAnimBlueprintFunction function)
	{
		var match = AnimBlueprintFunctions.FirstOrDefault(candidate =>
			candidate.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));
		if (match is null)
		{
			function = null!;
			return false;
		}

		function = match;
		return true;
	}

	public bool TryGetAnimNodePropertyData(int animNodePropertyIndex, out FAnimNodePropertyData propertyData)
	{
		if (animNodePropertyIndex >= 0 && animNodePropertyIndex < AnimNodePropertyData.Length)
		{
			propertyData = AnimNodePropertyData[animNodePropertyIndex];
			return true;
		}

		propertyData = null!;
		return false;
	}

	public bool TryGetAnimNodeData(int animNodePropertyIndex, out FAnimNodeData nodeData)
	{
		if (animNodePropertyIndex >= 0 && animNodePropertyIndex < ResolvedAnimNodeData.Length)
		{
			nodeData = ResolvedAnimNodeData[animNodePropertyIndex];
			return true;
		}

		nodeData = null!;
		return false;
	}

	public bool TryGetAnimNodeData(string propertyName, out FAnimNodeData nodeData)
	{
		var match = ResolvedAnimNodeData.FirstOrDefault(candidate =>
			candidate.PropertyName.Equals(propertyName, StringComparison.Ordinal));
		if (match is null)
		{
			nodeData = null!;
			return false;
		}

		nodeData = match;
		return true;
	}

	public bool TryGetNodeTypeData(string nodeTypeName, out FAnimNodeStructData nodeTypeData)
	{
		foreach (var lookupName in GetNodeTypeLookupNames(nodeTypeName))
		{
			if (NodeTypeDataAliases.TryGetValue(lookupName, out nodeTypeData!))
				return true;
		}

		nodeTypeData = null!;
		return false;
	}

	public bool TryGetNodeTypeFallbackData(string nodeTypeName, out FStructFallback rawData)
	{
		foreach (var lookupName in GetNodeTypeLookupNames(nodeTypeName))
		{
			if (NodeTypeFallbackAliases.TryGetValue(lookupName, out rawData!))
				return true;
		}

		rawData = null!;
		return false;
	}

	public int GetAnimNodePropertyIndex(string nodeTypeName, string propertyName)
	{
		if (TryGetNodeTypeData(nodeTypeName, out var nodeTypeData))
			return nodeTypeData.GetPropertyIndex(propertyName);

		if (!TryLoadAnimNodeStruct(nodeTypeName, out var nodeStruct) || nodeStruct.ChildProperties is not { Length: > 0 } childProperties)
			return -1;

		for (var propertyIndex = 0; propertyIndex < childProperties.Length; propertyIndex++)
		{
			if (childProperties[propertyIndex].Name.Text.Equals(propertyName, StringComparison.Ordinal))
				return propertyIndex;
		}

		return -1;
	}

	public int GetAnimNodePropertyCount(string nodeTypeName)
	{
		if (TryGetNodeTypeData(nodeTypeName, out var nodeTypeData))
			return nodeTypeData.GetNumProperties();

		return TryLoadAnimNodeStruct(nodeTypeName, out var nodeStruct) ? nodeStruct.ChildProperties?.Length ?? 0 : 0;
	}

	public int GetSyncGroupIndex(FName syncGroupName) => GetSyncGroupIndex(syncGroupName.Text);

	public int GetSyncGroupIndex(string syncGroupName)
	{
		for (var index = 0; index < SyncGroupNames.Length; index++)
		{
			if (SyncGroupNames[index].Text.Equals(syncGroupName, StringComparison.OrdinalIgnoreCase))
				return index;
		}

		return -1;
	}

	public bool TryGetConstantNodeValueRaw(int entryIndex, out FPropertyTag propertyTag)
	{
		var properties = ConstantNodeValueProperties;
		if (entryIndex >= 0 && entryIndex < properties.Count)
		{
			propertyTag = properties[entryIndex];
			return true;
		}

		propertyTag = null!;
		return false;
	}

	public bool TryGetMutableNodeValueRaw(int entryIndex, out FPropertyTag propertyTag)
	{
		var properties = MutableNodeValueProperties;
		if (entryIndex >= 0 && entryIndex < properties.Count)
		{
			propertyTag = properties[entryIndex];
			return true;
		}

		propertyTag = null!;
		return false;
	}

	public bool TryGetNodeValueRaw(FAnimNodeData nodeData, int propertyIndex, out FPropertyTag propertyTag)
	{
		if (nodeData.IsInstanceDataEntry(propertyIndex, out var instanceEntryIndex) &&
			TryGetMutableNodeValueRaw(instanceEntryIndex, out propertyTag))
			return true;

		if (nodeData.IsConstantDataEntry(propertyIndex, out var constantEntryIndex) &&
			TryGetConstantNodeValueRaw(constantEntryIndex, out propertyTag))
			return true;

		propertyTag = null!;
		return false;
	}

	public bool TryGetNodeValueRaw(FAnimNodeData nodeData, string propertyName, out FPropertyTag propertyTag)
	{
		var propertyIndex = GetAnimNodePropertyIndex(nodeData.StructTypeName, propertyName);
		if (propertyIndex < 0)
		{
			propertyTag = null!;
			return false;
		}

		return TryGetNodeValueRaw(nodeData, propertyIndex, out propertyTag);
	}

	public bool TryGetNodeValue<T>(FAnimNodeData nodeData, int propertyIndex, out T value)
	{
		if (TryGetNodeValueRaw(nodeData, propertyIndex, out var propertyTag) && propertyTag.Tag?.GetValue(typeof(T)) is T typedValue)
		{
			value = typedValue;
			return true;
		}

		value = default!;
		return false;
	}

	public bool TryGetNodeValue<T>(FAnimNodeData nodeData, string propertyName, out T value)
	{
		if (TryGetNodeValueRaw(nodeData, propertyName, out var propertyTag) && propertyTag.Tag?.GetValue(typeof(T)) is T typedValue)
		{
			value = typedValue;
			return true;
		}

		value = default!;
		return false;
	}

	public bool TryGetAnimNodeProperties(int animNodePropertyIndex, out FAnimNodePropertyCollection properties)
	{
		if (!TryGetAnimNodeData(animNodePropertyIndex, out var nodeData))
		{
			properties = null!;
			return false;
		}

		properties = GetAnimNodeProperties(nodeData);
		return true;
	}

	public bool TryGetAnimNodeProperties(string propertyName, out FAnimNodePropertyCollection properties)
	{
		if (!TryGetAnimNodeData(propertyName, out var nodeData))
		{
			properties = null!;
			return false;
		}

		properties = GetAnimNodeProperties(nodeData);
		return true;
	}

	public bool TryGetRootNodeIndexForFunction(string functionName, out int outputPoseNodeIndex)
	{
		outputPoseNodeIndex = -1;
		return TryGetAnimBlueprintFunction(functionName, out var function) &&
			   function.OutputPoseNodeIndex >= 0 &&
			   (outputPoseNodeIndex = function.OutputPoseNodeIndex) >= 0;
	}

	public bool TryGetRootNodePropertyForFunction(string functionName, out FAnimNodePropertyData? propertyData)
	{
		propertyData = null;
		if (!TryGetRootNodeIndexForFunction(functionName, out var outputPoseNodeIndex))
			return false;

		propertyData = AnimNodePropertyData.FirstOrDefault(candidate => candidate.AnimNodePropertyIndex == outputPoseNodeIndex);
		return propertyData is not null;
	}

	public int GetNodeIndexFromGuid(FGuid guid)
	{
		for (var index = 0; index < AnimNodePropertyData.Length; index++)
		{
			if (TryGetNodeGuid(AnimNodePropertyData[index], out var nodeGuid) && nodeGuid == guid)
				return index;
		}

		return -1;
	}

	private void InvalidateCaches()
	{
		_animNodePropertyData = null;
		_resolvedAnimNodeData = null;
		_animBlueprintFunctions = null;
		_orderedSavedPoseIndices = null;
		_orderedSavedPoseIndexData = null;
		_graphAssetPlayerInformationData = null;
		_graphBlendOptionsData = null;
		_nodeTypeData = null;
		_nodeTypeFallbackData = null;
		_nodeTypeDataAliases = null;
		_nodeTypeFallbackAliases = null;
		_constantNodeValueProperties = null;
		_mutableNodeValueProperties = null;
		_mutableNodeDataProperty = null;
		PreUpdateNodeProperties = [];
		DynamicResetNodeProperties = [];
		InitializationNodeProperties = [];
	}

	private FAnimNodePropertyData[] BuildAnimNodePropertyData()
	{
		var result = new List<FAnimNodePropertyData>();
		var childProperties = ChildProperties ?? [];
		for (var childPropertyIndex = 0; childPropertyIndex < childProperties.Length; childPropertyIndex++)
		{
			if (childProperties[childPropertyIndex] is not FStructProperty structProperty)
				continue;

			if (!IsAnimNodeStruct(structProperty))
				continue;

			result.Add(new FAnimNodePropertyData(structProperty, result.Count, childPropertyIndex,
				ResolveDefaultValue(structProperty.Name.Text)));
		}

		return [.. result];
	}

	private FAnimNodeData[] BuildAnimNodeData()
	{
		var propertyData = AnimNodePropertyData;
		var count = Math.Max(propertyData.Length, AnimNodeData.Length);
		var result = new List<FAnimNodeData>(count);
		for (var index = 0; index < count; index++)
		{
			var property = index < propertyData.Length ? propertyData[index] : null;
			var rawData = index < AnimNodeData.Length ? AnimNodeData[index] : null;
			result.Add(new FAnimNodeData(index, property, rawData));
		}

		return [.. result];
	}

	private FAnimNodePropertySchemaEntry[] BuildAnimNodePropertySchema(FAnimNodeData nodeData)
	{
		var result = new List<FAnimNodePropertySchemaEntry>();
		var propertyIndexByName = new Dictionary<string, int>(StringComparer.Ordinal);

		if (TryGetNodeTypeData(nodeData.StructTypeName, out var nodeTypeData))
		{
			foreach (var (propertyName, propertyIndex) in nodeTypeData.NameToIndexMap.OrderBy(static pair => pair.Value))
			{
				result.Add(new FAnimNodePropertySchemaEntry(propertyName, propertyIndex, string.Empty, null));
				propertyIndexByName[propertyName] = propertyIndex;
			}
		}

		if (!TryLoadAnimNodeStruct(nodeData.StructTypeName, out var nodeStruct) || nodeStruct.ChildProperties is not { Length: > 0 } childProperties)
			return [.. result];

		for (var propertyIndex = 0; propertyIndex < childProperties.Length; propertyIndex++)
		{
			var propertyName = childProperties[propertyIndex].Name.Text;
			var declaredType = ResolveDeclaredPropertyType(childProperties[propertyIndex]);

			if (propertyIndexByName.TryGetValue(propertyName, out var schemaIndex))
			{
				var existingIndex = result.FindIndex(entry =>
					entry.PropertyIndex == schemaIndex && entry.Name.Equals(propertyName, StringComparison.Ordinal));
				if (existingIndex >= 0)
					result[existingIndex] = new FAnimNodePropertySchemaEntry(propertyName, schemaIndex, declaredType, childProperties[propertyIndex]);
				continue;
			}

			result.Add(new FAnimNodePropertySchemaEntry(propertyName, propertyIndex, declaredType, childProperties[propertyIndex]));
			propertyIndexByName[propertyName] = propertyIndex;
		}

		result.Sort(static (left, right) => left.PropertyIndex.CompareTo(right.PropertyIndex));
		return [.. result];
	}

	private FAnimNodePropertyCollection GetAnimNodeProperties(FAnimNodeData nodeData)
	{
		var propertySchema = BuildAnimNodePropertySchema(nodeData);
		var propertiesByName = new Dictionary<string, FAnimNodeResolvedProperty>(StringComparer.Ordinal);

		foreach (var schemaEntry in propertySchema)
			GetOrCreateResolvedProperty(propertiesByName, schemaEntry.Name, schemaEntry.PropertyIndex, schemaEntry.DeclaredType);

		var defaultValue = nodeData.PropertyData?.DefaultValue;
		if (defaultValue is null && !string.IsNullOrEmpty(nodeData.PropertyName))
			defaultValue = ResolveDefaultValue(nodeData.PropertyName);

		AddStructProperties(defaultValue, EAnimNodePropertySource.DefaultObject, propertySchema, propertiesByName);
		AddEntryProperties(nodeData, propertySchema, propertiesByName);

		var resolvedProperties = propertiesByName.Values
			.OrderBy(static property => property.PropertyIndex)
			.ThenBy(static property => property.Name, StringComparer.Ordinal)
			.ToArray();

		return new FAnimNodePropertyCollection(nodeData, resolvedProperties);
	}

	private static FAnimNodeResolvedProperty GetOrCreateResolvedProperty(
		IDictionary<string, FAnimNodeResolvedProperty> propertiesByName,
		string propertyName,
		int propertyIndex,
		string declaredType)
	{
		if (!propertiesByName.TryGetValue(propertyName, out var property))
		{
			property = new FAnimNodeResolvedProperty(propertyName, propertyIndex, declaredType);
			propertiesByName[propertyName] = property;
			return property;
		}

		property.TryUpdateMetadata(propertyIndex, declaredType);
		return property;
	}

	private void AddEntryProperties(
		FAnimNodeData nodeData,
		IReadOnlyList<FAnimNodePropertySchemaEntry> propertySchema,
		IDictionary<string, FAnimNodeResolvedProperty> propertiesByName)
	{
		for (var propertyIndex = 0; propertyIndex < nodeData.Entries.Length; propertyIndex++)
		{
			if (nodeData.GetResolvedEntryIndex(propertyIndex) < 0 ||
				!nodeData.TryGetRawValue(this, propertyIndex, out var propertyTag) ||
				propertyTag.Tag is null)
				continue;

			var source = nodeData.IsInstanceDataEntry(propertyIndex, out _)
				? EAnimNodePropertySource.MutableDataEntry
				: EAnimNodePropertySource.ConstantDataEntry;

			var propertyName = propertyTag.Name.Text;
			var resolvedPropertyIndex = ResolveSchemaPropertyIndex(propertySchema, propertyName, propertyTag);
			var schemaEntry = GetSchemaEntry(propertySchema, propertyName, resolvedPropertyIndex);
			var resolvedProperty = GetOrCreateResolvedProperty(
				propertiesByName,
				schemaEntry.Name ?? propertyName,
				schemaEntry.PropertyIndex >= 0 ? schemaEntry.PropertyIndex : resolvedPropertyIndex,
				schemaEntry.DeclaredType);

			var resolvedValue = TryResolveNodeEntryValue(nodeData, propertyIndex, schemaEntry.PropertyField, out var typedValue)
				? typedValue
				: ResolvePropertyValue(propertyTag, schemaEntry.PropertyField);

			resolvedProperty.AddValue(new FAnimNodePropertyValue(
				source,
				propertyIndex,
				propertyTag,
				resolvedValue));
		}
	}

	private void AddStructProperties(
		FStructFallback? structData,
		EAnimNodePropertySource source,
		IReadOnlyList<FAnimNodePropertySchemaEntry> propertySchema,
		IDictionary<string, FAnimNodeResolvedProperty> propertiesByName)
	{
		if (structData?.Properties is not { Count: > 0 } propertyTags)
			return;

		foreach (var propertyTag in propertyTags)
		{
			if (propertyTag.Tag is null)
				continue;

			var propertyName = propertyTag.Name.Text;
			var resolvedPropertyIndex = ResolveSchemaPropertyIndex(propertySchema, propertyName, propertyTag);
			var schemaEntry = GetSchemaEntry(propertySchema, propertyName, resolvedPropertyIndex);
			var resolvedProperty = GetOrCreateResolvedProperty(
				propertiesByName,
				schemaEntry.Name ?? propertyName,
				schemaEntry.PropertyIndex >= 0 ? schemaEntry.PropertyIndex : resolvedPropertyIndex,
				schemaEntry.DeclaredType);

			resolvedProperty.AddValue(new FAnimNodePropertyValue(
				source,
				resolvedPropertyIndex,
				propertyTag,
				ResolvePropertyValue(propertyTag, schemaEntry.PropertyField)));
		}
	}

	private bool TryResolveNodeEntryValue(FAnimNodeData nodeData, int propertyIndex, FField? propertyField, out object? value)
	{
		if (propertyField is null)
		{
			value = null;
			return false;
		}

		switch (propertyField)
		{
			case FNameProperty:
				if (nodeData.TryGetValue(this, propertyIndex, out FName nameValue))
				{
					value = nameValue;
					return true;
				}
				break;
			case FTextProperty:
				if (nodeData.TryGetValue(this, propertyIndex, out FText textValue))
				{
					value = textValue;
					return true;
				}
				break;
			case FBoolProperty:
				if (nodeData.TryGetValue(this, propertyIndex, out bool boolValue))
				{
					value = boolValue;
					return true;
				}
				break;
			case FInt8Property:
				if (nodeData.TryGetValue(this, propertyIndex, out sbyte int8Value))
				{
					value = int8Value;
					return true;
				}
				break;
			case FInt16Property:
				if (nodeData.TryGetValue(this, propertyIndex, out short int16Value))
				{
					value = int16Value;
					return true;
				}
				break;
			case FIntProperty:
				if (nodeData.TryGetValue(this, propertyIndex, out int intValue))
				{
					value = intValue;
					return true;
				}
				break;
			case FInt64Property:
				if (nodeData.TryGetValue(this, propertyIndex, out long int64Value))
				{
					value = int64Value;
					return true;
				}
				break;
			case FUInt16Property:
				if (nodeData.TryGetValue(this, propertyIndex, out ushort uint16Value))
				{
					value = uint16Value;
					return true;
				}
				break;
			case FUInt32Property:
				if (nodeData.TryGetValue(this, propertyIndex, out uint uint32Value))
				{
					value = uint32Value;
					return true;
				}
				break;
			case FUInt64Property:
				if (nodeData.TryGetValue(this, propertyIndex, out ulong uint64Value))
				{
					value = uint64Value;
					return true;
				}
				break;
			case FFloatProperty:
				if (nodeData.TryGetValue(this, propertyIndex, out float floatValue))
				{
					value = floatValue;
					return true;
				}
				break;
			case FDoubleProperty:
				if (nodeData.TryGetValue(this, propertyIndex, out double doubleValue))
				{
					value = doubleValue;
					return true;
				}
				break;
			case FStrProperty:
			case FUtf8StrProperty:
				if (nodeData.TryGetValue(this, propertyIndex, out string stringValue))
				{
					value = stringValue;
					return true;
				}
				break;
			case FSoftClassProperty:
			case FSoftObjectProperty:
				if (nodeData.TryGetValue(this, propertyIndex, out FSoftObjectPath softObjectValue))
				{
					value = softObjectValue;
					return true;
				}
				break;
			case FClassProperty:
			case FObjectProperty:
				if (nodeData.TryGetValue(this, propertyIndex, out FPackageIndex objectValue))
				{
					value = objectValue;
					return true;
				}
				break;
		}

		value = null;
		return false;
	}

	private Dictionary<string, FAnimNodeStructData> BuildNodeTypeData()
	{
		var result = new Dictionary<string, FAnimNodeStructData>(StringComparer.OrdinalIgnoreCase);
		foreach (var (nodeTypeName, rawData) in NodeTypeFallbackData)
		{
			if (string.IsNullOrEmpty(nodeTypeName))
				continue;

			result[nodeTypeName] = new FAnimNodeStructData(nodeTypeName, rawData, TryLoadAnimNodeStruct(nodeTypeName, out var nodeStruct) ? nodeStruct : null);
		}

		foreach (var property in AnimNodePropertyData)
		{
			if (string.IsNullOrEmpty(property.StructName) || result.ContainsKey(property.StructName))
				continue;

			result[property.StructName] = new FAnimNodeStructData(property.StructName, null,
				property.Property.Struct.TryLoad<UStruct>(out var nodeStruct) ? nodeStruct : null);
		}

		return result;
	}

	private FPropertyTag[] BuildConstantNodeValueProperties()
	{
		if (!TryLoadClassDefaultObject(out var defaultObject))
			return [];

		return BuildFlattenedPropertyTagTable(defaultObject?.SerializedSparseClassData, defaultObject?.SerializedSparseClassDataStruct);
	}

	private FPropertyTag[] BuildMutableNodeValueProperties()
	{
		if (!TryLoadClassDefaultObject(out var defaultObject))
			return [];

		var mutableNodeProperty = MutableNodeDataProperty;
		if (defaultObject is null || mutableNodeProperty is null || !defaultObject.TryGetValue(out FStructFallback mutableNodeData, mutableNodeProperty.Name.Text))
			return [];

		return BuildFlattenedPropertyTagTable(mutableNodeData,
			mutableNodeProperty.Struct.TryLoad<UStruct>(out var mutableNodeStruct) ? mutableNodeStruct : null);
	}

	private FStructProperty? ResolveMutableNodeDataProperty()
	{
		foreach (var childProperty in ChildProperties ?? [])
		{
			if (childProperty is FStructProperty structProperty && IsStructOrDerivedFrom(structProperty, "AnimBlueprintMutableData"))
				return structProperty;
		}

		return null;
	}

	private static FPropertyTag[] BuildFlattenedPropertyTagTable(FStructFallback? structData, UStruct? structType)
	{
		if (structData?.Properties is not { Count: > 0 } serializedProperties)
			return [];

		if (structType?.ChildProperties is not { Length: > 0 } childProperties)
			return [.. serializedProperties.Where(static property => property.Tag is not null)];

		var result = new List<FPropertyTag>(serializedProperties.Count);
		var usedProperties = new HashSet<FPropertyTag>();

		foreach (var childProperty in childProperties)
		{
			var propertyTag = serializedProperties.FirstOrDefault(candidate =>
				candidate.Tag is not null &&
				candidate.ArrayIndex == 0 &&
				candidate.Name.Text.Equals(childProperty.Name.Text, StringComparison.Ordinal));
			if (propertyTag is null)
				continue;

			result.Add(propertyTag);
			usedProperties.Add(propertyTag);
		}

		foreach (var propertyTag in serializedProperties)
		{
			if (propertyTag.Tag is null || usedProperties.Contains(propertyTag))
				continue;

			result.Add(propertyTag);
		}

		return [.. result];
	}

	private FAnimBlueprintFunction[] GenerateAnimationBlueprintFunctions()
	{
		if (FuncMap is not { Count: > 0 })
			return [];

		var functions = new List<FAnimBlueprintFunction>(FuncMap.Count);
		foreach (var (name, packageIndex) in FuncMap)
		{
			if (!packageIndex.TryLoad<UFunction>(out var function))
				continue;

			if (!TryCreateAnimBlueprintFunction(name, function, out var animBlueprintFunction))
				continue;

			functions.Add(animBlueprintFunction);
		}

		functions.Sort(static (left, right) =>
		{
			var leftIsAnimGraph = left.Name.Equals("AnimGraph", StringComparison.OrdinalIgnoreCase);
			var rightIsAnimGraph = right.Name.Equals("AnimGraph", StringComparison.OrdinalIgnoreCase);
			if (leftIsAnimGraph != rightIsAnimGraph)
				return leftIsAnimGraph ? -1 : 1;
			return StringComparer.Ordinal.Compare(left.Name, right.Name);
		});

		LinkFunctionsToDefaultObjectNodes(functions);

		return [.. functions];
	}

	private bool TryCreateAnimBlueprintFunction(FName functionName, UFunction function, out FAnimBlueprintFunction animBlueprintFunction)
	{
		animBlueprintFunction = null!;

		var inputPoseNames = new List<string>();
		FStructProperty? outputPoseProperty = null;
		foreach (var childProperty in function.ChildProperties ?? [])
		{
			if (childProperty is not FStructProperty structProperty || !IsPoseLinkStruct(structProperty))
				continue;

			var isOutParm = structProperty.PropertyFlags.HasFlag(EPropertyFlags.OutParm) &&
							!structProperty.PropertyFlags.HasFlag(EPropertyFlags.ReturnParm);
			if (isOutParm && outputPoseProperty is null)
			{
				outputPoseProperty = structProperty;
				continue;
			}

			if (structProperty.PropertyFlags.HasFlag(EPropertyFlags.Parm))
				inputPoseNames.Add(structProperty.Name.Text);
		}

		if (outputPoseProperty is null && inputPoseNames.Count == 0 && !functionName.Text.Equals("AnimGraph", StringComparison.OrdinalIgnoreCase))
			return false;

		var outputLinkId = -1;
		var outputNodeIndex = -1;
		if (outputPoseProperty is not null)
		{
			if (TryResolvePoseLinkForFunction(functionName.Text, outputPoseProperty.Name.Text, out var poseLink))
			{
				outputLinkId = poseLink.LinkID;
				outputNodeIndex = ResolveAnimNodePropertyIndexFromLinkId(outputLinkId, true);
			}
		}

		var inputNodeIndices = new int[inputPoseNames.Count];
		Array.Fill(inputNodeIndices, -1);

		animBlueprintFunction = new FAnimBlueprintFunction(functionName.Text, function, outputPoseProperty?.Name.Text,
			inputPoseNames.ToArray(), inputNodeIndices, outputLinkId, outputNodeIndex);
		return true;
	}

	private void LinkFunctionsToDefaultObjectNodes(List<FAnimBlueprintFunction> functions)
	{
		if (functions.Count == 0 || AnimNodePropertyData.Length == 0)
			return;

		foreach (var propertyData in AnimNodePropertyData)
		{
			var defaultValue = propertyData.DefaultValue;
			if (defaultValue is null && !TryGetAnimNodeData(propertyData.AnimNodePropertyIndex, out _))
				continue;

			if (propertyData.IsRootNode)
			{
				var rootNodeName = ResolveRootNodeFunctionName(propertyData, defaultValue);
				if (string.IsNullOrEmpty(rootNodeName))
					continue;

				var function = functions.FirstOrDefault(candidate =>
					candidate.Name.Equals(rootNodeName, StringComparison.OrdinalIgnoreCase));
				if (function is null)
					continue;

				function.OutputPoseNodeIndex = propertyData.AnimNodePropertyIndex;
				if (defaultValue is not null && TryGetNestedPoseLink(defaultValue, "Result", out var poseLink))
					function.OutputPoseLinkID = poseLink.LinkID;
			}
			else if (propertyData.IsLinkedAnimGraphNode || propertyData.IsLinkedAnimLayerNode)
			{
				if (defaultValue is null)
					continue;

				var graphName = ResolveTextValue(defaultValue, "Graph");
				var inputPoseName = ResolveTextValue(defaultValue, "Name");
				if (string.IsNullOrEmpty(graphName) || string.IsNullOrEmpty(inputPoseName))
					continue;

				var function = functions.FirstOrDefault(candidate =>
					candidate.Name.Equals(graphName, StringComparison.OrdinalIgnoreCase));
				if (function is null)
					continue;

				for (var inputIndex = 0; inputIndex < function.InputPoseNames.Length; inputIndex++)
				{
					if (function.InputPoseNames[inputIndex].Equals(inputPoseName, StringComparison.OrdinalIgnoreCase))
						function.InputPoseNodeIndices[inputIndex] = propertyData.AnimNodePropertyIndex;
				}
			}
		}
	}

	private string ResolveRootNodeFunctionName(FAnimNodePropertyData propertyData, FStructFallback? defaultValue)
	{
		if (TryGetAnimNodeData(propertyData.AnimNodePropertyIndex, out var nodeData) &&
			TryResolveNodeDataTextValue(nodeData, out var rootNodeName, "Name", "NodeName", "GraphName"))
		{
			return rootNodeName;
		}

		return defaultValue is not null ? ResolveTextValue(defaultValue, "Name", "NodeName", "GraphName") : string.Empty;
	}

	private bool TryResolveNodeDataTextValue(FAnimNodeData nodeData, out string value, params string[] propertyNames)
	{
		foreach (var propertyName in propertyNames)
		{
			if (nodeData.TryGetValue(this, propertyName, out FName textName))
			{
				value = textName.Text;
				if (!string.IsNullOrEmpty(value))
					return true;
			}

			if (nodeData.TryGetValue(this, propertyName, out string textValue) && !string.IsNullOrEmpty(textValue))
			{
				value = textValue;
				return true;
			}
		}

		value = string.Empty;
		return false;
	}

	private Dictionary<string, int> BuildOrderedSavedPoseNodeIndices()
	{
		var result = new Dictionary<string, int>(StringComparer.Ordinal);
		foreach (var (keyName, value) in OrderedSavedPoseIndexData)
		{
			if (TryExtractSavedPoseNodeIndex(value, out var savedPoseIndex))
				result[keyName] = savedPoseIndex;
		}

		return result;
	}

	private static bool TryExtractSavedPoseNodeIndex(FStructFallback savedPoseIndexData, out int savedPoseIndex)
	{
		if (savedPoseIndexData.TryGetValue(out savedPoseIndex, "SavedPoseNodeIndex") ||
			savedPoseIndexData.TryGetValue(out savedPoseIndex, "PoseNodeIndex") ||
			savedPoseIndexData.TryGetValue(out savedPoseIndex, "CachePoseNodeIndex"))
			return true;

		if (savedPoseIndexData.TryGetValue(out int[] nodeIndices, "OrderedSavedPoseNodeIndices") && nodeIndices.Length > 0)
		{
			savedPoseIndex = nodeIndices[0];
			return true;
		}

		savedPoseIndex = -1;
		return false;
	}

	private static Dictionary<string, FStructFallback> BuildStructFallbackMap(UScriptMap? map)
	{
		var result = new Dictionary<string, FStructFallback>(StringComparer.Ordinal);
		if (map?.Properties is not { Count: > 0 })
			return result;

		foreach (var (key, value) in map.Properties)
		{
			var keyName = GetMapString(key);
			if (string.IsNullOrEmpty(keyName) || !TryGetStructFallbackValue(value, out var structValue))
				continue;

			result[keyName] = structValue;
		}

		return result;
	}

	private static bool TryGetStructFallbackValue(FPropertyTagType? value, out FStructFallback structValue)
	{
		if (value is null)
		{
			structValue = null!;
			return false;
		}

		if (value.GenericValue is FStructFallback fallback)
		{
			structValue = fallback;
			return true;
		}

		if (value.GenericValue is FScriptStruct { StructType: FStructFallback scriptStructFallback })
		{
			structValue = scriptStructFallback;
			return true;
		}

		if (value.GetValue(typeof(FScriptStruct)) is FScriptStruct { StructType: FStructFallback typedScriptStructFallback })
		{
			structValue = typedScriptStructFallback;
			return true;
		}

		structValue = null!;
		return false;
	}

	private static int ResolveSchemaPropertyIndex(IReadOnlyList<FAnimNodePropertySchemaEntry> propertySchema,
		string propertyName, FPropertyTag propertyTag)
	{
		foreach (var schemaEntry in propertySchema)
		{
			if (schemaEntry.Name.Equals(propertyName, StringComparison.Ordinal))
				return schemaEntry.PropertyIndex;
		}

		return propertyTag.ArrayIndex > 0 ? propertyTag.ArrayIndex : -1;
	}

	private static FAnimNodePropertySchemaEntry GetSchemaEntry(IReadOnlyList<FAnimNodePropertySchemaEntry> propertySchema,
		string propertyName, int propertyIndex)
	{
		foreach (var schemaEntry in propertySchema)
		{
			if (schemaEntry.PropertyIndex == propertyIndex || schemaEntry.Name.Equals(propertyName, StringComparison.Ordinal))
				return schemaEntry;
		}

		return default;
	}

	private static object? ResolvePropertyValue(FPropertyTag propertyTag, FField? propertyField)
	{
		if (propertyTag.Tag is null)
			return null;

		if (propertyField is null)
			return ResolveUntypedPropertyValue(propertyTag.Tag);

		return propertyField switch
		{
			FArrayProperty arrayProperty => ResolveArrayPropertyValue(propertyTag.Tag, arrayProperty.Inner),
			FSetProperty setProperty => ResolveSetPropertyValue(propertyTag.Tag, setProperty.ElementProp),
			FMapProperty mapProperty => ResolveMapPropertyValue(propertyTag.Tag, mapProperty.KeyProp, mapProperty.ValueProp),
			FStructProperty => ResolveStructPropertyValue(propertyTag.Tag),
			FNameProperty => propertyTag.Tag.GetValue<FName>(),
			FTextProperty => propertyTag.Tag.GetValue<FText>(),
			FBoolProperty => propertyTag.Tag.GetValue<bool>(),
			FByteProperty => ResolveUntypedPropertyValue(propertyTag.Tag),
			FInt8Property => propertyTag.Tag.GetValue<sbyte>(),
			FInt16Property => propertyTag.Tag.GetValue<short>(),
			FIntProperty => propertyTag.Tag.GetValue<int>(),
			FInt64Property => propertyTag.Tag.GetValue<long>(),
			FFloatProperty => propertyTag.Tag.GetValue<float>(),
			FDoubleProperty => propertyTag.Tag.GetValue<double>(),
			FUInt16Property => propertyTag.Tag.GetValue<ushort>(),
			FUInt32Property => propertyTag.Tag.GetValue<uint>(),
			FUInt64Property => propertyTag.Tag.GetValue<ulong>(),
			FStrProperty => propertyTag.Tag.GetValue<string>(),
			FUtf8StrProperty => propertyTag.Tag.GetValue<string>(),
			FSoftClassProperty => propertyTag.Tag.GetValue(typeof(FSoftObjectPath)) ?? ResolveUntypedPropertyValue(propertyTag.Tag),
			FSoftObjectProperty => propertyTag.Tag.GetValue(typeof(FSoftObjectPath)) ?? ResolveUntypedPropertyValue(propertyTag.Tag),
			FClassProperty => propertyTag.Tag.GetValue<FPackageIndex>() ?? ResolveUntypedPropertyValue(propertyTag.Tag),
			FObjectProperty => propertyTag.Tag.GetValue<FPackageIndex>() ?? ResolveUntypedPropertyValue(propertyTag.Tag),
			FEnumProperty => ResolveUntypedPropertyValue(propertyTag.Tag),
			_ => ResolveUntypedPropertyValue(propertyTag.Tag)
		};
	}

	private static object? ResolveStructPropertyValue(FPropertyTagType propertyTagType)
	{
		if (propertyTagType.GetValue(typeof(FScriptStruct)) is FScriptStruct scriptStruct)
			return scriptStruct.StructType is not null ? scriptStruct.StructType : scriptStruct;

		return ResolveUntypedPropertyValue(propertyTagType);
	}

	private static object? ResolveArrayPropertyValue(FPropertyTagType propertyTagType, FProperty? innerProperty)
	{
		if (propertyTagType.GetValue(typeof(UScriptArray)) is not UScriptArray arrayValue)
			return ResolveUntypedPropertyValue(propertyTagType);

		var result = new object?[arrayValue.Properties.Count];
		for (var index = 0; index < arrayValue.Properties.Count; index++)
			result[index] = ResolvePropertyTagTypeValue(arrayValue.Properties[index], innerProperty);

		return result;
	}

	private static object? ResolveSetPropertyValue(FPropertyTagType propertyTagType, FProperty? elementProperty)
	{
		if (propertyTagType.GetValue(typeof(UScriptSet)) is not UScriptSet setValue)
			return ResolveUntypedPropertyValue(propertyTagType);

		var result = new object?[setValue.Properties.Count];
		for (var index = 0; index < setValue.Properties.Count; index++)
			result[index] = ResolvePropertyTagTypeValue(setValue.Properties[index], elementProperty);

		return result;
	}

	private static object? ResolveMapPropertyValue(FPropertyTagType propertyTagType, FProperty? keyProperty, FProperty? valueProperty)
	{
		if (propertyTagType.GetValue(typeof(UScriptMap)) is not UScriptMap mapValue)
			return ResolveUntypedPropertyValue(propertyTagType);

		var result = new List<KeyValuePair<object?, object?>>(mapValue.Properties.Count);
		foreach (var (key, value) in mapValue.Properties)
		{
			result.Add(new KeyValuePair<object?, object?>(
				ResolvePropertyTagTypeValue(key, keyProperty),
				value is not null ? ResolvePropertyTagTypeValue(value, valueProperty) : null));
		}

		return result;
	}

	private static object? ResolvePropertyTagTypeValue(FPropertyTagType? propertyTagType, FField? propertyField)
	{
		if (propertyTagType is null)
			return null;

		var propertyTag = new FPropertyTag
		{
			Tag = propertyTagType,
			PropertyType = new FName(propertyTagType.GetType().Name)
		};

		return ResolvePropertyValue(propertyTag, propertyField);
	}

	private static object? ResolveUntypedPropertyValue(FPropertyTagType propertyTagType)
	{
		if (propertyTagType.GetValue(typeof(FScriptStruct)) is FScriptStruct scriptStruct)
			return scriptStruct.StructType is not null ? scriptStruct.StructType : scriptStruct;

		if (propertyTagType.GetValue(typeof(UScriptArray)) is UScriptArray arrayValue)
			return arrayValue.Properties.Select(static item => ResolveUntypedPropertyValue(item)).ToArray();

		if (propertyTagType.GetValue(typeof(UScriptSet)) is UScriptSet setValue)
			return setValue.Properties.Select(static item => ResolveUntypedPropertyValue(item)).ToArray();

		if (propertyTagType.GetValue(typeof(UScriptMap)) is UScriptMap mapValue)
			return mapValue.Properties
				.Select(static pair => new KeyValuePair<object?, object?>(
					ResolveUntypedPropertyValue(pair.Key),
					pair.Value is not null ? ResolveUntypedPropertyValue(pair.Value) : null))
				.ToList();

		return propertyTagType.GenericValue;
	}

	private int ResolveRootAnimNodeIndex()
	{
		if (TryGetRootNodeIndexForFunction("AnimGraph", out var animGraphRootNodeIndex))
			return animGraphRootNodeIndex;

		for (var index = 0; index < AnimNodePropertyData.Length; index++)
		{
			if (AnimNodePropertyData[index].IsRootNode)
				return index;
		}

		return -1;
	}

	private bool TryLoadAnimNodeStruct(string nodeTypeName, out UStruct nodeStruct)
	{
		nodeStruct = null!;
		var normalizedNodeTypeName = NormalizeNodeTypeName(nodeTypeName);

		foreach (var property in AnimNodeProperties)
		{
			if (!property.Struct.TryLoad<UStruct>(out var structType))
				continue;

			if (!structType.Name.Equals(nodeTypeName, StringComparison.OrdinalIgnoreCase) &&
				!structType.Name.Equals(normalizedNodeTypeName, StringComparison.OrdinalIgnoreCase))
				continue;

			nodeStruct = structType;
			return true;
		}

		foreach (var (key, _) in NodeTypeFallbackData)
		{
			if (!key.Equals(nodeTypeName, StringComparison.OrdinalIgnoreCase) &&
				!NormalizeNodeTypeName(key).Equals(normalizedNodeTypeName, StringComparison.OrdinalIgnoreCase))
				continue;

			var matchingProperty = AnimNodeProperties.FirstOrDefault(property =>
				property.Struct.ResolvedObject?.Name.Text.Equals(key, StringComparison.OrdinalIgnoreCase) == true);
			if (matchingProperty?.Struct.TryLoad<UStruct>(out var structType) == true)
			{
				nodeStruct = structType;
				return true;
			}
		}

		return false;
	}

	private bool TryResolvePoseLinkForFunction(string functionName, string outputPosePropertyName, out FPoseLinkDescription poseLink)
	{
		TryLoadClassDefaultObject(out var cdo);
		if (cdo is not null && TryResolvePoseLinkForHolder(cdo, functionName, outputPosePropertyName, out poseLink))
			return true;

		if (cdo?.SerializedSparseClassData is not null &&
			TryResolvePoseLinkForHolder(cdo.SerializedSparseClassData, functionName, outputPosePropertyName, out poseLink))
			return true;

		poseLink = FPoseLinkDescription.Invalid;
		return false;
	}

	private static bool TryResolvePoseLinkForHolder(IPropertyHolder holder, string functionName,
		string outputPosePropertyName, out FPoseLinkDescription poseLink)
	{
		if (holder.TryGetValue(out FStructFallback functionStruct, functionName))
		{
			if (TryResolvePoseLinkFromStruct(functionStruct, outputPosePropertyName, out poseLink))
				return true;
		}

		if (!outputPosePropertyName.Equals(functionName, StringComparison.OrdinalIgnoreCase) &&
			holder.TryGetValue(out FStructFallback outputPoseStruct, outputPosePropertyName) &&
			TryCreatePoseLink(outputPoseStruct, out poseLink))
			return true;

		poseLink = FPoseLinkDescription.Invalid;
		return false;
	}

	private static bool TryResolvePoseLinkFromStruct(FStructFallback functionStruct, string outputPosePropertyName,
		out FPoseLinkDescription poseLink)
	{
		if (functionStruct.TryGetValue(out FStructFallback outputPoseStruct, outputPosePropertyName) &&
			TryCreatePoseLink(outputPoseStruct, out poseLink))
			return true;

		if (TryCreatePoseLink(functionStruct, out poseLink))
			return true;

		poseLink = FPoseLinkDescription.Invalid;
		return false;
	}

	private static bool TryCreatePoseLink(FStructFallback poseLinkStruct, out FPoseLinkDescription poseLink)
	{
		if (!poseLinkStruct.TryGetValue(out int linkId, "LinkID"))
		{
			poseLink = FPoseLinkDescription.Invalid;
			return false;
		}

		var sourceLinkId = poseLinkStruct.GetOrDefault<int>("SourceLinkID", -1);
		var sourceProperty = poseLinkStruct.GetOrDefault<FName>("SourceProperty").Text;
		poseLink = new FPoseLinkDescription(linkId, sourceLinkId, sourceProperty);
		return true;
	}

	private static bool TryGetNestedPoseLink(FStructFallback holder, string propertyName, out FPoseLinkDescription poseLink)
	{
		if (holder.TryGetValue(out FStructFallback poseLinkStruct, propertyName) && TryCreatePoseLink(poseLinkStruct, out poseLink))
			return true;

		poseLink = FPoseLinkDescription.Invalid;
		return false;
	}

	private int ResolveAnimNodePropertyIndexFromLinkId(int linkId, bool preferRootNode)
	{
		if (linkId < 0 || AnimNodePropertyData.Length == 0)
			return -1;

		var candidates = new List<int>();
		AddCandidate(linkId);

		var childPropertyIndexMatch = Array.FindIndex(AnimNodePropertyData,
			data => data.ChildPropertyIndex == linkId);
		AddCandidate(childPropertyIndexMatch);

		AddCandidate(AnimNodePropertyData.Length - 1 - linkId);

		var reversedChildPropertyIndex = (ChildProperties?.Length ?? 0) - 1 - linkId;
		var reversedChildPropertyMatch = Array.FindIndex(AnimNodePropertyData,
			data => data.ChildPropertyIndex == reversedChildPropertyIndex);
		AddCandidate(reversedChildPropertyMatch);

		if (preferRootNode)
		{
			var rootCandidate = candidates.FirstOrDefault(index => index >= 0 && index < AnimNodePropertyData.Length &&
				AnimNodePropertyData[index].IsRootNode);
			if (rootCandidate >= 0)
				return rootCandidate;
		}

		return candidates.FirstOrDefault(index => index >= 0 && index < AnimNodePropertyData.Length, -1);

		void AddCandidate(int index)
		{
			if (index < 0 || candidates.Contains(index))
				return;
			candidates.Add(index);
		}
	}

	private bool IsAnimNodeStruct(FStructProperty structProperty)
	{
		if (IsKnownAnimNodeType(structProperty))
			return true;

		return IsStructOrDerivedFrom(structProperty, "AnimNode_Base") ||
			   structProperty.Name.Text.Contains("AnimGraphNode", StringComparison.OrdinalIgnoreCase);
	}

	private bool IsKnownAnimNodeType(FStructProperty structProperty)
	{
		foreach (var candidateName in GetStructTypeLookupNames(structProperty))
		{
			foreach (var lookupName in GetNodeTypeLookupNames(candidateName))
			{
				if (NodeTypeFallbackAliases.ContainsKey(lookupName))
					return true;
			}
		}

		return false;
	}

	private static IEnumerable<string> GetStructTypeLookupNames(FStructProperty structProperty)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		var resolvedName = structProperty.Struct.ResolvedObject?.Name.Text;
		if (!string.IsNullOrWhiteSpace(resolvedName) && seen.Add(resolvedName))
			yield return resolvedName;

		if (structProperty.Struct.TryLoad<UStruct>(out var structType))
		{
			if (!string.IsNullOrWhiteSpace(structType.Name) && seen.Add(structType.Name))
				yield return structType.Name;
		}
	}

	private static bool IsPoseLinkStruct(FStructProperty structProperty) =>
		IsStructName(structProperty, "PoseLink") ||
		IsStructName(structProperty, "FPoseLink") ||
		IsStructName(structProperty, "ComponentSpacePoseLink") ||
		IsStructName(structProperty, "FComponentSpacePoseLink");

	private static bool IsStructOrDerivedFrom(FStructProperty structProperty, string baseStructName)
	{
		if (structProperty?.Struct is null || !structProperty.Struct.TryLoad<UStruct>(out var current) || current is null)
			return false;

		while (current is not null)
		{
			if (current.Name.Equals(baseStructName, StringComparison.OrdinalIgnoreCase))
				return true;

			if (current.SuperStruct is null || current.SuperStruct.IsNull || !current.SuperStruct.TryLoad<UStruct>(out current))
				break;
		}

		return false;
	}

	private static bool IsStructName(FStructProperty structProperty, string structName) =>
		structProperty.Struct.ResolvedObject?.Name.Text.Equals(structName, StringComparison.OrdinalIgnoreCase) == true;

	private static string ResolveDeclaredPropertyType(FField property)
	{
		return property switch
		{
			FStructProperty structProperty => structProperty.Struct.ResolvedObject?.Name.Text ?? "Struct",
			FArrayProperty arrayProperty => $"Array<{(arrayProperty.Inner is not null ? ResolveDeclaredPropertyType(arrayProperty.Inner) : "Unknown") }>",
			FEnumProperty enumProperty => enumProperty.Enum.ResolvedObject?.Name.Text ?? "Enum",
			FSoftClassProperty => "SoftClass",
			FSoftObjectProperty => "SoftObject",
			FClassProperty classProperty => classProperty.MetaClass?.Name ?? "Class",
			FObjectProperty objectProperty => objectProperty.PropertyClass?.Name ?? "Object",
			FNameProperty => "Name",
			FStrProperty => "String",
			FTextProperty => "Text",
			FBoolProperty => "Bool",
			FByteProperty => "Byte",
			FIntProperty => "Int",
			FInt64Property => "Int64",
			FFloatProperty => "Float",
			FDoubleProperty => "Double",
			_ => property.GetType().Name
		};
	}

	private static bool TryGetNodeGuid(FAnimNodePropertyData propertyData, out FGuid guid)
	{
		if (propertyData.DefaultValue is not null && propertyData.DefaultValue.TryGetValue(out guid, "NodeGuid"))
			return true;

		guid = default;
		return false;
	}

	private static string ResolveTextValue(FStructFallback fallback, params string[] propertyNames)
	{
		foreach (var propertyName in propertyNames)
		{
			if (fallback.TryGetValue(out FName name, propertyName))
				return name.Text;

			if (fallback.TryGetValue(out string text, propertyName))
				return text;
		}

		return string.Empty;
	}

	private static string GetMapString(FPropertyTagType property)
	{
		if (property.GetValue(typeof(FName)) is FName name)
			return name.Text;

		if (property.GetValue(typeof(string)) is string text)
			return text;

		return property.GenericValue?.ToString() ?? string.Empty;
	}

	private static Dictionary<string, TValue> BuildAliasMap<TValue>(IReadOnlyDictionary<string, TValue> source)
	{
		var aliases = new Dictionary<string, TValue>(StringComparer.OrdinalIgnoreCase);
		foreach (var (key, value) in source)
		{
			foreach (var alias in GetNodeTypeLookupNames(key))
			{
				aliases.TryAdd(alias, value);
			}
		}

		return aliases;
	}

	private static IEnumerable<string> GetNodeTypeLookupNames(string nodeTypeName)
	{
		if (string.IsNullOrWhiteSpace(nodeTypeName))
			yield break;

		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (var candidate in ExpandNodeTypeLookupNames(nodeTypeName))
		{
			if (!string.IsNullOrEmpty(candidate) && seen.Add(candidate))
				yield return candidate;
		}
	}

	private static IEnumerable<string> ExpandNodeTypeLookupNames(string nodeTypeName)
	{
		var trimmedName = nodeTypeName.Trim();
		if (trimmedName.Length == 0)
			yield break;

		yield return trimmedName;

		if (TryExtractQuotedObjectPath(trimmedName, out var quotedObjectPath))
			yield return quotedObjectPath;

		var normalizedName = NormalizeNodeTypeName(trimmedName);
		if (!string.IsNullOrEmpty(normalizedName) && !normalizedName.Equals(trimmedName, StringComparison.OrdinalIgnoreCase))
			yield return normalizedName;
	}

	private static string NormalizeNodeTypeName(string nodeTypeName)
	{
		if (string.IsNullOrWhiteSpace(nodeTypeName))
			return string.Empty;

		var normalizedName = nodeTypeName.Trim();
		if (TryExtractQuotedObjectPath(normalizedName, out var quotedObjectPath))
			normalizedName = quotedObjectPath;

		var lastDotIndex = normalizedName.LastIndexOf('.');
		if (lastDotIndex >= 0 && lastDotIndex + 1 < normalizedName.Length)
			normalizedName = normalizedName[(lastDotIndex + 1)..];
		else
		{
			var lastSlashIndex = normalizedName.LastIndexOf('/');
			if (lastSlashIndex >= 0 && lastSlashIndex + 1 < normalizedName.Length)
				normalizedName = normalizedName[(lastSlashIndex + 1)..];
		}

		return normalizedName.Trim();
	}

	private static bool TryExtractQuotedObjectPath(string value, out string objectPath)
	{
		var firstQuoteIndex = value.IndexOf('\'');
		var lastQuoteIndex = value.LastIndexOf('\'');
		if (firstQuoteIndex >= 0 && lastQuoteIndex > firstQuoteIndex)
		{
			objectPath = value.Substring(firstQuoteIndex + 1, lastQuoteIndex - firstQuoteIndex - 1);
			return true;
		}

		objectPath = string.Empty;
		return false;
	}

	private FStructFallback? ResolveDefaultValue(string propertyName)
	{
		TryLoadClassDefaultObject(out var defaultObject);
		if (defaultObject != null && defaultObject.TryGetValue(out FStructFallback value, propertyName))
			return value;

		if (defaultObject?.SerializedSparseClassData != null &&
			defaultObject.SerializedSparseClassData.TryGetValue(out value, propertyName))
			return value;

		return null;
	}

	private bool TryLoadClassDefaultObject(out UObjectExport? defaultObject) =>
		ClassDefaultObject.TryLoad<UObjectExport>(out defaultObject);
}

[StructFallback]
public class FAnimBlueprintFunction
{
	public string Name;
	[JsonIgnore] public UFunction Function;
	public string OutputPosePropertyName;
	public string[] InputPoseNames;
	public int[] InputPoseNodeIndices;
	public int OutputPoseLinkID;
	public int OutputPoseNodeIndex;

	public FAnimBlueprintFunction(string name, UFunction function, string? outputPosePropertyName,
		string[] inputPoseNames, int[] inputPoseNodeIndices, int outputPoseLinkId, int outputPoseNodeIndex)
	{
		Name = name;
		Function = function;
		OutputPosePropertyName = outputPosePropertyName ?? string.Empty;
		InputPoseNames = inputPoseNames;
		InputPoseNodeIndices = inputPoseNodeIndices;
		OutputPoseLinkID = outputPoseLinkId;
		OutputPoseNodeIndex = outputPoseNodeIndex;
	}

	public bool HasOutputPose => !string.IsNullOrEmpty(OutputPosePropertyName);
}

public class FAnimNodePropertyData
{
	public string Name { get; }
	public string StructName { get; }
	public FStructProperty Property { get; }
	public int AnimNodePropertyIndex { get; }
	public int ChildPropertyIndex { get; }
	public FStructFallback? DefaultValue { get; }

	public bool IsRootNode => StructName.EndsWith("_Root", StringComparison.OrdinalIgnoreCase) ||
							  StructName.Equals("AnimNode_Root", StringComparison.OrdinalIgnoreCase);

	public bool IsLinkedAnimLayerNode => StructName.Equals("AnimNode_LinkedAnimLayer", StringComparison.OrdinalIgnoreCase);

	public bool IsLinkedAnimGraphNode => StructName.Equals("AnimNode_LinkedInputPose", StringComparison.OrdinalIgnoreCase) ||
										 StructName.Equals("AnimNode_LinkedAnimGraph", StringComparison.OrdinalIgnoreCase);

	public bool IsStateMachineNode => StructName.Equals("AnimNode_StateMachine", StringComparison.OrdinalIgnoreCase);

	public FAnimNodePropertyData(FStructProperty property, int animNodePropertyIndex, int childPropertyIndex,
		FStructFallback? defaultValue)
	{
		Property = property;
		Name = property.Name.Text;
		StructName = property.Struct.ResolvedObject?.Name.Text ?? string.Empty;
		AnimNodePropertyIndex = animNodePropertyIndex;
		ChildPropertyIndex = childPropertyIndex;
		DefaultValue = defaultValue;
	}
}

[Flags]
public enum EAnimNodeDataFlags : uint
{
	None = 0x00000000,
	HasInitialUpdateFunction = 0x00000001,
	HasBecomeRelevantFunction = 0x00000002,
	HasUpdateFunction = 0x00000004
}

public enum EAnimNodePropertySource
{
	DefaultObject,
	NodeData,
	ConstantData,
	MutableData,
	ConstantDataEntry,
	MutableDataEntry
}

public sealed class FAnimNodePropertyValue
{
	public EAnimNodePropertySource Source { get; }
	public int PropertyIndex { get; }
	public FPropertyTag PropertyTag { get; }
	public object? ResolvedValue { get; }

	public string Name => PropertyTag.Name.Text;
	public string PropertyType => PropertyTag.PropertyType.Text;
	public int ArrayIndex => PropertyTag.ArrayIndex;

	public FAnimNodePropertyValue(EAnimNodePropertySource source, int propertyIndex, FPropertyTag propertyTag, object? resolvedValue)
	{
		Source = source;
		PropertyIndex = propertyIndex;
		PropertyTag = propertyTag;
		ResolvedValue = resolvedValue;
	}
}

public sealed class FAnimNodeResolvedProperty
{
	public string Name { get; }
	public int PropertyIndex { get; private set; }
	public string DeclaredType { get; private set; }
	public IReadOnlyList<FAnimNodePropertyValue> Values => _values;

	private readonly List<FAnimNodePropertyValue> _values = [];

	public FAnimNodeResolvedProperty(string name, int propertyIndex, string declaredType)
	{
		Name = name;
		PropertyIndex = propertyIndex;
		DeclaredType = declaredType;
	}

	public void TryUpdateMetadata(int propertyIndex, string declaredType)
	{
		if (PropertyIndex < 0 && propertyIndex >= 0)
			PropertyIndex = propertyIndex;

		if (string.IsNullOrEmpty(DeclaredType) && !string.IsNullOrEmpty(declaredType))
			DeclaredType = declaredType;
	}

	public void AddValue(FAnimNodePropertyValue value)
	{
		if (_values.Any(existing => existing.Source == value.Source && ReferenceEquals(existing.PropertyTag, value.PropertyTag)))
			return;

		_values.Add(value);
	}
	}

public sealed class FAnimNodePropertyCollection
{
	public FAnimNodeData NodeData { get; }
	public IReadOnlyList<FAnimNodeResolvedProperty> Properties { get; }

	public FAnimNodePropertyCollection(FAnimNodeData nodeData, IReadOnlyList<FAnimNodeResolvedProperty> properties)
	{
		NodeData = nodeData;
		Properties = properties;
	}
	}

internal readonly record struct FAnimNodePropertySchemaEntry(string Name, int PropertyIndex, string DeclaredType, FField? PropertyField);

public class FAnimNodeData
{
	public const uint InvalidEntry = 0xFFFFFFFF;
	public const uint InstanceDataFlag = 0x80000000;
	public const uint InstanceDataMask = ~InstanceDataFlag;

	public int AnimNodePropertyIndex { get; }
	public string PropertyName { get; }
	public string StructTypeName { get; }
	public FAnimNodePropertyData? PropertyData { get; }
	public FStructFallback? RawData { get; }
	public FStructFallback? NodeData { get; }
	public FStructFallback? ConstantData { get; }
	public FStructFallback? MutableData { get; }
	public FGuid? NodeGuid { get; }
	public uint[] Entries { get; }
	public int NodeIndex { get; }
	public EAnimNodeDataFlags Flags { get; }

	public bool HasData => RawData is not null || NodeData is not null || ConstantData is not null || MutableData is not null;
	public bool HasEntries => Entries.Length > 0;

	public FAnimNodeData(int animNodePropertyIndex, FAnimNodePropertyData? propertyData, FStructFallback? rawData)
	{
		AnimNodePropertyIndex = animNodePropertyIndex;
		PropertyData = propertyData;
		RawData = rawData;
		PropertyName = propertyData?.Name ?? ResolveName(rawData, "PropertyName", "Property", "SourceProperty") ?? string.Empty;
		StructTypeName = propertyData?.StructName ?? ResolveName(rawData, "NodeType", "StructType", "ScriptStruct") ?? string.Empty;
		NodeData = ResolveNestedStruct(rawData, "NodeData", "Data");
		ConstantData = ResolveNestedStruct(rawData, "ConstantData", "Constants", "FoldedData");
		MutableData = ResolveNestedStruct(rawData, "MutableData", "Mutables", "InstanceData");
		NodeGuid = ResolveGuid(rawData) ?? ResolveGuid(propertyData?.DefaultValue);
		Entries = rawData?.GetOrDefault<uint[]>("Entries", []) ?? [];
		NodeIndex = rawData?.GetOrDefault("NodeIndex", animNodePropertyIndex) ?? animNodePropertyIndex;
		Flags = (EAnimNodeDataFlags) (rawData?.GetOrDefault<uint>("Flags", (uint) EAnimNodeDataFlags.None) ?? 0);
	}

	public bool HasNodeAnyFlags(EAnimNodeDataFlags flags) => (Flags & flags) != 0;

	public int GetResolvedEntryIndex(int propertyIndex)
	{
		if (propertyIndex < 0 || propertyIndex >= Entries.Length)
			return -1;

		var entry = Entries[propertyIndex];
		if (entry == InvalidEntry)
			return -1;

		return unchecked((int) (entry & InstanceDataMask));
	}

	public bool IsInstanceDataEntry(int propertyIndex, out int entryIndex)
	{
		entryIndex = GetResolvedEntryIndex(propertyIndex);
		return entryIndex >= 0 && propertyIndex >= 0 && propertyIndex < Entries.Length && (Entries[propertyIndex] & InstanceDataFlag) != 0;
	}

	public bool IsConstantDataEntry(int propertyIndex, out int entryIndex)
	{
		entryIndex = GetResolvedEntryIndex(propertyIndex);
		return entryIndex >= 0 && propertyIndex >= 0 && propertyIndex < Entries.Length && (Entries[propertyIndex] & InstanceDataFlag) == 0;
	}

	public bool TryGetRawValue(UAnimBlueprintGeneratedClass animBlueprintClass, int propertyIndex, out FPropertyTag propertyTag) =>
		animBlueprintClass.TryGetNodeValueRaw(this, propertyIndex, out propertyTag);

	public bool TryGetRawValue(UAnimBlueprintGeneratedClass animBlueprintClass, string propertyName, out FPropertyTag propertyTag) =>
		animBlueprintClass.TryGetNodeValueRaw(this, propertyName, out propertyTag);

	public bool TryGetValue<T>(UAnimBlueprintGeneratedClass animBlueprintClass, int propertyIndex, out T value) =>
		animBlueprintClass.TryGetNodeValue(this, propertyIndex, out value);

	public bool TryGetValue<T>(UAnimBlueprintGeneratedClass animBlueprintClass, string propertyName, out T value) =>
		animBlueprintClass.TryGetNodeValue(this, propertyName, out value);

	private static FStructFallback? ResolveNestedStruct(FStructFallback? rawData, params string[] names)
	{
		if (rawData is null)
			return null;

		foreach (var name in names)
		{
			if (rawData.TryGetValue(out FStructFallback nestedStruct, name))
				return nestedStruct;
		}

		return null;
	}

	private static FGuid? ResolveGuid(FStructFallback? rawData)
	{
		if (rawData is not null && rawData.TryGetValue(out FGuid guid, "NodeGuid"))
			return guid;

		return null;
	}

	private static string? ResolveName(FStructFallback? rawData, params string[] names)
	{
		if (rawData is null)
			return null;

		foreach (var name in names)
		{
			if (rawData.TryGetValue(out FName textName, name))
				return textName.Text;

			if (rawData.TryGetValue(out string text, name))
				return text;
		}

		return null;
	}
}

public class FAnimNodeStructData
{
	public string NodeTypeName { get; }
	public FStructFallback? RawData { get; }
	public IReadOnlyDictionary<string, int> NameToIndexMap => _nameToIndexMap;
	public int NumProperties { get; }

	private readonly Dictionary<string, int> _nameToIndexMap;

	public FAnimNodeStructData(string nodeTypeName, FStructFallback? rawData, UStruct? nodeStruct)
	{
		NodeTypeName = nodeTypeName;
		RawData = rawData;
		_nameToIndexMap = BuildNameToIndexMap(rawData, nodeStruct);
		NumProperties = rawData?.GetOrDefault("NumProperties", _nameToIndexMap.Count) ?? _nameToIndexMap.Count;
	}

	public int GetPropertyIndex(string propertyName) =>
		_nameToIndexMap.TryGetValue(propertyName, out var propertyIndex) ? propertyIndex : -1;

	public int GetPropertyIndex(FName propertyName) => GetPropertyIndex(propertyName.Text);

	public int GetNumProperties() => NumProperties;

	public bool DoesLayoutMatch(FAnimNodeStructData other)
	{
		if (other is null || NumProperties != other.NumProperties || _nameToIndexMap.Count != other._nameToIndexMap.Count)
			return false;

		foreach (var (name, propertyIndex) in _nameToIndexMap)
		{
			if (!other._nameToIndexMap.TryGetValue(name, out var otherIndex) || otherIndex != propertyIndex)
				return false;
		}

		return true;
	}

	private static Dictionary<string, int> BuildNameToIndexMap(FStructFallback? rawData, UStruct? nodeStruct)
	{
		if (TryBuildMapFromFallback(rawData, out var nameToIndexMap))
			return nameToIndexMap;

		nameToIndexMap = new Dictionary<string, int>(StringComparer.Ordinal);
		var childProperties = nodeStruct?.ChildProperties;
		if (childProperties is null)
			return nameToIndexMap;

		for (var propertyIndex = 0; propertyIndex < childProperties.Length; propertyIndex++)
			nameToIndexMap[childProperties[propertyIndex].Name.Text] = propertyIndex;

		return nameToIndexMap;
	}

	private static bool TryBuildMapFromFallback(FStructFallback? rawData, out Dictionary<string, int> nameToIndexMap)
	{
		nameToIndexMap = new Dictionary<string, int>(StringComparer.Ordinal);
		if (rawData is null || !rawData.TryGetValue(out UScriptMap rawMap, "NameToIndexMap") || rawMap.Properties.Count == 0)
			return false;

		foreach (var (key, value) in rawMap.Properties)
		{
			var propertyName = GetMapString(key);
			if (string.IsNullOrEmpty(propertyName) || value is null)
				continue;

			if (value.GetValue(typeof(int)) is int propertyIndex)
				nameToIndexMap[propertyName] = propertyIndex;
		}

		return nameToIndexMap.Count > 0;
	}

	private static string GetMapString(FPropertyTagType property)
	{
		if (property.GetValue(typeof(FName)) is FName name)
			return name.Text;

		if (property.GetValue(typeof(string)) is string text)
			return text;

		return property.GenericValue?.ToString() ?? string.Empty;
	}
}

public readonly record struct FPoseLinkDescription(int LinkID, int SourceLinkID, string SourceProperty)
{
	public static FPoseLinkDescription Invalid => new(-1, -1, string.Empty);
}

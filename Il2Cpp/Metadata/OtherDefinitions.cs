// Source of Truth: Transpiler Omnibus §18.6

namespace Rosetta.Metadata;

/// <summary>
/// Il2CppFieldDefinition: { int NameIndex, idx TypeIndex, uint Token }
/// Source: Transpiler Omnibus §18.6, §102
/// </summary>
public sealed class FieldDefinition
{
    public int NameIndex { get; set; }
    public int TypeIndex { get; set; }
    public uint Token { get; set; }

    public string? Name { get; set; }

    /// <summary>
    /// Whether this field is static.
    /// Set during resolution from Il2CppType.Attrs: (Attrs & 0x0010) != 0
    /// Source: ECMA-335 §II.23.1.5 — FieldAttributes.Static = 0x0010
    /// </summary>
    public bool IsStatic { get; set; }

    /// <summary>
    /// Whether this field is a literal (const). Literal fields are compile-time
    /// constants and are NOT stored in the runtime static_fields block.
    /// Set from Il2CppType.Attrs: (Attrs &amp; 0x0040) != 0
    /// </summary>
    public bool IsLiteral { get; set; }

    /// <summary>
    /// Whether this field is init-only (readonly).
    /// Set from Il2CppType.Attrs: (Attrs & 0x0020) != 0
    /// Source: ECMA-335 §II.23.1.5 — FieldAttributes.InitOnly = 0x0020
    /// </summary>
    public bool IsReadOnly { get; set; }

    /// <summary>
    /// Field access level from ECMA-335 FieldAttributes (bits 0-2).
    ///   0x00 = CompilerControlled, 0x01 = Private, 0x02 = FamANDAssem,
    ///   0x03 = Assembly (internal), 0x04 = Family (protected),
    ///   0x05 = FamORAssem (protected internal), 0x06 = Public
    /// </summary>
    public uint FieldAccess { get; set; }

    /// <summary>
    /// Whether this field is marked [NonSerialized].
    /// Set from Il2CppType.Attrs: (Attrs &amp; 0x0080) != 0
    /// Source: ECMA-335 §II.23.1.5 — FieldAttributes.NotSerialized = 0x0080
    /// This is a pseudo-custom attribute (flag bit, not in attribute blob).
    /// </summary>
    public bool IsNotSerialized { get; set; }

    public override string ToString() => Name ?? $"Field_{Token:X8}";
}

/// <summary>
/// Il2CppParameterDefinition: { int NameIndex, uint Token, idx TypeIndex }
/// Source: Transpiler Omnibus §18.6, §102
/// </summary>
public sealed class ParameterDefinition
{
    public int NameIndex { get; set; }
    public uint Token { get; set; }
    public int TypeIndex { get; set; }

    public string? Name { get; set; }
    public override string ToString() => Name ?? $"Param_{Token:X8}";
}

/// <summary>
/// Il2CppImageDefinition: Module/assembly image metadata.
/// Source: Transpiler Omnibus §18.6, §102
/// </summary>
public sealed class ImageDefinition
{
    public int NameIndex { get; set; }
    public int AssemblyIndex { get; set; }
    public int TypeStart { get; set; }
    public uint TypeCount { get; set; }
    public int ExportedTypeStart { get; set; }
    public uint ExportedTypeCount { get; set; }
    public int EntryPointIndex { get; set; }
    public uint Token { get; set; }
    public int CustomAttributeStart { get; set; }
    public uint CustomAttributeCount { get; set; }

    public string? Name { get; set; }
    public override string ToString() => Name ?? $"Image_{AssemblyIndex}";
}

/// <summary>
/// Il2CppGenericParameter from the GenericParameters section (Section 12).
///
/// Source: Il2CppGenericParameter.Serialize() field order:
///   OwnerIndex:       WriteIndex(GCI)     — GenericContainerIndex size (variable)
///   NameIndex:        WriteInt            — 4 bytes
///   ConstraintsStart: WriteShort          — 2 bytes
///   ConstraintsCount: WriteShort          — 2 bytes
///   Num:              WriteUShort         — 2 bytes (position in the parameter list)
///   Flags:            WriteUShort         — 2 bytes
///
/// Source: GenericsDataWriter.cs line 41-47
/// </summary>
public sealed class GenericParameterDef
{
    /// <summary>Index into GenericContainers section — the owner of this param.</summary>
    public int OwnerIndex { get; set; }

    /// <summary>Index into MetadataStrings — the name of this param (T, TKey, TValue, etc.).</summary>
    public int NameIndex { get; set; }

    /// <summary>Start index into GenericConstraints section.</summary>
    public short ConstraintsStart { get; set; }

    /// <summary>Number of constraints.</summary>
    public short ConstraintsCount { get; set; }

    /// <summary>Position of this parameter in the owner's parameter list (0-based).</summary>
    public ushort Num { get; set; }

    /// <summary>GenericParameterAttributes flags.</summary>
    public ushort Flags { get; set; }

    public string? Name { get; set; }
    public override string ToString() => Name ?? $"T{Num}";
}

/// <summary>
/// Generic container from the GenericContainers section (Section 14).
///
/// Source: GenericsDataWriter.WriteGenericContainers() lines 76-79:
///   stream.WriteInt(ownerIndex);       — 4 bytes (method or type index)
///   stream.WriteInt(count);            — 4 bytes (number of generic params)
///   stream.WriteInt(isMethod ? 1 : 0); — 4 bytes
///   stream.WriteInt(parameterStart);   — 4 bytes (first generic param index)
///
/// Total: 16 bytes always (no variable-width indices).
/// </summary>
public sealed class GenericContainerDef
{
    /// <summary>
    /// Owner index. If IsMethod=true, this is a MethodDefinition index.
    /// If IsMethod=false, this is a TypeDefinition index.
    /// Source: GenericsDataWriter.cs line 76
    /// </summary>
    public int OwnerIndex { get; set; }

    /// <summary>Number of generic parameters in this container.</summary>
    public int Count { get; set; }

    /// <summary>True if this container belongs to a method, false for a type.</summary>
    public bool IsMethod { get; set; }

    /// <summary>Index of the first GenericParameter in the GenericParameters section.</summary>
    public int ParameterStart { get; set; }
}

/// <summary>
/// Property definition from the Properties section (Section 4).
///
/// Source Path: PropertyWriter.cs lines 20-24:
///   stream.WriteInt(NameIndex);
///   stream.WriteInt(getMethodOffset or -1);
///   stream.WriteInt(setMethodOffset or -1);
///   stream.WriteInt(attributes);
///   stream.WriteUInt(token);
///
/// Note: Get/Set are relative method offsets from the declaring type's first method.
/// Total: 20 bytes always (5 × WriteInt/WriteUInt).
/// </summary>
public sealed class PropertyDef
{
    /// <summary>Index into MetadataStrings → property name.</summary>
    public int NameIndex { get; set; }

    /// <summary>
    /// Offset of the get method relative to the declaring type's first method.
    /// -1 if no getter.
    /// Source: PropertyWriter.cs line 21
    /// </summary>
    public int Get { get; set; }

    /// <summary>
    /// Offset of the set method relative to the declaring type's first method.
    /// -1 if no setter.
    /// Source: PropertyWriter.cs line 22
    /// </summary>
    public int Set { get; set; }

    /// <summary>PropertyAttributes flags.</summary>
    public int Attrs { get; set; }

    /// <summary>Metadata token.</summary>
    public uint Token { get; set; }

    public string? Name { get; set; }
    public override string ToString() => Name ?? $"Property_{Token:X8}";
}

/// <summary>
/// Event definition from the Events section (Section 3).
///
/// Source Path: Il2CppEventDefinition.cs Serialize() lines 21-26:
///   stream.WriteInt(NameIndex);
///   stream.WriteIndex(TypeIndex, sizes.TypeIndex);
///   stream.WriteInt(Add);
///   stream.WriteInt(Remove);
///   stream.WriteInt(Raise);
///   stream.WriteUInt(Token);
///
/// Note: Add/Remove/Raise are relative method offsets from the declaring type's first method.
/// Total: TypeIndex + 20 bytes.
/// </summary>
public sealed class EventDef
{
    /// <summary>Index into MetadataStrings → event name.</summary>
    public int NameIndex { get; set; }

    /// <summary>
    /// Index into Il2CppType table — the event's handler type.
    /// Source: EventWriter.cs line 31
    /// </summary>
    public int TypeIndex { get; set; }

    /// <summary>
    /// Offset of the add method relative to the declaring type's first method.
    /// -1 if none.
    /// Source: Il2CppEventDefinition.cs line 23
    /// </summary>
    public int Add { get; set; }

    /// <summary>
    /// Offset of the remove method relative to the declaring type's first method.
    /// -1 if none.
    /// </summary>
    public int Remove { get; set; }

    /// <summary>
    /// Offset of the raise (invoke) method relative to the declaring type's first method.
    /// -1 if none.
    /// </summary>
    public int Raise { get; set; }

    /// <summary>Metadata token.</summary>
    public uint Token { get; set; }

    public string? Name { get; set; }
    public override string ToString() => Name ?? $"Event_{Token:X8}";
}

/// <summary>
/// Il2CppFieldDefaultValue from section 7 (FieldDefaultValues).
///
/// Source Path: Il2CppFieldDefaultValue.cs lines 7-11:
///   FieldIndex: WriteInt                — 4 bytes
///   TypeIndex:  WriteIndex(TypeIndex)   — variable
///   DataIndex:  WriteInt                — 4 bytes
///
/// Source Path: FieldAndParameterDataWriter.cs lines 62-69
/// </summary>
public sealed class FieldDefaultValueDef
{
    /// <summary>Index into FieldDefinitions.</summary>
    public int FieldIndex { get; set; }

    /// <summary>Index into Il2CppType table — the type of the default value.</summary>
    public int TypeIndex { get; set; }

    /// <summary>Index into the DefaultValuesData blob (section 8).</summary>
    public int DataIndex { get; set; }
}

/// <summary>
/// Il2CppParameterDefaultValue from section 6 (ParamDefaultValues).
///
/// Source Path: Il2CppParameterDefaultValue.cs lines 7-11:
///   ParameterIndex: WriteIndex(ParameterIndex) — variable
///   TypeIndex:      WriteIndex(TypeIndex)       — variable
///   DataIndex:      WriteInt                    — 4 bytes
///
/// Source Path: FieldAndParameterDataWriter.cs lines 39-47
/// </summary>
public sealed class ParameterDefaultValueDef
{
    /// <summary>Index into ParameterDefinitions.</summary>
    public int ParameterIndex { get; set; }

    /// <summary>Index into Il2CppType table — the type of the default value.</summary>
    public int TypeIndex { get; set; }

    /// <summary>Index into the DefaultValuesData blob (section 8).</summary>
    public int DataIndex { get; set; }
}

/// <summary>
/// Il2CppFieldMarshaledSize from section 9 (FieldMarshaledSizes).
///
/// Source Path: Il2CppFieldMarshaledSize.cs lines 7-11:
///   FieldIndex: WriteInt                — 4 bytes
///   TypeIndex:  WriteIndex(TypeIndex)   — variable
///   Size:       WriteInt                — 4 bytes
///
/// Source Path: FieldAndParameterDataWriter.cs lines 96-103
/// </summary>
public sealed class FieldMarshaledSizeDef
{
    /// <summary>Index into FieldDefinitions.</summary>
    public int FieldIndex { get; set; }

    /// <summary>Index into Il2CppType table.</summary>
    public int TypeIndex { get; set; }

    /// <summary>Marshaled size in bytes.</summary>
    public int Size { get; set; }
}

/// <summary>
/// Il2CppFieldRef from section 22 (FieldRefs).
///
/// Source Path: Il2CppFieldRef.cs lines 7-9:
///   TypeIndex:  WriteIndex(TypeIndex) — variable
///   FieldIndex: WriteInt              — 4 bytes
///
/// Source Path: AssemblyAndAttributeDataWriter.cs lines 107-122
/// </summary>
public sealed class FieldRefDef
{
    /// <summary>Index into Il2CppType table — the declaring type.</summary>
    public int TypeIndex { get; set; }

    /// <summary>Field index within the declaring type (NOT a global FieldDefinitions index).
    /// To get the global FieldDef index: TypeDef.FieldStart + FieldIndex.</summary>
    public int FieldIndex { get; set; }
}

/// <summary>
/// Il2CppInterfaceOffsetPair from section 18 (InterfaceOffsets).
///
/// Source Path: Il2CppInterfaceOffsetPair.cs lines 7-9:
///   InterfaceTypeIndex: WriteIndex(TypeIndex) — variable
///   Offset:             WriteInt              — 4 bytes
///
/// Source Path: InterfaceOffsetsWriter.cs lines 21-28
/// </summary>
public sealed class InterfaceOffsetPairDef
{
    /// <summary>Index into Il2CppType table — the interface type.</summary>
    public int InterfaceTypeIndex { get; set; }

    /// <summary>Offset into the VTable for this interface.</summary>
    public int Offset { get; set; }
}

/// <summary>
/// Assembly definition from section 21 (Assemblies).
///
/// Source Path: AssemblyAndAttributeDataWriter.cs lines 73-98:
///   imageIndex:           WriteInt     — 4 bytes
///   assemblyToken:        WriteUInt    — 4 bytes
///   moduleToken:          WriteUInt    — 4 bytes
///   referencedAssemblyStart: WriteInt  — 4 bytes
///   referencedAssemblyCount: WriteInt  — 4 bytes
///   nameIndex:            WriteInt     — 4 bytes
///   cultureIndex:         WriteInt     — 4 bytes
///   publicKeyDataIndex:   WriteInt     — 4 bytes
///   hashAlgorithm:        WriteUInt    — 4 bytes
///   hashLength:           WriteInt     — 4 bytes
///   attributes:           WriteUInt    — 4 bytes
///   major:                WriteInt     — 4 bytes
///   minor:                WriteInt     — 4 bytes
///   build:                WriteInt     — 4 bytes
///   revision:             WriteInt     — 4 bytes
///   publicKeyToken:       8 × WriteByte — 8 bytes
///
/// Total: 15 × 4 + 8 = 68 bytes (fixed size, no variable-width indices).
/// </summary>
public sealed class AssemblyDef
{
    public int ImageIndex { get; set; }
    public uint AssemblyToken { get; set; }
    public uint ModuleToken { get; set; }
    public int ReferencedAssemblyStart { get; set; }
    public int ReferencedAssemblyCount { get; set; }
    public int NameIndex { get; set; }
    public int CultureIndex { get; set; }
    public int PublicKeyDataIndex { get; set; }
    public uint HashAlgorithm { get; set; }
    public int HashLength { get; set; }
    public uint Attributes { get; set; }
    public int Major { get; set; }
    public int Minor { get; set; }
    public int Build { get; set; }
    public int Revision { get; set; }
    public byte[] PublicKeyToken { get; set; } = Array.Empty<byte>(); // always 8 bytes

    public string? Name { get; set; }
    public override string ToString() => Name ?? $"Assembly_{AssemblyToken:X8}";
}

/// <summary>
/// AttributeDataRange from section 25 (AttributeDataRanges).
///
/// Source Path: AssemblyAndAttributeDataWriter.cs lines 197-203:
///   token:  WriteUInt    — 4 bytes
///   offset: WriteUInt    — 4 bytes
///
/// Total: 8 bytes per entry.
/// </summary>
public sealed class AttributeDataRangeDef
{
    /// <summary>Metadata token (type 0x02=TypeDef, 0x06=Method, etc.).</summary>
    public uint Token { get; set; }

    /// <summary>Byte offset into the AttributeData blob (section 24).</summary>
    public uint StartOffset { get; set; }

    /// <summary>Start index into the attributeTypes array. Only used for < v29.</summary>
    public int Start { get; set; }

    /// <summary>Number of attributes. Only used for < v29.</summary>
    public int Count { get; set; }
}

/// <summary>
/// UnresolvedVirtualCallParameterRange from section 27 (UnresolvedVC_ParamRanges).
///
/// Source Path: UnresolvedVirtualCallWriter.cs lines 47-53:
///   start: WriteInt    — 4 bytes (start index into UnresolvedVC_ParamTypes)
///   count: WriteInt    — 4 bytes (number of type indices)
///
/// Total: 8 bytes per entry.
/// </summary>
public sealed class UnresolvedVCallRange
{
    /// <summary>Start index into UnresolvedVC_ParamTypes section (section 26).</summary>
    public int Start { get; set; }

    /// <summary>Number of parameter types in this range.</summary>
    public int Length { get; set; }
}

/// <summary>
/// Il2CppWindowsRuntimeTypeNamePair from section 28 (WinRT_TypeNamePairs).
///
/// Source Path: Il2CppWindowsRuntimeTypeNamePair.cs lines 7-9:
///   NameIndex: WriteInt              — 4 bytes
///   TypeIndex: WriteIndex(TypeIndex) — variable
///
/// Note: Section 28 has 0 items in this binary, but included for completeness.
/// </summary>
public sealed class WinRuntimeTypeNamePairDef
{
    /// <summary>Index into MetadataStrings.</summary>
    public int NameIndex { get; set; }

    /// <summary>Index into Il2CppType table.</summary>
    public int TypeIndex { get; set; }
}


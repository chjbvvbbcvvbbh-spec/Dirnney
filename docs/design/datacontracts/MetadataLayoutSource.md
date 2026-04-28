# Contract MetadataLayoutSource

This contract is one of the sources consulted by [`Target.GetTypeInfo(DataType)`](./data_descriptors.md#type-lookup)
to synthesize layout information for well-known managed types (CoreLib types such as
`System.Exception`, `System.String`, `System.Runtime.InteropServices.ComWrappers`,
`System.Runtime.CompilerServices.ConditionalWeakTable`, …) by looking up their fields
in ECMA metadata rather than hard-coding offsets in native `datadescriptor.inc`
entries.

`MetadataLayoutSource` implements the `ITypeInfoSource` capability interface:

``` csharp
public interface ITypeInfoSource
{
    bool TryGetTypeInfo(DataType type, out Target.TypeInfo info);
}

public interface IMetadataLayoutSource : IContract, ITypeInfoSource { }
```

The `Target` layer resolves a `GetTypeInfo(DataType)` call by first consulting the
native data descriptor (keyed by the `DataType` enum member name) and then invoking
each registered `ITypeInfoSource` contract in registration order, merging the
results. Callers do not invoke this contract directly; they call
`target.GetTypeInfo(DataType.Exception)` (or any other well-known `DataType`).

A `DataType` enum member is only resolvable via `MetadataLayoutSource` if it is
decorated with `[ManagedType("Fully.Qualified.Name")]`:

``` csharp
public enum DataType
{
    ...
    [ManagedType("System.Exception")]
    Exception,

    [ManagedType("System.Threading.Lock")]
    Lock,

    [ManagedType("System.Runtime.CompilerServices.ConditionalWeakTable`2+Entry")]
    ConditionalWeakTableEntry,
    ...
}
```

The returned `Target.TypeInfo` is *partial* — it carries only what metadata can
supply:

- **Fields.** Instance-field offsets, pre-shifted by `sizeof(Object)` so callers
  can read a field from an object via `address + field.Offset`, matching the
  convention used by every native-descriptor-backed `Target.TypeInfo`. Field
  `TypeName` is mapped from `CorElementType` to the descriptor primitive strings
  (`"int32"`, `"uint16"`, `"pointer"`, `"nuint"`, …) so the debug-mode primitive
  assertions in `TargetFieldExtensions` still apply.
- **StaticFields.** Absolute storage-slot addresses (`TargetPointer`) returned by
  `IRuntimeTypeSystem.GetFieldDescStaticAddress`, keyed by field name. Callers
  still perform their own `ReadPointer` / `Read<T>` off that address.
- **TypeHandle.** The runtime `TypeHandle` for the resolved type. Useful for
  identity comparisons — e.g. `Object.GetMethodTableAddress(obj) == typeInfo.TypeHandle!.Value.Address`.
- **Size.** Not set by this source. Instance-size semantics differ for heap
  objects (where `sizeof(Object)` is appropriate) vs. value-type elements inlined
  in an array, so this source leaves `Size` `null` and lets consumers decide.

ThreadStatic fields are skipped: their storage addresses are per-thread and
cannot be encoded as a single `TargetPointer`.

Value-type fields embedded inline (e.g. `ConditionalWeakTable`+`Entry` stored in
an `Entry[]` element storage) need the element-relative offset, not the
object-relative offset. Because this source pre-shifts by `sizeof(Object)`,
consumers reading such storage must subtract `sizeof(Object)` back out:

``` csharp
Target.TypeInfo entryType = target.GetTypeInfo(DataType.ConditionalWeakTableEntry);
int objectSize = (int)target.GetTypeInfo(DataType.Object).Size!.Value;
int relOffset = entryType.Fields["HashCode"].Offset - objectSize;
```

## Version 1

All lookups are against the system assembly (CoreLib). The implementation
delegates to `IRuntimeTypeSystem` primitives (`GetTypeByNameAndModule`,
`GetTypeDefToken`, `GetFieldDescByName`, `GetFieldDescOffset`,
`GetFieldDescStaticAddress`, `GetFieldDescType`) plus the module's ECMA
`MetadataReader` to enumerate `TypeDefinition.GetFields()`. If the target has no
CoreLib ECMA metadata available (e.g. a stripped dump), `TryGetTypeInfo` returns
`false`.

Data descriptors used: none — the entire point of this contract is that
managed-type layouts are not described by native data descriptors.

Contracts used:
| Contract Name |
| --- |
| `Loader` |
| `RuntimeTypeSystem` |
| `EcmaMetadata` |

``` csharp
bool TryGetTypeInfo(DataType type, out Target.TypeInfo info)
{
    info = default;

    string? managedFqName = GetManagedTypeAttributeValue(type);
    if (managedFqName is null)
        return false;

    (string @namespace, string typeName) = SplitManagedFqName(managedFqName);

    IRuntimeTypeSystem rts = target.Contracts.RuntimeTypeSystem;
    TargetPointer systemAssembly = target.Contracts.Loader.GetSystemAssembly();
    ModuleHandle moduleHandle = target.Contracts.Loader.GetModuleHandleFromAssemblyPtr(systemAssembly);
    MetadataReader? reader = target.Contracts.EcmaMetadata.GetMetadata(moduleHandle);
    if (reader is null)
        return false;

    TypeHandle th = rts.GetTypeByNameAndModule(typeName, @namespace, moduleHandle);
    uint typeDefToken = rts.GetTypeDefToken(th);
    TypeDefinition typeDef = reader.GetTypeDefinition(
        (TypeDefinitionHandle)MetadataTokens.Handle((int)typeDefToken));

    uint objectSize = target.GetTypeInfo(DataType.Object).Size!.Value;
    var instanceFields = new Dictionary<string, Target.FieldInfo>();
    var staticFields = new Dictionary<string, TargetPointer>();

    foreach (FieldDefinitionHandle fdh in typeDef.GetFields())
    {
        FieldDefinition fieldDef = reader.GetFieldDefinition(fdh);
        string name = reader.GetString(fieldDef.Name);
        TargetPointer fd = rts.GetFieldDescByName(th, name);
        if (fd == TargetPointer.Null) continue;

        bool isStatic = (fieldDef.Attributes & FieldAttributes.Static) != 0;
        if (isStatic && HasThreadStaticAttribute(reader, fieldDef))
            continue;

        if (isStatic)
        {
            staticFields[name] = rts.GetFieldDescStaticAddress(fd);
        }
        else
        {
            uint rawOffset = rts.GetFieldDescOffset(fd, fieldDef);
            instanceFields[name] = new Target.FieldInfo
            {
                Offset = (int)(rawOffset + objectSize),
                TypeName = MapCorElementTypeToDescriptorName(rts.GetFieldDescType(fd)),
            };
        }
    }

    info = new Target.TypeInfo
    {
        TypeHandle = th,
        Fields = instanceFields,
        StaticFields = staticFields,
    };
    return true;
}
```

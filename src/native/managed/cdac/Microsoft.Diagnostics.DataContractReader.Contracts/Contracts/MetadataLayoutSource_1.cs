// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Microsoft.Diagnostics.DataContractReader.Contracts;

internal sealed class MetadataLayoutSource_1 : IMetadataLayoutSource
{
    private readonly Target _target;
    private readonly Dictionary<DataType, Target.TypeInfo> _typeInfoCache = new();

    public MetadataLayoutSource_1(Target target)
    {
        _target = target;
    }

    public void Flush()
    {
        _typeInfoCache.Clear();
    }

    bool ITypeInfoSource.TryGetTypeInfo(DataType type, out Target.TypeInfo info)
    {
        if (_typeInfoCache.TryGetValue(type, out info))
            return true;

        string? managedFqName = GetManagedFqName(type);
        if (managedFqName is null)
        {
            info = default;
            return false;
        }

        if (!TryBuildTypeInfo(managedFqName, out info))
            return false;

        _typeInfoCache[type] = info;
        return true;
    }

    /// <summary>
    /// Returns the fully-qualified managed name attached to a <see cref="DataType"/> enum
    /// member via <see cref="ManagedTypeAttribute"/>, or <c>null</c> if the member is not
    /// decorated (i.e. the type is not resolvable via this source).
    /// </summary>
    private static string? GetManagedFqName(DataType type)
    {
        System.Type enumType = typeof(DataType);
        string name = type.ToString();
        FieldInfo? field = enumType.GetField(name, BindingFlags.Public | BindingFlags.Static);
        if (field is null)
            return null;
        ManagedTypeAttribute? attr = field.GetCustomAttribute<ManagedTypeAttribute>(inherit: false);
        return attr?.FullyQualifiedName;
    }

    private bool TryBuildTypeInfo(string managedFqName, out Target.TypeInfo info)
    {
        info = default;

        SplitFqName(managedFqName, out string @namespace, out string typeName);

        ILoader loader = _target.Contracts.Loader;
        TargetPointer systemAssembly = loader.GetSystemAssembly();
        ModuleHandle moduleHandle = loader.GetModuleHandleFromAssemblyPtr(systemAssembly);
        IRuntimeTypeSystem rts = _target.Contracts.RuntimeTypeSystem;
        TypeHandle th = rts.GetTypeByNameAndModule(typeName, @namespace, moduleHandle);
        if (th.Address == TargetPointer.Null)
            return false;

        MetadataReader? mdReader = _target.Contracts.EcmaMetadata.GetMetadata(moduleHandle);
        if (mdReader is null)
            return false;

        uint typeDefToken = rts.GetTypeDefToken(th);
        TypeDefinitionHandle typeDefHandle = (TypeDefinitionHandle)MetadataTokens.Handle((int)typeDefToken);
        TypeDefinition typeDef = mdReader.GetTypeDefinition(typeDefHandle);

        uint objectSize = _target.GetTypeInfo(DataType.Object).Size!.Value;

        Dictionary<string, Target.FieldInfo> instanceFields = new();
        Dictionary<string, TargetPointer> staticFields = new();

        foreach (FieldDefinitionHandle fieldHandle in typeDef.GetFields())
        {
            FieldDefinition fieldDef = mdReader.GetFieldDefinition(fieldHandle);
            string name = mdReader.GetString(fieldDef.Name);
            bool isStatic = (fieldDef.Attributes & FieldAttributes.Static) != 0;

            // [ThreadStatic] fields have per-thread storage and cannot be resolved to a single
            // absolute address; skip them here and let callers read per-thread state directly.
            if (isStatic && HasThreadStaticAttribute(mdReader, fieldDef))
                continue;

            TargetPointer fieldDescAddr = rts.GetFieldDescByName(th, name);
            if (fieldDescAddr == TargetPointer.Null)
                continue;

            if (isStatic)
            {
                staticFields[name] = rts.GetFieldDescStaticAddress(fieldDescAddr);
            }
            else
            {
                uint fdOffset = rts.GetFieldDescOffset(fieldDescAddr, fieldDef);
                CorElementType elementType = rts.GetFieldDescType(fieldDescAddr);
                instanceFields[name] = new Target.FieldInfo
                {
                    Offset = (int)(fdOffset + objectSize),
                    TypeName = MapCorElementTypeToDescriptorName(elementType),
                };
            }
        }

        info = new Target.TypeInfo
        {
            Fields = instanceFields,
            StaticFields = staticFields,
            TypeHandle = th,
        };
        return true;
    }

    /// <summary>
    /// Splits a fully-qualified managed type name into (namespace, typeName). Nested types are
    /// preserved in the <c>typeName</c> portion (e.g. <c>Container+Entry</c>).
    /// </summary>
    private static void SplitFqName(string fqName, out string @namespace, out string typeName)
    {
        int lastDot = fqName.LastIndexOf('.');
        if (lastDot < 0)
        {
            @namespace = string.Empty;
            typeName = fqName;
            return;
        }
        @namespace = fqName.Substring(0, lastDot);
        typeName = fqName.Substring(lastDot + 1);
    }

    private static bool HasThreadStaticAttribute(MetadataReader mdReader, FieldDefinition fieldDef)
    {
        foreach (CustomAttributeHandle h in fieldDef.GetCustomAttributes())
        {
            CustomAttribute attr = mdReader.GetCustomAttribute(h);
            if (attr.Constructor.Kind == HandleKind.MemberReference)
            {
                MemberReference memberRef = mdReader.GetMemberReference((MemberReferenceHandle)attr.Constructor);
                if (memberRef.Parent.Kind == HandleKind.TypeReference)
                {
                    TypeReference typeRef = mdReader.GetTypeReference((TypeReferenceHandle)memberRef.Parent);
                    if (mdReader.StringComparer.Equals(typeRef.Name, "ThreadStaticAttribute") &&
                        mdReader.StringComparer.Equals(typeRef.Namespace, "System"))
                    {
                        return true;
                    }
                }
            }
        }
        return false;
    }

    /// <summary>
    /// Maps an ECMA-335 <see cref="CorElementType"/> to the descriptor-type-name string used by
    /// <see cref="TargetFieldExtensions"/> debug assertions. Returns null when no precise mapping
    /// applies (the assertions treat null/empty TypeName as "skip validation").
    /// </summary>
    private static string? MapCorElementTypeToDescriptorName(CorElementType type) => type switch
    {
        CorElementType.Boolean => "bool",
        CorElementType.I1 => "int8",
        CorElementType.U1 => "uint8",
        CorElementType.Char or CorElementType.U2 => "uint16",
        CorElementType.I2 => "int16",
        CorElementType.I4 => "int32",
        CorElementType.U4 => "uint32",
        CorElementType.I8 => "int64",
        CorElementType.U8 => "uint64",
        CorElementType.I or CorElementType.U => "nuint",
        CorElementType.String
            or CorElementType.Ptr
            or CorElementType.Byref
            or CorElementType.Class
            or CorElementType.Array
            or CorElementType.SzArray
            or CorElementType.GenericInst
            or CorElementType.Object
            or CorElementType.Var
            or CorElementType.MVar
            or CorElementType.FnPtr => "pointer",
        _ => null,
    };
}

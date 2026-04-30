// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Diagnostics.DataContractReader.Contracts;

/// <summary>
/// Contract that supplies layout information for well-known types by looking them up in ECMA
/// metadata in the system assembly (CoreLib). See
/// <c>docs/design/datacontracts/MetadataLayoutSource.md</c>.
/// </summary>
/// <remarks>
/// <para>
/// As an <see cref="ITypeInfoSource"/>, this contract is invoked by
/// <see cref="Target.GetTypeInfo(DataType)"/> to supplement the native data descriptor with
/// field offsets and static-field addresses read from metadata. A <see cref="DataType"/> is
/// only resolvable through this source if its enum member is decorated with
/// <see cref="ManagedTypeAttribute"/> (names the fully-qualified managed type to look up).
/// </para>
/// <para>
/// Returned <see cref="Target.TypeInfo"/> values are partial: fields are populated with
/// pre-shifted offsets (shifted by <c>sizeof(Object)</c> so callers can use
/// <c>objectAddress + field.Offset</c>). <see cref="Target.TypeInfo.Size"/> is not set because
/// instance-size semantics differ for heap objects vs. value types.
/// <see cref="Target.TypeInfo.TypeHandle"/> and <see cref="Target.TypeInfo.StaticFields"/> are
/// populated when available from the runtime type system.
/// </para>
/// </remarks>
public interface IMetadataLayoutSource : IContract, ITypeInfoSource
{
    static string IContract.Name { get; } = nameof(MetadataLayoutSource);

    bool ITypeInfoSource.TryGetTypeInfo(DataType type, out Target.TypeInfo info)
    {
        info = default;
        return false;
    }
}

public readonly struct MetadataLayoutSource : IMetadataLayoutSource
{
    // Default: no-op source.
}

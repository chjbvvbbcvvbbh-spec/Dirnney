// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Diagnostics.DataContractReader.Contracts;

/// <summary>
/// Capability implemented by a contract that can supply layout information for well-known
/// <see cref="DataType"/> values.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Target.GetTypeInfo(DataType)"/> discovers sources by looking up contracts that
/// implement this interface and merging their contributions. A contract opts in to being a
/// layout source by deriving its contract interface from <see cref="ITypeInfoSource"/> in
/// addition to <see cref="IContract"/>. Today the only such contract is
/// <see cref="IMetadataLayoutSource"/>.
/// </para>
/// <para>
/// Sources may return partial <see cref="Target.TypeInfo"/> values (for example, only instance
/// fields, without <see cref="Target.TypeInfo.Size"/>). <see cref="Target.GetTypeInfo(DataType)"/>
/// merges partials via <c>MergeTypeInfo</c> using a fixed precedence: native data descriptor
/// (JSON) first, then each registered <see cref="ITypeInfoSource"/> in registration order.
/// </para>
/// </remarks>
public interface ITypeInfoSource
{
    /// <summary>
    /// Attempt to produce a (possibly partial) <see cref="Target.TypeInfo"/> for
    /// <paramref name="type"/>. Returns <c>false</c> when this source has no information for
    /// the requested type (for example, when the type is not tagged with a managed FQ name the
    /// source knows how to resolve).
    /// </summary>
    bool TryGetTypeInfo(DataType type, out Target.TypeInfo info);
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.DataContractReader.Contracts;

namespace Microsoft.Diagnostics.DataContractReader.Data;

internal sealed class ConditionalWeakTable : IData<ConditionalWeakTable>
{
    static ConditionalWeakTable IData<ConditionalWeakTable>.Create(Target target, TargetPointer address)
        => new ConditionalWeakTable(target, address);

    public ConditionalWeakTable(Target target, TargetPointer address)
    {
        Target.TypeInfo type = target.GetTypeInfo(DataType.ConditionalWeakTable);

        Container = target.ReadPointerField(address, type, "_container");
    }

    public TargetPointer Container { get; init; }
}

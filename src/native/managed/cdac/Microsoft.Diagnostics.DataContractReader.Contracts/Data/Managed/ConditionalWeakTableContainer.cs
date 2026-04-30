// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.DataContractReader.Contracts;

namespace Microsoft.Diagnostics.DataContractReader.Data;

internal sealed class ConditionalWeakTableContainer : IData<ConditionalWeakTableContainer>
{
    static ConditionalWeakTableContainer IData<ConditionalWeakTableContainer>.Create(Target target, TargetPointer address)
        => new ConditionalWeakTableContainer(target, address);

    public ConditionalWeakTableContainer(Target target, TargetPointer address)
    {
        Target.TypeInfo type = target.GetTypeInfo(DataType.ConditionalWeakTableContainer);

        Buckets = target.ReadPointerField(address, type, "_buckets");
        Entries = target.ReadPointerField(address, type, "_entries");
    }

    public TargetPointer Buckets { get; init; }
    public TargetPointer Entries { get; init; }
}

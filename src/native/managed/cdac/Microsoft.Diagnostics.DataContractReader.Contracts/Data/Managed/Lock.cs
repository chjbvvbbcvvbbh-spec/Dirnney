// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.DataContractReader.Contracts;

namespace Microsoft.Diagnostics.DataContractReader.Data;

internal sealed class Lock : IData<Lock>
{
    static Lock IData<Lock>.Create(Target target, TargetPointer address)
        => new Lock(target, address);

    public Lock(Target target, TargetPointer address)
    {
        Target.TypeInfo type = target.GetTypeInfo(DataType.Lock);

        State = target.ReadField<uint>(address, type, "_state");
        OwningThreadId = (uint)target.ReadField<int>(address, type, "_owningThreadId");
        RecursionCount = target.ReadField<uint>(address, type, "_recursionCount");
    }

    public uint State { get; init; }
    public uint OwningThreadId { get; init; }
    public uint RecursionCount { get; init; }
}

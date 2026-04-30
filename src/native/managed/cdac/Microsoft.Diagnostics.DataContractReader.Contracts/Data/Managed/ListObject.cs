// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.DataContractReader.Contracts;

namespace Microsoft.Diagnostics.DataContractReader.Data;

internal sealed class ListObject : IData<ListObject>
{
    static ListObject IData<ListObject>.Create(Target target, TargetPointer address)
        => new ListObject(target, address);

    public ListObject(Target target, TargetPointer address)
    {
        Target.TypeInfo type = target.GetTypeInfo(DataType.List);

        Items = target.ReadPointerField(address, type, "_items");
        Size = target.ReadField<int>(address, type, "_size");
    }

    public TargetPointer Items { get; init; }
    public int Size { get; init; }
}

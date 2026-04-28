// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.DataContractReader.Contracts;

namespace Microsoft.Diagnostics.DataContractReader.Data;

internal sealed class String : IData<String>
{
    static String IData<String>.Create(Target target, TargetPointer address)
        => new String(target, address);

    public String(Target target, TargetPointer address)
    {
        Target.TypeInfo type = target.GetTypeInfo(DataType.String);

        FirstChar = address + (ulong)type.Fields["_firstChar"].Offset;
        StringLength = target.ReadField<int>(address, type, "_stringLength");
    }

    public TargetPointer FirstChar { get; init; }
    public int StringLength { get; init; }
}

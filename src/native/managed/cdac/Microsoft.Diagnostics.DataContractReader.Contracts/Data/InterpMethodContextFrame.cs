// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.Diagnostics.DataContractReader.Data;

internal sealed class InterpMethodContextFrame : IData<InterpMethodContextFrame>
{
    static InterpMethodContextFrame IData<InterpMethodContextFrame>.Create(Target target, TargetPointer address)
        => new InterpMethodContextFrame(target, address);

    public InterpMethodContextFrame(Target target, TargetPointer address)
    {
        Target.TypeInfo type = target.GetTypeInfo(DataType.InterpMethodContextFrame);
        StartIp = target.ReadPointerField(address, type, nameof(StartIp));
        ParentPtr = target.ReadPointerField(address, type, nameof(ParentPtr));
    }

    public TargetPointer StartIp { get; init; }
    public TargetPointer ParentPtr { get; init; }
}

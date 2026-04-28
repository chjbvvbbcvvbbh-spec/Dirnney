// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Diagnostics.DataContractReader.Contracts;

namespace Microsoft.Diagnostics.DataContractReader.Data;

internal sealed class ManagedObjectWrapperHolderObject : IData<ManagedObjectWrapperHolderObject>
{
    static ManagedObjectWrapperHolderObject IData<ManagedObjectWrapperHolderObject>.Create(Target target, TargetPointer address) => new ManagedObjectWrapperHolderObject(target, address);

    public ManagedObjectWrapperHolderObject(Target target, TargetPointer address)
    {
        Target.TypeInfo type = target.GetTypeInfo(DataType.ManagedObjectWrapperHolder);

        WrappedObject = target.ReadPointerField(address, type, "_wrappedObject");
        Wrapper = target.ReadPointerField(address, type, "_wrapper");
    }

    public TargetPointer WrappedObject { get; init; }
    public TargetPointer Wrapper { get; init; }
}

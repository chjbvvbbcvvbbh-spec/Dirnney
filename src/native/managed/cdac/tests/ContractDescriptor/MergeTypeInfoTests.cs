// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.DataContractReader.Contracts;
using Xunit;

namespace Microsoft.Diagnostics.DataContractReader.Tests.ContractDescriptor;

public class MergeTypeInfoTests
{
    private static Target.FieldInfo Fi(int offset, string? typeName = null)
        => new() { Offset = offset, TypeName = typeName };

    [Fact]
    public void MergesDisjointFields_FromBothSources()
    {
        Target.TypeInfo primary = new()
        {
            Size = 8,
            Fields = new Dictionary<string, Target.FieldInfo>
            {
                ["A"] = Fi(0, "int32"),
            },
        };
        Target.TypeInfo partial = new()
        {
            Fields = new Dictionary<string, Target.FieldInfo>
            {
                ["B"] = Fi(4, "int32"),
            },
        };

        Target.TypeInfo merged = ContractDescriptorTarget.MergeTypeInfo(primary, partial);

        Assert.Equal(8, (int)merged.Size!);
        Assert.Equal(2, merged.Fields.Count);
        Assert.Equal(0, merged.Fields["A"].Offset);
        Assert.Equal(4, merged.Fields["B"].Offset);
    }

    [Fact]
    public void OverlappingFieldsWithMatchingOffsets_AreKept_PrimaryWins()
    {
        Target.TypeInfo primary = new()
        {
            Fields = new Dictionary<string, Target.FieldInfo>
            {
                ["A"] = Fi(0, "primary-type"),
            },
        };
        Target.TypeInfo partial = new()
        {
            Fields = new Dictionary<string, Target.FieldInfo>
            {
                ["A"] = Fi(0, "partial-type"),
            },
        };

        Target.TypeInfo merged = ContractDescriptorTarget.MergeTypeInfo(primary, partial);

        Assert.Single(merged.Fields);
        Assert.Equal("primary-type", merged.Fields["A"].TypeName);
    }

    [Fact]
    public void OverlappingFieldsWithDifferingOffsets_Throw()
    {
        Target.TypeInfo primary = new()
        {
            Fields = new Dictionary<string, Target.FieldInfo>
            {
                ["A"] = Fi(0),
            },
        };
        Target.TypeInfo partial = new()
        {
            Fields = new Dictionary<string, Target.FieldInfo>
            {
                ["A"] = Fi(8),
            },
        };

        Assert.Throws<InvalidOperationException>(
            () => ContractDescriptorTarget.MergeTypeInfo(primary, partial));
    }

    [Fact]
    public void PrimarySizeWinsOverPartialSize()
    {
        Target.TypeInfo primary = new()
        {
            Size = 16,
            Fields = new Dictionary<string, Target.FieldInfo>(),
        };
        Target.TypeInfo partial = new()
        {
            Size = 32,
            Fields = new Dictionary<string, Target.FieldInfo>(),
        };

        Target.TypeInfo merged = ContractDescriptorTarget.MergeTypeInfo(primary, partial);

        Assert.Equal(16, (int)merged.Size!);
    }

    [Fact]
    public void PartialSizeFillsInWhenPrimaryHasNone()
    {
        Target.TypeInfo primary = new()
        {
            Fields = new Dictionary<string, Target.FieldInfo>(),
        };
        Target.TypeInfo partial = new()
        {
            Size = 24,
            Fields = new Dictionary<string, Target.FieldInfo>(),
        };

        Target.TypeInfo merged = ContractDescriptorTarget.MergeTypeInfo(primary, partial);

        Assert.Equal(24, (int)merged.Size!);
    }

    [Fact]
    public void TypeHandleAndStaticFieldsFillInFromPartialWhenPrimaryHasNone()
    {
        TypeHandle handle = new(new TargetPointer(0x1000));
        Dictionary<string, TargetPointer> statics = new() { ["s_x"] = new(0x2000) };

        Target.TypeInfo primary = new()
        {
            Size = 8,
            Fields = new Dictionary<string, Target.FieldInfo>(),
        };
        Target.TypeInfo partial = new()
        {
            Fields = new Dictionary<string, Target.FieldInfo>(),
            TypeHandle = handle,
            StaticFields = statics,
        };

        Target.TypeInfo merged = ContractDescriptorTarget.MergeTypeInfo(primary, partial);

        Assert.Equal(handle, merged.TypeHandle);
        Assert.Same(statics, merged.StaticFields);
    }

    [Fact]
    public void PrimaryTypeHandleAndStaticFieldsWinWhenBothSet()
    {
        TypeHandle primaryHandle = new(new TargetPointer(0x1000));
        TypeHandle partialHandle = new(new TargetPointer(0x2000));
        Dictionary<string, TargetPointer> primaryStatics = new() { ["s_primary"] = new(0x3000) };
        Dictionary<string, TargetPointer> partialStatics = new() { ["s_partial"] = new(0x4000) };

        Target.TypeInfo primary = new()
        {
            Fields = new Dictionary<string, Target.FieldInfo>(),
            TypeHandle = primaryHandle,
            StaticFields = primaryStatics,
        };
        Target.TypeInfo partial = new()
        {
            Fields = new Dictionary<string, Target.FieldInfo>(),
            TypeHandle = partialHandle,
            StaticFields = partialStatics,
        };

        Target.TypeInfo merged = ContractDescriptorTarget.MergeTypeInfo(primary, partial);

        Assert.Equal(primaryHandle, merged.TypeHandle);
        Assert.Same(primaryStatics, merged.StaticFields);
    }
}

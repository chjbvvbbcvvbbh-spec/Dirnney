// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Diagnostics.DataContractReader.Contracts.StackWalkHelpers;
using Xunit;

namespace Microsoft.Diagnostics.DataContractReader.Tests;

public class FrameIteratorTests
{
    private record TypeFields
    {
        public required DataType DataType;
        public required TargetTestHelpers.Field[] Fields;
        public TypeFields? BaseTypeFields;
    }

    private static Dictionary<DataType, Target.TypeInfo> GetTypesForTypeFields(TargetTestHelpers helpers, TypeFields[] typeFields)
    {
        Dictionary<DataType, Target.TypeInfo> types = new();
        foreach (var toAdd in typeFields)
        {
            TargetTestHelpers.LayoutResult layout = toAdd.BaseTypeFields is null
                ? helpers.LayoutFields(toAdd.Fields)
                : helpers.ExtendLayout(toAdd.Fields, GetLayout(helpers, toAdd.BaseTypeFields));
            types[toAdd.DataType] = new Target.TypeInfo()
            {
                Fields = layout.Fields,
                Size = layout.Stride,
            };
        }
        return types;

        static TargetTestHelpers.LayoutResult GetLayout(TargetTestHelpers helpers, TypeFields typeFields)
        {
            return typeFields.BaseTypeFields is null
                ? helpers.LayoutFields(typeFields.Fields)
                : helpers.ExtendLayout(typeFields.Fields, GetLayout(helpers, typeFields.BaseTypeFields));
        }
    }

    private static readonly TypeFields FrameFields = new TypeFields()
    {
        DataType = DataType.Frame,
        Fields =
        [
            new("_vptr", DataType.pointer),
            new(nameof(Data.Frame.Next), DataType.pointer),
        ]
    };

    private static readonly TypeFields FramedMethodFrameFields = new TypeFields()
    {
        DataType = DataType.FramedMethodFrame,
        Fields =
        [
            new(nameof(Data.FramedMethodFrame.TransitionBlockPtr), DataType.pointer),
            new(nameof(Data.FramedMethodFrame.MethodDescPtr), DataType.pointer),
        ],
        BaseTypeFields = FrameFields
    };

    private static readonly TypeFields InterpreterFrameFields = new TypeFields()
    {
        DataType = DataType.InterpreterFrame,
        Fields =
        [
            new(nameof(Data.InterpreterFrame.TopInterpMethodContextFrame), DataType.pointer),
        ],
        BaseTypeFields = FrameFields
    };

    private static readonly TypeFields InterpMethodContextFrameFields = new TypeFields()
    {
        DataType = DataType.InterpMethodContextFrame,
        Fields =
        [
            new(nameof(Data.InterpMethodContextFrame.StartIp), DataType.pointer),
            new(nameof(Data.InterpMethodContextFrame.ParentPtr), DataType.pointer),
        ]
    };

    private static readonly TypeFields InterpByteCodeStartFields = new TypeFields()
    {
        DataType = DataType.InterpByteCodeStart,
        Fields =
        [
            new(nameof(Data.InterpByteCodeStart.Method), DataType.pointer),
        ]
    };

    private static readonly TypeFields InterpMethodFields = new TypeFields()
    {
        DataType = DataType.InterpMethod,
        Fields =
        [
            new(nameof(Data.InterpMethod.MethodDesc), DataType.pointer),
        ]
    };

    private static Dictionary<DataType, Target.TypeInfo> GetTypes(TargetTestHelpers helpers)
    {
        return GetTypesForTypeFields(helpers,
        [
            FrameFields,
            FramedMethodFrameFields,
            InterpreterFrameFields,
            InterpMethodContextFrameFields,
            InterpByteCodeStartFields,
            InterpMethodFields,
        ]);
    }

    public static IEnumerable<object[]> InterpreterFrameArchitectures =>
    [
        [new MockTarget.Architecture { Is64Bit = true, IsLittleEndian = true }],
        [new MockTarget.Architecture { Is64Bit = false, IsLittleEndian = true }],
    ];

    [Theory]
    [MemberData(nameof(InterpreterFrameArchitectures))]
    public void GetMethodDescPtr_InterpreterFrame_FollowsFullChain(MockTarget.Architecture arch)
    {
        TargetTestHelpers helpers = new(arch);
        Dictionary<DataType, Target.TypeInfo> types = GetTypes(helpers);

        var builder = new TestPlaceholderTarget.Builder(arch)
            .AddTypes(types);

        int pointerSize = helpers.PointerSize;

        var alloc = builder.MemoryBuilder.CreateAllocator(0x1000_0000, 0x2000_0000);

        ulong interpreterFrameIdentifierValue = 0xAAAA_1111;

        ulong expectedMethodDesc = 0xDEAD_BEEF;

        var interpMethodFrag = alloc.Allocate((ulong)types[DataType.InterpMethod].Size!, "InterpMethod");
        helpers.WritePointer(
            interpMethodFrag.Data.AsSpan(types[DataType.InterpMethod].Fields[nameof(Data.InterpMethod.MethodDesc)].Offset, pointerSize),
            expectedMethodDesc);

        var byteCodeStartFrag = alloc.Allocate((ulong)types[DataType.InterpByteCodeStart].Size!, "InterpByteCodeStart");
        helpers.WritePointer(
            byteCodeStartFrag.Data.AsSpan(types[DataType.InterpByteCodeStart].Fields[nameof(Data.InterpByteCodeStart.Method)].Offset, pointerSize),
            interpMethodFrag.Address);

        var contextFrameFrag = alloc.Allocate((ulong)types[DataType.InterpMethodContextFrame].Size!, "InterpMethodContextFrame");
        helpers.WritePointer(
            contextFrameFrag.Data.AsSpan(types[DataType.InterpMethodContextFrame].Fields[nameof(Data.InterpMethodContextFrame.StartIp)].Offset, pointerSize),
            byteCodeStartFrag.Address);
        helpers.WritePointer(
            contextFrameFrag.Data.AsSpan(types[DataType.InterpMethodContextFrame].Fields[nameof(Data.InterpMethodContextFrame.ParentPtr)].Offset, pointerSize),
            0);

        int frameNextOffset = types[DataType.Frame].Fields[nameof(Data.Frame.Next)].Offset;
        int topContextFrameOffset = types[DataType.InterpreterFrame].Fields[nameof(Data.InterpreterFrame.TopInterpMethodContextFrame)].Offset;
        int totalFrameSize = Math.Max(topContextFrameOffset + pointerSize, frameNextOffset + pointerSize);
        var frameFrag = alloc.Allocate((ulong)totalFrameSize, "InterpreterFrame");

        helpers.WritePointer(frameFrag.Data.AsSpan(0, pointerSize), interpreterFrameIdentifierValue);
        ulong terminator = arch.Is64Bit ? ulong.MaxValue : uint.MaxValue;
        helpers.WritePointer(frameFrag.Data.AsSpan(frameNextOffset, pointerSize), terminator);
        helpers.WritePointer(frameFrag.Data.AsSpan(topContextFrameOffset, pointerSize), contextFrameFrag.Address);

        builder.AddGlobals(("InterpreterFrameIdentifier", interpreterFrameIdentifierValue));

        var target = builder.Build();

        TargetPointer result = FrameIterator.GetMethodDescPtr(target, new TargetPointer(frameFrag.Address));

        Assert.Equal(new TargetPointer(expectedMethodDesc), result);
    }

    [Theory]
    [MemberData(nameof(InterpreterFrameArchitectures))]
    public void GetMethodDescPtr_InterpreterFrame_NullContextFrame_ReturnsNull(MockTarget.Architecture arch)
    {
        TargetTestHelpers helpers = new(arch);
        Dictionary<DataType, Target.TypeInfo> types = GetTypes(helpers);

        var builder = new TestPlaceholderTarget.Builder(arch)
            .AddTypes(types);

        int pointerSize = helpers.PointerSize;
        var alloc = builder.MemoryBuilder.CreateAllocator(0x1000_0000, 0x2000_0000);

        ulong interpreterFrameIdentifierValue = 0xAAAA_2222;

        int topContextFrameOffset = types[DataType.InterpreterFrame].Fields[nameof(Data.InterpreterFrame.TopInterpMethodContextFrame)].Offset;
        int frameNextOffset = types[DataType.Frame].Fields[nameof(Data.Frame.Next)].Offset;
        int totalFrameSize = Math.Max(topContextFrameOffset + pointerSize, frameNextOffset + pointerSize);
        var frameFrag = alloc.Allocate((ulong)totalFrameSize, "InterpreterFrame");

        helpers.WritePointer(frameFrag.Data.AsSpan(0, pointerSize), interpreterFrameIdentifierValue);
        ulong terminator = arch.Is64Bit ? ulong.MaxValue : uint.MaxValue;
        helpers.WritePointer(frameFrag.Data.AsSpan(frameNextOffset, pointerSize), terminator);
        helpers.WritePointer(frameFrag.Data.AsSpan(topContextFrameOffset, pointerSize), 0);

        builder.AddGlobals(("InterpreterFrameIdentifier", interpreterFrameIdentifierValue));

        var target = builder.Build();

        TargetPointer result = FrameIterator.GetMethodDescPtr(target, new TargetPointer(frameFrag.Address));

        Assert.Equal(TargetPointer.Null, result);
    }

    [Theory]
    [MemberData(nameof(InterpreterFrameArchitectures))]
    public void GetMethodDescPtr_InterpreterFrame_NullStartIp_ReturnsNull(MockTarget.Architecture arch)
    {
        TargetTestHelpers helpers = new(arch);
        Dictionary<DataType, Target.TypeInfo> types = GetTypes(helpers);

        var builder = new TestPlaceholderTarget.Builder(arch)
            .AddTypes(types);

        int pointerSize = helpers.PointerSize;
        var alloc = builder.MemoryBuilder.CreateAllocator(0x1000_0000, 0x2000_0000);

        ulong interpreterFrameIdentifierValue = 0xAAAA_3333;

        var contextFrameFrag = alloc.Allocate((ulong)types[DataType.InterpMethodContextFrame].Size!, "InterpMethodContextFrame");
        helpers.WritePointer(
            contextFrameFrag.Data.AsSpan(types[DataType.InterpMethodContextFrame].Fields[nameof(Data.InterpMethodContextFrame.StartIp)].Offset, pointerSize),
            0);
        helpers.WritePointer(
            contextFrameFrag.Data.AsSpan(types[DataType.InterpMethodContextFrame].Fields[nameof(Data.InterpMethodContextFrame.ParentPtr)].Offset, pointerSize),
            0);

        int topContextFrameOffset = types[DataType.InterpreterFrame].Fields[nameof(Data.InterpreterFrame.TopInterpMethodContextFrame)].Offset;
        int frameNextOffset = types[DataType.Frame].Fields[nameof(Data.Frame.Next)].Offset;
        int totalFrameSize = Math.Max(topContextFrameOffset + pointerSize, frameNextOffset + pointerSize);
        var frameFrag = alloc.Allocate((ulong)totalFrameSize, "InterpreterFrame");

        helpers.WritePointer(frameFrag.Data.AsSpan(0, pointerSize), interpreterFrameIdentifierValue);
        ulong terminator = arch.Is64Bit ? ulong.MaxValue : uint.MaxValue;
        helpers.WritePointer(frameFrag.Data.AsSpan(frameNextOffset, pointerSize), terminator);
        helpers.WritePointer(frameFrag.Data.AsSpan(topContextFrameOffset, pointerSize), contextFrameFrag.Address);

        builder.AddGlobals(("InterpreterFrameIdentifier", interpreterFrameIdentifierValue));

        var target = builder.Build();

        TargetPointer result = FrameIterator.GetMethodDescPtr(target, new TargetPointer(frameFrag.Address));

        Assert.Equal(TargetPointer.Null, result);
    }

    [Theory]
    [MemberData(nameof(InterpreterFrameArchitectures))]
    public void GetMethodDescPtr_InterpreterFrame_NullMethod_ReturnsNull(MockTarget.Architecture arch)
    {
        TargetTestHelpers helpers = new(arch);
        Dictionary<DataType, Target.TypeInfo> types = GetTypes(helpers);

        var builder = new TestPlaceholderTarget.Builder(arch)
            .AddTypes(types);

        int pointerSize = helpers.PointerSize;
        var alloc = builder.MemoryBuilder.CreateAllocator(0x1000_0000, 0x2000_0000);

        ulong interpreterFrameIdentifierValue = 0xAAAA_4444;

        var byteCodeStartFrag = alloc.Allocate((ulong)types[DataType.InterpByteCodeStart].Size!, "InterpByteCodeStart");
        helpers.WritePointer(
            byteCodeStartFrag.Data.AsSpan(types[DataType.InterpByteCodeStart].Fields[nameof(Data.InterpByteCodeStart.Method)].Offset, pointerSize),
            0);

        var contextFrameFrag = alloc.Allocate((ulong)types[DataType.InterpMethodContextFrame].Size!, "InterpMethodContextFrame");
        helpers.WritePointer(
            contextFrameFrag.Data.AsSpan(types[DataType.InterpMethodContextFrame].Fields[nameof(Data.InterpMethodContextFrame.StartIp)].Offset, pointerSize),
            byteCodeStartFrag.Address);
        helpers.WritePointer(
            contextFrameFrag.Data.AsSpan(types[DataType.InterpMethodContextFrame].Fields[nameof(Data.InterpMethodContextFrame.ParentPtr)].Offset, pointerSize),
            0);

        int topContextFrameOffset = types[DataType.InterpreterFrame].Fields[nameof(Data.InterpreterFrame.TopInterpMethodContextFrame)].Offset;
        int frameNextOffset = types[DataType.Frame].Fields[nameof(Data.Frame.Next)].Offset;
        int totalFrameSize = Math.Max(topContextFrameOffset + pointerSize, frameNextOffset + pointerSize);
        var frameFrag = alloc.Allocate((ulong)totalFrameSize, "InterpreterFrame");

        helpers.WritePointer(frameFrag.Data.AsSpan(0, pointerSize), interpreterFrameIdentifierValue);
        ulong terminator = arch.Is64Bit ? ulong.MaxValue : uint.MaxValue;
        helpers.WritePointer(frameFrag.Data.AsSpan(frameNextOffset, pointerSize), terminator);
        helpers.WritePointer(frameFrag.Data.AsSpan(topContextFrameOffset, pointerSize), contextFrameFrag.Address);

        builder.AddGlobals(("InterpreterFrameIdentifier", interpreterFrameIdentifierValue));

        var target = builder.Build();

        TargetPointer result = FrameIterator.GetMethodDescPtr(target, new TargetPointer(frameFrag.Address));

        Assert.Equal(TargetPointer.Null, result);
    }

    [Theory]
    [MemberData(nameof(InterpreterFrameArchitectures))]
    public void ResolveMethodDescFromContextFrame_MultipleContextFrames_ResolvesEach(MockTarget.Architecture arch)
    {
        TargetTestHelpers helpers = new(arch);
        Dictionary<DataType, Target.TypeInfo> types = GetTypes(helpers);

        var builder = new TestPlaceholderTarget.Builder(arch)
            .AddTypes(types);

        int pointerSize = helpers.PointerSize;
        var alloc = builder.MemoryBuilder.CreateAllocator(0x1000_0000, 0x2000_0000);

        ulong methodDescA = 0xAA00_0001;
        ulong methodDescB = 0xBB00_0002;
        ulong methodDescC = 0xCC00_0003;

        // Build three independent InterpMethod → InterpByteCodeStart chains
        MockMemorySpace.HeapFragment CreateContextChainEntry(ulong methodDesc, ulong parentPtr, out MockMemorySpace.HeapFragment interpMethodFrag, out MockMemorySpace.HeapFragment byteCodeStartFrag)
        {
            interpMethodFrag = alloc.Allocate((ulong)types[DataType.InterpMethod].Size!, "InterpMethod");
            helpers.WritePointer(
                interpMethodFrag.Data.AsSpan(types[DataType.InterpMethod].Fields[nameof(Data.InterpMethod.MethodDesc)].Offset, pointerSize),
                methodDesc);

            byteCodeStartFrag = alloc.Allocate((ulong)types[DataType.InterpByteCodeStart].Size!, "InterpByteCodeStart");
            helpers.WritePointer(
                byteCodeStartFrag.Data.AsSpan(types[DataType.InterpByteCodeStart].Fields[nameof(Data.InterpByteCodeStart.Method)].Offset, pointerSize),
                interpMethodFrag.Address);

            var contextFrameFrag = alloc.Allocate((ulong)types[DataType.InterpMethodContextFrame].Size!, "InterpMethodContextFrame");
            helpers.WritePointer(
                contextFrameFrag.Data.AsSpan(types[DataType.InterpMethodContextFrame].Fields[nameof(Data.InterpMethodContextFrame.StartIp)].Offset, pointerSize),
                byteCodeStartFrag.Address);
            helpers.WritePointer(
                contextFrameFrag.Data.AsSpan(types[DataType.InterpMethodContextFrame].Fields[nameof(Data.InterpMethodContextFrame.ParentPtr)].Offset, pointerSize),
                parentPtr);

            return contextFrameFrag;
        }

        // Build chain: C (leaf) → B → A (root, ParentPtr=0)
        var contextFrameA = CreateContextChainEntry(methodDescA, 0, out var interpMethodA, out var byteCodeStartA);
        var contextFrameB = CreateContextChainEntry(methodDescB, contextFrameA.Address, out var interpMethodB, out var byteCodeStartB);
        var contextFrameC = CreateContextChainEntry(methodDescC, contextFrameB.Address, out var interpMethodC, out var byteCodeStartC);

        var target = builder.Build();

        // Resolve each context frame individually — verifies the chain links resolve to distinct MethodDescs
        Assert.Equal(new TargetPointer(methodDescC), FrameIterator.ResolveMethodDescFromInterpFrame(target, new TargetPointer(contextFrameC.Address)));
        Assert.Equal(new TargetPointer(methodDescB), FrameIterator.ResolveMethodDescFromInterpFrame(target, new TargetPointer(contextFrameB.Address)));
        Assert.Equal(new TargetPointer(methodDescA), FrameIterator.ResolveMethodDescFromInterpFrame(target, new TargetPointer(contextFrameA.Address)));

        // Verify null terminates correctly
        Assert.Equal(TargetPointer.Null, FrameIterator.ResolveMethodDescFromInterpFrame(target, TargetPointer.Null));
    }

    [Theory]
    [MemberData(nameof(InterpreterFrameArchitectures))]
    public void GetFrameName_InterpreterFrame_ReturnsName(MockTarget.Architecture arch)
    {
        TargetTestHelpers helpers = new(arch);
        Dictionary<DataType, Target.TypeInfo> types = GetTypes(helpers);

        var builder = new TestPlaceholderTarget.Builder(arch)
            .AddTypes(types);

        ulong interpreterFrameIdentifierValue = 0xAAAA_5555;

        builder.AddGlobals(("InterpreterFrameIdentifier", interpreterFrameIdentifierValue));

        var target = builder.Build();

        string name = FrameIterator.GetFrameName(target, new TargetPointer(interpreterFrameIdentifierValue));

        Assert.Equal("InterpreterFrame", name);
    }

    [Theory]
    [MemberData(nameof(InterpreterFrameArchitectures))]
    public void GetFrameName_UnknownFrame_ReturnsEmpty(MockTarget.Architecture arch)
    {
        var builder = new TestPlaceholderTarget.Builder(arch)
            .AddTypes(GetTypes(new TargetTestHelpers(arch)));

        var target = builder.Build();

        string name = FrameIterator.GetFrameName(target, new TargetPointer(0x9999_9999));

        Assert.Equal(string.Empty, name);
    }
}

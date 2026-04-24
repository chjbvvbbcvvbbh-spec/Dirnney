// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

namespace Microsoft.Diagnostics.DataContractReader.Contracts.StackWalkHelpers;

internal sealed class FrameIterator
{
    internal enum FrameType
    {
        Unknown,

        InlinedCallFrame,
        SoftwareExceptionFrame,

        /* TransitionFrame Types */
        FramedMethodFrame,
        PInvokeCalliFrame,
        PrestubMethodFrame,
        StubDispatchFrame,
        CallCountingHelperFrame,
        ExternalMethodFrame,
        DynamicHelperFrame,
        InterpreterFrame,

        FuncEvalFrame,

        /* ResumableFrame Types */
        ResumableFrame,
        RedirectedThreadFrame,

        FaultingExceptionFrame,

        HijackFrame,

        TailCallFrame,

        /* Other Frame Types not handled by the iterator */
        ProtectValueClassFrame,
        DebuggerClassInitMarkFrame,
        DebuggerExitFrame,
        DebuggerU2MCatchHandlerFrame,
        ExceptionFilterFrame,
    }

    private readonly Target target;
    private readonly TargetPointer terminator;
    private TargetPointer currentFramePointer;

    internal Data.Frame CurrentFrame => target.ProcessedData.GetOrAdd<Data.Frame>(currentFramePointer);

    public TargetPointer CurrentFrameAddress => currentFramePointer;

    public FrameIterator(Target target, ThreadData threadData)
    {
        this.target = target;
        terminator = new TargetPointer(target.PointerSize == 8 ? ulong.MaxValue : uint.MaxValue);
        currentFramePointer = threadData.Frame;
    }

    public bool IsValid()
    {
        return currentFramePointer != terminator;
    }

    public bool Next()
    {
        if (currentFramePointer == terminator)
            return false;

        currentFramePointer = CurrentFrame.Next;
        return currentFramePointer != terminator;
    }

    public void UpdateContextFromFrame(IPlatformAgnosticContext context)
    {
        switch (GetFrameType(target, CurrentFrame.Identifier))
        {
            case FrameType.InlinedCallFrame:
                Data.InlinedCallFrame inlinedCallFrame = target.ProcessedData.GetOrAdd<Data.InlinedCallFrame>(CurrentFrame.Address);
                GetFrameHandler(context).HandleInlinedCallFrame(inlinedCallFrame);
                return;

            case FrameType.SoftwareExceptionFrame:
                Data.SoftwareExceptionFrame softwareExceptionFrame = target.ProcessedData.GetOrAdd<Data.SoftwareExceptionFrame>(CurrentFrame.Address);
                GetFrameHandler(context).HandleSoftwareExceptionFrame(softwareExceptionFrame);
                return;

            // TransitionFrame type frames
            case FrameType.FramedMethodFrame:
            case FrameType.PInvokeCalliFrame:
            case FrameType.PrestubMethodFrame:
            case FrameType.StubDispatchFrame:
            case FrameType.CallCountingHelperFrame:
            case FrameType.ExternalMethodFrame:
            case FrameType.DynamicHelperFrame:
            case FrameType.InterpreterFrame:
                // FrameMethodFrame is the base type for all transition Frames
                Data.FramedMethodFrame framedMethodFrame = target.ProcessedData.GetOrAdd<Data.FramedMethodFrame>(CurrentFrame.Address);
                GetFrameHandler(context).HandleTransitionFrame(framedMethodFrame);
                return;

            case FrameType.FuncEvalFrame:
                Data.FuncEvalFrame funcEvalFrame = target.ProcessedData.GetOrAdd<Data.FuncEvalFrame>(CurrentFrame.Address);
                GetFrameHandler(context).HandleFuncEvalFrame(funcEvalFrame);
                return;

            // ResumableFrame type frames
            case FrameType.ResumableFrame:
            case FrameType.RedirectedThreadFrame:
                Data.ResumableFrame resumableFrame = target.ProcessedData.GetOrAdd<Data.ResumableFrame>(CurrentFrame.Address);
                GetFrameHandler(context).HandleResumableFrame(resumableFrame);
                return;

            case FrameType.FaultingExceptionFrame:
                Data.FaultingExceptionFrame faultingExceptionFrame = target.ProcessedData.GetOrAdd<Data.FaultingExceptionFrame>(CurrentFrame.Address);
                GetFrameHandler(context).HandleFaultingExceptionFrame(faultingExceptionFrame);
                return;

            case FrameType.HijackFrame:
                Data.HijackFrame hijackFrame = target.ProcessedData.GetOrAdd<Data.HijackFrame>(CurrentFrame.Address);
                GetFrameHandler(context).HandleHijackFrame(hijackFrame);
                return;
            case FrameType.TailCallFrame:
                Data.TailCallFrame tailCallFrame = target.ProcessedData.GetOrAdd<Data.TailCallFrame>(CurrentFrame.Address);
                GetFrameHandler(context).HandleTailCallFrame(tailCallFrame);
                return;
            default:
                // Unknown Frame type. This could either be a Frame that we don't know how to handle,
                // or a Frame that does not update the context.
                return;
        }
    }

    public bool IsInlineCallFrameWithActiveCall()
    {
        if (GetFrameType(target, CurrentFrame.Identifier) != FrameType.InlinedCallFrame)
        {
            return false;
        }
        Data.InlinedCallFrame inlinedCallFrame = target.ProcessedData.GetOrAdd<Data.InlinedCallFrame>(currentFramePointer);
        return InlinedCallFrameHasActiveCall(inlinedCallFrame);
    }

    public static bool IsInlinedCallFrame(Target target, TargetPointer framePointer)
    {
        Data.Frame frame = target.ProcessedData.GetOrAdd<Data.Frame>(framePointer);
        return GetFrameType(target, frame.Identifier) == FrameType.InlinedCallFrame;
    }

    public static string GetFrameName(Target target, TargetPointer frameIdentifier)
    {
        FrameType frameType = GetFrameType(target, frameIdentifier);
        if (frameType == FrameType.Unknown)
        {
            return string.Empty;
        }
        return frameType.ToString();
    }

    public FrameType GetCurrentFrameType() => GetFrameType(target, CurrentFrame.Identifier);

    private static FrameType GetFrameType(Target target, TargetPointer frameIdentifier)
    {
        foreach (FrameType frameType in Enum.GetValues<FrameType>())
        {
            if (target.TryReadGlobalPointer(frameType.ToString() + "Identifier", out TargetPointer? id))
            {
                if (frameIdentifier == id)
                {
                    return frameType;
                }
            }
        }

        return FrameType.Unknown;
    }

    private IPlatformFrameHandler GetFrameHandler(IPlatformAgnosticContext context)
    {
        return context switch
        {
            ContextHolder<X86Context> contextHolder => new X86FrameHandler(target, contextHolder),
            ContextHolder<AMD64Context> contextHolder => new AMD64FrameHandler(target, contextHolder),
            ContextHolder<ARMContext> contextHolder => new ARMFrameHandler(target, contextHolder),
            ContextHolder<ARM64Context> contextHolder => new ARM64FrameHandler(target, contextHolder),
            ContextHolder<RISCV64Context> contextHolder => new RISCV64FrameHandler(target, contextHolder),
            ContextHolder<LoongArch64Context> contextHolder => new LoongArch64FrameHandler(target, contextHolder),
            _ => throw new InvalidOperationException("Unsupported context type"),
        };
    }

    public static TargetPointer GetMethodDescPtr(Target target, TargetPointer framePtr)
    {
        Data.Frame frame = target.ProcessedData.GetOrAdd<Data.Frame>(framePtr);
        FrameType frameType = GetFrameType(target, frame.Identifier);
        switch (frameType)
        {
            case FrameType.FramedMethodFrame:
            case FrameType.DynamicHelperFrame:
            case FrameType.ExternalMethodFrame:
            case FrameType.PrestubMethodFrame:
            case FrameType.CallCountingHelperFrame:
                Data.FramedMethodFrame framedMethodFrame = target.ProcessedData.GetOrAdd<Data.FramedMethodFrame>(frame.Address);
                return framedMethodFrame.MethodDescPtr;
            case FrameType.InterpreterFrame:
                {
                    Data.InterpreterFrame interpreterFrame = target.ProcessedData.GetOrAdd<Data.InterpreterFrame>(frame.Address);
                    TargetPointer topContextFrame = ResolveTopInterpMethodContextFrame(target, interpreterFrame.TopInterpMethodContextFrame);
                    return ResolveMethodDescFromInterpFrame(target, topContextFrame);
                }
            case FrameType.PInvokeCalliFrame:
                return TargetPointer.Null;
            case FrameType.StubDispatchFrame:
                Data.StubDispatchFrame stubDispatchFrame = target.ProcessedData.GetOrAdd<Data.StubDispatchFrame>(frame.Address);
                if (stubDispatchFrame.MethodDescPtr != TargetPointer.Null)
                {
                    return stubDispatchFrame.MethodDescPtr;
                }
                else if (stubDispatchFrame.RepresentativeMTPtr != TargetPointer.Null)
                {
                    IRuntimeTypeSystem rtsContract = target.Contracts.RuntimeTypeSystem;
                    TypeHandle mtHandle = rtsContract.GetTypeHandle(stubDispatchFrame.RepresentativeMTPtr);
                    return rtsContract.GetMethodDescForSlot(mtHandle, (ushort)stubDispatchFrame.RepresentativeSlot);
                }
                else
                {
                    return TargetPointer.Null;
                }
            case FrameType.InlinedCallFrame:
                Data.InlinedCallFrame inlinedCallFrame = target.ProcessedData.GetOrAdd<Data.InlinedCallFrame>(frame.Address);
                if (InlinedCallFrameHasActiveCall(inlinedCallFrame) && InlinedCallFrameHasFunction(inlinedCallFrame, target))
                    return inlinedCallFrame.Datum & ~(ulong)(target.PointerSize - 1);
                else
                    return TargetPointer.Null;
            default:
                return TargetPointer.Null;
        }
    }

    /// <summary>
    /// Resolves the MethodDesc from a specific InterpMethodContextFrame by following:
    /// InterpMethodContextFrame.StartIp -> InterpByteCodeStart.Method -> InterpMethod.MethodDesc
    /// </summary>
    internal static TargetPointer ResolveMethodDescFromInterpFrame(Target target, TargetPointer interpMethodFramePtr)
    {
        if (interpMethodFramePtr == TargetPointer.Null)
            return TargetPointer.Null;

        Data.InterpMethodContextFrame contextFrame = target.ProcessedData.GetOrAdd<Data.InterpMethodContextFrame>(interpMethodFramePtr);
        if (contextFrame.StartIp == TargetPointer.Null)
            return TargetPointer.Null;

        Data.InterpByteCodeStart byteCodeStart = target.ProcessedData.GetOrAdd<Data.InterpByteCodeStart>(contextFrame.StartIp);
        if (byteCodeStart.Method == TargetPointer.Null)
            return TargetPointer.Null;

        Data.InterpMethod interpMethod = target.ProcessedData.GetOrAdd<Data.InterpMethod>(byteCodeStart.Method);

        return interpMethod.MethodDesc;
    }

    /// <summary>
    /// Resolves the actual top InterpMethodContextFrame from the hint stored in InterpreterFrame,
    /// replicating InterpreterFrame::GetTopInterpMethodContextFrame() from frames.cpp.
    /// The stored TopInterpMethodContextFrame is only an approximate hint; during dump or native
    /// debugging it may point to a stale frame. This method seeks to the correct top frame using
    /// the Ip field (null = inactive, non-null = active) and the NextPtr/ParentPtr chains.
    /// </summary>
    internal static TargetPointer ResolveTopInterpMethodContextFrame(Target target, TargetPointer hintPtr)
    {
        if (hintPtr == TargetPointer.Null)
            return TargetPointer.Null;

        Data.InterpMethodContextFrame frame = target.ProcessedData.GetOrAdd<Data.InterpMethodContextFrame>(hintPtr);
        TargetPointer currentPtr = hintPtr;

        if (frame.Ip != TargetPointer.Null)
        {
            // Active frame — seek upward via NextPtr while next frame is also active
            while (frame.NextPtr != TargetPointer.Null)
            {
                Data.InterpMethodContextFrame next = target.ProcessedData.GetOrAdd<Data.InterpMethodContextFrame>(frame.NextPtr);
                if (next.Ip == TargetPointer.Null)
                    break;
                currentPtr = frame.NextPtr;
                frame = next;
            }
        }
        else
        {
            // Inactive frame — seek downward via ParentPtr to find first active frame
            while (frame.ParentPtr != TargetPointer.Null && frame.Ip == TargetPointer.Null)
            {
                currentPtr = frame.ParentPtr;
                frame = target.ProcessedData.GetOrAdd<Data.InterpMethodContextFrame>(currentPtr);
            }
        }

        return currentPtr;
    }

    /// <summary>
    /// Walks the InterpMethodContextFrame chain for an InterpreterFrame,
    /// yielding one context frame pointer per active interpreted method in the call chain.
    /// The TopInterpMethodContextFrame hint is first resolved to the actual top frame
    /// via ResolveTopInterpMethodContextFrame, then the ParentPtr chain is walked.
    /// Only active frames (Ip != null) are yielded.
    /// </summary>
    internal static IEnumerable<TargetPointer> WalkInterpreterFrameChain(Target target, TargetPointer frameAddress)
    {
        Data.InterpreterFrame interpFrame = target.ProcessedData.GetOrAdd<Data.InterpreterFrame>(frameAddress);
        TargetPointer interpMethodFramePtr = ResolveTopInterpMethodContextFrame(target, interpFrame.TopInterpMethodContextFrame);
        while (interpMethodFramePtr != TargetPointer.Null)
        {
            Data.InterpMethodContextFrame contextFrame = target.ProcessedData.GetOrAdd<Data.InterpMethodContextFrame>(interpMethodFramePtr);
            if (contextFrame.Ip != TargetPointer.Null)
                yield return interpMethodFramePtr;
            interpMethodFramePtr = contextFrame.ParentPtr;
        }
    }

    public static TargetPointer GetReturnAddress(Target target, TargetPointer framePtr)
    {
        Data.Frame frame = target.ProcessedData.GetOrAdd<Data.Frame>(framePtr);
        FrameType frameType = GetFrameType(target, frame.Identifier);
        switch (frameType)
        {
            case FrameType.InlinedCallFrame:
                Data.InlinedCallFrame inlinedCallFrame = target.ProcessedData.GetOrAdd<Data.InlinedCallFrame>(frame.Address);
                return InlinedCallFrameHasActiveCall(inlinedCallFrame) ? inlinedCallFrame.CallerReturnAddress : TargetPointer.Null;
            default:
                // NotImplemented for other frame types
                return TargetPointer.Null;
        }
    }

    private static bool InlinedCallFrameHasFunction(Data.InlinedCallFrame frame, Target target)
    {
        if (target.PointerSize == sizeof(ulong))
        {
            return frame.Datum != TargetPointer.Null && (frame.Datum.Value & 0x1) == 0;
        }
        else
        {
            return ((long)frame.Datum.Value & ~0xffff) != 0;
        }
    }

    private static bool InlinedCallFrameHasActiveCall(Data.InlinedCallFrame frame)
    {
        return frame.CallerReturnAddress != TargetPointer.Null;
    }
}

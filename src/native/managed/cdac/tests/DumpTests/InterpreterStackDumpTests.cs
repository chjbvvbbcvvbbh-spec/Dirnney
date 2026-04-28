// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using Microsoft.Diagnostics.DataContractReader.Contracts;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Microsoft.Diagnostics.DataContractReader.DumpTests;

/// <summary>
/// Dump-based integration tests for cDAC interpreter support.
/// Uses the InterpreterStack debuggee dump, which has a deterministic call stack:
/// Main -> MethodA -> MethodB -> JitTrampoline.Bounce -> MethodC -> MethodD -> FailFast.
/// Under DOTNET_Interpreter=MethodA, MethodA/B/C/D are interpreted while Main,
/// Bounce, and FailFast remain JIT'd. The trampoline is in a separate assembly
/// so it is NOT in g_interpModule, creating two distinct InterpreterFrame regions
/// on the stack with a JIT'd gap between them. Both InterpreterFrame regions have
/// multiple interpreted methods (pParent chain).
/// </summary>
public class InterpreterStackDumpTests : DumpTestBase
{
    protected override string DebuggeeName => "InterpreterStack";
    protected override string DumpType => "full";

    private void SkipIfInterpreterNotAvailable()
    {
        try
        {
            Target.GetTypeInfo(DataType.InterpreterFrame);
        }
        catch (InvalidOperationException)
        {
            throw new SkipTestException("Interpreter support not available in this runtime build (FEATURE_INTERPRETER not enabled).");
        }
    }

    private void AssertInterpreted(ResolvedFrame f)
    {
        Assert.Equal("InterpreterFrame", f.FrameName);

        IRuntimeTypeSystem rts = Target.Contracts.RuntimeTypeSystem;
        IExecutionManager executionManager = Target.Contracts.ExecutionManager;

        MethodDescHandle md = rts.GetMethodDescHandle(f.MethodDescPtr);
        TargetCodePointer nativeCode = rts.GetNativeCode(md);
        TargetCodePointer resolvedCode = Target.Contracts.PrecodeStubs.GetInterpreterCodeFromInterpreterPrecodeIfPresent(nativeCode);
        Assert.NotEqual(TargetCodePointer.Null, resolvedCode);

        CodeBlockHandle? codeBlock = executionManager.GetCodeBlockHandle(resolvedCode);
        Assert.NotNull(codeBlock);
        Assert.Equal(JitType.Interpreter, executionManager.GetJITType(codeBlock.Value));
    }

    private void AssertJitted(ResolvedFrame f)
    {
        Assert.Null(f.FrameName);

        IRuntimeTypeSystem rts = Target.Contracts.RuntimeTypeSystem;
        IExecutionManager executionManager = Target.Contracts.ExecutionManager;

        MethodDescHandle md = rts.GetMethodDescHandle(f.MethodDescPtr);
        TargetCodePointer nativeCode = rts.GetNativeCode(md);
        Assert.NotEqual(TargetCodePointer.Null, nativeCode);
        CodeBlockHandle? codeBlock = executionManager.GetCodeBlockHandle(nativeCode);
        Assert.NotNull(codeBlock);
        Assert.Equal(JitType.Jit, executionManager.GetJITType(codeBlock.Value));
    }

    [ConditionalTheory]
    [MemberData(nameof(TestConfigurations))]
    public void StackWalk_VerifyInterleavedStackLayout(TestConfiguration config)
    {
        InitializeDumpTest(config);
        SkipIfInterpreterNotAvailable();

        ThreadData crashingThread = DumpTestHelpers.FindFailFastThread(Target);

        // The debuggee routes: Main -> MethodA -> MethodB -> Bounce -> MethodC -> MethodD -> FailFast.
        //
        // MethodA and MethodB are in InterpreterFrame 1 (interpreted, adjacent via pParent chain).
        // Bounce is JIT'd (separate assembly, not in g_interpModule).
        // MethodC and MethodD are in InterpreterFrame 2 (interpreted, adjacent via pParent chain).
        // Main and FailFast are JIT'd.
        //
        // This verifies:
        //  - Full frame ordering
        //  - Which frames are interpreted vs JIT'd (via FrameName and JitType)
        //  - Multiple adjacent interpreted frames in each InterpreterFrame (pParent chain walk)
        //  - Two distinct InterpreterFrame regions separated by JIT'd Bounce
        DumpTestStackWalker.Walk(Target, crashingThread)
            .ExpectFrame("MethodD", AssertInterpreted)
            .ExpectAdjacentFrame("MethodC", AssertInterpreted)
            .ExpectFrame("Bounce", AssertJitted)
            .ExpectFrame("MethodB", AssertInterpreted)
            .ExpectAdjacentFrame("MethodA", AssertInterpreted)
            .ExpectFrame("Main", AssertJitted)
            .Verify();
    }

    [ConditionalTheory]
    [MemberData(nameof(TestConfigurations))]
    public void StackWalk_InterpreterMethodNativeCodeIsPrecode(TestConfiguration config)
    {
        InitializeDumpTest(config);
        SkipIfInterpreterNotAvailable();
        IRuntimeTypeSystem rts = Target.Contracts.RuntimeTypeSystem;
        IExecutionManager executionManager = Target.Contracts.ExecutionManager;

        ThreadData crashingThread = DumpTestHelpers.FindFailFastThread(Target);

        DumpTestStackWalker walker = DumpTestStackWalker.Walk(Target, crashingThread);

        // Find the first interpreter method (MethodA/B/C) on the stack.
        ResolvedFrame interpFrame = walker.Frames
            .First(f => f.Name is "MethodA" or "MethodB" or "MethodC" or "MethodD");

        MethodDescHandle mdHandle = rts.GetMethodDescHandle(interpFrame.MethodDescPtr);
        TargetCodePointer nativeCode = rts.GetNativeCode(mdHandle);
        Assert.NotEqual(TargetCodePointer.Null, nativeCode);

        // For interpreter methods, GetCodeBlockHandle returns null because the native code
        // slot points to a precode, not a managed code heap entry.
        CodeBlockHandle? codeBlock = executionManager.GetCodeBlockHandle(nativeCode);
        Assert.Null(codeBlock);
    }

    [ConditionalTheory]
    [MemberData(nameof(TestConfigurations))]
    public void Thread_CanEnumerateWithInterpreterFrames(TestConfiguration config)
    {
        InitializeDumpTest(config);
        SkipIfInterpreterNotAvailable();
        IThread threadContract = Target.Contracts.Thread;

        ThreadStoreData storeData = threadContract.GetThreadStoreData();
        Assert.True(storeData.ThreadCount >= 1,
            "Expected at least one thread in the thread store");

        int threadCount = 0;
        TargetPointer currentThreadPtr = storeData.FirstThread;
        while (currentThreadPtr != TargetPointer.Null)
        {
            ThreadData threadData = threadContract.GetThreadData(currentThreadPtr);
            threadCount++;
            currentThreadPtr = threadData.NextThread;
        }

        Assert.True(threadCount >= 1, "Expected at least one thread when walking the list");
    }

    [ConditionalTheory]
    [MemberData(nameof(TestConfigurations))]
    public void StackWalk_NoDoubledInterpreterFrames(TestConfiguration config)
    {
        InitializeDumpTest(config);
        SkipIfInterpreterNotAvailable();

        ThreadData crashingThread = DumpTestHelpers.FindFailFastThread(Target);
        DumpTestStackWalker walker = DumpTestStackWalker.Walk(Target, crashingThread);

        // Verify that no interpreted method appears more than once in the stack walk.
        // This guards against the double-walking bug where interpreter frames are yielded
        // both from the initial context and again from the InterpreterFrame expansion.
        var interpreterMethods = walker.Frames
            .Where(f => f.FrameName is "InterpreterFrame")
            .Select(f => f.Name)
            .Where(n => n is not null)
            .ToList();

        var duplicates = interpreterMethods
            .GroupBy(n => n)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.True(duplicates.Count == 0,
            $"Doubled interpreter frames detected: [{string.Join(", ", duplicates)}]. " +
            $"Full stack: [{string.Join(", ", walker.Frames.Select(f => f.Name ?? "<null>"))}]");

        // Also verify all expected interpreter methods are present exactly once
        Assert.Contains(interpreterMethods, n => n is "MethodA");
        Assert.Contains(interpreterMethods, n => n is "MethodB");
        Assert.Contains(interpreterMethods, n => n is "MethodC");
        Assert.Contains(interpreterMethods, n => n is "MethodD");
    }
}

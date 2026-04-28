// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Linq;
using Microsoft.Diagnostics.DataContractReader.Contracts;
using Microsoft.DotNet.XUnitExtensions;
using Xunit;

namespace Microsoft.Diagnostics.DataContractReader.DumpTests;

/// <summary>
/// Dump-based integration tests for the InterpreterStackDoubleWalk debuggee.
/// This debuggee uses two threads:
///   - Worker thread: MethodA -> MethodB -> Bounce -> MethodC -> MethodD -> spin loop (interpreted)
///   - Main thread: waits for worker, then calls FailFast
///
/// The tests walk the <b>worker thread</b> (not the crashing thread) to verify
/// interpreter frame handling on a thread that has a fully populated InterpreterFrame
/// chain while spinning in interpreted code. Even though the worker executes interpreted
/// code, the CPU IP is inside the native interpreter engine at dump time, so the
/// walk starts from SW_FRAME state and encounters InterpreterFrames via the Frame chain.
/// </summary>
public class InterpreterStackDoubleWalkDumpTests : DumpTestBase
{
    protected override string DebuggeeName => "InterpreterStackDoubleWalk";
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
        // Interpreted methods may appear as frameless frames (via normal unwind
        // after InterpreterFrame sets the context) or as InterpreterFrame entries.
        // We verify only that the MethodDesc has interpreter code.
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

    /// <summary>
    /// Walks the worker thread and verifies the interleaved JIT/interpreter frame layout.
    /// The worker is spinning in MethodD (interpreted code; CPU IP in native interpreter),
    /// so the full call chain should be:
    ///   MethodD (interp) -> MethodC (interp) -> Bounce (JIT) -> MethodB (interp) -> MethodA (interp)
    /// </summary>
    [ConditionalTheory]
    [MemberData(nameof(TestConfigurations))]
    public void StackWalk_VerifyInterleavedStackLayout(TestConfiguration config)
    {
        InitializeDumpTest(config);
        SkipIfInterpreterNotAvailable();

        ThreadData workerThread = DumpTestHelpers.FindThreadWithMethod(Target, "MethodD");

        DumpTestStackWalker.Walk(Target, workerThread)
            .ExpectFrame("MethodD", AssertInterpreted)
            .ExpectAdjacentFrame("MethodC", AssertInterpreted)
            .ExpectFrame("Bounce", AssertJitted)
            .ExpectFrame("MethodB", AssertInterpreted)
            .ExpectAdjacentFrame("MethodA", AssertInterpreted)
            .Verify();
    }

    /// <summary>
    /// Walks the worker thread and verifies no interpreted method appears more than once.
    /// The worker thread is spinning in interpreted code but the CPU IP is inside the native
    /// interpreter engine, so the walk starts from SW_FRAME and encounters InterpreterFrames
    /// via the Frame chain. This verifies that interpreter frame expansion produces exactly
    /// one entry per interpreted method — no doubled frames.
    ///
    /// Note: The SkipNextInterpreterFrame double-walk prevention logic (which fires when a
    /// walk starts with IP in interpreter-managed code, e.g. a debugger breakpoint) cannot
    /// be exercised from a crash dump, since the interpreter's CPU IP is always in native code.
    /// That logic is covered by the FrameIterator unit tests.
    /// </summary>
    [ConditionalTheory]
    [MemberData(nameof(TestConfigurations))]
    public void StackWalk_NoDoubledInterpreterFrames(TestConfiguration config)
    {
        InitializeDumpTest(config);
        SkipIfInterpreterNotAvailable();

        ThreadData workerThread = DumpTestHelpers.FindThreadWithMethod(Target, "MethodD");
        DumpTestStackWalker walker = DumpTestStackWalker.Walk(Target, workerThread);

        // MethodA-D should each appear exactly once on the stack.
        // They may appear as frameless frames (via normal unwind after
        // InterpreterFrame sets the context) rather than as InterpreterFrame entries.
        string[] expectedMethods = ["MethodA", "MethodB", "MethodC", "MethodD"];
        foreach (string method in expectedMethods)
        {
            int count = walker.Frames.Count(f => string.Equals(f.Name, method, StringComparison.Ordinal));
            Assert.True(count == 1,
                $"Expected '{method}' to appear exactly once but found {count} occurrence(s). " +
                $"Full stack: [{string.Join(", ", walker.Frames.Select(f => $"{f.Name ?? "<null>"}({f.FrameName ?? "frameless"})"))}]");
        }
    }
}

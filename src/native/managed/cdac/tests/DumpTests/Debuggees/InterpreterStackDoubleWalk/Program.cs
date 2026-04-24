// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Runtime.CompilerServices;
using System.Threading;
using InterpreterStack.Trampoline;

/// <summary>
/// Debuggee for cDAC dump tests — validates interpreter stack frame walking on a
/// thread that has a full interpreter call chain. A worker thread builds the chain
/// and spins in interpreted code, then the main thread triggers a FailFast dump.
///
/// Under DOTNET_Interpreter=Method*, methods from this assembly that match
/// the filter are interpreted. The call chain routes through JitTrampoline.Bounce
/// (in a separate assembly, always JIT'd) to create two distinct InterpreterFrame
/// regions on the stack:
///
///   Worker thread:
///     MethodA (interp) -> MethodB (interp) -> [InterpreterFrame 1]
///       -> JitTrampoline.Bounce (JIT) -> MethodC (interp) -> MethodD (interp) -> [InterpreterFrame 2]
///         -> spinning in interpreted code (volatile field-read loop)
///
///   Main thread:
///     Main (JIT) -> waits for signal -> FailFast
///
/// Note: Even though the worker is executing interpreted code, the CPU's instruction
/// pointer is inside the native interpreter engine at dump time. The stack walk
/// starts in SW_FRAME state and encounters InterpreterFrames via the Frame chain.
/// This tests that interpreter frame expansion produces correct, non-duplicated results.
/// </summary>
internal static class Program
{
    private static readonly ManualResetEventSlim s_workerReady = new(false);

    private static void Main()
    {
        Thread worker = new(MethodA)
        {
            IsBackground = true,
            Name = "InterpreterWorker",
        };
        worker.Start();

        // Wait for the worker to reach MethodD (full call chain on stack).
        s_workerReady.Wait();

        Environment.FailFast("cDAC dump test: InterpreterStackDoubleWalk debuggee intentional crash");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MethodA()
    {
        MethodB();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MethodB()
    {
        JitTrampoline.Bounce(MethodC);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MethodC()
    {
        MethodD();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void MethodD()
    {
        // Signal the main thread that the full call chain is on the stack.
        s_workerReady.Set();

        // Spin in interpreted code so that the worker thread's IP is inside
        // interpreter-managed code at dump time. This ensures startedInInterpreterCode=true
        // in the stack walker, exercising the SkipNextInterpreterFrame logic.
        // Use a simple volatile field-read loop to stay in interpreted code without
        // calling native methods like Thread.Sleep or Thread.SpinWait.
        while (s_keepSpinning) { }
    }

    private static volatile bool s_keepSpinning = true;
}

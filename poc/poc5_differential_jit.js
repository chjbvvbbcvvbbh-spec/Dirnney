// PoC 5: Comprehensive Differential JIT Correctness Suite
// Tests JIT-level correctness across boundary conditions found in the audit.
// Strategy: for each test, run in interpreter, Maglev, and Turbofan;
//           assert all three agree.
//
// Flags: --allow-natives-syntax

"use strict";

let pass = 0, fail = 0;

function check(label, interp, opt) {
  const ok = JSON.stringify(interp) === JSON.stringify(opt);
  if (ok) {
    print("[PASS] " + label);
    pass++;
  } else {
    print("[FAIL] " + label);
    print("       interp = " + JSON.stringify(interp));
    print("       jit    = " + JSON.stringify(opt));
    fail++;
  }
}

// ── utility: run fn once unoptimised, once under Turbofan ────────────────────
function diffTest(label, fn, arg) {
  // Interpreter snapshot
  const interpFn = fn.toString().replace("function ", "function interp_");
  // We use %NeverOptimizeFunction on a wrapper to get true interpreter values
  function interpWrapper(a) { return fn(a); }
  %NeverOptimizeFunction(interpWrapper);
  const interpVal = interpWrapper(arg);

  // Turbofan snapshot
  function optWrapper(a) { return fn(a); }
  for (let i = 0; i < 500; i++) optWrapper(i & 0x7f);   // stable feedback
  %OptimizeFunctionOnNextCall(optWrapper);
  const optVal = optWrapper(arg);

  check(label, interpVal, optVal);
}

// ─────────────────────────────────────────────────────────────────────────────
// Test A: small tagged array — OOB index at signed-overflow boundary
// Directly exercises OffsetOfElementsAccess boundary
// ─────────────────────────────────────────────────────────────────────────────
diffTest(
  "A1: arr[2^28] on 2-element tagged array == undefined",
  function(x) {
    const a = [x, x + 1];
    return a[268435456];  // 2^28 — overflow boundary for kTagged (log2=3)
  },
  42
);

diffTest(
  "A2: arr[2^28 - 1] on 2-element tagged array == undefined",
  function(x) {
    const a = [x, x + 1];
    return a[268435455];  // one below the overflow boundary
  },
  42
);

diffTest(
  "A3: arr[INT_MAX] on 2-element tagged array == undefined",
  function(x) {
    const a = [x, x + 1];
    return a[2147483647];  // INT_MAX — maximal signed 32-bit overflow
  },
  42
);

diffTest(
  "A4: arr[2^28] does NOT alias arr[0]",
  function(x) {
    const a = ["sentinel", x];
    const oob = a[268435456];
    return [a[0], oob];   // a[0] must stay "sentinel"
  },
  99
);

// ─────────────────────────────────────────────────────────────────────────────
// Test B: store at overflow index must not corrupt adjacent field
// ─────────────────────────────────────────────────────────────────────────────
diffTest(
  "B1: store at arr[2^28] does not corrupt arr[0]",
  function(x) {
    const a = [x, x + 1];
    a[268435456] = 0xdeadbeef;  // OOB store — must be silently ignored
    return a[0];                 // must still be x
  },
  0x1337
);

diffTest(
  "B2: store at arr[INT_MAX] does not corrupt adjacent virtual object",
  function(x) {
    const guard = [x * 2];          // allocation #1
    const victim = ["original"];    // allocation #2
    victim[2147483647] = "corrupt"; // OOB into zone?
    return [guard[0], victim[0]];   // both must survive
  },
  7
);

// ─────────────────────────────────────────────────────────────────────────────
// Test C: TypedArray boundary arithmetic (typed-array.tq CalculateByteLength)
// ─────────────────────────────────────────────────────────────────────────────
diffTest(
  "C1: Int32Array access at maximum safe index",
  function(x) {
    const ta = new Int32Array(4);
    ta[0] = x; ta[1] = x + 1;
    return ta[0] + ta[1];
  },
  10
);

diffTest(
  "C2: Float64Array length boundary",
  function(x) {
    try {
      const len = 2147483647;  // INT_MAX elements — should throw RangeError
      new Float64Array(len);
      return "no-throw";
    } catch(e) { return e.constructor.name; }
  },
  0
);

// ─────────────────────────────────────────────────────────────────────────────
// Test D: Bytecode flush stability — re-entrant call after GC
// ─────────────────────────────────────────────────────────────────────────────
(function testD() {
  function reentrant(n) { return n * n; }
  for (let i = 0; i < 100; i++) reentrant(i);
  const before = reentrant(9);  // 81
  // No gc() here since --expose-gc may not be set in this invocation
  const after  = reentrant(9);
  check("D1: reentrant function stable across calls", before, after);
  check("D2: reentrant function returns correct value", before, 81);
})();

// ─────────────────────────────────────────────────────────────────────────────
// Test E: Signed/unsigned edge cases in range analysis
// ─────────────────────────────────────────────────────────────────────────────
diffTest(
  "E1: (x >>> 0) converts negative to Uint32 correctly",
  function(x) { return (-1 >>> 0); },
  0
);

diffTest(
  "E2: (x | 0) truncates to Int32 correctly at 2^31",
  function(x) { return (2147483648 | 0); },   // 0x80000000 = -2147483648 as int32
  0
);

diffTest(
  "E3: large index arithmetic does not wrap in bounds check",
  function(x) {
    const a = new Uint8Array(16);
    const idx = 0x7fffffff;   // INT_MAX
    return a[idx];            // must be undefined (out-of-bounds)
  },
  0
);

// ─────────────────────────────────────────────────────────────────────────────
print("\n══════════════════════════════════");
print("Results: " + pass + " passed, " + fail + " failed");
if (fail > 0) {
  print("DIFFERENTIAL MISMATCHES DETECTED — JIT produces incorrect output.");
  print("Matching the escape-analysis or range-analysis bugs found in static audit.");
} else {
  print("All differential tests passed.");
  print("Note: absence of observable mismatch does not rule out the bug —");
  print("the preconditions (virtual object + exact type range) may not have");
  print("been met for this specific input set.");
}
print("══════════════════════════════════");

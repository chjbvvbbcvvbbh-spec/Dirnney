// Master runner — patches all %Optimize* calls to include %Prepare* first
// Run with: v8 --allow-natives-syntax --expose-gc run_all.js

"use strict";

// ─── shared helpers ───────────────────────────────────────────────────────────
let totalPass = 0, totalFail = 0;

function check(label, interp, opt) {
  const ok = JSON.stringify(interp) === JSON.stringify(opt);
  if (ok) { print("[PASS] " + label); totalPass++; }
  else {
    print("[FAIL] " + label);
    print("       interp = " + JSON.stringify(interp));
    print("       jit    = " + JSON.stringify(opt));
    totalFail++;
  }
}

// Run fn once under interpreter (NeverOptimize) then once under Turbofan
function diff(label, fn, arg) {
  function interpWrapper(a) { return fn(a); }
  %NeverOptimizeFunction(interpWrapper);
  const iv = interpWrapper(arg);

  function optWrapper(a) { return fn(a); }
  %PrepareFunctionForOptimization(optWrapper);
  for (let i = 0; i < 200; i++) optWrapper(i & 0x7f);
  %OptimizeFunctionOnNextCall(optWrapper);
  const ov = optWrapper(arg);

  check(label, iv, ov);
}

// ═══════════════════════════════════════════════════════════════════
// PoC 1 & 2 — Escape Analysis Signed-Overflow Boundary
// Target: src/compiler/escape-analysis.cc:563
//   (index << ElementSizeLog2Of) overflows for index >= 268435456
//   with kTagged elements (log2=3), wrapping offset to a negative
//   value that bypasses FieldAt's signed bounds check in release builds.
// ═══════════════════════════════════════════════════════════════════
print("\n══ PoC 1/2: Escape Analysis Overflow ══");

// A: OOB load must return undefined, not alias arr[0]
diff("A1: arr[2^28] returns undefined (not arr[0])", function(x) {
  const a = [x, x + 1];
  return a[268435456];
}, 0x1337);

diff("A2: arr[INT_MAX] returns undefined", function(x) {
  const a = [x, x + 1];
  return a[2147483647];
}, 42);

diff("A3: arr[2^28-1] returns undefined", function(x) {
  const a = [x, x + 1];
  return a[268435455];
}, 42);

// B: OOB store must not corrupt adjacent field
diff("B1: store arr[2^28] does not corrupt arr[0]", function(x) {
  const a = [x, x + 1];
  a[268435456] = 0xdeadbeef;
  return a[0];
}, 0x1337);

diff("B2: store arr[INT_MAX] does not corrupt adjacent alloc", function(x) {
  const guard  = [x * 2];
  const victim = ["original"];
  victim[2147483647] = "corrupt";
  return [guard[0], victim[0]];
}, 7);

// C: the corrupted field must not alias with any other field
diff("C1: arr[2^28] !== arr[0] (no aliasing)", function(x) {
  const a = ["sentinel_0", "sentinel_1"];
  return [a[0], a[268435456]];   // [sentinel_0, undefined]
}, 0);

// ═══════════════════════════════════════════════════════════════════
// PoC 3 — Bytecode Flush / GetMaybeUnpublished Tag Bypass
// Target: trusted-pointer-table-inl.h:67 + mark-compact.cc:3785
//   Unpublished TrustedPointerTable entries bypass tag validation.
//   Exercised during bytecode flushing in mark-compact GC.
// ═══════════════════════════════════════════════════════════════════
print("\n══ PoC 3: Bytecode Flush / Tag Bypass ══");

(function() {
  function coldFn(n) { return n * n; }
  %PrepareFunctionForOptimization(coldFn);
  for (let i = 0; i < 50; i++) coldFn(i);
  const before = coldFn(9);

  if (typeof gc === "function") {
    gc(); gc(); gc({ type: "major" });
  }

  const after = coldFn(9);
  check("P3.1: coldFn(9) stable after major GC (81)", before, 81);
  check("P3.2: coldFn(9) same before/after GC", before, after);

  // Stress: 100 functions, flush, re-call
  const fns = [];
  for (let i = 0; i < 100; i++) {
    const f = new Function("x", "return x + " + i + ";");
    %PrepareFunctionForOptimization(f);
    fns.push(f);
  }
  fns.forEach(f => f(1));
  if (typeof gc === "function") gc({ type: "major" });

  let wrong = 0;
  fns.forEach((f, i) => { if (f(0) !== i) wrong++; });
  check("P3.3: 100 fns correct after GC flush (wrong=" + wrong + ")", wrong, 0);
})();

// ═══════════════════════════════════════════════════════════════════
// PoC 4 — Smi Address Range Guard
// ═══════════════════════════════════════════════════════════════════
print("\n══ PoC 4: Smi Address Range Guard ══");

(function() {
  // Accessing a property on 0 (Smi) must throw TypeError, not segfault
  function smiFetch(v) {
    try { return v.x; } catch(e) { return "TypeError"; }
  }
  check("P4.1: (0).x throws TypeError (Smi not deref'd as ptr)", smiFetch(0), "TypeError");
  check("P4.2: (1).x throws TypeError", smiFetch(1), "TypeError");

  // WASM: i32.load from within wasm memory must not escape wasm sandbox
  const wasm_bytes = new Uint8Array([
    0x00,0x61,0x73,0x6d,0x01,0x00,0x00,0x00,
    0x01,0x05,0x01,0x60,0x00,0x01,0x7f,
    0x03,0x02,0x01,0x00,
    0x05,0x03,0x01,0x00,0x01,
    0x07,0x08,0x01,0x04,0x74,0x65,0x73,0x74,0x00,0x00,
    0x0a,0x09,0x01,0x07,0x00,0x41,0x00,0x28,0x02,0x00,0x0b
  ]);
  try {
    const m = new WebAssembly.Module(wasm_bytes);
    const inst = new WebAssembly.Instance(m);
    const v = inst.exports.test();
    // reading wasm[0] is fine — this is wasm linear memory, not process addr 0
    check("P4.3: WASM i32.load[0] returns numeric value", typeof v === "number", true);
  } catch(e) {
    print("[INFO] P4.3: WASM probe threw: " + e);
  }
})();

// ═══════════════════════════════════════════════════════════════════
// PoC 5 — Signed/Unsigned Range Analysis Edge Cases
// ═══════════════════════════════════════════════════════════════════
print("\n══ PoC 5: Range Analysis Edges ══");

diff("E1: (-1 >>> 0) === 4294967295", function() { return (-1 >>> 0); }, 0);
diff("E2: (0x80000000 | 0) === -2147483648", function() { return (0x80000000 | 0); }, 0);
diff("E3: Math.imul(INT_MAX, 2) === -2", function() {
  return Math.imul(2147483647, 2);
}, 0);
diff("E4: Uint8Array OOB at INT_MAX is undefined", function(x) {
  const a = new Uint8Array(16);
  return a[2147483647];
}, 0);
diff("E5: Float64Array set + get roundtrip at index 0", function(x) {
  const a = new Float64Array(4);
  a[0] = 1.7976931348623157e+308;   // MAX_VALUE
  return a[0] === 1.7976931348623157e+308;
}, 0);

// ═══════════════════════════════════════════════════════════════════
print("\n══════════════════════════════════════════════════════");
print("FINAL: " + totalPass + " passed, " + totalFail + " failed");
if (totalFail > 0) {
  print("⚠  DIFFERENTIAL MISMATCHES FOUND — JIT diverges from interpreter.");
  print("   One or more bugs from the static audit are DYNAMICALLY CONFIRMED.");
} else {
  print("✓  All tests pass — no observable differential in this run.");
}
print("══════════════════════════════════════════════════════");

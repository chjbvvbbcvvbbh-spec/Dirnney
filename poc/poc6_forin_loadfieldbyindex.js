// PoC 6: For-In Optimization — LoadFieldByIndex with Stale Map (Differential Probe)
//
// Target: src/maglev/maglev-graph-builder.cc:7426-7468
//         TryBuildGetKeyedPropertyWithEnumeratedKey
//
// Background:
//   Maglev's for-in fast path (kUseEnumCacheKeysAndIndices) skips the receiver
//   map check in GetEnumeratedKeyedProperty when receiver_needs_map_check=false.
//   The flag is set to false by ForInNext after its own CheckDynamicValue guard.
//
//   If, between ForInNext and GetEnumeratedKeyedProperty in the SAME iteration,
//   the receiver's map is silently transitioned (e.g. by a property store that
//   moves the object from a fast map to a deprecated / newly-branched map),
//   LoadTaggedFieldByFieldIndex proceeds with a stale enum-cache field index.
//
//   LoadTaggedFieldByFieldIndex (machine-lowering-reducer-inl.h:2097) has NO
//   bounds checking on field_index — it is an arithmetic address computation
//   directly into the JSObject's in-object or out-of-object storage.
//
// Attack surface:
//   For a clean type confusion we would need the loaded field to have a
//   different machine type than expected.  The differential test here is
//   simpler: we expose whether Maglev ever returns a value that disagrees
//   with the interpreter for the same code path.
//
// Strategy: DIFFERENTIAL TESTING under Maglev vs interpreter.
//
// Flags: --allow-natives-syntax
//        (optionally: --maglev for explicit Maglev tier)

"use strict";

let pass = 0, fail = 0;

function check(label, iv, ov) {
  const ok = JSON.stringify(iv) === JSON.stringify(ov);
  print((ok ? "[PASS] " : "[FAIL] ") + label);
  if (!ok) {
    print("       interp = " + JSON.stringify(iv));
    print("       jit    = " + JSON.stringify(ov));
  }
  ok ? pass++ : fail++;
}

// ── Test F1: Basic for-in with static receiver — baseline correctness ──────
(function testF1() {
  const obj = { a: 1, b: 2, c: 3 };

  function victim(o) {
    const results = [];
    for (const key in o) {
      results.push(o[key]);
    }
    return JSON.stringify(results);
  }

  function interpWrapper(o) { return victim(o); }
  %NeverOptimizeFunction(interpWrapper);
  const iv = interpWrapper(obj);

  function optWrapper(o) { return victim(o); }
  %PrepareFunctionForOptimization(optWrapper);
  for (let i = 0; i < 200; i++) optWrapper(obj);
  %OptimizeFunctionOnNextCall(optWrapper);
  const ov = optWrapper(obj);

  check("F1: basic for-in enum-cache path", iv, ov);
})();

// ── Test F2: For-in receiver != enumerator (speculating path) ────────────
// When object != receiver, Maglev inserts a map check (CheckDynamicValue).
// This tests that path doesn't silently skip the check.
(function testF2() {
  const template = { x: 10, y: 20 };
  const receiver = { x: 99, y: 88 };

  function victim(enumerator, recv) {
    const results = [];
    for (const key in enumerator) {
      results.push(recv[key]);  // receiver != enumerator
    }
    return JSON.stringify(results);
  }

  function interpWrapper(e, r) { return victim(e, r); }
  %NeverOptimizeFunction(interpWrapper);
  const iv = interpWrapper(template, receiver);

  function optWrapper(e, r) { return victim(e, r); }
  %PrepareFunctionForOptimization(optWrapper);
  for (let i = 0; i < 200; i++) optWrapper(template, receiver);
  %OptimizeFunctionOnNextCall(optWrapper);
  const ov = optWrapper(template, receiver);

  check("F2: for-in with different receiver", iv, ov);
})();

// ── Test F3: Map transition mid-iteration probe ───────────────────────────
// In each iteration, we store a new property to a DIFFERENT object
// (not the receiver) so the receiver's map is unchanged but the
// JIT might mis-classify the side-effect chain.
(function testF3() {
  const obj = { p: 42, q: 99 };
  let sideEffect = {};

  function victim(o) {
    const results = [];
    for (const key in o) {
      sideEffect[key] = 1;   // side-effecting store to a DIFFERENT object
      results.push(o[key]);  // GetEnumeratedKeyedProperty on original receiver
    }
    return JSON.stringify(results);
  }

  function interpWrapper(o) { return victim(o); }
  %NeverOptimizeFunction(interpWrapper);
  const iv = interpWrapper(obj);

  function optWrapper(o) { return victim(o); }
  %PrepareFunctionForOptimization(optWrapper);
  for (let i = 0; i < 200; i++) { sideEffect = {}; optWrapper(obj); }
  sideEffect = {};
  %OptimizeFunctionOnNextCall(optWrapper);
  const ov = optWrapper(obj);

  check("F3: for-in with side-effect store to different object", iv, ov);
})();

// ── Test F4: LoadFieldByIndex with enum-cache path and numeric-like values ─
// We use objects where all property values look like Smis.
// Type confusion would manifest as one Smi being misread as another.
(function testF4() {
  const obj = { sentinel: 0xdeadbeef >>> 0, guard: 0xcafe >>> 0 };

  function victim(o) {
    const r = [];
    for (const key in o) {
      r.push(o[key]);
    }
    return r;
  }

  function interpWrapper(o) { return victim(o); }
  %NeverOptimizeFunction(interpWrapper);
  const iv = interpWrapper(obj);

  function optWrapper(o) { return victim(o); }
  %PrepareFunctionForOptimization(optWrapper);
  for (let i = 0; i < 200; i++) optWrapper(obj);
  %OptimizeFunctionOnNextCall(optWrapper);
  const ov = optWrapper(obj);

  // If JIT confuses field indices, sentinel and guard values may be swapped.
  check("F4: enum-cache field indices not confused for Smi-like values", iv, ov);
})();

// ── Test F5: Prototype-chain for-in (inherited properties) ───────────────
// Tests that the JIT does not apply the enum-cache fast-path incorrectly
// when the enumerated properties are inherited.
(function testF5() {
  function Base() {}
  Base.prototype.inherited = 77;
  const obj = new Base();
  obj.own = 55;

  function victim(o) {
    const r = [];
    for (const key in o) {
      r.push(o[key]);
    }
    return JSON.stringify(r);
  }

  function interpWrapper(o) { return victim(o); }
  %NeverOptimizeFunction(interpWrapper);
  const iv = interpWrapper(obj);

  function optWrapper(o) { return victim(o); }
  %PrepareFunctionForOptimization(optWrapper);
  for (let i = 0; i < 200; i++) optWrapper(obj);
  %OptimizeFunctionOnNextCall(optWrapper);
  const ov = optWrapper(obj);

  check("F5: for-in with inherited properties", iv, ov);
})();

// ── Test F6: escape-analysis + for-in interaction ─────────────────────────
// The escape-analysis bug (OffsetOfElementsAccess signed overflow at index
// 2^28) can cause a VirtualObject::FieldAt OOB read.  When the escape
// analysis happens to virtualise an allocation inside a for-in body, the
// OOB Variable returned might alias the receiver's enum-cache index variable,
// creating a confused field index for LoadTaggedFieldByFieldIndex.
//
// This test encodes the scenario in the simplest differential form:
// the inner allocation arr is small (2 elements); if escape analysis
// virtualises it and then handles the 2^28 access with the overflow,
// it may return a wrong field, causing the return value to differ from
// the interpreter's correct "undefined".
(function testF6() {
  const obj = { alpha: 1111, beta: 2222 };
  const OVERFLOW_IDX = 268435456; // 2^28 — overflow boundary for kTagged

  function victim(o) {
    let captured;
    for (const key in o) {
      const arr = [o[key], o[key] + 1]; // 2-element inner allocation
      const oob  = arr[OVERFLOW_IDX];   // constant OOB → triggers EA bug
      captured = (oob === undefined) ? o[key] : oob;
    }
    return captured;
  }

  function interpWrapper(o) { return victim(o); }
  %NeverOptimizeFunction(interpWrapper);
  const iv = interpWrapper(obj);

  function optWrapper(o) { return victim(o); }
  %PrepareFunctionForOptimization(optWrapper);
  for (let i = 0; i < 200; i++) optWrapper(obj);
  %OptimizeFunctionOnNextCall(optWrapper);
  const ov = optWrapper(obj);

  check("F6: escape-analysis overflow inside for-in body", iv, ov);
  if (iv !== ov) {
    print("  !! INTERACTION BUG: EA overflow affects for-in field resolution");
    print("  !! interp=" + iv + "  jit=" + ov);
  }
})();

print("\n== For-In / LoadFieldByIndex audit ==");
print("Results: " + pass + " passed, " + fail + " failed");
if (fail > 0) {
  print("DIFFERENTIAL MISMATCH — JIT diverges from interpreter.");
  print("Suggests incorrect map check elision or stale enum-cache index.");
} else {
  print("No differential mismatch observed.");
  print("Note: absence of mismatch does not rule out the underlying bug.");
  print("The safety relies on CheckDynamicValue in ForInNext — if that");
  print("guard can be bypassed (e.g. via type-confusion in map comparison),");
  print("LoadTaggedFieldByFieldIndex proceeds with an unvalidated field_index.");
}

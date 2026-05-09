# V8 Security Audit Findings — Project Zero Style

## Summary of Findings

| # | Severity | File | Description |
|---|----------|------|-------------|
| 1 | P0 | escape-analysis.cc:563 | Signed overflow → EA VirtualObject OOB |
| 2 | P1 | trusted-pointer-table-inl.h:63 | Tag check bypassed for unpublished entries |
| 3 | P1 | sandbox.cc:317 | Smi address-range guard silent failure |
| 4 | P1 | maglev-graph-builder.cc:7448 | LoadTaggedFieldByFieldIndex without bounds |
| 5 | P2 | js-native-context-specialization.cc:1884 | Homomorphic IC double-field CheckHeapObject removed |

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

**Severity**: P2 — JIT type confusion / controlled OOB read; requires `--homomorphic-ic` or `--future` flag (currently non-default). Severity escalates to P1/P0 if `homomorphic_ic` becomes the default.

### Bug locations

```
src/compiler/js-native-context-specialization.cc:1883–1894   ReduceHomomorphicAccess
src/compiler/typed-optimization.cc:166–174                   ReduceCheckHeapObject
src/compiler/turbofan-types.h:95–131                         OtherInternal vs SignedSmall bits
src/flags/flag-definitions.h:3257                            homomorphic_ic flag (default false)
src/objects/map-updater.cc:1396–1407                         GeneralizeField deopt path
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
3. TypedLoweringPhase runs TypedOptimization:
     - OtherInternal ∩ SignedSmall = ∅ → CheckHeapObject REMOVED
4. Assign receivers[k].x = 42 (Smi):
     - MapUpdater::GeneralizeField: Double → Tagged in-place (no map transition)
     - No kFieldRepresentationGroup dep on compiled code → no deoptimization
5. Call readX(receivers[k]) through stale JIT code:
     - LoadField returns Smi(42) (tagged: 0x55 on 64-bit)
     - No CheckHeapObject gate anymore
     - LoadField(ForMap(), Smi(42)) dereferences 0x55 as a HeapObject pointer
     - Controlled OOB read / type confusion
```

### Impact

- An attacker who controls the Smi value placed in the generalized field controls
  the "pointer" passed to `LoadField(ForMap())`.
- Primitive: semi-controlled read at `Smi_value & ~1` (low-bit tag stripped by V8).
- Combined with Finding 3 (Smi guard absent), this can dereference low addresses.
- Severity is P2 today because `--homomorphic-ic` defaults to `false`. When/if
  homomorphic ICs ship by default (via `--future` graduating), this becomes P1/P0.

### PoC

- `poc/poc9_homomorphic_double_field_confusion.js`

Run with:
```
./d8 --future --allow-natives-syntax poc/poc9_homomorphic_double_field_confusion.js
# or:
./d8 --homomorphic-ic --allow-natives-syntax poc/poc9_homomorphic_double_field_confusion.js
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

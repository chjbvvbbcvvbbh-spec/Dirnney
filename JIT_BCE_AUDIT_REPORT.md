# RyuJIT Bounds-Check Elimination Audit

**Scope:** `/home/user/runtime/src/coreclr/jit/rangecheck.cpp`, `assertionprop.cpp`, `valuenum.cpp`, `rangecheck.h`, `compiler.h`, `rangecheckcloning.cpp`

---

## Executive Summary

After exhaustive static analysis of the RyuJIT bounds-check elimination (BCE) pipeline, no directly exploitable memory safety vulnerabilities were found in the current code. However, five significant technical findings are documented below — including dead code, missed safety checks, historical BCE bypass bugs (all patched), and one subtle logic gap that warrants attention.

---

## Finding 1 — Dead Variable: `srcIsUnsigned` in VNF_Cast Range Inference

**File:** `rangecheck.cpp:730-756`

```cpp
case VNF_Cast:
{
    var_types castToType;
    bool      srcIsUnsigned;   // ← OBTAINED but NEVER READ
    comp->vnStore->GetCastOperFromVN(funcApp.m_args[1], &castToType, &srcIsUnsigned);

    Range castToTypeRange = GetRangeFromType(castToType);
    if (castToTypeRange.IsConstantRange())
    {
        result = castToTypeRange;
        if (genActualType(comp->vnStore->TypeOfVN(funcApp.m_args[0])) == TYP_INT)
        {
            Range castOpRange = GetRangeFromAssertionsWorker(comp, funcApp.m_args[0], ...);
            if (castOpRange.IsConstantRange() &&
                (castOpRange.LowerLimit().GetConstant() >= castToTypeRange.LowerLimit().GetConstant()) &&
                (castOpRange.UpperLimit().GetConstant() <= castToTypeRange.UpperLimit().GetConstant()))
            {
                result = castOpRange;  // srcIsUnsigned not considered
            }
        }
    }
}
```

**Analysis:** `srcIsUnsigned` encodes whether the source of the cast is zero-extended (unsigned) or sign-extended. For narrowing to be safe, the source range must fit within the target type range (subset check). The check `castOpRange ⊆ castToTypeRange` is invariant to signedness because:

- For same-size casts (`int` → `int`): `TYP_INT` range is `[INT32_MIN, INT32_MAX]`; the subset check always passes; result = source range. This is correct since `(int)(uint)x = x` for any int x.
- For widening casts (byte→int): the source VN already carries the correct range from prior analysis; subset check validates correctly.
- For `TYP_UINT` as `castToType`: `GetRangeFromType(TYP_UINT)` returns `keUnknown`, so `IsConstantRange()` is false — the entire VNF_Cast branch is skipped. TYP_UINT is intentionally unsupported.

**Verdict:** Not currently exploitable. The dead variable is a code quality issue — a future developer adding `srcIsUnsigned`-aware logic could introduce a bug if they misunderstand why it's safe to ignore.

**Recommendation:** Either remove the variable or add a comment explaining why it's intentionally unused.

---

## Finding 2 — `Span.Length` Not Recognized as Never-Negative

**File:** `valuenum.cpp:7080-7085`

```cpp
bool ValueNumStore::IsVNNeverNegative(ValueNum vn)
{
    // Array length can never be negative.
    if (IsVNArrLen(vn))
        return VNVisit::Continue;

    // TODO-VN: Recognize Span.Length
    // Handle more intrinsics such as Math.Max(neverNegative1, neverNegative2)
```

**Analysis:** `IsVNNeverNegative` is used in BCE and range analysis to establish lower bounds. `Array.Length` is recognized (always >= 0). `Span<T>.Length` and `ReadOnlySpan<T>.Length` are not. 

However, Span.Length IS correctly handled in the assertion path: when `(uint)i < span.Length` appears, `IsVNUnsignedCompareCheckedBound` detects it, `CreateNoThrowArrBnd` marks the bound as `m_checkedBoundIsNeverNegative = true`, and `MergeEdgeAssertionsWorker` correctly derives `i ∈ [0, span.Length)`. So the Span.Length gap causes **missed optimizations**, not incorrect BCE.

**Verdict:** Missed optimization, not a security bug. The TODO comment is accurate.

**Recommendation:** Implement Span.Length recognition in `IsVNNeverNegative` for better range narrowing (CQ improvement only).

---

## Finding 3 — Unsigned Shift-to-Multiply Produces Technically-UB Constant

**File:** `rangecheck.h:639-646`

```cpp
static Range ConvertShiftToMultiply(const Range& r1)
{
    int r1loConstant = r1lo.GetConstant();
    int r1hiConstant = r1hi.GetConstant();
    if (r1loConstant <= 0 || r1loConstant > 31 || r1hiConstant <= 0 || r1hiConstant > 31)
        return Limit(Limit::keUnknown);

    result.lLimit = Limit(Limit::keConstant, 1 << r1loConstant);
    result.uLimit = Limit(Limit::keConstant, 1 << r1hiConstant);  // 1 << 31 = UB in C++!
```

**Analysis:** `1 << 31` is undefined behavior in C++ (shift into sign bit of signed integer). On all relevant platforms (x86, arm64) this produces `INT32_MIN = -2147483648`. This value then flows into `Multiply` where:
- `MulOverflows(r1hi, INT32_MIN, false)` returns true for r1hi > 1
- For r1hi = 1: 1 × INT32_MIN = INT32_MIN; `BetweenBounds` checks `lcns >= 0`, returns false → safe

**Verdict:** Not exploitable. C++ UB that produces the expected platform-specific result. However, it should be replaced with `(int)(1u << r1hiConstant)` or guarded with `r1hiConstant < 31`.

---

## Finding 4 — BCE `DoesOverflow` Only Checks Upper-Bound Overflow for GT_ADD

**File:** `rangecheck.cpp:1953-1955`

```cpp
if (binop->OperIs(GT_ADD))
{
    return AddOverflows(op1Range->UpperLimit(), op2Range->UpperLimit());
}
```

**Analysis:** For GT_ADD, overflow is only checked on the upper bounds. Lower-bound overflow (e.g., `INT32_MIN + (-1)`) is not checked. However, this is safe because:

- If the lower-bound sum overflows, `RangeOps::Add` returns `keUnknown` for the lower limit (via `CheckedOps::AddOverflows`)
- A `keUnknown` lower limit causes `OptimizeRangeCheck` to bail out at line 419-423 before reaching `BetweenBounds`

**Verdict:** Safe by defense-in-depth. Lower overflow is handled upstream. No false BCE possible.

---

## Finding 5 — Historical BCE Bypass Bugs (All Patched)

Three recent regression tests document previously exploitable false BCE bugs in the current codebase (now fixed):

### Runtime_95226 — Incorrect assertion propagation at loop entry
```csharp
// arr has 1 element
static void Foo(int[] arr)
{
    int i = 0;
    goto Bottom;
Top:;
    i++;
    while (true) { if (AlwaysTrue()) break; }
Bottom:;
    Use(arr[i]);       // i=0 first time (valid), i=1 second time (OOB!)
    if (i < arr.Length) goto Top;
}
```
The JIT incorrectly used the `i < arr.Length` assertion (from the JTRUE at the bottom) at the `Bottom:` label BCE site, where it didn't always hold.

### Runtime_96623 — i = INT32_MAX loop with incorrect monotonic increase assumption
```csharp
// arr has 15 elements  
// i is set to INT32_MAX then loop accesses arr[i] — clearly OOB
// The JIT was treating some loop path as starting from 0
```

### Runtime_116457 — i * 2 range not bounded by arr.Length
```csharp
// arr has 40 elements
for (int i = 0; i < arr.Length; i++)
{
    var element = i < a ? arr[i * 2] : arr[i - a];  // i * 2 up to 78, arr only has 40 elements!
}
```
The JIT incorrectly eliminated the bounds check for `arr[i * 2]` — proving `i < 40` does not prove `i * 2 < 40`.

**Verdict:** All three are fixed. They demonstrate the attack surface: any incorrect range narrowing or assertion propagation can directly cause OOB memory access.

---

## Architecture Assessment

The BCE pipeline flows as:

```
GT_BOUNDS_CHECK node
    ↓
OptimizeRangeCheck()
    ├── Fast path: UMOD(X, arr.Length) → always in-bounds (correct)
    ├── Fast path: arr[arr.Length - cns] with lower-bound assertion (correct)
    ├── TryGetRange() → GetRangeWorker() + Widen()
    │       └── MergeAssertion() → MergeEdgeAssertions()
    │               └── MergeEdgeAssertionsWorker() — COMPLEX, many assertion types
    └── BetweenBounds() — final range vs. arrLen check
            ├── keBinOpArray path: symbolic upper bound
            └── keConstant path: numeric bounds
```

The `MergeEdgeAssertionsWorker` is the most complex component with 400+ lines handling:
- `OAK_LT_UN` (unsigned less-than, from checked bounds)
- `O2K_CHECKED_BOUND_ADD_CNS` with `IsCheckedBoundNeverNegative` flag
- `OAK_GE`/`OAK_LE`/`OAK_GT`/`OAK_LT` signed relops
- `OAK_EQUAL`/`OAK_NOT_EQUAL` constant assertions
- `IsBoundsCheckNoThrow` assertions
- VN-to-VN comparisons (unsigned skipped — conservative)
- PHI definition chain following via `optVisitReachingAssertions`

**Critical safety properties observed:**
1. Unsigned GT/GE creates non-contiguous ranges → correctly skipped (line 1531)
2. Assertion edges matched precisely to predecessor blocks (line 5883-5899)
3. PHI arg pred validation (line 5639-5650) — aborts if pred coverage mismatch
4. keBinOpArray upper limits require VN equality (line 1133) — prevents cross-array confusion
5. `IsCheckedBoundNeverNegative` flag only set for bounds from `GT_BOUNDS_CHECK` nodes (always non-negative)

---

## Summary Table

| # | Finding | Severity | Exploitable | Status |
|---|---------|----------|-------------|--------|
| 1 | `srcIsUnsigned` dead variable in VNF_Cast | Low | No | Open (code quality) |
| 2 | Span.Length not in `IsVNNeverNegative` | Low | No | Open (TODO) |
| 3 | `1 << 31` UB in ConvertShiftToMultiply | Low | No | Open (cleanup) |
| 4 | `DoesOverflow` GT_ADD only checks upper bound | Medium | No | Open (by design) |
| 5 | Historical BCE bypass bugs | Critical | Fixed | Closed (regression tests) |

---

## Recommended Mitigations

1. **Remove or document** the `srcIsUnsigned` dead variable in `GetRangeFromAssertionsWorker`.
2. **Implement** Span.Length recognition in `IsVNNeverNegative` (follow the TODO).
3. **Replace** `1 << 31` with `(int)(1u << r1hiConstant)` to avoid C++ UB.
4. **Fuzzing:** The bug pattern from Runtime_116457 (`arr[i*k]` with `i < arr.Length`) suggests
   fuzzing loop patterns with non-linear index expressions could find new BCE bugs.
5. **Differential testing:** Run the JIT with `DOTNET_JitStress=1` and compare output against
   interpreted mode for programs involving complex loop structures with goto.

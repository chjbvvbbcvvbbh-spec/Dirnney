# Contract ConditionalWeakTable

This contract provides the ability to look up values in a `ConditionalWeakTable<TKey, TValue>` managed object by key identity.

## APIs of contract

``` csharp
// Try to find the value associated with the given key in the conditional weak table.
// Returns true and sets value if found, false otherwise.
bool TryGetValue(TargetPointer conditionalWeakTable, TargetPointer key, out TargetPointer value);
```

## Version 1

This contract reads the field layout of `ConditionalWeakTable<TKey, TValue>` and its nested types
(`Container`, `Container+Entry`) by calling `Target.GetTypeInfo(DataType)` with the corresponding
`DataType` enum members (see the [`MetadataLayoutSource`](MetadataLayoutSource.md) contract) rather
than via cDAC data descriptors. Field offsets are resolved by name at runtime.

Data descriptors used:
| Data Descriptor Name | Field | Meaning |
| --- | --- | --- |
| `Array` | `m_NumComponents` | Number of elements in the array |

Contracts used:
| Contract Name |
| --- |
| `Object` |
| `GC` |
| `MetadataLayoutSource` |
| `RuntimeTypeSystem` |

Managed types used:

| Managed Type | Field | Meaning |
| --- | --- | --- |
| `System.Runtime.CompilerServices.ConditionalWeakTable\`2` | `_container` | Pointer to the active `Container` holding buckets and entries |
| `System.Runtime.CompilerServices.ConditionalWeakTable\`2+Container` | `_buckets` | `int[]` buckets array; each slot is an index into `_entries` or `-1` |
| `System.Runtime.CompilerServices.ConditionalWeakTable\`2+Container` | `_entries` | `Entry[]` storage for the table's entries |
| `System.Runtime.CompilerServices.ConditionalWeakTable\`2+Entry` | `HashCode` | Hash code of the key (masked to positive int); chain terminator when `-1` |
| `System.Runtime.CompilerServices.ConditionalWeakTable\`2+Entry` | `Next` | Index of the next entry in the bucket chain, or `-1` |
| `System.Runtime.CompilerServices.ConditionalWeakTable\`2+Entry` | `depHnd` | Dependent handle tying the key to the value |

The algorithm looks up the `_container` field of the `ConditionalWeakTable` object, then reads the
`_buckets` and `_entries` fields from the container. Each `Entry` is then read via its inline
address in the entries array; its stride is determined from the entries array's component size.

``` csharp
bool TryGetValue(TargetPointer conditionalWeakTable, TargetPointer key, out TargetPointer value)
{
    value = TargetPointer.Null;

    Data.ConditionalWeakTable cwt = target.ProcessedData.GetOrAdd<Data.ConditionalWeakTable>(conditionalWeakTable);
    Data.ConditionalWeakTableContainer container =
        target.ProcessedData.GetOrAdd<Data.ConditionalWeakTableContainer>(cwt.Container);

    // Get the runtime default hash code for the key object (returns 0 if none assigned)
    int hashCode = target.Contracts.Object.TryGetHashCode(key);
    if (hashCode == 0)
        return false;

    hashCode &= int.MaxValue;

    // Read the buckets array and find the bucket (bucketCount is a power of 2)
    Data.Array bucketsArray = target.ProcessedData.GetOrAdd<Data.Array>(container.Buckets);
    int bucket = hashCode & (int)(bucketsArray.NumComponents - 1);
    int entriesIndex = target.Read<int>(bucketsArray.DataPointer + bucket * sizeof(int));

    // Get entry size from the entries array's component size
    Data.Array entriesArray = target.ProcessedData.GetOrAdd<Data.Array>(container.Entries);
    IRuntimeTypeSystem rts = target.Contracts.RuntimeTypeSystem;
    TargetPointer entriesMT = target.Contracts.Object.GetMethodTableAddress(container.Entries);
    uint entrySize = rts.GetComponentSize(rts.GetTypeHandle(entriesMT));

    // Walk the chain
    while (entriesIndex != -1)
    {
        TargetPointer entryAddr = entriesArray.DataPointer + (uint)entriesIndex * entrySize;
        Data.ConditionalWeakTableEntry entry =
            target.ProcessedData.GetOrAdd<Data.ConditionalWeakTableEntry>(entryAddr);

        if (entry.HashCode == hashCode)
        {
            // DepHnd is an OBJECTHANDLE — a pointer to a pointer to the object
            Data.ObjectHandle handle = target.ProcessedData.GetOrAdd<Data.ObjectHandle>(entry.DepHnd);
            if (handle.Object == key)
            {
                value = target.Contracts.GC.GetHandleExtraInfo(entry.DepHnd);
                return true;
            }
        }

        entriesIndex = entry.Next;
    }

    return false;
}
```

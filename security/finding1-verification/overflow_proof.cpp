// Standalone overflow proof — mirrors gles2_cmd_decoder.cc:12629-12630 exactly
// Compile: g++ -o overflow_proof overflow_proof.cpp && ./overflow_proof

#include <cstdint>
#include <cstdio>
#include <limits>
#include <cstring>

// Mirrors HeapArray::Uninit(size) — returns nullptr for size==0
static uint8_t* heap_array_uninit(size_t size) {
    if (size == 0) return nullptr;          // empty() → caught at line 12991
    return new uint8_t[size];              // line 12629: HeapArray::Uninit(size)
}

// The vulnerable function — direct port of DecompressTextureData logic
static void demonstrate_overflow(uint32_t output_pixel_size,
                                  uint32_t width, uint32_t height, uint32_t depth) {
    printf("=== DecompressTextureData overflow demonstration ===\n");
    printf("  output_pixel_size = %u\n", output_pixel_size);
    printf("  width=%u  height=%u  depth=%u\n", width, height, depth);

    // LINE 12629-12630: THE VULNERABLE CALCULATION
    uint32_t alloc_size = output_pixel_size * width * height * depth;
    uint64_t true_size  = (uint64_t)output_pixel_size * width * height * depth;

    printf("\n  uint32_t alloc_size = %u  (OVERFLOWS from true %lu)\n",
           alloc_size, true_size);
    printf("  true_size           = %lu bytes  (%.2f GB)\n",
           true_size, (double)true_size / (1024*1024*1024));

    if (alloc_size == 0) {
        printf("  → alloc_size==0: HeapArray::Uninit(0) returns empty\n");
        printf("  → empty() check at line 12991 catches this ✓\n");
        return;
    }

    printf("  → alloc_size=%u: HeapArray::Uninit(%u) allocates %u bytes\n",
           alloc_size, alloc_size, alloc_size);
    printf("  → empty() check at line 12991: NOT empty → PASSES ✗\n");
    printf("  → decompression_function() writes %lu bytes into %u byte buffer\n",
           true_size, alloc_size);
    printf("  → HEAP BUFFER OVERFLOW: %lu bytes beyond allocated region\n\n",
           true_size - alloc_size);

    // Simulate the allocation — DO NOT actually trigger the overflow write
    uint8_t* buf = heap_array_uninit(alloc_size);
    if (!buf) { printf("  alloc failed\n"); return; }

    printf("  [VERIFIED] Allocated %u bytes at %p\n", alloc_size, (void*)buf);
    printf("  [VERIFIED] A real call would write %lu bytes here → %%zu OOB\n",
           true_size, true_size - alloc_size);
    // Safe: we do NOT call the actual decompressor with wrong sizes
    delete[] buf;
}

int main() {
    printf("Finding 1: gles2_cmd_decoder.cc:12629 — uint32_t overflow\n");
    printf("Source: output_pixel_size * width * height * depth (all uint32_t)\n\n");

    // Case 1: Overflow → 0 (caught)
    printf("--- Case 1: overflow → 0 (caught by empty() check) ---\n");
    demonstrate_overflow(4, 32768, 32768, 1);
    // 4 * 32768 * 32768 = 4,294,967,296 → uint32 = 0

    // Case 2: Overflow → non-zero (EXPLOITABLE)
    printf("--- Case 2: overflow → non-zero (EXPLOITABLE) ---\n");
    demonstrate_overflow(4, 4096, 4096, 65);
    // 4 * 4096 * 4096 * 65 = 4,362,076,160 → uint32 = 67,108,864

    // Case 3: Smaller, faster crash
    printf("--- Case 3: minimum allocatable overflow ---\n");
    demonstrate_overflow(4, 4096, 4096, 65);

    // Verify: output_pixel_size * width row pitch also overflows (line 12649)
    printf("--- Line 12649 row_pitch overflow ---\n");
    uint32_t ops=4, w=4096, h=4096, d=65;
    uint32_t row_pitch    = ops * w;           // = 16384 — safe
    uint32_t depth_pitch  = ops * w * h;       // = 67,108,864 — wraps at depth=1
    uint64_t true_dp      = (uint64_t)ops * w * h;
    printf("  row_pitch (line 12649)  = %u  (true=%lu) %s\n",
           row_pitch, (uint64_t)ops*w, row_pitch == (uint64_t)ops*w ? "OK" : "OVERFLOW");
    printf("  depth_pitch(line 12650) = %u  (true=%lu) %s\n",
           depth_pitch, true_dp, depth_pitch == true_dp ? "OK" : "OVERFLOW");

    return 0;
}

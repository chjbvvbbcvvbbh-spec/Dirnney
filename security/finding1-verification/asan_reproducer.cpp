// ASan reproducer for gles2_cmd_decoder.cc:12629
// Replicates DecompressTextureData exactly: same allocation, same write pattern.
//
// Compile: g++ -fsanitize=address -fno-omit-frame-pointer -g -O1 \
//              -o asan_reproducer asan_reproducer.cpp
// Run:     ASAN_OPTIONS=halt_on_error=1 ./asan_reproducer
//
// Expected ASan output:
//   ==ERROR: AddressSanitizer: heap-buffer-overflow on address 0x...
//   WRITE of size 8 at 0x... thread T0
//   ...allocated 67108864 bytes here

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <optional>
#include <limits>

// ── Mirrors base::HeapArray<uint8_t>::Uninit() ───────────────────────────────
// Returns nullptr (empty) if size==0, else new uint8_t[size]
struct HeapArray {
    uint8_t* data_;
    size_t   size_;

    static HeapArray Uninit(size_t size) {
        if (!size) return {nullptr, 0};
        return {new uint8_t[size], size};  // line 12629-12630 equivalent
    }
    bool empty() const { return size_ == 0; }
    uint8_t* data() { return data_; }
    ~HeapArray() { delete[] data_; }
};

// ── Mirrors angle::LoadETC2RGBA8ToRGBA8 write pattern ────────────────────────
// Real decompressor writes output_pixel_size bytes per pixel sequentially.
// We replicate: for each depth slice, for each row, for each pixel → write ops bytes.
static void simulated_decompression_function(
    size_t width, size_t height, size_t depth,
    const uint8_t* /*input*/,
    size_t /*inputRowPitch*/,
    size_t /*inputDepthPitch*/,
    uint8_t* output,            // ← points into the UNDERALLOCATED buffer
    size_t outputRowPitch,      // = output_pixel_size * width
    size_t outputDepthPitch)    // = output_pixel_size * width * height
{
    const size_t output_pixel_size = outputRowPitch / width;

    // This is what the real ANGLE decompressor does:
    // iterate over every output pixel and write ops bytes
    for (size_t d = 0; d < depth; d++) {
        for (size_t h = 0; h < height; h++) {
            for (size_t w = 0; w < width; w++) {
                // pointer arithmetic matches real decoder
                uint8_t* pixel = output
                    + d * outputDepthPitch
                    + h * outputRowPitch
                    + w * output_pixel_size;
                // ← HEAP OVERFLOW HERE when d*outputDepthPitch overflows
                //   the allocated buffer size
                memset(pixel, 0xFF, output_pixel_size);
            }
        }
        printf("  [depth slice %zu written, offset=%zu / alloc=%zu]\n",
               d,
               (size_t)(d * outputDepthPitch),
               (size_t)67108864);
        fflush(stdout);
        // Stop after first overflow slice for readability
        // (real crash happens here — remove break for full crash)
        if (d * outputDepthPitch >= 67108864) {
            printf("  [!] Now writing past allocation boundary → ASan fires\n");
        }
    }
}

// ── Mirrors DecompressTextureData (gles2_cmd_decoder.cc:12619) ───────────────
static HeapArray DecompressTextureData(uint32_t width, uint32_t height, uint32_t depth,
                                        uint32_t output_pixel_size,
                                        const uint8_t* input_data) {
    // LINE 12629-12630: THE BUG — all operands are uint32_t
    auto decompressed_data = HeapArray::Uninit(
        output_pixel_size * width * height * depth);   // ← OVERFLOW

    if (decompressed_data.empty()) {
        printf("  [safe] empty() → kLostContext (overflow→0 case caught)\n");
        return HeapArray{nullptr, 0};
    }

    printf("  [vuln] HeapArray::Uninit(%u) allocated %zu bytes\n",
           output_pixel_size * width * height * depth,
           decompressed_data.size_);
    printf("  [vuln] empty() = false → line 12991 check PASSES\n");
    printf("  [vuln] calling decompression_function (writes %llu bytes)...\n",
           (unsigned long long)output_pixel_size * width * height * depth);
    fflush(stdout);

    // LINE 12645-12650: the decompressor is called with the true (large) dimensions
    // but output points to the small (overflowed) allocation
    simulated_decompression_function(
        width, height, depth,
        input_data,
        output_pixel_size * width,              // outputRowPitch — uint32_t, safe
        output_pixel_size * width * height,     // outputDepthPitch — uint32_t, MAY overflow too
        decompressed_data.data(),
        output_pixel_size * width,
        output_pixel_size * width * height);

    return decompressed_data;
}

int main() {
    printf("=== Finding 1: gles2_cmd_decoder.cc:12629 ASan Reproducer ===\n\n");

    const uint32_t output_pixel_size = 4;   // RGBA8 from ETC2 decompression
    const uint32_t W = 4096, H = 4096, D = 65;

    uint64_t true_size = (uint64_t)output_pixel_size * W * H * D;
    uint32_t u32_size  = output_pixel_size * W * H * D;

    printf("Dimensions: %u x %u x %u  (depth=65 << MAX_3D_TEXTURE_SIZE=2048)\n", W, H, D);
    printf("output_pixel_size: %u (RGBA8)\n\n", output_pixel_size);
    printf("True output size  : %llu bytes  (%.2f GB)\n",
           (unsigned long long)true_size, (double)true_size / (1<<30));
    printf("uint32_t truncated: %u bytes    (%.1f MB)  ← HeapArray::Uninit(this)\n",
           u32_size, (double)u32_size / (1<<20));
    printf("Overflow amount   : %llu bytes  (%.2f GB)\n\n",
           (unsigned long long)(true_size - u32_size),
           (double)(true_size - u32_size) / (1<<30));

    // Dummy input buffer (1 GB ETC2 compressed data — small value, valid pointer)
    uint32_t compressed_size = (W/4) * (H/4) * D * 16;
    printf("Allocating %u bytes input data...\n", compressed_size);
    auto input = std::make_unique<uint8_t[]>(compressed_size);
    memset(input.get(), 0x00, compressed_size);

    printf("Calling DecompressTextureData(%u, %u, %u, pixel_size=%u)...\n\n",
           W, H, D, output_pixel_size);

    // This call will trigger the heap-buffer-overflow
    // ASan will catch it and print a detailed report
    auto result = DecompressTextureData(W, H, D, output_pixel_size, input.get());

    printf("\nReturned without crash (ASan not enabled or overflow not reached)\n");
    return 0;
}

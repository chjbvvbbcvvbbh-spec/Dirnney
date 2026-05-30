/*
 * EXACT reproduction of the doubling loop in QuicRecvBufferWrite()
 * src/core/recv_buffer.c  lines 763-772  (msquic HEAD c62a7e17)
 *
 * Uses *identical* types: uint32_t everywhere, uint64_t for offsets.
 * No iteration guard — will genuinely hang if the bug fires.
 * SIGALRM kills it after 3 seconds so CI doesn't block.
 *
 * Compile:  gcc -O0 -o recvbuf_exact recvbuf_exact.c && ./recvbuf_exact
 */
#include <stdio.h>
#include <stdint.h>
#include <signal.h>
#include <setjmp.h>
#include <string.h>
#include <time.h>
#include <unistd.h>

/* ---- exact msquic types ---- */
typedef uint32_t BOOLEAN;
typedef uint32_t QUIC_STATUS;
#define TRUE  1
#define FALSE 0
#define UINT32_MAX_V 0xFFFFFFFFu

/* ---- mirrors of the relevant constants ---- */
#define QUIC_DEFAULT_STREAM_FC_WINDOW_SIZE   0x10000u    /* 64 KB  */
#define QUIC_DEFAULT_STREAM_RECV_BUFFER_SIZE 0x1000u     /* 4 KB   */
#define QUIC_DEFAULT_CONN_FLOW_CONTROL_WINDOW 0x1000000u /* 16 MB  */
#define CXPLAT_MIN(a,b) ((a)<(b)?(a):(b))

/* ---- the exact line-by-line doubling loop from recv_buffer.c ---- */
static volatile int timed_out = 0;
static sigjmp_buf alarm_jmp;

static void alarm_handler(int sig) {
    (void)sig;
    timed_out = 1;
    siglongjmp(alarm_jmp, 1);
}

typedef struct {
    const char *name;
    uint64_t    BaseOffset;
    uint32_t    VirtualBufferLength;   /* server-side receive window */
    uint32_t    LastChunkAllocLength;  /* current last chunk size    */
    uint64_t    AbsoluteLength;        /* WriteOffset+WriteLength    */
} TestCase;

static void run(TestCase *tc) {
    printf("  Case: %-55s\n", tc->name);
    printf("        VBL=0x%08X  LastChunk=0x%08X  AbsLen=0x%016llX\n",
           tc->VirtualBufferLength, tc->LastChunkAllocLength,
           (unsigned long long)tc->AbsoluteLength);

    /* ---------- line 718 guard (attacker cannot bypass this) --------- */
    if (tc->AbsoluteLength > tc->BaseOffset + (uint64_t)tc->VirtualBufferLength) {
        printf("  RESULT: blocked by VirtualBufferLength guard (line 718)\n\n");
        return;
    }

    /* ---------- exact lines 765-768 from recv_buffer.c --------------- */
    timed_out = 0;
    uint32_t NewBufferLength;
    uint64_t iters = 0;
    clock_t t0 = clock();

    if (sigsetjmp(alarm_jmp, 1) == 0) {
        alarm(3);   /* 3-second watchdog */

        /* LINE 765 */ NewBufferLength = tc->LastChunkAllocLength << 1;
        /* LINE 766 */ while (tc->AbsoluteLength > tc->BaseOffset + (uint64_t)NewBufferLength) {
        /* LINE 767 */     NewBufferLength <<= 1;
                           ++iters;
                    }
        alarm(0);
    }

    clock_t t1 = clock();
    double elapsed = (double)(t1 - t0) / CLOCKS_PER_SEC;

    if (timed_out) {
        printf("  RESULT: *** INFINITE LOOP — server thread hangs (DoS confirmed) ***\n");
        printf("          Loop ran for 3 seconds before watchdog fired.\n");
        printf("          NewBufferLength stuck at: 0x%08X  iterations: %llu\n\n",
               NewBufferLength, (unsigned long long)iters);
    } else {
        printf("  RESULT: terminates normally — NewBufferLength=0x%08X  iters=%llu  %.6fs\n\n",
               NewBufferLength, (unsigned long long)iters, elapsed);
    }
}

int main(void) {
    signal(SIGALRM, alarm_handler);

    printf("=================================================================\n");
    printf("  msquic QuicRecvBufferWrite doubling-loop — exact-type harness\n");
    printf("  Source: src/core/recv_buffer.c lines 763-772 (HEAD c62a7e17)\n");
    printf("=================================================================\n\n");

    /* ------------------------------------------------------------------ *
     * 1. SAFE: default .NET 10 / msquic settings                         *
     *    ConnFlowControlWindow = 16 MB → VBL never grows past 16 MB      *
     *    LastChunk can reach at most 8 MB before final resize to 16 MB   *
     * ------------------------------------------------------------------ */
    TestCase safe = {
        "SAFE — default .NET 10 (ConnFC=16MB, chunk=8MB)",
        /* BaseOffset */ 0,
        /* VBL        */ 0x01000000u,          /* 16 MB  */
        /* LastChunk  */ 0x00800000u,          /* 8 MB   */
        /* AbsLen     */ 0x01000000u,          /* write to end of window */
    };
    run(&safe);

    /* ------------------------------------------------------------------ *
     * 2. EDGE: 1 GB window (non-default but plausible media server)       *
     * ------------------------------------------------------------------ */
    TestCase edge = {
        "EDGE — 1 GB window  (chunk=512 MB)",
        0,
        0x40000000u,   /* 1 GB  */
        0x20000000u,   /* 512 MB */
        0x40000000u,
    };
    run(&edge);

    /* ------------------------------------------------------------------ *
     * 3. VULNERABLE: ConnFlowControlWindow set to UINT32_MAX             *
     *    No upper clamp in settings.c — app can pass 0xFFFFFFFF          *
     *    VBL auto-tunes upward via QuicStreamOnBytesDelivered:            *
     *       NewLength = CXPLAT_MIN(VBL*2, UINT32_MAX)                   *
     *    so VBL reaches 0xFFFFFFFF when ConnFC = UINT32_MAX              *
     *    Attack: send ONE STREAM frame at offset = UINT32_MAX-1          *
     *    (QUIC allows sparse offsets; attacker does NOT need to send 4GB) *
     * ------------------------------------------------------------------ */
    TestCase vuln = {
        "VULNERABLE — ConnFC=UINT32_MAX, chunk=0x80000000 (attacker 1 frame)",
        0,
        0xFFFFFFFFu,           /* VBL = UINT32_MAX — allowed window      */
        0x80000000u,           /* 2 GB chunk: 0x80000000 << 1 = 0        */
        0xFFFFFFFFull,         /* attacker writes 1 byte at offset UINT32_MAX-1 */
    };
    run(&vuln);

    /* ------------------------------------------------------------------ *
     * 4. ALSO VULNERABLE: smaller trigger — chunk=1 GB                   *
     * ------------------------------------------------------------------ */
    TestCase vuln2 = {
        "VULNERABLE — ConnFC=UINT32_MAX, chunk=0x40000000",
        0,
        0xFFFFFFFFu,
        0x40000000u,           /* 1 GB: shifts -> 0x80000000 -> 0 -> loop */
        0xFFFFFFFFull,
    };
    run(&vuln2);

    /* ------------------------------------------------------------------ *
     * 5. NEAR-MISS: ConnFC = 0x80000000 (2 GB)                          *
     *    VBL can reach 0x80000000. Chunk = 0x40000000.                   *
     *    0x40000000 << 1 = 0x80000000 — exactly satisfies the while      *
     *    condition: AbsLen (0x80000000) > BaseOffset + 0x80000000? NO.   *
     *    So this terminates on the first iteration. Safe.                 *
     * ------------------------------------------------------------------ */
    TestCase nearmiss = {
        "NEAR-MISS — ConnFC=2GB, VBL=2GB, chunk=1GB (safe by 1 bit)",
        0,
        0x80000000u,
        0x40000000u,
        0x80000000ull,
    };
    run(&nearmiss);

    printf("=================================================================\n");
    printf("Summary:\n");
    printf("  Default .NET 10 settings (16 MB FC window): NOT exploitable.\n");
    printf("  App sets ConnFlowControlWindow >= 2^32-1  : EXPLOITABLE.\n");
    printf("  Attack cost: single STREAM frame (1 byte at large offset).\n");
    printf("  Effect: server receive-loop thread spins at 100%% CPU forever.\n");
    printf("=================================================================\n");
    return 0;
}

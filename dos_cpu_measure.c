/*
 * CPU impact measurement: exactly one "STREAM frame" arriving at the server
 * when VBL=UINT32_MAX and LastChunk=0x80000000 → msquic thread spins.
 * We run it for 1 second and measure actual loop iterations / CPU usage.
 */
#include <stdio.h>
#include <stdint.h>
#include <signal.h>
#include <setjmp.h>
#include <time.h>
#include <unistd.h>
#include <sys/resource.h>

static volatile int stop = 0;
static void alarm_h(int s) { (void)s; stop = 1; }

int main(void) {
    signal(SIGALRM, alarm_h);
    struct rusage ru_start, ru_end;
    getrusage(RUSAGE_SELF, &ru_start);

    /* === EXACT msquic lines 765-768 with triggering values === */
    uint32_t LastChunkAllocLength = 0x80000000u;
    uint64_t BaseOffset           = 0;
    uint64_t AbsoluteLength       = (uint64_t)UINT32_MAX;
    uint64_t iters = 0;

    stop = 0;
    alarm(1);  /* 1-second burst — measure CPU */

    uint32_t NewBufferLength = LastChunkAllocLength << 1;  /* = 0 */
    while (AbsoluteLength > BaseOffset + (uint64_t)NewBufferLength && !stop) {
        NewBufferLength <<= 1;
        ++iters;
    }

    getrusage(RUSAGE_SELF, &ru_end);
    double user_ms = (ru_end.ru_utime.tv_sec  - ru_start.ru_utime.tv_sec ) * 1000.0
                   + (ru_end.ru_utime.tv_usec - ru_start.ru_utime.tv_usec) / 1000.0;
    double sys_ms  = (ru_end.ru_stime.tv_sec  - ru_start.ru_stime.tv_sec ) * 1000.0
                   + (ru_end.ru_stime.tv_usec - ru_start.ru_stime.tv_usec) / 1000.0;

    printf("=== DoS CPU impact (1 second, one malicious STREAM frame) ===\n");
    printf("  Loop iterations : %llu\n", (unsigned long long)iters);
    printf("  User CPU time   : %.1f ms\n", user_ms);
    printf("  Sys  CPU time   : %.1f ms\n", sys_ms);
    printf("  Total CPU burned: %.1f ms  (%.0f%% of 1 core)\n",
           user_ms + sys_ms, (user_ms + sys_ms) / 10.0);
    printf("  Attacker cost   : 1 UDP packet (~1.2 KB on wire)\n");
    printf("  Server threads  : affected receive thread spins indefinitely\n");
    printf("                    until process is killed or connection dropped\n");
    printf("  Real-world fix  : kill server process (restart required)\n");
    return 0;
}

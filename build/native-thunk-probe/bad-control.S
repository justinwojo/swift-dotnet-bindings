// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// Linked only into the opt-in host control executable, never any framework.
.text
#if defined(__arm64__)
.p2align 2
.globl _bt_native_thunk_bad
_bt_native_thunk_bad:
    mov x20, #0
    mov w0, #22
    ret
#elif defined(__x86_64__)
.p2align 4
.globl _bt_native_thunk_bad
_bt_native_thunk_bad:
    xorq %r13, %r13
    movl $22, %eax
    retq
#else
#error Unsupported probe architecture
#endif

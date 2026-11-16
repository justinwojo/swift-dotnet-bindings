// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// uint64_t bt_native_thunk_sentinel(void *fn, void *self, int32_t delta, int32_t *result,
//                                   void *errorOut).
// Calls fn(delta, self, errorOut) with every callee-saved register set to a known value and
// returns a mask of what the callee failed to preserve: bit 0 rbx, bits 2-5 r12-r15, bit 10 the
// frame or stack pointer. A non-throwing thunk ignores errorOut.
//
// The sentinel builds the canonical frame (rbp over the return address, CFA = rbp + 16, the saved
// registers below it) and describes it with CFI, so an unwinder walking out of the callee reaches
// the sentinel's frame and through it the caller. bt_native_thunk_sentinel_sp records rsp at the
// call (see sentinel-arm64.S).
.text
.p2align 4
.globl _bt_native_thunk_sentinel
.private_extern _bt_native_thunk_sentinel_sp
_bt_native_thunk_sentinel:
    .cfi_startproc
    pushq %rbp
    .cfi_def_cfa_offset 16
    .cfi_offset %rbp, -16
    movq %rsp, %rbp
    .cfi_def_cfa_register %rbp
    pushq %r15
    pushq %r14
    pushq %r13
    pushq %r12
    pushq %rbx
    .cfi_offset %rbx, -56
    .cfi_offset %r12, -48
    .cfi_offset %r13, -40
    .cfi_offset %r14, -32
    .cfi_offset %r15, -24
    subq $8, %rsp
    movq %rcx, (%rsp)
    movq %rdi, %r11
    movl %edx, %edi
    movq %r8, %rdx
    movq %rsp, _bt_native_thunk_sentinel_sp(%rip)
    movq $768, %rbx
    movq $770, %r12
    movq $771, %r13
    movq $772, %r14
    movq $773, %r15
    callq *%r11
    movq (%rsp), %rcx
    movl %eax, (%rcx)
    xorq %rax, %rax
    cmpq $768, %rbx
    setne %cl
    movzbq %cl, %rcx
    shlq $0, %rcx
    orq %rcx, %rax
    cmpq $770, %r12
    setne %cl
    movzbq %cl, %rcx
    shlq $2, %rcx
    orq %rcx, %rax
    cmpq $771, %r13
    setne %cl
    movzbq %cl, %rcx
    shlq $3, %rcx
    orq %rcx, %rax
    cmpq $772, %r14
    setne %cl
    movzbq %cl, %rcx
    shlq $4, %rcx
    orq %rcx, %rax
    cmpq $773, %r15
    setne %cl
    movzbq %cl, %rcx
    shlq $5, %rcx
    orq %rcx, %rax
    // rsp and rbp must still be the sentinel's: rsp = rbp - 48 (five pushes and the local slot).
    leaq -48(%rbp), %rcx
    cmpq %rcx, %rsp
    setne %cl
    movzbq %cl, %rcx
    shlq $10, %rcx
    orq %rcx, %rax
    addq $8, %rsp
    popq %rbx
    popq %r12
    popq %r13
    popq %r14
    popq %r15
    popq %rbp
    retq
    .cfi_endproc

.zerofill __DATA,__bss,_bt_native_thunk_sentinel_sp,8,3

// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

.text
.p2align 4
.globl _bt_native_thunk_sentinel
_bt_native_thunk_sentinel:
    pushq %rbx
    pushq %rbp
    pushq %r12
    pushq %r13
    pushq %r14
    pushq %r15
    subq $40, %rsp
    movq %rcx, (%rsp)
    movq %rsp, 8(%rsp)
    movq %rdi, %rax
    movl %edx, %edi
    movq $768, %rbx
    movq $769, %rbp
    movq $770, %r12
    movq $771, %r13
    movq $772, %r14
    movq $773, %r15
    callq *%rax
    movq (%rsp), %rcx
    movl %eax, (%rcx)
    xorq %rax, %rax
    cmpq $768, %rbx
    setne %cl
    movzbq %cl, %rcx
    shlq $0, %rcx
    orq %rcx, %rax
    cmpq $769, %rbp
    setne %cl
    movzbq %cl, %rcx
    shlq $1, %rcx
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
    cmpq %rsp, 8(%rsp)
    setne %cl
    movzbq %cl, %rcx
    shlq $6, %rcx
    orq %rcx, %rax
    addq $40, %rsp
    popq %r15
    popq %r14
    popq %r13
    popq %r12
    popq %rbp
    popq %rbx
    retq

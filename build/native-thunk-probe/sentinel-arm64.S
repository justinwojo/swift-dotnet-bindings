// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// uint64_t bt_native_thunk_sentinel(void *fn, void *self, int32_t delta, int32_t *result).
// Low ten bits: x19-x28; bit 10: frame/stack; bits 12-19: low d8-d15.
// x18 is the platform register and is never read or written.
.text
.p2align 2
.globl _bt_native_thunk_sentinel
_bt_native_thunk_sentinel:
    sub sp, sp, #192
    stp x19, x20, [sp, #0]
    stp x21, x22, [sp, #16]
    stp x23, x24, [sp, #32]
    stp x25, x26, [sp, #48]
    stp x27, x28, [sp, #64]
    stp x29, x30, [sp, #80]
    stp d8, d9, [sp, #96]
    stp d10, d11, [sp, #112]
    stp d12, d13, [sp, #128]
    stp d14, d15, [sp, #144]
    str x3, [sp, #160]
    mov x16, x0
    mov w0, w2
    mov x29, sp
    mov x19, #275
    mov x20, #276
    mov x21, #277
    mov x22, #278
    mov x23, #279
    mov x24, #280
    mov x25, #281
    mov x26, #282
    mov x27, #283
    mov x28, #284
    mov x9, #520
    fmov d8, x9
    mov x9, #521
    fmov d9, x9
    mov x9, #522
    fmov d10, x9
    mov x9, #523
    fmov d11, x9
    mov x9, #524
    fmov d12, x9
    mov x9, #525
    fmov d13, x9
    mov x9, #526
    fmov d14, x9
    mov x9, #527
    fmov d15, x9
    blr x16
    ldr x9, [sp, #160]
    str w0, [x9]
    mov x0, #0
    cmp x19, #275
    cset x9, ne
    orr x0, x0, x9, lsl #0
    cmp x20, #276
    cset x9, ne
    orr x0, x0, x9, lsl #1
    cmp x21, #277
    cset x9, ne
    orr x0, x0, x9, lsl #2
    cmp x22, #278
    cset x9, ne
    orr x0, x0, x9, lsl #3
    cmp x23, #279
    cset x9, ne
    orr x0, x0, x9, lsl #4
    cmp x24, #280
    cset x9, ne
    orr x0, x0, x9, lsl #5
    cmp x25, #281
    cset x9, ne
    orr x0, x0, x9, lsl #6
    cmp x26, #282
    cset x9, ne
    orr x0, x0, x9, lsl #7
    cmp x27, #283
    cset x9, ne
    orr x0, x0, x9, lsl #8
    cmp x28, #284
    cset x9, ne
    orr x0, x0, x9, lsl #9
    mov x9, sp
    cmp x29, x9
    cset x9, ne
    orr x0, x0, x9, lsl #10
    fmov x9, d8
    cmp x9, #520
    cset x9, ne
    orr x0, x0, x9, lsl #12
    fmov x9, d9
    cmp x9, #521
    cset x9, ne
    orr x0, x0, x9, lsl #13
    fmov x9, d10
    cmp x9, #522
    cset x9, ne
    orr x0, x0, x9, lsl #14
    fmov x9, d11
    cmp x9, #523
    cset x9, ne
    orr x0, x0, x9, lsl #15
    fmov x9, d12
    cmp x9, #524
    cset x9, ne
    orr x0, x0, x9, lsl #16
    fmov x9, d13
    cmp x9, #525
    cset x9, ne
    orr x0, x0, x9, lsl #17
    fmov x9, d14
    cmp x9, #526
    cset x9, ne
    orr x0, x0, x9, lsl #18
    fmov x9, d15
    cmp x9, #527
    cset x9, ne
    orr x0, x0, x9, lsl #19
    ldp d8, d9, [sp, #96]
    ldp d10, d11, [sp, #112]
    ldp d12, d13, [sp, #128]
    ldp d14, d15, [sp, #144]
    ldp x19, x20, [sp, #0]
    ldp x21, x22, [sp, #16]
    ldp x23, x24, [sp, #32]
    ldp x25, x26, [sp, #48]
    ldp x27, x28, [sp, #64]
    ldp x29, x30, [sp, #80]
    add sp, sp, #192
    ret

// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

// uint64_t bt_native_thunk_sentinel(void *fn, void *self, int32_t delta, int32_t *result,
//                                   void *errorOut).
// Calls fn(delta, self, errorOut) with every callee-saved register set to a known value and
// returns a mask of what the callee failed to preserve. Low ten bits: x19-x28; bit 10: frame
// pointer or stack pointer; bits 12-19: low d8-d15. A non-throwing thunk ignores errorOut.
//
// The sentinel builds the canonical frame (frame record at the top, CFA = fp + 16, the saved pairs
// below it) and describes it with CFI, so an unwinder walking out of the callee reaches the
// sentinel's frame and through it the caller. bt_native_thunk_sentinel_sp records sp at the call:
// the native-thunk probe compares it, and the callee-saved values an unwind recovers for this
// frame, with what an unwind from inside the thunk's target reports.
// x18 is the platform register and is never read or written.
.text
.p2align 2
.globl _bt_native_thunk_sentinel
.private_extern _bt_native_thunk_sentinel_sp
_bt_native_thunk_sentinel:
    .cfi_startproc
    stp x29, x30, [sp, #-16]!
    mov x29, sp
    .cfi_def_cfa w29, 16
    .cfi_offset w30, -8
    .cfi_offset w29, -16
    stp x20, x19, [sp, #-16]!
    stp x22, x21, [sp, #-16]!
    stp x24, x23, [sp, #-16]!
    stp x26, x25, [sp, #-16]!
    stp x28, x27, [sp, #-16]!
    stp d9, d8, [sp, #-16]!
    stp d11, d10, [sp, #-16]!
    stp d13, d12, [sp, #-16]!
    stp d15, d14, [sp, #-16]!
    .cfi_offset w19, -24
    .cfi_offset w20, -32
    .cfi_offset w21, -40
    .cfi_offset w22, -48
    .cfi_offset w23, -56
    .cfi_offset w24, -64
    .cfi_offset w25, -72
    .cfi_offset w26, -80
    .cfi_offset w27, -88
    .cfi_offset w28, -96
    .cfi_offset d8, -104
    .cfi_offset d9, -112
    .cfi_offset d10, -120
    .cfi_offset d11, -128
    .cfi_offset d12, -136
    .cfi_offset d13, -144
    .cfi_offset d14, -152
    .cfi_offset d15, -160
    sub sp, sp, #16
    str x3, [sp]
    mov x16, x0
    mov w0, w2
    mov x2, x4
    adrp x9, _bt_native_thunk_sentinel_sp@PAGE
    add x9, x9, _bt_native_thunk_sentinel_sp@PAGEOFF
    mov x10, sp
    str x10, [x9]
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
    ldr x9, [sp]
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
    // The frame pointer must still be the sentinel's: sp + 160 (nine pairs and the local slot).
    add x9, sp, #160
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
    add sp, sp, #16
    ldp d15, d14, [sp], #16
    ldp d13, d12, [sp], #16
    ldp d11, d10, [sp], #16
    ldp d9, d8, [sp], #16
    ldp x28, x27, [sp], #16
    ldp x26, x25, [sp], #16
    ldp x24, x23, [sp], #16
    ldp x22, x21, [sp], #16
    ldp x20, x19, [sp], #16
    ldp x29, x30, [sp], #16
    ret
    .cfi_endproc

.zerofill __DATA,__bss,_bt_native_thunk_sentinel_sp,8,3

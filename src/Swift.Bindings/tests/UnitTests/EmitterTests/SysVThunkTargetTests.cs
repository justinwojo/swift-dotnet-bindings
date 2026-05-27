// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests
{
    /// <summary>
    /// Tests for the x86_64 (System V AMD64 + swiftcc) thunk backend. The descriptor is
    /// arch-neutral and shared with the ARM64 path; these tests assert the SysV register
    /// mapping, the hidden-sret argument shift, the swiftself/swifterror register choices,
    /// and the encodability guard.
    /// </summary>
    public class SysVThunkTargetTests
    {
        private static string EmitX64(ThunkDescriptor descriptor) =>
            ThunkAssemblyEmitter.EmitThunk(descriptor, ThunkTargetArch.X86_64);

        #region File Header / Symbol Declaration

        [Fact]
        public void EmitFileHeader_ContainsX86Section()
        {
            var sb = new System.Text.StringBuilder();
            ThunkTargetArch.X86_64.EmitFileHeader(sb, "TestModule");
            var header = sb.ToString();

            Assert.Contains("Native x86_64 thunks for TestModule", header);
            Assert.Contains(".text", header);
        }

        [Fact]
        public void ArchTag_IsX86_64()
        {
            Assert.Equal("x86_64", ThunkTargetArch.X86_64.ArchTag);
        }

        [Fact]
        public void EmitThunk_SymbolDecl_UsesX86Alignment()
        {
            var asm = EmitX64(FreeVoid("thunk_t_simple", "_$s4Test6simpleyyF"));

            Assert.Contains(".globl _thunk_t_simple", asm);
            Assert.Contains(".p2align 4, 0x90", asm);
            Assert.Contains("_thunk_t_simple:", asm);
        }

        #endregion

        #region Tail Call

        [Fact]
        public void EmitThunk_TrivialForward_EmitsJmp()
        {
            var asm = EmitX64(FreeVoid("thunk_t_simple", "_$s4Test6simpleyyF"));

            // No bridging → jump straight to the Swift symbol; no frame.
            Assert.Contains("jmp     _$s4Test6simpleyyF", asm);
            Assert.DoesNotContain("callq", asm);
            Assert.DoesNotContain("pushq   %rbp", asm);
        }

        [Fact]
        public void EmitThunk_IndirectReturnNoSelf_EmitsJmp()
        {
            // Struct returned indirectly in BOTH conventions: cdecl sret in %rdi passes straight
            // through to swiftcc's %rdi sret. No self/error → tail call.
            var descriptor = Free("thunk_t_big", "_$s4Test7makeBigAA0D0VyF",
                ReturnIndirect(40), parameterCount: 0);

            var asm = EmitX64(descriptor);

            Assert.Contains("jmp     _$s4Test7makeBigAA0D0VyF", asm);
            Assert.DoesNotContain("movq    %rdi, %rbx", asm);
        }

        #endregion

        #region Instance Method (self in %r13)

        [Fact]
        public void EmitThunk_InstanceMethod_MovesSelfToR13()
        {
            // 1 value param + self: cdecl [value=rdi, self=rsi]. self → %r13; value stays in %rdi.
            var descriptor = Instance("thunk_t_add", "_$s4Test7CounterC3addyS2iF",
                ReturnInt(), parameterCount: 1);

            var asm = EmitX64(descriptor);

            Assert.Contains("movq    %rsi, %r13", asm);   // self at cdecl index 1
            Assert.Contains("callq   _$s4Test7CounterC3addyS2iF", asm);
            Assert.Contains("retq", asm);
            Assert.DoesNotContain("jmp", asm);
            // No hidden sret (8-byte return) → no argument shift.
            Assert.DoesNotContain("movq    %rsi, %rdi", asm);
        }

        [Fact]
        public void EmitThunk_InstanceMethodZeroParams_SelfInRdi()
        {
            // 0 params + self: cdecl [self=rdi]. self → %r13.
            var descriptor = Instance("thunk_t_getter", "_$s4Test3FooC8getValueSiyF",
                ReturnInt(), parameterCount: 0);

            var asm = EmitX64(descriptor);

            Assert.Contains("movq    %rdi, %r13", asm);
        }

        [Fact]
        public void EmitThunk_InstanceMethodWithStructReturn_BridgeShiftsPastSret()
        {
            // Instance method returning a 24-byte struct: cdecl [sret=rdi, self=rsi].
            // self is read from %rsi (shifted by the hidden sret); the buffer is stashed in %rbx.
            var descriptor = Instance("thunk_t_getVec", "_$s4Test3FooC6getVecAA4Vec3VyF",
                Return3Int(24), parameterCount: 0);

            var asm = EmitX64(descriptor);

            Assert.Contains("movq    %rdi, %rbx", asm);    // stash sret buffer
            Assert.Contains("movq    %rsi, %r13", asm);    // self at cdecl index 1 (after sret)
            Assert.Contains("movq    %rax, (%rbx)", asm);  // return store
            Assert.Contains("movq    %rbx, %rax", asm);    // return sret pointer in %rax
        }

        #endregion

        #region Struct Return Bridge (17-32 bytes)

        [Fact]
        public void EmitThunk_Rect32Bytes_BridgesAndShifts()
        {
            // Rect{ x,y,w,h:Int } = 32B. cdecl [sret=rdi, a0=rsi, a1=rdx, a2=rcx, a3=r8].
            // swiftcc wants args in rdi.. so each integer arg shifts down one register.
            var ret = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8),
                    new RegisterSlot(RegisterFile.Integer, 3, 8),
                },
                IsIndirect: false, TotalByteSize: 32);

            var descriptor = Free("thunk_t_rect", "_$s4Test8makeRectAA0D0VyF", ret, parameterCount: 4);
            var asm = EmitX64(descriptor);

            Assert.Contains("movq    %rdi, %rbx", asm);   // stash sret
            // Argument shift (ascending, no clobber):
            Assert.Contains("movq    %rsi, %rdi", asm);
            Assert.Contains("movq    %rdx, %rsi", asm);
            Assert.Contains("movq    %rcx, %rdx", asm);
            Assert.Contains("movq    %r8, %rcx", asm);
            Assert.Contains("callq   _$s4Test8makeRectAA0D0VyF", asm);
            // Return registers (rax, rdx, rcx, r8) → buffer.
            Assert.Contains("movq    %rax, (%rbx)", asm);
            Assert.Contains("movq    %rdx, 8(%rbx)", asm);
            Assert.Contains("movq    %rcx, 16(%rbx)", asm);
            Assert.Contains("movq    %r8, 24(%rbx)", asm);
            Assert.Contains("movq    %rbx, %rax", asm);
        }

        [Fact]
        public void EmitThunk_MixedIntFloatReturn_UsesIntAndXmmRegs()
        {
            // { intVal:Int, floatVal:Double, intVal2:Int, floatVal2:Double } = 32B.
            // swiftcc returns field-wise: rax, xmm0, rdx, xmm1.
            var ret = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Float, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8),
                },
                IsIndirect: false, TotalByteSize: 32);

            var descriptor = Free("thunk_t_mixed", "_$s4Test9makeMixedAA0D0VyF", ret, parameterCount: 4);
            var asm = EmitX64(descriptor);

            Assert.Contains("movq    %rax, (%rbx)", asm);       // intVal @ 0
            Assert.Contains("movsd   %xmm0, 8(%rbx)", asm);     // floatVal @ 8
            Assert.Contains("movq    %rdx, 16(%rbx)", asm);     // intVal2 @ 16
            Assert.Contains("movsd   %xmm1, 24(%rbx)", asm);    // floatVal2 @ 24
        }

        [Fact]
        public void EmitThunk_FloatOnlyReturn_UsesXmmRegs()
        {
            // { x,y,z:Double } = 24B → swiftcc returns in xmm0, xmm1, xmm2.
            var ret = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Float, 0, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8),
                    new RegisterSlot(RegisterFile.Float, 2, 8),
                },
                IsIndirect: false, TotalByteSize: 24);

            var descriptor = Free("thunk_t_vecf", "_$s4Test9makeVecfAA0D0VyF", ret, parameterCount: 0);
            var asm = EmitX64(descriptor);

            Assert.Contains("movsd   %xmm0, (%rbx)", asm);
            Assert.Contains("movsd   %xmm1, 8(%rbx)", asm);
            Assert.Contains("movsd   %xmm2, 16(%rbx)", asm);
        }

        [Fact]
        public void EmitThunk_MixedWidthReturn_WidthCorrectStoresAtNaturalOffsets()
        {
            // Mixed { i:Int32, f:Float, j:Int64, d:Double } = 24B, natural offsets 0,4,8,16.
            // swiftcc returns field-wise: eax, xmm0, rdx, xmm1. The store must use width-matched
            // instructions/sub-registers at the packed offsets, not an 8-byte stride.
            var ret = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 4),
                    new RegisterSlot(RegisterFile.Float, 0, 4),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8),
                },
                IsIndirect: false, TotalByteSize: 24);

            var descriptor = Free("thunk_t_mixedw", "_$s4Test14makeMixedWidthAA0E0VyF", ret, parameterCount: 0);
            var asm = EmitX64(descriptor);

            Assert.Contains("movl    %eax, (%rbx)", asm);     // Int32 @ 0 (32-bit store)
            Assert.Contains("movss   %xmm0, 4(%rbx)", asm);   // Float @ 4 (32-bit float store)
            Assert.Contains("movq    %rdx, 8(%rbx)", asm);    // Int64 @ 8
            Assert.Contains("movsd   %xmm1, 16(%rbx)", asm);  // Double @ 16
            // The buggy 8-byte stride would have stored xmm0/rdx at 8/16 with full width.
            Assert.DoesNotContain("movsd   %xmm0, 8(%rbx)", asm);
        }

        [Fact]
        public void EmitThunk_NarrowIntegerReturn_UsesSizedSubRegisters()
        {
            // Packed integers of 1/2/4/8 bytes at natural offsets 0,2,4,8 → al/dx/ecx/r8.
            var ret = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 1),
                    new RegisterSlot(RegisterFile.Integer, 1, 2),
                    new RegisterSlot(RegisterFile.Integer, 2, 4),
                    new RegisterSlot(RegisterFile.Integer, 3, 8),
                },
                IsIndirect: false, TotalByteSize: 24);

            var descriptor = Free("thunk_t_narrow", "_$s4Test10makeNarrowAA0D0VyF", ret, parameterCount: 0);
            var asm = EmitX64(descriptor);

            Assert.Contains("movb    %al, (%rbx)", asm);     // 1 byte @ 0
            Assert.Contains("movw    %dx, 2(%rbx)", asm);    // 2 bytes @ 2
            Assert.Contains("movl    %ecx, 4(%rbx)", asm);   // 4 bytes @ 4
            Assert.Contains("movq    %r8, 8(%rbx)", asm);    // 8 bytes @ 8
        }

        #endregion

        #region Constructor / Static (metatype in %r13)

        [Fact]
        public void EmitThunk_Constructor_CallsMetadataAccessorIntoR13()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_t_Foo_init",
                SwiftSymbol: "_$s4Test3FooCACycfC",
                ReturnLowering: ReturnInt(),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = EmitX64(descriptor);

            Assert.Contains("xorl    %edi, %edi", asm);          // request = 0
            Assert.Contains("callq   _$s4Test3FooCMa", asm);     // metadata accessor
            Assert.Contains("movq    %rax, %r13", asm);          // metatype → swiftself
            Assert.Contains("callq   _$s4Test3FooCACycfC", asm); // allocating init
        }

        [Fact]
        public void EmitThunk_ConstructorWithParam_SpillsAcrossAccessor()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_t_Foo_initVal",
                SwiftSymbol: "_$s4Test3FooC5valueSi_tcfC",
                ReturnLowering: ReturnInt(),
                SelfLowering: null,
                ParameterCount: 1,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = EmitX64(descriptor);

            Assert.Contains("subq    $16, %rsp", asm);        // 16-byte aligned spill
            Assert.Contains("movq    %rdi, (%rsp)", asm);     // save arg before accessor
            Assert.Contains("callq   _$s4Test3FooCMa", asm);
            Assert.Contains("movq    (%rsp), %rdi", asm);     // restore arg after accessor
            Assert.Contains("addq    $16, %rsp", asm);
        }

        [Fact]
        public void EmitThunk_ConstructorWithFloatParam_SpillsXmm()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_t_Foo_initF",
                SwiftSymbol: "_$s4Test3FooC1xSdcfC",
                ReturnLowering: ReturnInt(),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 1,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = EmitX64(descriptor);

            Assert.Contains("movsd   %xmm0, (%rsp)", asm);
            Assert.Contains("movsd   (%rsp), %xmm0", asm);
        }

        [Fact]
        public void EmitThunk_StaticMethod_CallsMetadataAccessorIntoR13()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_t_Foo_create",
                SwiftSymbol: "_$s4Test3FooC6createACyFZ",
                ReturnLowering: ReturnInt(),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: true,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = EmitX64(descriptor);

            Assert.Contains("callq   _$s4Test3FooCMa", asm);
            Assert.Contains("movq    %rax, %r13", asm);
            Assert.Contains("callq   _$s4Test3FooC6createACyFZ", asm);
        }

        #endregion

        #region Throwing (swifterror in %r12)

        [Fact]
        public void EmitThunk_ThrowingFreeFunction_ClearsAndCapturesR12()
        {
            // Throwing free fn, 2 int params, returns Int: cdecl [a0=rdi, a1=rsi, errorOut=rdx].
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_t_divide",
                SwiftSymbol: "_$s4Test6divideyS2i_SitKF",
                ReturnLowering: ReturnInt(),
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: true,
                MetadataAccessorSymbol: null);

            var asm = EmitX64(descriptor);

            Assert.Contains("movq    %rdx, -32(%rbp)", asm);  // stash error-out pointer (index 2)
            Assert.Contains("xorl    %r12d, %r12d", asm);     // clear swifterror
            Assert.Contains("callq   _$s4Test6divideyS2i_SitKF", asm);
            Assert.Contains("movq    -32(%rbp), %r10", asm);  // reload error-out pointer
            Assert.Contains("movq    %r12, (%r10)", asm);     // write swifterror
        }

        [Fact]
        public void EmitThunk_ThrowingInstanceMethod_ErrorAfterSelf()
        {
            // Throwing instance method, 2 int params: cdecl [a0=rdi, a1=rsi, self=rdx, errorOut=rcx].
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_t_obj_div",
                SwiftSymbol: "_$s4Test3FooC6divideyS2i_SitKF",
                ReturnLowering: ReturnInt(),
                SelfLowering: ReturnInt(),
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: true,
                MetadataAccessorSymbol: null);

            var asm = EmitX64(descriptor);

            Assert.Contains("movq    %rdx, %r13", asm);        // self at index 2
            Assert.Contains("movq    %rcx, -32(%rbp)", asm);   // error-out at index 3
        }

        #endregion

        #region Encodability Guard (CanEmit)

        [Fact]
        public void CanEmit_WithinRegisterFile_True()
        {
            // sret + 4 args + 0 self + 0 error = 5 integer registers ≤ 6.
            var ret = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8),
                    new RegisterSlot(RegisterFile.Integer, 3, 8),
                },
                IsIndirect: false, TotalByteSize: 32);
            var descriptor = Free("thunk_t_ok", "_$sOk", ret, parameterCount: 4);

            Assert.True(ThunkTargetArch.X86_64.CanEmit(descriptor));
        }

        [Fact]
        public void CanEmit_TooManyIntegerArgs_False()
        {
            // 7 integer args > 6 SysV integer registers → cannot encode; falls back to @_cdecl.
            var descriptor = Free("thunk_t_wide", "_$sWide", ReturnInt(), parameterCount: 7);

            Assert.False(ThunkTargetArch.X86_64.CanEmit(descriptor));
            // ARM64 is unaffected by the SysV register cap.
            Assert.True(ThunkTargetArch.Arm64.CanEmit(descriptor));
        }

        [Fact]
        public void CanEmit_SretPlusArgsPlusSelfPlusError_RespectsCap()
        {
            // sret(1) + 4 args + self(1) + error(1) = 7 > 6 → cannot encode.
            var ret = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8),
                    new RegisterSlot(RegisterFile.Integer, 3, 8),
                },
                IsIndirect: false, TotalByteSize: 32);
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "thunk_t_full",
                SwiftSymbol: "_$sFull",
                ReturnLowering: ret,
                SelfLowering: ReturnInt(),
                ParameterCount: 4,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: true,
                MetadataAccessorSymbol: null);

            Assert.False(ThunkTargetArch.X86_64.CanEmit(descriptor));
        }

        #endregion

        #region Arch Equivalence / Byte-Identity Guard

        [Fact]
        public void Arm64Overload_MatchesExplicitArm64Target()
        {
            // The back-compat no-arg overload must be byte-identical to the explicit ARM64 target.
            var descriptor = Instance("thunk_t_add", "_$s4Test7CounterC3addyS2iF", ReturnInt(), 1);

            var implicitArm64 = ThunkAssemblyEmitter.EmitThunk(descriptor);
            var explicitArm64 = ThunkAssemblyEmitter.EmitThunk(descriptor, ThunkTargetArch.Arm64);

            Assert.Equal(explicitArm64, implicitArm64);
        }

        [Fact]
        public void Arm64AndX86_64_DifferInBranchMnemonic()
        {
            var descriptor = FreeVoid("thunk_t_simple", "_$s4Test6simpleyyF");

            var arm64 = ThunkAssemblyEmitter.EmitThunk(descriptor, ThunkTargetArch.Arm64);
            var x64 = ThunkAssemblyEmitter.EmitThunk(descriptor, ThunkTargetArch.X86_64);

            Assert.Contains("b       _$s4Test6simpleyyF", arm64);
            Assert.Contains("jmp     _$s4Test6simpleyyF", x64);
            Assert.NotEqual(arm64, x64);
        }

        #endregion

        #region Helpers

        private static TypeLoweringResult ReturnInt() => new(
            new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
            IsIndirect: false, TotalByteSize: 8);

        private static TypeLoweringResult Return3Int(int totalBytes) => new(
            new[] {
                new RegisterSlot(RegisterFile.Integer, 0, 8),
                new RegisterSlot(RegisterFile.Integer, 1, 8),
                new RegisterSlot(RegisterFile.Integer, 2, 8),
            },
            IsIndirect: false, TotalByteSize: totalBytes);

        private static TypeLoweringResult ReturnIndirect(int totalBytes) => new(
            new[] {
                new RegisterSlot(RegisterFile.Integer, 0, 8),
                new RegisterSlot(RegisterFile.Integer, 1, 8),
                new RegisterSlot(RegisterFile.Integer, 2, 8),
                new RegisterSlot(RegisterFile.Integer, 3, 8),
                new RegisterSlot(RegisterFile.Integer, 4, 8),
            },
            IsIndirect: true, TotalByteSize: totalBytes);

        private static ThunkDescriptor FreeVoid(string thunkSymbol, string swiftSymbol) =>
            Free(thunkSymbol, swiftSymbol, returnLowering: null, parameterCount: 0);

        private static ThunkDescriptor Free(string thunkSymbol, string swiftSymbol,
            TypeLoweringResult returnLowering, int parameterCount) =>
            new(
                ThunkSymbol: thunkSymbol,
                SwiftSymbol: swiftSymbol,
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: parameterCount,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

        private static ThunkDescriptor Instance(string thunkSymbol, string swiftSymbol,
            TypeLoweringResult returnLowering, int parameterCount) =>
            new(
                ThunkSymbol: thunkSymbol,
                SwiftSymbol: swiftSymbol,
                ReturnLowering: returnLowering,
                SelfLowering: ReturnInt(),
                ParameterCount: parameterCount,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

        #endregion
    }
}

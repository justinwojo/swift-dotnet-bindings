// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using Xunit;

namespace BindingsGeneration.Tests
{
    public class ThunkAssemblyEmitterTests
    {
        #region File Header/Footer

        [Fact]
        public void EmitFileHeader_ContainsSectionDirectives()
        {
            var header = ThunkAssemblyEmitter.EmitFileHeader("TestModule");

            Assert.Contains(".text", header);
            Assert.Contains(".align 4", header);
            Assert.Contains("TestModule", header);
        }

        [Fact]
        public void EmitFileFooter_ReturnsEmpty()
        {
            var footer = ThunkAssemblyEmitter.EmitFileFooter();
            Assert.Equal(string.Empty, footer);
        }

        #endregion

        #region Tail Call (Trivial Forward)

        [Fact]
        public void EmitThunk_TrivialForward_EmitsTailCall()
        {
            // Free function, return ≤ 16 bytes, no self, no throws → tail call (b instruction)
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_simple",
                SwiftSymbol: "_$s4Test6simpleyyF",
                ReturnLowering: null, // void
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains(".globl _thunk_test_simple", asm);
            Assert.Contains("_thunk_test_simple:", asm);
            Assert.Contains("b       _$s4Test6simpleyyF", asm);
            // Tail call should NOT have ret, stp, ldp
            Assert.DoesNotContain("ret", asm);
            Assert.DoesNotContain("stp", asm);
        }

        [Fact]
        public void EmitThunk_ScalarReturn_EmitsTailCall()
        {
            // Free function returning Int (≤ 16 bytes, single register) → tail call
            var returnLowering = new TypeLoweringResult(
                new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                IsIndirect: false,
                TotalByteSize: 8);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_getInt",
                SwiftSymbol: "_$s4Test6getIntSiyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // ≤ 16 bytes return, no self/throws → tail call
            Assert.Contains("b       _$s4Test6getIntSiyF", asm);
            Assert.DoesNotContain("ret", asm);
        }

        [Fact]
        public void EmitThunk_PointReturn_16Bytes_EmitsTailCall()
        {
            // Point { x: Int, y: Int } = 16 bytes → cdecl returns in x0+x1, same as swiftcc → tail call
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8)
                },
                IsIndirect: false,
                TotalByteSize: 16);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_makePoint",
                SwiftSymbol: "_$s4Test9makePointAA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // 16 bytes ≤ 16 limit → tail call
            Assert.Contains("b       ", asm);
            Assert.DoesNotContain("mov     x19, x8", asm);
        }

        #endregion

        #region Struct Return Bridge (17-32 bytes)

        [Fact]
        public void EmitThunk_RectReturn_32Bytes_BridgesViaX8()
        {
            // Rect { x, y, w, h: Int } = 32 bytes → Swift uses x0-x3, cdecl uses x8
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8),
                    new RegisterSlot(RegisterFile.Integer, 3, 8)
                },
                IsIndirect: false,
                TotalByteSize: 32);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_makeRect",
                SwiftSymbol: "_$s4Test8makeRectAA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 4,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save x8 (return buffer) to x19
            Assert.Contains("mov     x19, x8", asm);
            // Must call, not tail-call
            Assert.Contains("bl      _$s4Test8makeRectAA0D0VyF", asm);
            // Must store return registers to buffer
            Assert.Contains("str     x0, [x19]", asm);
            Assert.Contains("str     x1, [x19, #8]", asm);
            Assert.Contains("str     x2, [x19, #16]", asm);
            Assert.Contains("str     x3, [x19, #24]", asm);
            // Must have prologue/epilogue
            Assert.Contains("stp     x20, x19, [sp, #-32]!", asm);
            Assert.Contains("ldp     x20, x19, [sp], #32", asm);
            Assert.Contains("ret", asm);
        }

        [Fact]
        public void EmitThunk_Vec3Return_24Bytes_BridgesViaX8()
        {
            // Vec3 { x, y, z: Int } = 24 bytes → Swift uses x0-x2, cdecl uses x8
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8)
                },
                IsIndirect: false,
                TotalByteSize: 24);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_makeVec3",
                SwiftSymbol: "_$s4Test8makeVec3AA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 3,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains("mov     x19, x8", asm);
            Assert.Contains("bl      ", asm);
            Assert.Contains("str     x0, [x19]", asm);
            Assert.Contains("str     x1, [x19, #8]", asm);
            Assert.Contains("str     x2, [x19, #16]", asm);
        }

        [Fact]
        public void EmitThunk_MixedIntFloat_InterleavedStores()
        {
            // MixedIntFloat { intVal: Int, floatVal: Double, intVal2: Int, floatVal2: Double }
            // Swift returns: x0=intVal, d0=floatVal, x1=intVal2, d1=floatVal2
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Float, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8)
                },
                IsIndirect: false,
                TotalByteSize: 32);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_makeMixed",
                SwiftSymbol: "_$s4Test9makeMixedAA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 4,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Verify interleaved int/float stores at correct offsets
            Assert.Contains("str     x0, [x19]", asm);        // intVal at offset 0
            Assert.Contains("str     d0, [x19, #8]", asm);    // floatVal at offset 8
            Assert.Contains("str     x1, [x19, #16]", asm);   // intVal2 at offset 16
            Assert.Contains("str     d1, [x19, #24]", asm);   // floatVal2 at offset 24
        }

        [Fact]
        public void EmitThunk_IndirectReturn_NoBridge()
        {
            // Struct > 32 bytes → indirect via x8 in BOTH conventions → no bridge needed → tail call
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8),
                    new RegisterSlot(RegisterFile.Integer, 3, 8),
                    new RegisterSlot(RegisterFile.Integer, 4, 8)
                },
                IsIndirect: true,
                TotalByteSize: 40);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_makeBig",
                SwiftSymbol: "_$s4Test7makeBigAA0D0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Indirect return: both conventions use x8 → tail call
            Assert.Contains("b       _$s4Test7makeBigAA0D0VyF", asm);
            Assert.DoesNotContain("mov     x19, x8", asm);
        }

        #endregion

        #region Instance Method (self in x20)

        [Fact]
        public void EmitThunk_InstanceMethod_MovesSelfToX20()
        {
            // Instance method: cdecl self is x0, Swift self is x20
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_counter_add",
                SwiftSymbol: "_$s4Test7CounterC3addyS2iF",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 1,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must move self from x0 to x20
            Assert.Contains("mov     x20, x0", asm);
            // Must shift parameter: x1 → x0
            Assert.Contains("mov     x0, x1", asm);
            // Must use bl (not tail call, since we modify x20)
            Assert.Contains("bl      _$s4Test7CounterC3addyS2iF", asm);
            Assert.Contains("ret", asm);
        }

        [Fact]
        public void EmitThunk_InstanceMethodMultipleParams_ShiftsAll()
        {
            // Instance method with 3 params: self=x0, p0=x1, p1=x2, p2=x3
            // After bridge: x20=self, x0=p0, x1=p1, x2=p2
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_method3",
                SwiftSymbol: "_$s4Test3FooCmethod3yyF",
                ReturnLowering: null,
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 3,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains("mov     x20, x0", asm);
            Assert.Contains("mov     x0, x1", asm);
            Assert.Contains("mov     x1, x2", asm);
            Assert.Contains("mov     x2, x3", asm);
        }

        [Fact]
        public void EmitThunk_InstanceMethodWithStructReturn_BothBridges()
        {
            // Instance method returning a 24-byte struct: needs BOTH self bridge AND return bridge
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8)
                },
                IsIndirect: false,
                TotalByteSize: 24);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_getVec",
                SwiftSymbol: "_$s4Test3FooC6getVecAA4Vec3VyF",
                ReturnLowering: returnLowering,
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Both bridges present
            Assert.Contains("mov     x19, x8", asm);    // return buffer save
            Assert.Contains("mov     x20, x0", asm);    // self → x20
            Assert.Contains("str     x0, [x19]", asm);  // return store
        }

        #endregion

        #region Constructor (metatype in x20)

        [Fact]
        public void EmitThunk_Constructor_CallsMetadataAccessor()
        {
            // Constructor: calls metadata accessor → x0, moves to x20, then calls init
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_Foo_init",
                SwiftSymbol: "_$s4Test3FooCACycfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must call metadata accessor
            Assert.Contains("bl      _$s4Test3FooCMa", asm);
            // Must move metatype to x20
            Assert.Contains("mov     x20, x0", asm);
            // Request = 0 (complete metadata)
            Assert.Contains("mov     x0, #0", asm);
            // Must call the allocating init
            Assert.Contains("bl      _$s4Test3FooCACycfC", asm);
        }

        [Fact]
        public void EmitThunk_ConstructorWithParams_SavesAndRestoresParams()
        {
            // Constructor with params: must save params before metadata accessor call,
            // then restore them before the init call
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_Foo_initVal",
                SwiftSymbol: "_$s4Test3FooC5valueSi_tcfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 1,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save parameter to stack before metadata accessor
            Assert.Contains("str     x0, [sp, #0]", asm);
            // Must call metadata accessor
            Assert.Contains("bl      _$s4Test3FooCMa", asm);
            // Must restore parameter from stack
            Assert.Contains("ldr     x0, [sp, #0]", asm);
        }

        #endregion

        #region Static Method (metatype in x20)

        [Fact]
        public void EmitThunk_StaticMethod_CallsMetadataAccessor()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_Foo_create",
                SwiftSymbol: "_$s4Test3FooC6createACyFZ",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: true,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains("bl      _$s4Test3FooCMa", asm);
            Assert.Contains("mov     x20, x0", asm);
            Assert.Contains("bl      _$s4Test3FooC6createACyFZ", asm);
        }

        #endregion

        #region Throwing Functions (swifterror in x21)

        [Fact]
        public void EmitThunk_ThrowingFunction_ClearsAndCapturesX21()
        {
            // Throwing free function: error out pointer is the last cdecl parameter
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_divide",
                SwiftSymbol: "_$s4Test6divideyS2i_SitKF",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: true,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must clear swifterror on entry
            Assert.Contains("mov     x21, xzr", asm);
            // Must save error out pointer (x2 = last param after 2 regular params)
            Assert.Contains("mov     x19, x2", asm);
            // Must capture swifterror after call
            Assert.Contains("str     x21, [x19]", asm);
            Assert.Contains("bl      _$s4Test6divideyS2i_SitKF", asm);
        }

        [Fact]
        public void EmitThunk_ThrowingInstanceMethod_ErrorAfterShiftedParams()
        {
            // Instance method that throws: cdecl is (self, param0, error_out)
            // error_out is at x2 (self=x0, param=x1, error=x2)
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_throwing_method",
                SwiftSymbol: "_$s4Test3FooC6methodySiSiKF",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                ParameterCount: 1,
                FloatParameterCount: 0,
                IsInstanceMethod: true,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: true,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Instance method: self → x20, param shift, plus error handling
            Assert.Contains("mov     x20, x0", asm);
            Assert.Contains("mov     x21, xzr", asm);
            // Error out at x2 (self=x0 + 1 param at x1 → error at x2)
            Assert.Contains("mov     x19, x2", asm);
            Assert.Contains("str     x21, [x19]", asm);
        }

        [Fact]
        public void EmitThunk_ThrowingWithStructReturn_BothBridges()
        {
            // Throwing function with struct return > 16 bytes: needs both return bridge AND error bridge
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Integer, 0, 8),
                    new RegisterSlot(RegisterFile.Integer, 1, 8),
                    new RegisterSlot(RegisterFile.Integer, 2, 8)
                },
                IsIndirect: false,
                TotalByteSize: 24);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_throwingReturnStruct",
                SwiftSymbol: "_$s4Test9getVec3OryAA0D0VyKF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: true,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save x8 return buffer to x19
            Assert.Contains("mov     x19, x8", asm);
            // Must clear swifterror
            Assert.Contains("mov     x21, xzr", asm);
            // Must store return values to buffer
            Assert.Contains("str     x0, [x19]", asm);
            // Error out pointer saved on stack (since x19 is used for return buffer)
            // After call, error stored via stack-restored pointer
            Assert.Contains("str     x21, [x9]", asm);
        }

        #endregion

        #region Symbol Generation

        [Fact]
        public void GenerateThunkSymbol_ContainsModuleName()
        {
            var symbol = ThunkAssemblyEmitter.GenerateThunkSymbol("Nuke", "$s4Nuke5ImageC");

            Assert.StartsWith("_thunk_Nuke_", symbol);
        }

        [Fact]
        public void GenerateThunkSymbol_DeterministicForSameInput()
        {
            var symbol1 = ThunkAssemblyEmitter.GenerateThunkSymbol("Test", "$s4Test6methodyyF");
            var symbol2 = ThunkAssemblyEmitter.GenerateThunkSymbol("Test", "$s4Test6methodyyF");

            Assert.Equal(symbol1, symbol2);
        }

        [Fact]
        public void GenerateThunkSymbol_DifferentForDifferentMethods()
        {
            var symbol1 = ThunkAssemblyEmitter.GenerateThunkSymbol("Test", "$s4Test6methodAyyF");
            var symbol2 = ThunkAssemblyEmitter.GenerateThunkSymbol("Test", "$s4Test6methodByyF");

            Assert.NotEqual(symbol1, symbol2);
        }

        [Fact]
        public void GenerateThunkSymbol_SanitizesModuleName()
        {
            var symbol = ThunkAssemblyEmitter.GenerateThunkSymbol("My-Module.Name", "$s4Test");

            Assert.StartsWith("_thunk_My_Module_Name_", symbol);
            Assert.DoesNotContain("-", symbol);
            Assert.DoesNotContain(".", symbol);
        }

        #endregion

        #region SwiftCallTargetResolver

        [Fact]
        public void Resolve_FreeFunction_NoSuffix()
        {
            var method = CreateMethod("doWork", "$s4Test6doWorkyyF");
            var symbol = SwiftCallTargetResolver.Resolve(method, null);
            Assert.Equal("$s4Test6doWorkyyF", symbol);
        }

        [Fact]
        public void Resolve_NonFinalClassInstanceMethod_AppendsTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("add", "$s4Test7CounterC3addyS2iF",
                methodType: MethodType.Instance, parentDecl: classDecl);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.EndsWith("Tj", symbol);
            Assert.Equal("$s4Test7CounterC3addyS2iFTj", symbol);
        }

        [Fact]
        public void Resolve_FinalClassMethod_NoTj()
        {
            var classDecl = CreateClassDecl("Manager", isFinal: true);
            var method = CreateMethod("run", "$s4Test7ManagerC3runyyF",
                methodType: MethodType.Instance, parentDecl: classDecl);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.DoesNotContain("Tj", symbol);
        }

        [Fact]
        public void Resolve_FinalMethodOnNonFinalClass_NoTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("getValue", "$s4Test7CounterC8getValueSiyF",
                methodType: MethodType.Instance, parentDecl: classDecl, isFinal: true);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.DoesNotContain("Tj", symbol);
        }

        [Fact]
        public void Resolve_Constructor_NoTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("init", "$s4Test7CounterCACycfC",
                methodType: MethodType.Instance, parentDecl: classDecl, isConstructor: true);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.DoesNotContain("Tj", symbol);
        }

        [Fact]
        public void Resolve_StaticMethod_NoTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("create", "$s4Test7CounterC6createACyFZ",
                methodType: MethodType.Static, parentDecl: classDecl);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.DoesNotContain("Tj", symbol);
        }

        [Fact]
        public void Resolve_ExtensionMethod_NoTj()
        {
            var classDecl = CreateClassDecl("Counter", isFinal: false);
            var method = CreateMethod("extMethod", "$s4Test7CounterC9extMethodyyF",
                methodType: MethodType.Instance, parentDecl: classDecl, isExtensionMethod: true);

            var symbol = SwiftCallTargetResolver.Resolve(method, classDecl);

            Assert.DoesNotContain("Tj", symbol);
        }

        [Fact]
        public void ResolveWithPrefix_AddsUnderscore()
        {
            var method = CreateMethod("doWork", "$s4Test6doWorkyyF");
            var symbol = SwiftCallTargetResolver.ResolveWithPrefix(method, null);
            Assert.Equal("_$s4Test6doWorkyyF", symbol);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public void EmitThunk_VoidReturnNoParams_TailCall()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_noop",
                SwiftSymbol: "_$s4Test4noopyyF",
                ReturnLowering: null,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains("b       _$s4Test4noopyyF", asm);
        }

        [Fact]
        public void EmitThunk_ConstructorNoMetadataSymbol_SkipsMetatypeSetup()
        {
            // Edge case: constructor without metadata accessor symbol (shouldn't happen,
            // but must not crash)
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_init",
                SwiftSymbol: "_$s4Test3FooCACycfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: null); // No metadata accessor

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Should not crash; no metadata accessor call
            Assert.DoesNotContain("bl      null", asm);
            Assert.Contains("bl      _$s4Test3FooCACycfC", asm);
        }

        [Fact]
        public void EmitThunk_FloatOnlyReturn_CorrectRegisters()
        {
            // FloatPair { x: Double, y: Double } = 16 bytes → both conventions use d0+d1 → tail call
            var returnLowering = new TypeLoweringResult(
                new[] {
                    new RegisterSlot(RegisterFile.Float, 0, 8),
                    new RegisterSlot(RegisterFile.Float, 1, 8)
                },
                IsIndirect: false,
                TotalByteSize: 16);

            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_makeFloatPair",
                SwiftSymbol: "_$s4Test13makeFloatPairAA0dE0VyF",
                ReturnLowering: returnLowering,
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // 16 bytes with 2 float slots → tail call (no bridge needed)
            Assert.Contains("b       ", asm);
            Assert.DoesNotContain("str     d0", asm);
        }

        [Fact]
        public void EmitThunk_SymbolAlignment_HasP2Align()
        {
            // All thunks should have .p2align 2 for ARM64 instruction alignment
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_aligned",
                SwiftSymbol: "_$s4Test7alignedyyF",
                ReturnLowering: null,
                SelfLowering: null,
                ParameterCount: 0,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: false,
                Throws: false,
                MetadataAccessorSymbol: null);

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            Assert.Contains(".p2align 2", asm);
        }

        #endregion

        #region Constructor with Multiple Params

        [Fact]
        public void EmitThunk_ConstructorWith2Params_SavesAndRestores()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_Foo_init2",
                SwiftSymbol: "_$s4Test3FooC1x1ySi_SitcfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 2,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save both params
            Assert.Contains("str     x0, [sp, #0]", asm);
            Assert.Contains("str     x1, [sp, #8]", asm);
            // Must restore both params
            Assert.Contains("ldr     x0, [sp, #0]", asm);
            Assert.Contains("ldr     x1, [sp, #8]", asm);
            // Stack alignment: 2 params × 8 = 16 bytes (already 16-aligned)
            Assert.Contains("sub     sp, sp, #16", asm);
            Assert.Contains("add     sp, sp, #16", asm);
        }

        [Fact]
        public void EmitThunk_ConstructorWith3Params_StackAligned()
        {
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_Foo_init3",
                SwiftSymbol: "_$s4Test3FooC3init3yyF",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 3,
                FloatParameterCount: 0,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // 3 params × 8 = 24 bytes → aligned to 32 bytes
            Assert.Contains("sub     sp, sp, #32", asm);
            Assert.Contains("add     sp, sp, #32", asm);
        }

        [Fact]
        public void EmitThunk_ConstructorWithFloatParams_SavesAndRestoresFloatRegs()
        {
            // Constructor with 1 int param + 1 float param — must save/restore both
            var descriptor = new ThunkDescriptor(
                ThunkSymbol: "_thunk_test_Foo_initMixed",
                SwiftSymbol: "_$s4Test3FooC5value6factorSi_SdtcfC",
                ReturnLowering: new TypeLoweringResult(
                    new[] { new RegisterSlot(RegisterFile.Integer, 0, 8) },
                    IsIndirect: false,
                    TotalByteSize: 8),
                SelfLowering: null,
                ParameterCount: 1,
                FloatParameterCount: 1,
                IsInstanceMethod: false,
                IsStaticMethod: false,
                IsConstructor: true,
                Throws: false,
                MetadataAccessorSymbol: "_$s4Test3FooCMa");

            var asm = ThunkAssemblyEmitter.EmitThunk(descriptor);

            // Must save both int and float params
            Assert.Contains("str     x0, [sp, #0]", asm);
            Assert.Contains("str     d0, [sp, #8]", asm);
            // Must restore both
            Assert.Contains("ldr     x0, [sp, #0]", asm);
            Assert.Contains("ldr     d0, [sp, #8]", asm);
            // Must call metadata accessor
            Assert.Contains("bl      _$s4Test3FooCMa", asm);
        }

        #endregion

        #region Test Helpers

        private static MethodDecl CreateMethod(
            string name,
            string mangledName,
            MethodType methodType = MethodType.Static,
            BaseDecl parentDecl = null!,
            bool isConstructor = false,
            bool isFinal = false,
            bool isExtensionMethod = false)
        {
            return new MethodDecl
            {
                Name = name,
                MangledName = mangledName,
                MethodType = methodType,
                IsConstructor = isConstructor,
                IsFinal = isFinal,
                IsExtensionMethod = isExtensionMethod,
                CSSignature = new List<ArgumentDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                Throws = false,
                IsAsync = false,
                Visibility = Visibility.Public,
                ParentDecl = parentDecl,
                ModuleDecl = null,
            };
        }

        private static ClassDecl CreateClassDecl(string name, bool isFinal = false)
        {
            return new ClassDecl
            {
                Name = name,
                SwiftTypeName = SwiftTypeName.FromModuleQualifiedName($"Test.{name}"),
                MangledName = $"$s4Test{name.Length}{name}CN",
                IsFinal = isFinal,
                Conformances = new List<TypeConformance>(),
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Types = new List<TypeDecl>(),
                Operators = new List<OperatorDecl>(),
                Subscripts = new List<SubscriptDecl>(),
                GenericParameters = new List<GenericArgumentDecl>(),
                ParentDecl = null,
                ModuleDecl = null,
            };
        }

        #endregion
    }
}

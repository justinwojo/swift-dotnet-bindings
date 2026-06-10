// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. Block Detection Primitives

    public class PostProcessorBlockDetectionTests
    {
        [Fact]
        public void FindBlockEnd_SimpleBlock_ReturnsClosingLine()
        {
            var lines = new List<string>
            {
                "func foo() {\n",
                "    return 1\n",
                "}\n"
            };
            Assert.Equal(2, SwiftWrapperPostProcessor.FindBlockEnd(lines, 0));
        }

        [Fact]
        public void FindBlockEnd_NestedBlocks_ReturnsOuterClosingLine()
        {
            var lines = new List<string>
            {
                "extension Foo {\n",
                "    func bar() {\n",
                "        if true {\n",
                "            return\n",
                "        }\n",
                "    }\n",
                "}\n"
            };
            Assert.Equal(6, SwiftWrapperPostProcessor.FindBlockEnd(lines, 0));
        }

        [Fact]
        public void FindBlockEnd_UnterminatedBlock_ReturnsLastLine()
        {
            var lines = new List<string>
            {
                "func foo() {\n",
                "    return 1\n"
            };
            Assert.Equal(1, SwiftWrapperPostProcessor.FindBlockEnd(lines, 0));
        }

        [Fact]
        public void ScanBlockBody_ConcatenatesLines()
        {
            var lines = new List<string>
            {
                "line0\n",
                "line1\n",
                "line2\n"
            };
            var body = SwiftWrapperPostProcessor.ScanBlockBody(lines, 0, 2);
            Assert.Equal("line0\nline1\nline2\n", body);
        }
    }

    #endregion

    #region B. Pattern 1: EveryProtocol Blocks

    public class PostProcessorEveryProtocolTests
    {
        [Fact]
        public void Process_ExtensionEveryProtocol_PreservedWhenValid()
        {
            // Valid EveryProtocol extensions are now preserved (not unconditionally stripped)
            var input = """
                // before
                extension EveryProtocol: SomeProtocol {
                    func foo() { }
                }
                // after

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("// before", result.CleanedContent);
            Assert.Contains("// after", result.CleanedContent);
            Assert.Contains("EveryProtocol", result.CleanedContent);
        }

        [Fact]
        public void Process_ExtensionEveryProtocol_StrippedWhenReferencesInternalType()
        {
            // EveryProtocol extensions referencing internal types ARE stripped
            var internalTypes = new HashSet<string> { "InternalType" };
            var input = """
                // before
                extension EveryProtocol: SomeProtocol {
                    var prop: InternalType { fatalError() }
                }
                // after

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("// before", result.CleanedContent);
            Assert.Contains("// after", result.CleanedContent);
            Assert.DoesNotContain("EveryProtocol", result.CleanedContent);
        }

        [Fact]
        public void Process_ClassEveryProtocol_Preserved()
        {
            // EveryProtocol class definition is now preserved (valid code)
            var input = """
                class EveryProtocol {
                    var x: Int = 0
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("EveryProtocol", result.CleanedContent);
        }

        [Fact]
        public void Process_CodableStubExtension_PreservedEvenWithInternalTypes()
        {
            // Codable/Error stubs are always preserved, even when "Encodable" is an internal type
            var internalTypes = new HashSet<string> { "Encodable" };
            var input = """
                extension EveryProtocol: Encodable {
                    public func encode(to encoder: Encoder) throws {}
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("EveryProtocol", result.CleanedContent);
        }
    }

    #endregion

    #region C. Pattern 2: @_silgen_name Functions

    public class PostProcessorSilgenNameTests
    {
        [Fact]
        public void Process_SilgenNameWithEveryProtocol_Stripped()
        {
            var input = """
                @_silgen_name("wrapper_func")
                public func wrapper_func(_self: UnsafeMutableRawPointer) {
                    let proxy = EveryProtocol()
                    proxy.doSomething()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("EveryProtocol", result.CleanedContent);
        }

        [Fact]
        public void Process_SilgenNameWithBareSelf_Preserved()
        {
            // Pattern (b) removed — self. without _self: is now prevented at emission time.
            var input = """
                @_silgen_name("free_func")
                public func free_func() {
                    self.doSomething()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("self.doSomething", result.CleanedContent);
        }

        [Fact]
        public void Process_SilgenNameWithSelfParam_Kept()
        {
            var input = """
                @_silgen_name("method_func")
                public func method_func(_self: UnsafeMutableRawPointer) {
                    self.doSomething()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("self.doSomething", result.CleanedContent);
        }

        [Fact]
        public void Process_SilgenNameWithAsyncInit_Preserved()
        {
            // Pattern (c) removed — __self.init is now prevented at emission time.
            var input = """
                @_silgen_name("init_wrapper")
                public func init_wrapper() {
                    __self.init(value: 42)
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("__self.init", result.CleanedContent);
        }

        [Fact]
        public void Process_SilgenNameWithVarExistential_NotStripped()
        {
            // Pattern (d) was removed — all existentials now use `var`, so this code is safe.
            var input = """
                @_silgen_name("existential_func")
                public func existential_func(_self: UnsafeMutableRawPointer) {
                    var existential = buffer.load(as: (any SomeProtocol).self)
                    existential.mutate()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("var existential", result.CleanedContent);
        }

        [Fact]
        public void Process_SilgenNameWithLetExistential_NoLongerStripped()
        {
            // Pattern (d) was removed entirely — even `let existential` is no longer caught.
            // The emitter now always uses `var`, making this pattern dead code.
            var input = """
                @_silgen_name("existential_func")
                public func existential_func(_self: UnsafeMutableRawPointer) {
                    let existential = buffer.load(as: (any SomeProtocol).self)
                    existential.mutate()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("let existential", result.CleanedContent);
        }

        [Fact]
        public void Process_SilgenNameWithNonEscapingClosureTask_Preserved()
        {
            // Pattern (e) removed — non-escaping closure in Task is now prevented at emission time.
            var input = """
                @_silgen_name("closure_task_func")
                public func closure_task_func(_self: UnsafeMutableRawPointer, callback: (Int32) -> Int32) {
                    Task {
                        let result = callback(42)
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("closure_task_func", result.CleanedContent);
        }

        [Fact]
        public void Process_SilgenNameCleanFunction_Kept()
        {
            var input = """
                @_silgen_name("clean_wrapper")
                public func clean_wrapper(_self: UnsafeMutableRawPointer) {
                    let value = self.getValue()
                    return value
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("clean_wrapper", result.CleanedContent);
        }
    }

    #endregion

    #region D. Pattern 3: Extension Blocks

    public class PostProcessorExtensionTests
    {
        [Fact]
        public void Process_ExtensionWithEveryProtocol_Stripped()
        {
            var input = """
                extension SomeType {
                    func broken() {
                        let proxy = EveryProtocol()
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("SomeType", result.CleanedContent);
        }

        [Fact]
        public void Process_ExtensionWithAsyncInit_Preserved()
        {
            // Pattern (c) removed — __self.init is now prevented at emission time.
            var input = """
                extension SomeType {
                    func wrapper() {
                        __self.init(value: 1)
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SomeType", result.CleanedContent);
            Assert.Contains("__self.init", result.CleanedContent);
        }

        [Fact]
        public void Process_CleanExtension_Kept()
        {
            var input = """
                extension SomeType {
                    func goodWrapper() {
                        let value = 42
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SomeType", result.CleanedContent);
        }

        [Fact]
        public void Process_ExtensionWithClosureInTask_Preserved()
        {
            // Pattern (e) removed — non-escaping closure in Task is now prevented at emission time.
            var input = """
                extension SomeType {
                    func asyncFunc(_self: UnsafeMutableRawPointer, callback: (Int32) -> Void) {
                        Task {
                            callback(42)
                        }
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SomeType", result.CleanedContent);
            Assert.Contains("callback(42)", result.CleanedContent);
        }
    }

    #endregion

    #region E. Pattern 4: Standalone Public Funcs

    public class PostProcessorStandaloneFuncTests
    {
        [Fact]
        public void Process_SBWFuncWithEveryProtocol_Stripped()
        {
            var input = """
                public func SBW_doSomething() {
                    let proxy = EveryProtocol()
                    proxy.execute()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
        }

        [Fact]
        public void Process_PInvokeFuncWithEveryProtocol_Stripped()
        {
            var input = """
                public func PInvoke_doSomething() {
                    let proxy = EveryProtocol()
                    proxy.execute()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
        }

        [Fact]
        public void Process_SBWFuncWithVarExistential_NotStripped()
        {
            // Pattern (d) was removed — all existentials now use `var`, so this code is safe.
            var input = """
                public func SBW_existentialFunc(ptr: UnsafeMutableRawPointer) {
                    var existential = ptr.load(as: (any SomeProtocol).self)
                    existential.mutate()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("var existential", result.CleanedContent);
        }

        [Fact]
        public void Process_SBWFuncWithLetExistential_NoLongerStripped()
        {
            // Pattern (d) was removed entirely — even `let existential` is no longer caught.
            var input = """
                public func SBW_existentialFunc(ptr: UnsafeMutableRawPointer) {
                    let existential = ptr.load(as: (any SomeProtocol).self)
                    existential.mutate()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("let existential", result.CleanedContent);
        }

        [Fact]
        public void Process_SBWFuncClean_Kept()
        {
            var input = """
                public func SBW_clean() {
                    print("hello")
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SBW_clean", result.CleanedContent);
        }
    }

    #endregion

    #region F. Integration / Edge Cases

    public class PostProcessorIntegrationTests
    {
        [Fact]
        public void Process_EmptyInput_ReturnsEmpty()
        {
            var result = SwiftWrapperPostProcessor.Process("");
            Assert.Equal("", result.CleanedContent);
            Assert.Equal(0, result.StrippedBlockCount);
        }

        [Fact]
        public void Process_NullInput_ReturnsNull()
        {
            var result = SwiftWrapperPostProcessor.Process(null!);
            Assert.Null(result.CleanedContent);
            Assert.Equal(0, result.StrippedBlockCount);
        }

        [Fact]
        public void Process_NoBrokenPatterns_ReturnsUnchanged()
        {
            var input = """
                import Foundation

                @_silgen_name("good_func")
                public func good_func(_self: UnsafeMutableRawPointer) {
                    let x = self.getValue()
                    return x
                }

                extension MyType {
                    func helper() {
                        let y = compute()
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("good_func", result.CleanedContent);
            Assert.Contains("MyType", result.CleanedContent);
        }

        [Fact]
        public void Process_MixedContent_OnlyBrokenStripped()
        {
            var input = """
                import Foundation

                // Good function
                @_silgen_name("good_wrapper")
                public func good_wrapper(_self: UnsafeMutableRawPointer) {
                    let val = self.getValue()
                    return val
                }

                // Broken function
                @_silgen_name("broken_wrapper")
                public func broken_wrapper() {
                    let proxy = EveryProtocol()
                    proxy.doSomething()
                }

                // Another good function
                public func SBW_clean() {
                    print("ok")
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("good_wrapper", result.CleanedContent);
            Assert.Contains("SBW_clean", result.CleanedContent);
            Assert.DoesNotContain("broken_wrapper", result.CleanedContent);
        }

        [Fact]
        public void Process_MultiplePatterns_OnlyEveryProtocolStripped()
        {
            // Pattern (b) removed — self. without _self: is no longer stripped.
            // Only EveryProtocol-related blocks are stripped (class, extension, SBW_ func).
            // The @_silgen_name("bad1") func with self.foo() is now preserved.
            var input = """
                class EveryProtocol {
                    var x: Int = 0
                }

                extension EveryProtocol: P1 {
                    func conform() { }
                }

                @_silgen_name("bad1")
                public func bad1() {
                    self.foo()
                }

                public func SBW_bad2() {
                    let proxy = EveryProtocol()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            // Only SBW_bad2 is stripped (uses EveryProtocol() in non-system context).
            // class EveryProtocol and extension EveryProtocol: P1 are preserved.
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("EveryProtocol", result.CleanedContent);
            Assert.Contains("bad1", result.CleanedContent);
            Assert.DoesNotContain("SBW_bad2", result.CleanedContent);
        }

        [Fact]
        public void Process_PreservesImportsAndComments()
        {
            // Pattern (b) removed — self. without _self: is no longer stripped.
            // Both functions are preserved alongside imports and comments.
            var input = """
                import Foundation
                import SwiftBindingsTestLib

                // This is a comment
                @_silgen_name("broken")
                public func broken() {
                    self.foo()
                }

                // Keep this
                @_silgen_name("good")
                public func good(_self: UnsafeMutableRawPointer) {
                    let x = self.bar()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("import Foundation", result.CleanedContent);
            Assert.Contains("import SwiftBindingsTestLib", result.CleanedContent);
            Assert.Contains("// This is a comment", result.CleanedContent);
            Assert.Contains("// Keep this", result.CleanedContent);
            Assert.Contains("broken", result.CleanedContent);
            Assert.Contains("good", result.CleanedContent);
        }
    }

    #endregion

    #region G. Raw Generic Type Parameter Stripping (τ_0_0)

    public class PostProcessorRawGenericParamTests
    {
        [Fact]
        public void Process_SilgenNameWithTau_Preserved()
        {
            // Pattern (f) removed — raw generic params are now prevented at emission time.
            var input = """
                @_silgen_name("generic_wrapper")
                public func generic_wrapper(_self: UnsafeMutableRawPointer) -> τ_0_0 {
                    let result = _self.load(as: τ_0_0.self)
                    return result
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("τ_0_0", result.CleanedContent);
        }

        [Fact]
        public void Process_ExtensionWithTau_Preserved()
        {
            // Pattern (f) removed — raw generic params are now prevented at emission time.
            var input = """
                extension SomeType {
                    func wrapper() -> τ_1_0 {
                        return self.getValue() as! τ_1_0
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("τ_1_0", result.CleanedContent);
        }

        [Fact]
        public void Process_StandaloneFuncWithTau_Preserved()
        {
            // Pattern (f) removed — raw generic params are now prevented at emission time.
            var input = """
                public func SBW_generic(_ arg: τ_0_1) {
                    print(arg)
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("τ_0_1", result.CleanedContent);
        }

        [Fact]
        public void Process_MultipleTauVariants_AllPreserved()
        {
            // Pattern (f) removed — raw generic params are now prevented at emission time.
            var input = """
                @_silgen_name("multi_generic")
                public func multi_generic(_self: UnsafeMutableRawPointer, _ a: τ_0_0, _ b: τ_1_0) -> τ_0_1 {
                    return a
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("τ_0_0", result.CleanedContent);
            Assert.Contains("τ_1_0", result.CleanedContent);
            Assert.Contains("τ_0_1", result.CleanedContent);
        }

        [Fact]
        public void Process_NoTau_Kept()
        {
            var input = """
                @_silgen_name("concrete_wrapper")
                public func concrete_wrapper(_self: UnsafeMutableRawPointer) -> Swift.String {
                    return self.getValue()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("concrete_wrapper", result.CleanedContent);
        }

        [Fact]
        public void Process_MixedTauAndClean_BothPreserved()
        {
            // Pattern (f) removed — raw generic params are now prevented at emission time.
            // Both the clean func and the tau func are preserved.
            var input = """
                @_silgen_name("good_func")
                public func good_func(_self: UnsafeMutableRawPointer) {
                    let x = self.getValue()
                }

                @_silgen_name("bad_generic")
                public func bad_generic(_self: UnsafeMutableRawPointer) -> τ_0_0 {
                    return _self.load(as: τ_0_0.self)
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("good_func", result.CleanedContent);
            Assert.Contains("bad_generic", result.CleanedContent);
            Assert.Contains("τ_0_0", result.CleanedContent);
        }
    }

    #endregion

    #region G2. @_cdecl Block Stripping

    public class PostProcessorCdeclTests
    {
        [Fact]
        public void Process_CdeclWithBrokenBody_StripsEntireBlock()
        {
            var input = """
                @_cdecl("SBW_BrokenFunc")
                public func SBW_BrokenFunc(_ ptr: UnsafeMutableRawPointer) {
                    let proxy = EveryProtocol()
                    proxy.execute()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("SBW_BrokenFunc", result.CleanedContent);
            Assert.DoesNotContain("@_cdecl", result.CleanedContent);
        }

        [Fact]
        public void Process_CdeclWithCleanBody_Preserved()
        {
            var input = """
                @_cdecl("SBW_GetErrorDescription_Mod")
                public func SBW_GetErrorDescription_Mod(_ ptr: UnsafeMutableRawPointer) -> UnsafeMutableRawPointer {
                    let result = ptr.load(as: Swift.String.self)
                    return result
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SBW_GetErrorDescription_Mod", result.CleanedContent);
            Assert.Contains("@_cdecl", result.CleanedContent);
        }

        [Fact]
        public void Process_CdeclWithRawGenericParam_Preserved()
        {
            // Pattern (f) removed — raw generic params are now prevented at emission time.
            var input = """
                @_cdecl("SBW_GenericFunc")
                public func SBW_GenericFunc(_ ptr: UnsafeMutableRawPointer) -> τ_0_0 {
                    return ptr.load(as: τ_0_0.self)
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SBW_GenericFunc", result.CleanedContent);
            Assert.Contains("τ_0_0", result.CleanedContent);
        }

        [Fact]
        public void Process_CdeclWithInternalType_StripsEntireBlock()
        {
            var input = """
                @_cdecl("SBW_CreateInternal")
                public func SBW_CreateInternal(_ ptr: UnsafeMutableRawPointer) -> InternalWidget {
                    return InternalWidget()
                }

                """;
            var internalTypes = new HashSet<string> { "InternalWidget" };
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("InternalWidget", result.CleanedContent);
            Assert.DoesNotContain("@_cdecl", result.CleanedContent);
        }

        [Fact]
        public void Process_CdeclNonSBWPrefix_BrokenStripped()
        {
            // Non-SBW_ prefixed @_cdecl function (e.g. future generator output patterns)
            var input = """
                @_cdecl("PInvoke_GetValue")
                public func PInvoke_GetValue(_ ptr: UnsafeMutableRawPointer) {
                    let proxy = EveryProtocol()
                    proxy.execute()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("PInvoke_GetValue", result.CleanedContent);
            Assert.DoesNotContain("@_cdecl", result.CleanedContent);
        }

        [Fact]
        public void Process_CdeclNonSBWPrefix_CleanKept()
        {
            var input = """
                @_cdecl("PInvoke_GetValue")
                public func PInvoke_GetValue(_ ptr: UnsafeMutableRawPointer) -> Int32 {
                    return ptr.load(as: Int32.self)
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("PInvoke_GetValue", result.CleanedContent);
            Assert.Contains("@_cdecl", result.CleanedContent);
        }

        [Fact]
        public void Process_OrphanedCdeclBug_Regression()
        {
            // Reproduces the orphaned @_cdecl scenario: broken @_cdecl block followed by clean @_cdecl block.
            // Before the fix, Pattern 4 stripped the function but left the @_cdecl attribute orphaned,
            // which attached to the next function → "duplicate attribute" Swift compilation error.
            var input = """
                @_cdecl("SBW_BrokenFunc")
                public func SBW_BrokenFunc(_ ptr: UnsafeMutableRawPointer) {
                    let proxy = EveryProtocol()
                    proxy.execute()
                }
                @_cdecl("SBW_GetErrorDescription_Mod")
                public func SBW_GetErrorDescription_Mod(_ ptr: UnsafeMutableRawPointer) -> UnsafeMutableRawPointer {
                    let result = ptr.load(as: Swift.String.self)
                    return result
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            // Broken block fully stripped (no orphaned @_cdecl)
            Assert.DoesNotContain("SBW_BrokenFunc", result.CleanedContent);

            // Clean block preserved with exactly one @_cdecl
            Assert.Contains("SBW_GetErrorDescription_Mod", result.CleanedContent);
            var cdeclCount = result.CleanedContent.Split("@_cdecl").Length - 1;
            Assert.Equal(1, cdeclCount);

            // No orphaned @_cdecl: every @_cdecl line must be immediately followed by a public func line
            var lines = result.CleanedContent.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("@_cdecl("))
                {
                    Assert.True(i + 1 < lines.Length, "Orphaned @_cdecl at end of file");
                    Assert.StartsWith("public func", lines[i + 1].TrimStart());
                }
            }
        }
    }

    #endregion

    #region H. Internal Type Stripping (WU2)

    public class PostProcessorInternalTypeTests
    {
        [Fact]
        public void Process_FunctionReferencingInternalType_IsStripped()
        {
            var input = """
                @_silgen_name("wrapper_create")
                public func SBW_create(_self: UnsafeMutableRawPointer) -> SkeletonLayer {
                    return SkeletonLayer()
                }

                @_silgen_name("wrapper_update")
                public func SBW_update(_self: UnsafeMutableRawPointer) {
                    let x = _self
                }

                """;
            var internalTypes = new HashSet<string> { "SkeletonLayer" };
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);

            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("SkeletonLayer", result.CleanedContent);
            Assert.Contains("SBW_update", result.CleanedContent);
        }

        [Fact]
        public void Process_FunctionReferencingPublicType_IsKept()
        {
            var input = """
                @_silgen_name("wrapper_create")
                public func SBW_create(_self: UnsafeMutableRawPointer) -> SkeletonLoader {
                    return SkeletonLoader()
                }

                """;
            var internalTypes = new HashSet<string> { "SkeletonLayer" };
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);

            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SkeletonLoader", result.CleanedContent);
        }

        [Fact]
        public void Process_SimilarNameNotFalseMatch_IsKept()
        {
            // "SkeletonLayerView" should NOT be stripped when "SkeletonLayer" is internal
            // — word boundary prevents substring match.
            var input = """
                @_silgen_name("wrapper_render")
                public func SBW_render(_self: UnsafeMutableRawPointer) -> SkeletonLayerView {
                    return SkeletonLayerView()
                }

                """;
            var internalTypes = new HashSet<string> { "SkeletonLayer" };
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);

            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SkeletonLayerView", result.CleanedContent);
        }

        [Fact]
        public void Process_NoInternalTypes_BehaviorUnchanged()
        {
            var input = """
                @_silgen_name("wrapper_foo")
                public func SBW_foo(_self: UnsafeMutableRawPointer) {
                    let x = _self
                }

                """;
            // null set → no internal type stripping
            var result = SwiftWrapperPostProcessor.Process(input, null);

            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SBW_foo", result.CleanedContent);
        }

        [Fact]
        public void Process_NestedInternalType_IsStripped()
        {
            var input = """
                @_silgen_name("wrapper_nested")
                public func SBW_nested(_self: UnsafeMutableRawPointer) -> Outer.InnerInternal {
                    return Outer.InnerInternal()
                }

                @_silgen_name("wrapper_clean")
                public func SBW_clean(_self: UnsafeMutableRawPointer) {
                    let x = _self
                }

                """;
            var internalTypes = new HashSet<string> { "Outer.InnerInternal" };
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);

            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("InnerInternal", result.CleanedContent);
            Assert.Contains("SBW_clean", result.CleanedContent);
        }
    }

    public class CollectInternalTypeNamesTests
    {
        [Fact]
        public void CollectInternalTypeNames_ShortNameCollision_UsesQualifiedOnly()
        {
            // Internal "Layer" + public "Layer" → short name removed from set
            var moduleDecl = new ModuleDecl
            {
                Name = "TestModule",
                Properties = new List<PropertyDecl>(),
                Methods = new List<MethodDecl>(),
                Dependencies = new List<string>(),
                Protocols = new List<ProtocolDecl>(),
                ParentDecl = null,
                ModuleDecl = null,
                Types = new List<TypeDecl>
                {
                    new StructDecl
                    {
                        Name = "Layer",
                        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Outer.Layer"),
                        MangledName = "$s10TestModule5Outer5LayerVN",
                        IsModuleInternal = true,
                        Properties = new List<PropertyDecl>(),
                        Methods = new List<MethodDecl>(),
                        Types = new List<TypeDecl>(),
                        Operators = new List<OperatorDecl>(),
                        Subscripts = new List<SubscriptDecl>(),
                        GenericParameters = new List<GenericArgumentDecl>(),
                        Conformances = new List<TypeConformance>(),
                        ParentDecl = null,
                        ModuleDecl = null,
                        IsFrozen = false,
                        MetadataAccessor = ""
                    },
                    new StructDecl
                    {
                        Name = "Layer",
                        SwiftTypeName = SwiftTypeName.FromModuleQualifiedName("TestModule.Layer"),
                        MangledName = "$s10TestModule5LayerVN",
                        IsModuleInternal = false,
                        Properties = new List<PropertyDecl>(),
                        Methods = new List<MethodDecl>(),
                        Types = new List<TypeDecl>(),
                        Operators = new List<OperatorDecl>(),
                        Subscripts = new List<SubscriptDecl>(),
                        GenericParameters = new List<GenericArgumentDecl>(),
                        Conformances = new List<TypeConformance>(),
                        ParentDecl = null,
                        ModuleDecl = null,
                        IsFrozen = false,
                        MetadataAccessor = ""
                    }
                }
            };

            var result = BindingsGenerator.CollectInternalTypeNames(moduleDecl);

            // Short name "Layer" should be removed (collides with public type)
            Assert.DoesNotContain("Layer", result);
            // Qualified name should remain
            Assert.Contains("TestModule.Outer.Layer", result);
        }
    }

    #endregion

    #region @MainActor Attribute Prefix Tests (Issue K)

    public class PostProcessorMainActorTests
    {
        [Fact]
        public void Process_MainActorSilgenNameWithBrokenBody_StripsEntireBlock()
        {
            // @MainActor @_silgen_name(...) should be detected and stripped like @_silgen_name(...)
            var input = """
                @MainActor @_silgen_name("SBW_Proto_get_prop_0")
                public func SBW_Proto_get_prop_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    let proxy = EveryProtocol()
                    proxy.execute()
                    return UnsafeMutableRawPointer(bitPattern: 0)!
                }
                @_silgen_name("SBW_Proto_free_get_prop_0")
                public func SBW_Proto_free_get_prop_0(_ ptr: UnsafeMutableRawPointer) {
                    ptr.assumingMemoryBound(to: Bool.self).deinitialize(count: 1)
                    ptr.deallocate()
                }
                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            // The getter has EveryProtocol() → should be stripped
            // But the free function should remain
            Assert.Contains("SBW_Proto_free_get_prop_0", result.CleanedContent);
        }

        [Fact]
        public void Process_MainActorSilgenName_NoOrphanedAttributes()
        {
            // When a @MainActor @_silgen_name getter is stripped, the attribute line
            // must also be removed — no orphaned @MainActor @_silgen_name lines.
            var input = """
                @MainActor @_silgen_name("SBW_Proto_get_prop_0")
                public func SBW_Proto_get_prop_0(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    let proxy = EveryProtocol()
                    proxy.execute()
                    return UnsafeMutableRawPointer(bitPattern: 0)!
                }
                @_silgen_name("SBW_Proto_free_get_prop_0")
                public func SBW_Proto_free_get_prop_0(_ ptr: UnsafeMutableRawPointer) {
                    ptr.assumingMemoryBound(to: Bool.self).deinitialize(count: 1)
                    ptr.deallocate()
                }
                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            // The @MainActor @_silgen_name line for the getter must not remain orphaned
            Assert.DoesNotContain("SBW_Proto_get_prop_0", result.CleanedContent);
        }

        [Fact]
        public void Process_MainActorCdeclWithBrokenBody_StripsEntireBlock()
        {
            var input = """
                @MainActor @_cdecl("SBW_broken_func")
                public func SBW_broken_func(_ ptr: UnsafeMutableRawPointer) {
                    let proxy = EveryProtocol()
                    proxy.execute()
                }
                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("SBW_broken_func", result.CleanedContent);
        }

        [Fact]
        public void Process_MainActorSilgenName_CleanBody_Preserved()
        {
            // @MainActor @_silgen_name with a clean body should NOT be stripped
            var input = """
                @MainActor @_silgen_name("SBW_setter")
                public func SBW_setter(_ containerPtr: UnsafeMutableRawPointer, _ valuePtr: UnsafeRawPointer) {
                    let typedPtr = containerPtr.assumingMemoryBound(to: (any TestModule.Proto).self)
                    var existential = typedPtr.pointee
                    existential.value = valuePtr.load(as: Bool.self)
                    typedPtr.pointee = existential
                }
                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SBW_setter", result.CleanedContent);
        }
        [Fact]
        public void Process_StandaloneMainActorLine_BeforeBrokenCdecl_StripsEntireBlock()
        {
            // ConstructorWrapperEmitter emits @MainActor on its own line, then @_cdecl on the next.
            // If the block is broken, both lines must be stripped — no orphaned @MainActor.
            var input = """
                @MainActor
                @_cdecl("SBW_init_broken")
                public func _sbw_init_broken(_ resultPtr: UnsafeMutableRawPointer) {
                    let proxy = EveryProtocol()
                    proxy.execute()
                }
                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("SBW_init_broken", result.CleanedContent);
            Assert.DoesNotContain("@MainActor", result.CleanedContent);
        }

        [Fact]
        public void Process_StandaloneMainActorLine_BeforeCleanCdecl_Preserved()
        {
            // Clean @MainActor + @_cdecl should be preserved.
            var input = """
                @MainActor
                @_cdecl("SBW_init_good")
                public func _sbw_init_good(_ resultPtr: UnsafeMutableRawPointer) {
                    let result = TestModule.SomeType()
                    resultPtr.initializeMemory(as: TestModule.SomeType.self, repeating: result, count: 1)
                }
                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SBW_init_good", result.CleanedContent);
            Assert.Contains("@MainActor", result.CleanedContent);
        }

        [Fact]
        public void Process_StandaloneMainActorLine_BeforeBrokenSilgenName_StripsEntireBlock()
        {
            var input = """
                @MainActor
                @_silgen_name("SBW_broken_getter")
                public func SBW_broken_getter(_ containerPtr: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    let proxy = EveryProtocol()
                    proxy.execute()
                    return UnsafeMutableRawPointer(bitPattern: 0)!
                }
                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            Assert.DoesNotContain("@MainActor", result.CleanedContent);
            Assert.DoesNotContain("SBW_broken_getter", result.CleanedContent);
            Assert.True(result.StrippedBlockCount >= 1);
        }
    }

    #endregion

    #region I. Safety-Net: Closure Metatype Stripping

    public class PostProcessorClosureMetatypeTests
    {
        [Fact]
        public void Process_LoadAsEscapingClosure_StripsFunction()
        {
            // .load(as: @escaping ...) is invalid Swift — @escaping is a storage qualifier
            // not valid in metatype position, and .self binds to the return type.
            // The post-processor strips the entire function block as a safety net.
            // Uses single-line signature format to match FindBlockEnd expectations.
            var input = """
                // before
                @_cdecl("SBW_callWithOptionalStringReturn_optbuf")
                public func PInvoke_callWithOptionalStringReturn(_ handler: UnsafeRawPointer, _ _resultBuf: UnsafeMutableRawPointer) {
                    let handlerVal = handler.load(as: @escaping (Swift.Int32) -> Swift.Optional<Swift.String>.self)
                    let _result = callWithOptionalStringReturn(handlerVal)
                }
                // after
                """;

            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("load(as: @escaping", result.CleanedContent);
            Assert.Contains("// before", result.CleanedContent);
            Assert.Contains("// after", result.CleanedContent);
        }

        [Fact]
        public void Process_LoadAsSendableClosure_StripsFunction()
        {
            // @Sendable is also a storage qualifier invalid in metatype position
            var input = "// before\n" +
                "@_cdecl(\"SBW_test\")\n" +
                "public func PInvoke_test(_ handler: UnsafeRawPointer) {\n" +
                "    let handlerVal = handler.load(as: @Sendable () -> Void.self)\n" +
                "}\n" +
                "// after\n";

            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("load(as: @Sendable", result.CleanedContent);
        }

        [Fact]
        public void Process_LoadAsNormalType_Preserved()
        {
            // Normal .load(as: SomeType.self) patterns must NOT be stripped
            var input = """
                @_cdecl("SBW_test")
                public func PInvoke_test(_ ptr: UnsafeRawPointer) -> Int32 {
                    return ptr.load(as: Int32.self)
                }
                """;

            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("load(as: Int32.self)", result.CleanedContent);
        }
    }

    #endregion

    // Region H (Module/Type Name Collision) was retired: the post-processor
    // no longer rewrites module-prefix collisions. Equivalent emission-time behavior is
    // covered by ModuleEmissionContextCollisionTests.

    #region K. StrippedSymbols Extraction

    public class PostProcessorStrippedSymbolsTests
    {
        [Fact]
        public void Process_StrippedCdeclBlock_ExtractsSymbol()
        {
            var input = "@_cdecl(\"SBW_broken_func\")\n" +
                        "public func SBW_broken_func() {\n" +
                        "    let proxy = EveryProtocol()\n" +
                        "}\n";
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("SBW_broken_func", result.StrippedSymbols);
        }

        [Fact]
        public void Process_StrippedSilgenNameBlock_ExtractsSymbol()
        {
            var input = "@_silgen_name(\"wrapper_symbol\")\n" +
                        "public func wrapper_symbol() {\n" +
                        "    let proxy = EveryProtocol()\n" +
                        "}\n";
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("wrapper_symbol", result.StrippedSymbols);
        }

        [Fact]
        public void Process_PreservedBlock_NoSymbolsExtracted()
        {
            var input = "@_cdecl(\"SBW_good_func\")\n" +
                        "public func SBW_good_func() {\n" +
                        "    print(\"hello\")\n" +
                        "}\n";
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Empty(result.StrippedSymbols);
        }

        [Fact]
        public void Process_ExtensionWithMultipleCdecls_ExtractsAllSymbols()
        {
            var internalTypes = new HashSet<string> { "InternalWidget" };
            var input = "extension SomeType {\n" +
                        "    @_cdecl(\"SBW_getter\")\n" +
                        "    public func SBW_getter() -> InternalWidget {\n" +
                        "        return InternalWidget()\n" +
                        "    }\n" +
                        "    @_cdecl(\"SBW_setter\")\n" +
                        "    public func SBW_setter(_ val: InternalWidget) {\n" +
                        "    }\n" +
                        "}\n";
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("SBW_getter", result.StrippedSymbols);
            Assert.Contains("SBW_setter", result.StrippedSymbols);
        }

        [Fact]
        public void Process_EveryProtocolExtensionWithInternalType_ExtractsSymbols()
        {
            var internalTypes = new HashSet<string> { "InternalType" };
            var input = "extension EveryProtocol: SomeProtocol {\n" +
                        "    @_cdecl(\"SBW_conform\")\n" +
                        "    func conform() -> InternalType { fatalError() }\n" +
                        "}\n";
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("SBW_conform", result.StrippedSymbols);
        }

        [Fact]
        public void Process_MainActorCdeclStripped_ExtractsSymbol()
        {
            var input = "@MainActor\n" +
                        "@_cdecl(\"SBW_init_broken\")\n" +
                        "public func SBW_init_broken() {\n" +
                        "    let proxy = EveryProtocol()\n" +
                        "}\n";
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("SBW_init_broken", result.StrippedSymbols);
        }

        [Fact]
        public void Process_StandaloneFuncStripped_NoSymbolsWithoutCdecl()
        {
            // Standalone funcs without @_cdecl have no symbol to extract
            var input = "public func SBW_standalone() {\n" +
                        "    let proxy = EveryProtocol()\n" +
                        "}\n";
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Empty(result.StrippedSymbols);
        }

        [Fact]
        public void Process_StandaloneFuncWithCdeclStripped_ExtractsSymbol()
        {
            // Standalone funcs WITH @_cdecl DO extract symbols (Pattern 4 with annotation)
            var input = "@_cdecl(\"SBW_standalone_symbol\")\n" +
                        "public func SBW_standalone() {\n" +
                        "    let proxy = EveryProtocol()\n" +
                        "}\n";
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("SBW_standalone_symbol", result.StrippedSymbols);
        }

        [Fact]
        public void ExtractSymbolsFromBlock_FindsCdeclAndSilgenName()
        {
            var lines = new List<string>
            {
                "@_cdecl(\"symbol_one\")\n",
                "func one() {\n",
                "    @_silgen_name(\"symbol_two\")\n",
                "    func two() {}\n",
                "}\n"
            };
            var symbols = new HashSet<string>();
            SwiftWrapperPostProcessor.ExtractSymbolsFromBlock(lines, 0, 4, symbols);
            Assert.Contains("symbol_one", symbols);
            Assert.Contains("symbol_two", symbols);
            Assert.Equal(2, symbols.Count);
        }
    }

    #endregion

    #region L. FindBlockEnd Multi-Line Signatures

    public class PostProcessorMultiLineSignatureTests
    {
        [Fact]
        public void FindBlockEnd_MultiLineSignature_ReturnsBlockEnd()
        {
            // Multi-line function signature: the opening { is on the line after params close
            var lines = new List<string>
            {
                "@_silgen_name(\"wrapper\")\n",
                "public func PInvoke_foo(\n",
                "    _ value: SomeType,\n",
                "    _ _self: UnsafeMutableRawPointer\n",
                ") {\n",
                "    let __self = Unmanaged<InternalType>.fromOpaque(_self).takeUnretainedValue()\n",
                "}\n"
            };
            // Should return line 6 (the closing }), not line 1 (no braces yet)
            Assert.Equal(6, SwiftWrapperPostProcessor.FindBlockEnd(lines, 0));
        }

        [Fact]
        public void FindBlockEnd_MultiLineSignature_NoBraceOnSecondLine_DoesNotReturnEarly()
        {
            // Decorator line + signature line with no braces — should NOT return on line 1
            var lines = new List<string>
            {
                "@_silgen_name(\"sym\")\n",
                "public func foo(\n",
                "    _ x: Int\n",
                ") {\n",
                "    return x\n",
                "}\n"
            };
            Assert.Equal(5, SwiftWrapperPostProcessor.FindBlockEnd(lines, 0));
        }

        [Fact]
        public void Process_MultiLineSignatureWithInternalType_StripsBlock()
        {
            // Full integration: @_silgen_name with multi-line signature referencing
            // an internal type should be stripped by the post-processor.
            var internalTypes = new HashSet<string> { "InternalDataSource" };
            var input = """
                // before
                @_silgen_name("PInvoke_get_dataSource")
                public func PInvoke_get_dataSource(
                    _ _self: UnsafeMutableRawPointer
                ) {
                    let __self = Unmanaged<InternalDataSource>.fromOpaque(_self).takeUnretainedValue()
                    return __self.dataSource
                }
                // after

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("// before", result.CleanedContent);
            Assert.Contains("// after", result.CleanedContent);
            Assert.DoesNotContain("InternalDataSource", result.CleanedContent);
        }
    }

    #endregion

    #region L. Swift-Unavailable Type Stripping Tests

    public class SwiftUnavailableTypeTests
    {
        [Fact]
        public void Process_CdeclReferencingNSInvocation_IsStripped()
        {
            var input = """
                @_cdecl("SBW_Quick_forwardInvocation")
                public func SBW_Quick_forwardInvocation(_ self_: UnsafeRawPointer, _ invocation: NSInvocation) {
                    let obj = Unmanaged<QuickSpec>.fromOpaque(self_).takeUnretainedValue()
                    obj.forwardInvocation(invocation)
                }

                @_cdecl("SBW_Quick_getName")
                public func SBW_Quick_getName(_ self_: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    let obj = Unmanaged<QuickSpec>.fromOpaque(self_).takeUnretainedValue()
                    return SwiftBindingsHelpers.stringToPointer(obj.name)
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("NSInvocation", result.CleanedContent);
            Assert.DoesNotContain("forwardInvocation", result.CleanedContent);
            // The clean function should be preserved
            Assert.Contains("SBW_Quick_getName", result.CleanedContent);
        }

        [Fact]
        public void Process_ExtensionReferencingNSInvocation_IsStripped()
        {
            var input = """
                extension QuickSpec {
                    @objc func forwardInvocation(_ invocation: NSInvocation) {
                        super.forwardInvocation(invocation)
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("NSInvocation", result.CleanedContent);
        }

        [Fact]
        public void Process_BlockWithoutNSInvocation_Preserved()
        {
            var input = """
                @_cdecl("SBW_Quick_getName")
                public func SBW_Quick_getName(_ self_: UnsafeRawPointer) -> UnsafeMutableRawPointer {
                    let obj = Unmanaged<QuickSpec>.fromOpaque(self_).takeUnretainedValue()
                    return SwiftBindingsHelpers.stringToPointer(obj.name)
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SBW_Quick_getName", result.CleanedContent);
        }
    }

    #endregion

    #region M. Wrapper Preamble Cleanup (dangling @available)

    /// <summary>
    /// When a `@_cdecl` / `@_silgen_name` block is stripped, the post-processor must
    /// also pop the wrapper-emitter preamble that precedes it (`// Property getter
    /// @_cdecl wrapper for ...` comments + `@available(...)` annotations + blank
    /// lines). Leaving them in place produces "expected declaration" errors at swiftc
    /// time, since the annotations end up attached to whatever comes next.
    /// </summary>
    public class PostProcessorPreambleCleanupTests
    {
        [Fact]
        public void Process_StripsDanglingAvailableAnnotationsBeforeStrippedCdecl()
        {
            var internalTypes = new HashSet<string> { "InternalType" };
            var input = """
                func unrelated() {}

                // Property getter @_cdecl wrapper for Foo.bar.
                // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
                @available(iOS 16.4, *)
                @available(visionOS 1.0, *)
                @_cdecl("SBW_Get_Foo_bar")
                public func _sbw_get_bar(_ resultPtr: UnsafeMutableRawPointer, _ self_: UnsafeRawPointer) {
                    let obj = self_.assumingMemoryBound(to: InternalType.self).pointee
                    let result = obj.bar
                }

                func nextThing() {}

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("@available", result.CleanedContent);
            Assert.DoesNotContain("@_cdecl", result.CleanedContent);
            Assert.DoesNotContain("// Property getter", result.CleanedContent);
            Assert.DoesNotContain("Routes through", result.CleanedContent);
            Assert.Contains("func unrelated()", result.CleanedContent);
            Assert.Contains("func nextThing()", result.CleanedContent);
        }

        [Fact]
        public void Process_StripsConsecutiveStrippedWrappersWithoutLeavingDanglingPreambles()
        {
            // Three stripped wrappers in a row — every preamble must come out.
            var internalTypes = new HashSet<string> { "InternalType" };
            var input = """
                // before

                // Property getter @_cdecl wrapper for Foo.first.
                // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
                @available(iOS 16.4, *)
                @_cdecl("SBW_Get_Foo_first")
                public func _sbw_get_first(_ resultPtr: UnsafeMutableRawPointer, _ self_: UnsafeRawPointer) {
                    let obj = self_.assumingMemoryBound(to: InternalType.self).pointee
                }

                // Property getter @_cdecl wrapper for Foo.second.
                // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
                @available(iOS 16.4, *)
                @_cdecl("SBW_Get_Foo_second")
                public func _sbw_get_second(_ resultPtr: UnsafeMutableRawPointer, _ self_: UnsafeRawPointer) {
                    let obj = self_.assumingMemoryBound(to: InternalType.self).pointee
                }

                // Method @_cdecl wrapper for Foo.third.
                // Routes method through C calling convention to avoid CallConvSwift crash on NativeAOT.
                @available(iOS 16.4, *)
                @_cdecl("SBW_Foo_third")
                public func _sbw_third(_ self_: UnsafeRawPointer) {
                    let obj = self_.assumingMemoryBound(to: InternalType.self).pointee
                }

                // after

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);
            Assert.Equal(3, result.StrippedBlockCount);
            Assert.DoesNotContain("@available", result.CleanedContent);
            Assert.DoesNotContain("@_cdecl", result.CleanedContent);
            Assert.DoesNotContain("Property getter", result.CleanedContent);
            Assert.DoesNotContain("Method @_cdecl wrapper", result.CleanedContent);
            Assert.Contains("// before", result.CleanedContent);
            Assert.Contains("// after", result.CleanedContent);
        }

        [Fact]
        public void Process_PreservesAvailableOnSurvivingWrapper()
        {
            // The @available preamble in front of a wrapper that is NOT stripped must remain.
            var input = """
                // Property getter @_cdecl wrapper for Foo.bar.
                // Routes through C calling convention to avoid CallConvSwift crash on NativeAOT.
                @available(iOS 16.4, *)
                @_cdecl("SBW_Get_Foo_bar")
                public func _sbw_get_bar(_ resultPtr: UnsafeMutableRawPointer, _ self_: UnsafeRawPointer) {
                    let obj = self_.assumingMemoryBound(to: PublicType.self).pointee
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypeNames: null);
            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("@available(iOS 16.4, *)", result.CleanedContent);
            Assert.Contains("@_cdecl(\"SBW_Get_Foo_bar\")", result.CleanedContent);
            Assert.Contains("// Property getter", result.CleanedContent);
        }

        [Fact]
        public void Process_DoesNotPopUnrelatedCommentsBelongingToPreviousDeclaration()
        {
            // The line above the stripped wrapper's preamble is a `}` (end of previous
            // declaration). The preamble cleanup must stop at the `}` and not eat code
            // from above.
            var internalTypes = new HashSet<string> { "InternalType" };
            var input = """
                public func keepMe() {
                    let x = 1
                }

                @available(iOS 16.4, *)
                @_cdecl("SBW_Foo_broken")
                public func _sbw_broken(_ self_: UnsafeRawPointer) {
                    let obj = self_.assumingMemoryBound(to: InternalType.self).pointee
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("public func keepMe()", result.CleanedContent);
            Assert.Contains("let x = 1", result.CleanedContent);
            Assert.DoesNotContain("@available", result.CleanedContent);
            Assert.DoesNotContain("@_cdecl", result.CleanedContent);
        }
    }

    #endregion

    #region G. Sub-cause classification

    public class PostProcessorSubCauseClassifierTests
    {
        [Fact]
        public void Process_ClassifiesInternalTypeStrip()
        {
            var internalTypes = new HashSet<string> { "InternalType" };
            var input = """
                @_cdecl("SBW_broken")
                public func _sbw_broken(_ self_: UnsafeRawPointer) {
                    let x = self_.assumingMemoryBound(to: InternalType.self).pointee
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);

            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Equal(1, result.StrippedBlocksBySubCause[StripSubCause.InternalType]);
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.NSInvocation]);
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.Other]);
        }

        [Fact]
        public void Process_ClassifiesNSInvocationStrip()
        {
            var input = """
                @_cdecl("SBW_broken")
                public func _sbw_broken(_ inv: NSInvocation) {
                    inv.invoke()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.InternalType]);
            Assert.Equal(1, result.StrippedBlocksBySubCause[StripSubCause.NSInvocation]);
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.Other]);
        }

        [Fact]
        public void Process_ClassifiesOtherStripFromEveryProtocolPlaceholder()
        {
            // EveryProtocol() placeholder body is a Pattern 2 (a) "broken" trigger,
            // not an internal-type or NSInvocation reach. Bucket = Other.
            var input = """
                @_cdecl("SBW_broken_placeholder")
                public func _sbw_broken_placeholder() -> EveryProtocol {
                    return EveryProtocol()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.InternalType]);
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.NSInvocation]);
            Assert.Equal(1, result.StrippedBlocksBySubCause[StripSubCause.Other]);
        }

        [Fact]
        public void Process_PrioritisesPatternBrokenOverInternalType()
        {
            // A block that hits BOTH the Pattern 2 (a) placeholder trigger AND references
            // an internal type must classify as Other (the broken-shape trigger is
            // higher priority than the internal-type reach, matching the post-processor's
            // short-circuit OR order). Otherwise the InternalType bucket would
            // mis-attribute strips that the new emission gate cannot prevent.
            var internalTypes = new HashSet<string> { "InternalType" };
            var input = """
                @_cdecl("SBW_broken_both")
                public func _sbw_broken_both() -> EveryProtocol {
                    let x: InternalType? = nil
                    _ = x
                    return EveryProtocol()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);

            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.InternalType]);
            Assert.Equal(1, result.StrippedBlocksBySubCause[StripSubCause.Other]);
        }

        [Fact]
        public void Process_AggregatesAcrossMultipleBlocks()
        {
            var internalTypes = new HashSet<string> { "InternalType" };
            var input = """
                @_cdecl("SBW_a")
                public func _sbw_a(_ self_: UnsafeRawPointer) {
                    let x = self_.assumingMemoryBound(to: InternalType.self).pointee
                }

                @_cdecl("SBW_b")
                public func _sbw_b(_ inv: NSInvocation) {
                    inv.invoke()
                }

                @_cdecl("SBW_c")
                public func _sbw_c() -> EveryProtocol {
                    return EveryProtocol()
                }

                @_cdecl("SBW_keep")
                public func _sbw_keep() -> Int { return 0 }

                """;
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);

            Assert.Equal(3, result.StrippedBlockCount);
            Assert.Equal(1, result.StrippedBlocksBySubCause[StripSubCause.InternalType]);
            Assert.Equal(1, result.StrippedBlocksBySubCause[StripSubCause.NSInvocation]);
            Assert.Equal(1, result.StrippedBlocksBySubCause[StripSubCause.Other]);
            Assert.Contains("_sbw_keep", result.CleanedContent);
        }

        [Fact]
        public void Process_NoStrips_ReturnsZeroBuckets()
        {
            var input = """
                @_cdecl("SBW_keep")
                public func _sbw_keep() -> Int { return 0 }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);

            Assert.Equal(0, result.StrippedBlockCount);
            // Buckets are eagerly initialized to zero so callers can format/aggregate
            // without null-checks.
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.InternalType]);
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.NSInvocation]);
            Assert.Equal(0, result.StrippedBlocksBySubCause[StripSubCause.Other]);
        }
    }

    #endregion

}

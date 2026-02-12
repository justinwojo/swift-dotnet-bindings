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
        public void Process_ExtensionEveryProtocol_Stripped()
        {
            var input = """
                // before
                extension EveryProtocol: SomeProtocol {
                    func foo() { }
                }
                // after

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("// before", result.CleanedContent);
            Assert.Contains("// after", result.CleanedContent);
            Assert.DoesNotContain("EveryProtocol", result.CleanedContent);
        }

        [Fact]
        public void Process_ClassEveryProtocol_Stripped()
        {
            var input = """
                class EveryProtocol {
                    var x: Int = 0
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("EveryProtocol", result.CleanedContent);
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
        public void Process_SilgenNameWithBareSelf_Stripped()
        {
            var input = """
                @_silgen_name("free_func")
                public func free_func() {
                    self.doSomething()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("self.doSomething", result.CleanedContent);
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
        public void Process_SilgenNameWithAsyncInit_Stripped()
        {
            var input = """
                @_silgen_name("init_wrapper")
                public func init_wrapper() {
                    __self.init(value: 42)
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.DoesNotContain("__self.init", result.CleanedContent);
        }

        [Fact]
        public void Process_SilgenNameWithMutatingLetExistential_Stripped()
        {
            var input = """
                @_silgen_name("existential_func")
                public func existential_func(_self: UnsafeMutableRawPointer) {
                    let existential = buffer.load(as: (any SomeProtocol).self)
                    existential.mutate()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
        }

        [Fact]
        public void Process_SilgenNameWithNonEscapingClosureTask_Stripped()
        {
            var input = """
                @_silgen_name("closure_task_func")
                public func closure_task_func(_self: UnsafeMutableRawPointer, callback: (Int32) -> Int32) {
                    Task {
                        let result = callback(42)
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
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
        public void Process_ExtensionWithAsyncInit_Stripped()
        {
            var input = """
                extension SomeType {
                    func wrapper() {
                        __self.init(value: 1)
                    }
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
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
        public void Process_ExtensionWithClosureInTask_Stripped()
        {
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
            Assert.Equal(1, result.StrippedBlockCount);
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
        public void Process_SBWFuncWithMutatingLetExistential_Stripped()
        {
            var input = """
                public func SBW_existentialFunc(ptr: UnsafeMutableRawPointer) {
                    let existential = ptr.load(as: (any SomeProtocol).self)
                    existential.mutate()
                }

                """;
            var result = SwiftWrapperPostProcessor.Process(input);
            Assert.Equal(1, result.StrippedBlockCount);
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
        public void Process_MultiplePatterns_AllStripped()
        {
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
            Assert.Equal(4, result.StrippedBlockCount);
            Assert.DoesNotContain("EveryProtocol", result.CleanedContent);
            Assert.DoesNotContain("bad1", result.CleanedContent);
            Assert.DoesNotContain("SBW_bad2", result.CleanedContent);
        }

        [Fact]
        public void Process_PreservesImportsAndComments()
        {
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
            Assert.Equal(1, result.StrippedBlockCount);
            Assert.Contains("import Foundation", result.CleanedContent);
            Assert.Contains("import SwiftBindingsTestLib", result.CleanedContent);
            Assert.Contains("// Keep this", result.CleanedContent);
            Assert.Contains("good", result.CleanedContent);
        }
    }

    #endregion

    #region G. Internal Type Stripping (WU2)

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
                public func SBW_create(_self: UnsafeMutableRawPointer) -> SkeletonView {
                    return SkeletonView()
                }

                """;
            var internalTypes = new HashSet<string> { "SkeletonLayer" };
            var result = SwiftWrapperPostProcessor.Process(input, internalTypes);

            Assert.Equal(0, result.StrippedBlockCount);
            Assert.Contains("SkeletonView", result.CleanedContent);
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
}

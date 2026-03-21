// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using Xunit;

namespace BindingsGeneration.Tests
{
    #region A. P/Invoke Detection

    public class CoGaterPInvokeDetectionTests
    {
        [Fact]
        public void Process_StrippedPInvoke_RemovesPInvokeDeclaration()
        {
            var input =
                "namespace Test {\n" +
                "public partial class Foo {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken_method\")]\n" +
                "    private static partial void PInvoke_broken_ABC123(IntPtr ptr);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken_method" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_broken_ABC123", result.Content);
            Assert.DoesNotContain("SBW_broken_method", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_FullyQualifiedAttribute_DetectsCorrectly()
        {
            var input =
                "public partial class Foo {\n" +
                "    [global::System.Runtime.InteropServices.LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_eq_broken\")]\n" +
                "    internal static partial int PInvoke_eq_DEADBEEF(IntPtr a, IntPtr b);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_eq_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_eq_DEADBEEF", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_NativeLibraryPInvoke_NotAffected()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindingsTestLib\", EntryPoint = \"$s20SwiftBindingsTestLib_mangled\")]\n" +
                "    private static partial int PInvoke_native_123(IntPtr ptr);\n" +
                "}\n";
            var stripped = new HashSet<string> { "$s20SwiftBindingsTestLib_mangled" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.Contains("PInvoke_native_123", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_SBWFree_NeverStripped()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Free_TestLib\")]\n" +
                "    private static partial void SBW_Free(IntPtr ptr);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Free_TestLib" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.Contains("SBW_Free", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_InternalVisibility_DetectsCorrectly()
        {
            var input =
                "public partial class Foo {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_GetMetadata_broken\")]\n" +
                "    internal static partial TypeMetadata PInvoke_getMetadata(TypeMetadataRequest req);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_GetMetadata_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_getMetadata", result.Content);
        }
    }

    #endregion

    #region B. Constructor Stripping (Level 1)

    public class CoGaterConstructorStrippingTests
    {
        [Fact]
        public void Process_ConstructorCallingStrippedPInvoke_Removed()
        {
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_init_broken\")]\n" +
                "    private static partial void PInvoke_init_ABC123(IntPtr resultPtr, int value);\n" +
                "\n" +
                "    public MyClass(int value)\n" +
                "    {\n" +
                "        unsafe\n" +
                "        {\n" +
                "            PInvoke_init_ABC123(resultPtr, value);\n" +
                "        }\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_init_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_init_ABC123", result.Content);
            Assert.DoesNotContain("public MyClass(int value)", result.Content);
        }
    }

    #endregion

    #region C. Method Stripping (Level 1)

    public class CoGaterMethodStrippingTests
    {
        [Fact]
        public void Process_StaticMethodCallingStrippedPInvoke_Removed()
        {
            // Non-virtual static methods are safe to strip
            var input =
                "public partial class MyClass {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_doStuff_broken\")]\n" +
                "    private static partial void PInvoke_doStuff_DEF456(IntPtr ptr, int arg);\n" +
                "\n" +
                "    public static string DoStuff(int arg)\n" +
                "    {\n" +
                "        unsafe\n" +
                "        {\n" +
                "            PInvoke_doStuff_DEF456(resultPtr, arg);\n" +
                "            return result;\n" +
                "        }\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_doStuff_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_doStuff_DEF456", result.Content);
            Assert.DoesNotContain("DoStuff", result.Content);
        }

        [Fact]
        public void Process_VirtualMethodNotInInterface_Stripped()
        {
            // Virtual methods that don't implement any interface CAN be stripped
            var input =
                "public partial class MyClass {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_doStuff_broken\")]\n" +
                "    private static partial void PInvoke_doStuff_DEF456(IntPtr ptr, int arg);\n" +
                "\n" +
                "    public virtual string DoStuff(int arg)\n" +
                "    {\n" +
                "        PInvoke_doStuff_DEF456(resultPtr, arg);\n" +
                "        return result;\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_doStuff_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_doStuff_DEF456", result.Content);
            Assert.DoesNotContain("DoStuff", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_InterfaceMethodCallingStrippedPInvoke_Preserved()
        {
            // Methods implementing protocol interface members must NOT be stripped
            var input =
                "public interface IConnectionDelegate {\n" +
                "    void DidReceive(ServerEvent @event);\n" +
                "}\n" +
                "public partial class WebSocketServer : ISwiftObject, IConnectionDelegate {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_didReceive_broken\")]\n" +
                "    private static partial void PInvoke_didReceive_AAA(IntPtr evt, IntPtr self);\n" +
                "\n" +
                "    public virtual void DidReceive(ServerEvent @event)\n" +
                "    {\n" +
                "        PInvoke_didReceive_AAA(@event.Handle, selfPtr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_didReceive_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // Both preserved — DidReceive implements IConnectionDelegate
            Assert.Contains("PInvoke_didReceive_AAA", result.Content);
            Assert.Contains("DidReceive", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_InterfacePropertyHelper_Preserved()
        {
            // Property helpers for interface properties must not be stripped
            var input =
                "public interface IProcessingMode {\n" +
                "    string ModeName { get; }\n" +
                "}\n" +
                "public partial class SimpleMode : ISwiftObject, IProcessingMode {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_modeName_broken\")]\n" +
                "    private static partial void PInvoke_modeName_Get_BBB(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "    private string ModeName_Get()\n" +
                "    {\n" +
                "        PInvoke_modeName_Get_BBB(resultPtr, selfPtr);\n" +
                "        return result;\n" +
                "    }\n" +
                "\n" +
                "    public virtual string ModeName\n" +
                "    {\n" +
                "        get => ModeName_Get();\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Get_modeName_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // All preserved — ModeName is an interface property on this type
            Assert.Contains("PInvoke_modeName_Get_BBB", result.Content);
            Assert.Contains("ModeName_Get", result.Content);
            Assert.Contains("public virtual string ModeName", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_TypeScopeDoesNotLeakPastClosingBrace()
        {
            // After TypeA's closing brace, TypeB must NOT inherit TypeA's interface protection.
            // This verifies the line-to-type map correctly pops types at their closing brace.
            var input =
                "public interface IFoo {\n" +
                "    void DoWork();\n" +
                "}\n" +
                "public partial class TypeA : ISwiftObject, IFoo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_doWorkA\")]\n" +
                "    private static partial void PInvoke_doWork_AAA(IntPtr self);\n" +
                "\n" +
                "    public virtual void DoWork()\n" +
                "    {\n" +
                "        PInvoke_doWork_AAA(selfPtr);\n" +
                "    }\n" +
                "}\n" +
                "public partial class TypeB : ISwiftObject {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_doWorkB\")]\n" +
                "    private static partial void PInvoke_doWork_BBB(IntPtr self);\n" +
                "\n" +
                "    public virtual void DoWork()\n" +
                "    {\n" +
                "        PInvoke_doWork_BBB(selfPtr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_doWorkA", "SBW_doWorkB" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // TypeA: DoWork preserved (implements IFoo)
            Assert.Contains("PInvoke_doWork_AAA", result.Content);
            // TypeB: DoWork stripped (does NOT implement IFoo — scope must not leak from TypeA)
            Assert.DoesNotContain("PInvoke_doWork_BBB", result.Content);
        }

        [Fact]
        public void Process_SameNameMemberOnNonImplementingType_Stripped()
        {
            // INameable declares 'Name'. TypeA implements INameable (protected).
            // TypeB does NOT implement INameable — its 'Name' should be stripped normally.
            var input =
                "public interface INameable {\n" +
                "    string Name { get; }\n" +
                "}\n" +
                "public partial class TypeA : ISwiftObject, INameable {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_nameA_broken\")]\n" +
                "    private static partial void PInvoke_name_Get_AAA(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "    private string Name_Get()\n" +
                "    {\n" +
                "        PInvoke_name_Get_AAA(resultPtr, selfPtr);\n" +
                "        return result;\n" +
                "    }\n" +
                "\n" +
                "    public virtual string Name\n" +
                "    {\n" +
                "        get => Name_Get();\n" +
                "    }\n" +
                "}\n" +
                "public partial class TypeB : ISwiftObject {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_nameB_broken\")]\n" +
                "    private static partial void PInvoke_name_Get_BBB(IntPtr resultPtr, IntPtr self);\n" +
                "\n" +
                "    private string Name_Get()\n" +
                "    {\n" +
                "        PInvoke_name_Get_BBB(resultPtr, selfPtr);\n" +
                "        return result;\n" +
                "    }\n" +
                "\n" +
                "    public virtual string Name\n" +
                "    {\n" +
                "        get => Name_Get();\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Get_nameA_broken", "SBW_Get_nameB_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // TypeA: preserved (Name implements INameable)
            Assert.Contains("PInvoke_name_Get_AAA", result.Content);
            Assert.Contains("TypeA", result.Content);
            // TypeB: stripped (Name is NOT an interface implementation)
            Assert.DoesNotContain("PInvoke_name_Get_BBB", result.Content);
        }

        [Fact]
        public void Process_MultipleOverloadsCallingStrippedPInvoke_BothRemoved()
        {
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_create_broken\")]\n" +
                "    private static partial void PInvoke_create_111(IntPtr ptr, int a, int b);\n" +
                "\n" +
                "    public MyClass(int a, int b)\n" +
                "    {\n" +
                "        PInvoke_create_111(resultPtr, a, b);\n" +
                "    }\n" +
                "\n" +
                "    public MyClass(int a)\n" +
                "    {\n" +
                "        PInvoke_create_111(resultPtr, a, 0);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_create_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_create_111", result.Content);
            Assert.DoesNotContain("public MyClass(int a, int b)", result.Content);
            Assert.DoesNotContain("public MyClass(int a)", result.Content);
        }
    }

    #endregion

    #region D. Property Stripping (Level 1 + Level 2 Transitivity)

    public class CoGaterPropertyStrippingTests
    {
        [Fact]
        public void Process_PropertyGetterStripped_HelperAndForwarderRemoved()
        {
            var input =
                "public partial class MyClass {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_count_broken\")]\n" +
                "    private static partial int PInvoke_count_Get_AAA(IntPtr self);\n" +
                "\n" +
                "    private int Count_Get()\n" +
                "    {\n" +
                "        unsafe\n" +
                "        {\n" +
                "            return PInvoke_count_Get_AAA(selfPtr);\n" +
                "        }\n" +
                "    }\n" +
                "\n" +
                "    public int Count\n" +
                "    {\n" +
                "        get => Count_Get();\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Get_count_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // P/Invoke removed
            Assert.DoesNotContain("PInvoke_count_Get_AAA", result.Content);
            // Level 1 helper removed
            Assert.DoesNotContain("Count_Get", result.Content);
            // Level 2 property forwarder removed
            Assert.DoesNotContain("public int Count", result.Content);
        }

        [Fact]
        public void Process_PropertySetterStripped_PropertyEntirelyRemoved()
        {
            // When setter P/Invoke is stripped, the setter helper is removed.
            // The property forwarder that references the setter helper is also removed.
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Set_name_broken\")]\n" +
                "    private static partial void PInvoke_name_Set_BBB(IntPtr self, IntPtr val);\n" +
                "\n" +
                "    private void Name_Set(string value)\n" +
                "    {\n" +
                "        PInvoke_name_Set_BBB(selfPtr, valPtr);\n" +
                "    }\n" +
                "\n" +
                "    public string Name\n" +
                "    {\n" +
                "        get => Name_Get();\n" +
                "        set => Name_Set(value);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Set_name_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_name_Set_BBB", result.Content);
            Assert.DoesNotContain("Name_Set", result.Content);
            // The property references Name_Set, so it's removed entirely
            Assert.DoesNotContain("public string Name", result.Content);
        }
    }

    #endregion

    #region E. GetMetadata Fallback Exemption

    public class CoGaterGetMetadataExemptionTests
    {
        [Fact]
        public void Process_GetMetadataWithFallback_BothPInvokeAndCallerPreserved()
        {
            // When a caller has DllNotFoundException fallback, both the P/Invoke declaration
            // AND the caller must be preserved. The P/Invoke will throw DllNotFoundException
            // at runtime, which the caller catches and falls back gracefully.
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_GetMetadata_TestLib_MyClass_FFF\")]\n" +
                "    internal static partial TypeMetadata PInvoke_getMetadata();\n" +
                "\n" +
                "    [LibraryImport(\"TestLib\", EntryPoint = \"$s7TestLib7MyClassVMa\")]\n" +
                "    internal static partial TypeMetadata PInvoke_getMetadata_fallback();\n" +
                "\n" +
                "    static TypeMetadata ISwiftObject.GetTypeMetadata()\n" +
                "    {\n" +
                "        try\n" +
                "        {\n" +
                "            return PInvoke_getMetadata();\n" +
                "        }\n" +
                "        catch (System.DllNotFoundException)\n" +
                "        {\n" +
                "            return PInvoke_getMetadata_fallback();\n" +
                "        }\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_GetMetadata_TestLib_MyClass_FFF" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // P/Invoke declaration IS preserved (exempted because caller has DllNotFoundException)
            Assert.Contains("PInvoke_getMetadata()", result.Content);
            // Caller IS preserved (has DllNotFoundException fallback)
            Assert.Contains("GetTypeMetadata", result.Content);
            Assert.Contains("PInvoke_getMetadata_fallback", result.Content);
            // No members stripped
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_GetMetadata_OtherPInvokesStillStripped()
        {
            // A GetMetadata exemption should not prevent stripping of other P/Invokes
            var input =
                "public partial class MyClass {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_GetMetadata_broken\")]\n" +
                "    internal static partial TypeMetadata PInvoke_getMetadata();\n" +
                "\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_method_broken\")]\n" +
                "    private static partial void PInvoke_method_CCC(IntPtr ptr);\n" +
                "\n" +
                "    static TypeMetadata ISwiftObject.GetTypeMetadata()\n" +
                "    {\n" +
                "        try { return PInvoke_getMetadata(); }\n" +
                "        catch (System.DllNotFoundException) { return default; }\n" +
                "    }\n" +
                "\n" +
                "    public void BrokenMethod()\n" +
                "    {\n" +
                "        PInvoke_method_CCC(ptr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_GetMetadata_broken", "SBW_method_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // GetMetadata P/Invoke and caller preserved
            Assert.Contains("PInvoke_getMetadata()", result.Content);
            Assert.Contains("GetTypeMetadata", result.Content);
            // Other P/Invoke and its caller stripped
            Assert.DoesNotContain("PInvoke_method_CCC", result.Content);
            Assert.DoesNotContain("BrokenMethod", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }
    }

    #endregion

    #region F. Edge Cases

    public class CoGaterEdgeCaseTests
    {
        [Fact]
        public void Process_EmptyStrippedSet_NoChanges()
        {
            var input = "public class Foo { }\n";
            var stripped = new HashSet<string>();
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.Equal(input, result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_EmptyContent_NoChanges()
        {
            var stripped = new HashSet<string> { "SBW_broken" };
            var result = CSharpWrapperCoGater.Process("", stripped);
            Assert.Equal("", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_NoMatchingPInvokes_NoChanges()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_good_func\")]\n" +
                "    private static partial void PInvoke_good_123(IntPtr ptr);\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_unrelated_symbol" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.Contains("PInvoke_good_123", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_NestedPInvokeClass_OnlyPInvokeRemoved()
        {
            var input =
                "public partial struct AcceptsSummable {\n" +
                "    internal static partial class AcceptsSummable_PInvoke {\n" +
                "        [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken_init\")]\n" +
                "        internal static partial void PInvoke_init_F22(IntPtr resultPtr, IntPtr item);\n" +
                "\n" +
                "        [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_good_get\")]\n" +
                "        internal static partial void PInvoke_item_Get_9EE(IntPtr resultPtr, IntPtr self);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken_init" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // Stripped P/Invoke is removed
            Assert.DoesNotContain("PInvoke_init_F22", result.Content);
            // Good P/Invoke is preserved
            Assert.Contains("PInvoke_item_Get_9EE", result.Content);
            // Class structure is preserved
            Assert.Contains("AcceptsSummable_PInvoke", result.Content);
        }

        [Fact]
        public void Process_DocCommentsAndAttributes_IncludedInRemoval()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_method_broken\")]\n" +
                "    private static partial void PInvoke_method_CCC(IntPtr ptr);\n" +
                "\n" +
                "    [global::Swift.UnsupportedSwiftType(\"fallback\", \"any Proto\")]\n" +
                "    /// <summary>\n" +
                "    /// Does the broken thing.\n" +
                "    /// </summary>\n" +
                "    public static string BrokenMethod(int arg)\n" +
                "    {\n" +
                "        PInvoke_method_CCC(ptr);\n" +
                "        return result;\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_method_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("BrokenMethod", result.Content);
            Assert.DoesNotContain("Does the broken thing", result.Content);
            Assert.DoesNotContain("UnsupportedSwiftType", result.Content);
        }

        [Fact]
        public void Process_PreservesUnrelatedMembers()
        {
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken\")]\n" +
                "    private static partial void PInvoke_broken_111(IntPtr ptr);\n" +
                "\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_good\")]\n" +
                "    private static partial void PInvoke_good_222(IntPtr ptr);\n" +
                "\n" +
                "    public void GoodMethod()\n" +
                "    {\n" +
                "        PInvoke_good_222(ptr);\n" +
                "    }\n" +
                "\n" +
                "    public void BrokenMethod()\n" +
                "    {\n" +
                "        PInvoke_broken_111(ptr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_broken_111", result.Content);
            Assert.DoesNotContain("BrokenMethod", result.Content);
            Assert.Contains("PInvoke_good_222", result.Content);
            Assert.Contains("GoodMethod", result.Content);
        }

        [Fact]
        public void Process_ModuleSpecificWrapperLibrary_DetectsCorrectly()
        {
            // Issue #2: Wrapper library name is "{ModuleName}SwiftBindings" in SDK mode.
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"NukeSwiftBindings\", EntryPoint = \"SBW_broken_func\")]\n" +
                "    private static partial void PInvoke_broken_AAA(IntPtr ptr);\n" +
                "\n" +
                "    public void BrokenMethod()\n" +
                "    {\n" +
                "        PInvoke_broken_AAA(ptr);\n" +
                "    }\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken_func" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            Assert.DoesNotContain("PInvoke_broken_AAA", result.Content);
            Assert.DoesNotContain("BrokenMethod", result.Content);
            Assert.Equal(1, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_AmbiguousPInvokeName_SkippedEntirely()
        {
            // When the same P/Invoke method name appears in multiple class scopes,
            // file-wide matching would false-match. Skip these entirely.
            var input =
                "public partial class TypeA {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_TypeA_eq_AAA\")]\n" +
                "    private static partial bool PInvoke_eq(IntPtr lhs, IntPtr rhs);\n" +
                "\n" +
                "    public bool Equals(TypeA? other) { return PInvoke_eq(lhs, rhs); }\n" +
                "}\n" +
                "public partial class TypeB {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_TypeB_eq_BBB\")]\n" +
                "    private static partial bool PInvoke_eq(IntPtr lhs, IntPtr rhs);\n" +
                "\n" +
                "    public bool Equals(TypeB? other) { return PInvoke_eq(lhs, rhs); }\n" +
                "}\n";
            // Only TypeA's eq symbol is stripped, but PInvoke_eq is ambiguous
            var stripped = new HashSet<string> { "SBW_TypeA_eq_AAA" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // Both types should be fully preserved (ambiguous name → skip entirely)
            Assert.Contains("TypeA", result.Content);
            Assert.Contains("TypeB", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void Process_PrefixCollision_DoesNotOverStrip()
        {
            // Issue #3: PInvoke_foo_ABC must not match PInvoke_foo_ABC123.
            var input =
                "public partial class Foo {\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_short\")]\n" +
                "    private static partial void PInvoke_foo_ABC(IntPtr ptr);\n" +
                "\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_long\")]\n" +
                "    private static partial void PInvoke_foo_ABC123(IntPtr ptr);\n" +
                "\n" +
                "    public void ShortMethod()\n" +
                "    {\n" +
                "        PInvoke_foo_ABC(ptr);\n" +
                "    }\n" +
                "\n" +
                "    public void LongMethod()\n" +
                "    {\n" +
                "        PInvoke_foo_ABC123(ptr);\n" +
                "    }\n" +
                "}\n";
            // Only strip the short symbol — long should be unaffected
            var stripped = new HashSet<string> { "SBW_short" };
            var result = CSharpWrapperCoGater.Process(input, stripped);
            // Short P/Invoke and its caller stripped
            Assert.DoesNotContain("PInvoke_foo_ABC(", result.Content);
            Assert.DoesNotContain("ShortMethod", result.Content);
            // Long P/Invoke and its caller preserved (not prefix-matched)
            Assert.Contains("PInvoke_foo_ABC123", result.Content);
            Assert.Contains("LongMethod", result.Content);
        }
    }

    #endregion

    #region G. Helper Method Tests

    public class CoGaterHelperTests
    {
        [Theory]
        [InlineData("[LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[global::System.Runtime.InteropServices.LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[LibraryImport(\"NukeSwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[LibraryImport(\"BlinkIDSwiftBindings\", EntryPoint = \"SBW_test\")]", true)]
        [InlineData("[LibraryImport(\"SwiftBindingsTestLib\", EntryPoint = \"$s_mangled\")]", false)]
        [InlineData("[LibraryImport(\"OtherLib\", EntryPoint = \"func\")]", false)]
        [InlineData("[LibraryImport(\"SwiftBindings\")]", false)] // no EntryPoint
        public void IsWrapperLibraryImportLine_DetectsCorrectly(string line, bool expected)
        {
            Assert.Equal(expected, CSharpWrapperCoGater.IsWrapperLibraryImportLine(line));
        }

        [Theory]
        [InlineData("    private static partial void PInvoke_method_ABC(IntPtr ptr);", "PInvoke_method_ABC")]
        [InlineData("    internal static partial TypeMetadata PInvoke_getMetadata();", "PInvoke_getMetadata")]
        [InlineData("    private static partial void SBW_Free(IntPtr ptr);", "SBW_Free")]
        public void ExtractMethodNameFromPartialDecl_ExtractsCorrectly(string line, string expected)
        {
            Assert.Equal(expected, CSharpWrapperCoGater.ExtractMethodNameFromPartialDecl(line));
        }

        [Theory]
        [InlineData("public static string DoStuff(int arg)", "DoStuff")]
        [InlineData("public MyClass(int value)", "MyClass")]
        [InlineData("private int Value_Get()", "Value_Get")]
        [InlineData("static TypeMetadata ISwiftObject.GetTypeMetadata()", "GetTypeMetadata")]
        [InlineData("public int Count", "Count")]
        [InlineData("public virtual void Execute()", "Execute")]
        public void ExtractMemberName_ExtractsCorrectly(string trimmed, string expected)
        {
            Assert.Equal(expected, CSharpWrapperCoGater.ExtractMemberName(trimmed));
        }
    }

    #endregion

    #region J. Suppressed Proxy Reference Co-Gating

    public class CoGaterSuppressedProxyReferenceTests
    {
        [Fact]
        public void ProcessProxyReferences_MethodConstructingProxy_Removed()
        {
            var input =
                "public partial class MyClass {\n" +
                "    public static IMyProtocol GetValue()\n" +
                "    {\n" +
                "        var result = PInvoke_getValue();\n" +
                "        return new MyProtocolProxy(result);\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "MyProtocolProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            Assert.DoesNotContain("GetValue", result.Content);
            Assert.DoesNotContain("MyProtocolProxy", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_QualifiedProxy_Removed()
        {
            var input =
                "public partial class MyClass {\n" +
                "    public static IFooProtocol MakeFoo()\n" +
                "    {\n" +
                "        var result = PInvoke_makeFoo();\n" +
                "        return new SwiftInterop.FooProtocolProxy(result);\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "FooProtocolProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            Assert.DoesNotContain("MakeFoo", result.Content);
            Assert.DoesNotContain("FooProtocolProxy", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_NonSuppressedProxy_Preserved()
        {
            var input =
                "public partial class MyClass {\n" +
                "    public static IMyProtocol GetValue()\n" +
                "    {\n" +
                "        var result = PInvoke_getValue();\n" +
                "        return new MyProtocolProxy(result);\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "OtherProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            Assert.Contains("MyProtocolProxy", result.Content);
            Assert.Contains("GetValue", result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void ProcessProxyReferences_EmptySet_ReturnsUnchanged()
        {
            var input =
                "public partial class MyClass {\n" +
                "    public static IFoo Get() { return new FooProxy(x); }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string>();
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            Assert.Equal(input, result.Content);
            Assert.Equal(0, result.StrippedMemberCount);
        }

        [Fact]
        public void ProcessProxyReferences_PropertyHelper_TransitiveStripping()
        {
            // When a property helper (Value_Get) constructs a suppressed proxy,
            // it should be stripped AND its property forwarder should also be stripped.
            var input =
                "public partial class MyClass {\n" +
                "    private IMyProto Value_Get()\n" +
                "    {\n" +
                "        var result = PInvoke_getValue();\n" +
                "        return new MyProtoProxy(result);\n" +
                "    }\n" +
                "\n" +
                "    public IMyProto Value\n" +
                "    {\n" +
                "        get { return Value_Get(); }\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "MyProtoProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            Assert.DoesNotContain("Value_Get", result.Content);
            Assert.DoesNotContain("MyProtoProxy", result.Content);
            Assert.DoesNotContain("public IMyProto Value", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_OptionalExistentialGetter_Removed()
        {
            // Optional<existential> getter: checks for default container, then constructs proxy
            var input =
                "public partial class MyClass {\n" +
                "    private IMyProto? OptValue_Get()\n" +
                "    {\n" +
                "        var result = PInvoke_optValue();\n" +
                "        if (result.Equals(default(ExistentialContainer1))) return null;\n" +
                "        return new MyProtoProxy(result);\n" +
                "    }\n" +
                "\n" +
                "    public IMyProto? OptValue\n" +
                "    {\n" +
                "        get { return OptValue_Get(); }\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "MyProtoProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            Assert.DoesNotContain("OptValue_Get", result.Content);
            Assert.DoesNotContain("MyProtoProxy", result.Content);
            Assert.DoesNotContain("public IMyProto? OptValue", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_MethodNotConstructingProxy_Preserved()
        {
            // Methods that don't construct the proxy should be preserved even if
            // they mention the proxy name in a comment or string
            var input =
                "public partial class MyClass {\n" +
                "    public static int GetCount()\n" +
                "    {\n" +
                "        return PInvoke_getCount();\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "MyProtoProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            Assert.Contains("GetCount", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_MultipleProxies_AllStripped()
        {
            var input =
                "public partial class MyClass {\n" +
                "    public static IFoo GetFoo()\n" +
                "    {\n" +
                "        return new FooProxy(result);\n" +
                "    }\n" +
                "\n" +
                "    public static IBar GetBar()\n" +
                "    {\n" +
                "        return new BarProxy(result);\n" +
                "    }\n" +
                "\n" +
                "    public static int GetCount()\n" +
                "    {\n" +
                "        return 42;\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "FooProxy", "BarProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            Assert.DoesNotContain("FooProxy", result.Content);
            Assert.DoesNotContain("BarProxy", result.Content);
            Assert.Contains("GetCount", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_InterfaceMember_ReplacedWithThrow()
        {
            // Interface member implementations must NOT be stripped (CS0535).
            // Instead, their body is replaced with throw NotSupportedException.
            var input =
                "public interface IObjectScopeProtocol {\n" +
                "    IStorageProtocol MakeStorage();\n" +
                "}\n" +
                "public partial class ObjectScope : IObjectScopeProtocol {\n" +
                "    public IStorageProtocol MakeStorage()\n" +
                "    {\n" +
                "        var result = PInvoke_makeStorage();\n" +
                "        return new StorageProtocolProxy(result);\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "StorageProtocolProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            // Method declaration is preserved (interface compliance)
            Assert.Contains("MakeStorage", result.Content);
            // Body is replaced with throw
            Assert.Contains("throw new NotSupportedException", result.Content);
            // Original body is gone
            Assert.DoesNotContain("PInvoke_makeStorage", result.Content);
            Assert.DoesNotContain("StorageProtocolProxy", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_NonInterfaceMember_StillStripped()
        {
            // Methods NOT implementing an interface should still be fully stripped
            var input =
                "public interface IObjectScopeProtocol {\n" +
                "    IStorageProtocol MakeStorage();\n" +
                "}\n" +
                "public partial class ObjectScope : IObjectScopeProtocol {\n" +
                "    public IStorageProtocol MakeStorage()\n" +
                "    {\n" +
                "        return new StorageProtocolProxy(result);\n" +
                "    }\n" +
                "    public static IFoo GetSomething()\n" +
                "    {\n" +
                "        return new FooProxy(result);\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "StorageProtocolProxy", "FooProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            // Interface member preserved with throw
            Assert.Contains("MakeStorage", result.Content);
            Assert.Contains("throw new NotSupportedException", result.Content);
            // Non-interface member fully stripped
            Assert.DoesNotContain("GetSomething", result.Content);
            Assert.DoesNotContain("FooProxy", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_CdeclExistentialReturn_Removed()
        {
            // @_cdecl existential return: reads from resultPtr then wraps in proxy
            var input =
                "public partial class MyClass {\n" +
                "    public static IMyProto CreateProto()\n" +
                "    {\n" +
                "        var resultPtr = PInvoke_create();\n" +
                "        var existentialResult = SwiftMarshal.MarshalFromSwift<ExistentialContainer1>(resultPtr);\n" +
                "        return new SwiftInterop.MyProtoProxy(existentialResult);\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "MyProtoProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            Assert.DoesNotContain("MyProtoProxy", result.Content);
            Assert.DoesNotContain("CreateProto", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_UnmanagedCallersOnlyReceiver_BodyReplaced()
        {
            // [UnmanagedCallersOnly] receiver callbacks inside proxy classes must NOT be stripped.
            // They're referenced by function pointers in vtable assignments.
            // Their body is replaced with a no-op comment.
            var input =
                "public partial class AssemblyProxy {\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void Receive_loaded_1(IntPtr vtHandle, IntPtr selfContainer, IntPtr arg0)\n" +
                "    {\n" +
                "        var resolver = new ResolverProxy(arg0Container);\n" +
                "        impl.Loaded(resolver);\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "ResolverProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            // Receiver declaration is preserved (not stripped)
            Assert.Contains("Receive_loaded_1", result.Content);
            // Body is replaced with no-op
            Assert.Contains("no-op callback", result.Content);
            // Original body with suppressed proxy type is gone
            Assert.DoesNotContain("ResolverProxy", result.Content);
        }
    }

    #endregion

    #region K. Proxy Emission Gating (ProtocolHandler)

    public class ProxyEmissionGatingTests
    {
        [Fact]
        public void SuppressedProxyClassNames_TrackedInEmissionContext()
        {
            var ctx = new ModuleEmissionContext();
            Assert.Empty(ctx.SuppressedProxyClassNames);

            ctx.RecordSuppressedProxy("FooProxy");
            ctx.RecordSuppressedProxy("BarProxy");

            Assert.Equal(2, ctx.SuppressedProxyClassNames.Count);
            Assert.Contains("FooProxy", ctx.SuppressedProxyClassNames);
            Assert.Contains("BarProxy", ctx.SuppressedProxyClassNames);
        }

        [Fact]
        public void SuppressedProxyClassNames_DeduplicatesEntries()
        {
            var ctx = new ModuleEmissionContext();
            ctx.RecordSuppressedProxy("FooProxy");
            ctx.RecordSuppressedProxy("FooProxy");
            Assert.Single(ctx.SuppressedProxyClassNames);
        }

        [Fact]
        public void WasConformanceEmitted_ControlsProxySuppression()
        {
            var ctx = new ModuleEmissionContext();
            // When no decisions recorded, WasConformanceEmitted returns false
            Assert.False(ctx.WasConformanceEmitted("SomeProtocol"));

            // Record an emitted conformance — proxy should NOT be suppressed
            ctx.RecordConformanceDecision("GoodProtocol", true, null);
            Assert.True(ctx.WasConformanceEmitted("GoodProtocol"));

            // Record a skipped conformance — proxy SHOULD be suppressed
            ctx.RecordConformanceDecision("BadProtocol", false, "class-bound");
            Assert.False(ctx.WasConformanceEmitted("BadProtocol"));
        }
    }

    #endregion

    #region F. ContainsCallTo Word Boundary

    public class CoGaterContainsCallToWordBoundaryTests
    {
        [Fact]
        public void ProxyCoGater_ValueGet_DoesNotFalseMatchDatabaseValueGet()
        {
            // Regression test: Value_Get was stripping DatabaseValue_Get due to substring match.
            // The fix ensures ContainsCallTo uses word-boundary checking.
            var input =
                "public interface IDatabaseValueConvertible {\n" +
                "    GRDB.DatabaseValue DatabaseValue { get; }\n" +
                "}\n" +
                "public partial class SomeType {\n" +
                "    private GRDB.DatabaseValue Value_Get()\n" +
                "    {\n" +
                "        var x = new DatabaseValueConvertibleProxy(result);\n" +
                "        return x;\n" +
                "    }\n" +
                "\n" +
                "    public GRDB.DatabaseValue Value\n" +
                "    {\n" +
                "        get => Value_Get();\n" +
                "    }\n" +
                "}\n" +
                "public partial class FTS3Pattern : IDatabaseValueConvertible {\n" +
                "    private GRDB.DatabaseValue DatabaseValue_Get()\n" +
                "    {\n" +
                "        PInvoke_databaseValue_Get(resultPtr, selfPtr);\n" +
                "        return SwiftMarshal.MarshalFromSwift<GRDB.DatabaseValue>(resultPtr);\n" +
                "    }\n" +
                "\n" +
                "    public GRDB.DatabaseValue DatabaseValue\n" +
                "    {\n" +
                "        get => DatabaseValue_Get();\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "DatabaseValueConvertibleProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);

            // SomeType.Value_Get references the proxy — should be stripped
            Assert.DoesNotContain("GRDB.DatabaseValue Value_Get()", result.Content);
            Assert.DoesNotContain("get => Value_Get()", result.Content);

            // FTS3Pattern.DatabaseValue_Get does NOT reference any proxy — must be PRESERVED
            Assert.Contains("DatabaseValue_Get()", result.Content);
            Assert.Contains("get => DatabaseValue_Get()", result.Content);
        }

        [Fact]
        public void ProxyCoGater_SubscriptGet_DoesNotFalseMatchOtherGetSuffixed()
        {
            // "Subscript_Get" should not match "SomeSubscript_Get" (prefix collision)
            var input =
                "public partial class TypeA {\n" +
                "    private int Subscript_Get()\n" +
                "    {\n" +
                "        var x = new FooProxy(result);\n" +
                "        return x;\n" +
                "    }\n" +
                "    public int Subscript\n" +
                "    {\n" +
                "        get => Subscript_Get();\n" +
                "    }\n" +
                "}\n" +
                "public partial class TypeB {\n" +
                "    private int SomeSubscript_Get()\n" +
                "    {\n" +
                "        return 42;\n" +
                "    }\n" +
                "    public int SomeSubscript\n" +
                "    {\n" +
                "        get => SomeSubscript_Get();\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "FooProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);

            // TypeA: Subscript_Get references proxy — stripped
            Assert.DoesNotContain("int Subscript_Get()", result.Content);
            Assert.DoesNotContain("get => Subscript_Get()", result.Content);

            // TypeB: SomeSubscript_Get does NOT reference proxy and name doesn't match — preserved
            Assert.Contains("SomeSubscript_Get()", result.Content);
            Assert.Contains("get => SomeSubscript_Get()", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_NonVoidCallback_HasReturnDefault()
        {
            // [UnmanagedCallersOnly] callbacks returning IntPtr (not void) need "return default;"
            // to avoid CS0161 when their body is replaced with a no-op stub.
            var input =
                "public interface IFooProtocol {\n" +
                "    string GetName();\n" +
                "}\n" +
                "public partial class Foo : IFooProtocol {\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static IntPtr GetNameReceiver(IntPtr vtHandle, IntPtr self)\n" +
                "    {\n" +
                "        var proxy = new FooProxy(self);\n" +
                "        return IntPtr.Zero;\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "FooProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            // Non-void callback must have a return statement
            Assert.Contains("return default;", result.Content);
            // Original body replaced
            Assert.DoesNotContain("FooProxy", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_VoidCallback_NoReturnDefault()
        {
            // [UnmanagedCallersOnly] void callbacks should NOT have "return default;"
            var input =
                "public interface IFooProtocol {\n" +
                "    void DoWork();\n" +
                "}\n" +
                "public partial class Foo : IFooProtocol {\n" +
                "    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]\n" +
                "    private static void DoWorkReceiver(IntPtr vtHandle, IntPtr self)\n" +
                "    {\n" +
                "        var proxy = new FooProxy(self);\n" +
                "        proxy.DoWork();\n" +
                "    }\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "FooProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            // Void callback should NOT have return default
            Assert.DoesNotContain("return default;", result.Content);
            // Should have the no-op comment
            Assert.Contains("no-op callback", result.Content);
        }

        [Fact]
        public void ProcessProxyReferences_NarrowingOverload_CrossTypeNotStripped()
        {
            // A narrowing overload in TypeA should NOT be stripped just because
            // TypeB (a different type nearby) has a matching nint subscript that was removed.
            // The scan must be scoped to the containing type.
            var input =
                "public partial class TypeA {\n" +
                "    public int this[nint index]\n" +
                "    {\n" +
                "        get => Get(index);\n" +
                "    }\n" +
                "    public int this[int index] => this[(nint)index];\n" +
                "}\n" +
                "public partial class TypeB {\n" +
                "    public string this[nint index]\n" +
                "    {\n" +
                "        get\n" +
                "        {\n" +
                "            return new BarProxy(Get(index)).Value;\n" +
                "        }\n" +
                "    }\n" +
                "    public string this[int index] => this[(nint)index];\n" +
                "}\n";
            var suppressedProxies = new HashSet<string> { "BarProxy" };
            var result = CSharpWrapperCoGater.ProcessSuppressedProxyReferences(input, suppressedProxies);
            // TypeA's nint subscript is preserved (not proxy-related)
            Assert.Contains("class TypeA", result.Content);
            // TypeA's narrowing overload should still exist (its nint target exists in TypeA)
            // Count occurrences of the narrowing pattern in TypeA context
            var typeASection = result.Content.Split("class TypeB")[0];
            Assert.Contains("this[int index] => this[(nint)index]", typeASection);
        }
    }

    #endregion

    #region F. Dangling ToString Stripping

    public class CoGaterDanglingToStringTests
    {
        [Fact]
        public void Process_StrippedDescriptionProperty_AlsoStripsToString()
        {
            // Simulates XMLCoder pattern: Description property gets stripped because its
            // P/Invoke wrapper was stripped, but ToString() => Description; is left dangling.
            var input =
                "namespace Test {\n" +
                "public partial class BoolBox {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_BoolBox_description\")]\n" +
                "    private static partial IntPtr PInvoke_Get_BoolBox_description_ABC(IntPtr self);\n" +
                "\n" +
                "    private string Description_Get()\n" +
                "    {\n" +
                "        var result = PInvoke_Get_BoolBox_description_ABC(Payload);\n" +
                "        return SwiftString.FromPayload(result);\n" +
                "    }\n" +
                "\n" +
                "    public string Description\n" +
                "    {\n" +
                "        get => Description_Get();\n" +
                "    }\n" +
                "\n" +
                "    public override string ToString() => Description;\n" +
                "\n" +
                "    public int Value { get; set; }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Get_BoolBox_description" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            // P/Invoke, helper, property, AND ToString should all be stripped
            Assert.DoesNotContain("PInvoke_Get_BoolBox_description", result.Content);
            Assert.DoesNotContain("Description_Get", result.Content);
            Assert.DoesNotContain("Description", result.Content);
            Assert.DoesNotContain("ToString", result.Content);

            // Unrelated property should survive
            Assert.Contains("Value", result.Content);
        }

        [Fact]
        public void Process_NonStrippedDescription_PreservesToString()
        {
            // When Description property is NOT stripped, ToString should be preserved
            var input =
                "namespace Test {\n" +
                "public partial class GoodBox {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Get_GoodBox_description\")]\n" +
                "    private static partial IntPtr PInvoke_Get_GoodBox_description_ABC(IntPtr self);\n" +
                "\n" +
                "    private string Description_Get()\n" +
                "    {\n" +
                "        var result = PInvoke_Get_GoodBox_description_ABC(Payload);\n" +
                "        return SwiftString.FromPayload(result);\n" +
                "    }\n" +
                "\n" +
                "    public string Description\n" +
                "    {\n" +
                "        get => Description_Get();\n" +
                "    }\n" +
                "\n" +
                "    public override string ToString() => Description;\n" +
                "}\n" +
                "}\n";
            // Different symbol stripped — not related to Description
            var stripped = new HashSet<string> { "SBW_unrelated_method" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.Contains("Description", result.Content);
            Assert.Contains("ToString", result.Content);
        }

        [Fact]
        public void Process_ToStringWithoutDescription_Preserved()
        {
            // ToString that references a non-property member should not be affected
            var input =
                "namespace Test {\n" +
                "public partial class Custom {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_broken\")]\n" +
                "    private static partial void PInvoke_broken_ABC(IntPtr self);\n" +
                "\n" +
                "    public override string ToString() => \"Custom\";\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            // ToString with string literal is preserved (not a property reference)
            Assert.Contains("ToString", result.Content);
        }
    }

    #endregion

    #region F. CreateSwiftInstance Constructor Stripping

    public class CoGaterCreateSwiftInstanceTests
    {
        [Fact]
        public void Process_StrippedConstructorHelper_AlsoStripsConstructor()
        {
            // When a CreateSwiftInstance_ helper is stripped (Level 1),
            // the constructor calling it via : base() must also be stripped (Level 2).
            var input =
                "namespace Test {\n" +
                "public partial class Widget : Base {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Widget_init_ABC\")]\n" +
                "    private static partial IntPtr PInvoke_init_ABC(IntPtr arg);\n" +
                "\n" +
                "    private static IntPtr CreateSwiftInstance_PInvoke_init_ABC(int arg)\n" +
                "    {\n" +
                "        return PInvoke_init_ABC(IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public Widget(int arg) : base(CreateSwiftInstance_PInvoke_init_ABC(arg))\n" +
                "    {\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_Widget_init_ABC" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.DoesNotContain("PInvoke_init_ABC", result.Content);
            Assert.DoesNotContain("CreateSwiftInstance_", result.Content);
            Assert.DoesNotContain("public Widget(", result.Content);
        }

        [Fact]
        public void Process_ValidConstructorHelper_Preserved()
        {
            // When the helper's P/Invoke is NOT stripped, everything is preserved.
            var input =
                "namespace Test {\n" +
                "public partial class Widget : Base {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_Widget_init_ABC\")]\n" +
                "    private static partial IntPtr PInvoke_init_ABC(IntPtr arg);\n" +
                "\n" +
                "    private static IntPtr CreateSwiftInstance_PInvoke_init_ABC(int arg)\n" +
                "    {\n" +
                "        return PInvoke_init_ABC(IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public Widget(int arg) : base(CreateSwiftInstance_PInvoke_init_ABC(arg))\n" +
                "    {\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_unrelated_symbol" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.Contains("PInvoke_init_ABC", result.Content);
            Assert.Contains("CreateSwiftInstance_", result.Content);
            Assert.Contains("public Widget(", result.Content);
        }
    }

    #endregion

    #region G. Narrowing Overload Stripping

    public class CoGaterNarrowingOverloadTests
    {
        [Fact]
        public void Process_SingleLineIndexerNarrowing_StrippedWhenTargetMissing()
        {
            // Single-line indexer: "this[int x] => this[(nint)x];" with no this[nint x]
            var input =
                "namespace Test {\n" +
                "public partial class Store {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_SubGet_broken\")]\n" +
                "    private static partial IntPtr PInvoke_Sub_Get_ABC(nint idx, IntPtr self);\n" +
                "\n" +
                "    public nuint this[int index0] => this[(nint)index0];\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_SubGet_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.DoesNotContain("this[int index0]", result.Content);
        }

        [Fact]
        public void Process_SingleLineIndexerNarrowing_PreservedWhenTargetExists()
        {
            // Single-line indexer with a valid this[nint x] target — should NOT be stripped
            var input =
                "namespace Test {\n" +
                "public partial class Store {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_SubGet_ok\")]\n" +
                "    private static partial IntPtr PInvoke_Sub_Get_OK(nint idx, IntPtr self);\n" +
                "\n" +
                "    public nuint this[nint index0] => Subscript_Get(index0);\n" +
                "\n" +
                "    public nuint this[int index0] => this[(nint)index0];\n" +
                "}\n" +
                "}\n";
            // Strip an unrelated symbol so co-gater runs but doesn't touch the indexer
            var stripped = new HashSet<string> { "SBW_unrelated" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.Contains("this[int index0]", result.Content);
        }

        [Fact]
        public void Process_MultiLineIndexerNarrowing_StrippedWhenTargetMissing()
        {
            // Multi-line indexer: "this[int x] { get => this[(nint)x]; set => ... }"
            var input =
                "namespace Test {\n" +
                "public partial class BigNum {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_SubGet_broken\")]\n" +
                "    private static partial bool PInvoke_Sub_Get_ABC(nint bitAt, IntPtr self);\n" +
                "\n" +
                "    public bool this[int bitAt]\n" +
                "    {\n" +
                "        get => this[(nint)bitAt];\n" +
                "        set => this[(nint)bitAt] = value;\n" +
                "    }\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_SubGet_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.DoesNotContain("this[int bitAt]", result.Content);
        }

        [Fact]
        public void Process_MethodNarrowingSingleLine_StrippedWhenTargetMissing()
        {
            // Expression-bodied method: "Encode(int x) => Encode((nint)x);"
            var input =
                "namespace Test {\n" +
                "public partial class Encoder {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_encode_broken\")]\n" +
                "    private static partial void PInvoke_encode_ABC(nint val, IntPtr self);\n" +
                "\n" +
                "    public void Encode(int value) => Encode((nint)value);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_encode_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.DoesNotContain("Encode(int value)", result.Content);
        }

        [Fact]
        public void Process_MethodNarrowingMultiLine_StrippedWhenTargetMissing()
        {
            // Multi-line expression-bodied method (wrapped signature):
            // "static IBox Parse(byte[] with, uint errorContextLength, ...)\n    => Parse(with, (nuint)errorContextLength, ...);"
            var input =
                "namespace Test {\n" +
                "public partial class Parser {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_parse_broken\")]\n" +
                "    private static partial IntPtr PInvoke_parse_ABC(IntPtr data, nuint len, IntPtr self);\n" +
                "\n" +
                "    public static IBox Parse(byte[] with, uint errorContextLength)\n" +
                "        => Parse(with, (nuint)errorContextLength);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_parse_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.DoesNotContain("Parse(byte[] with, uint errorContextLength)", result.Content);
        }

        [Fact]
        public void Process_MethodNarrowing_PreservedWhenTargetExists()
        {
            // Method narrowing with valid target — should NOT be stripped
            var input =
                "namespace Test {\n" +
                "public partial class Encoder {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_encode_ok\")]\n" +
                "    private static partial void PInvoke_encode_OK(nint val, IntPtr self);\n" +
                "\n" +
                "    public void Encode(nint value)\n" +
                "    {\n" +
                "        PInvoke_encode_OK(value, _handle);\n" +
                "    }\n" +
                "\n" +
                "    public void Encode(int value) => Encode((nint)value);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_unrelated" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.Contains("Encode(int value)", result.Content);
            Assert.Contains("Encode(nint value)", result.Content);
        }

        [Fact]
        public void Process_NarrowingUint_StrippedWhenTargetMissing()
        {
            // uint → nuint narrowing
            var input =
                "namespace Test {\n" +
                "public partial class Encoder {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_encode_broken\")]\n" +
                "    private static partial void PInvoke_encode_ABC(nuint val, IntPtr self);\n" +
                "\n" +
                "    public void Encode(uint value) => Encode((nuint)value);\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_encode_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.DoesNotContain("Encode(uint value)", result.Content);
        }

        [Fact]
        public void Process_NarrowingWithDifferentArityTarget_StillStripped()
        {
            // Narrowing overload Foo(int x) => Foo((nint)x) where the real target
            // Foo(nint x) is stripped, but a different-arity Foo(string, nint) survives.
            // The narrowing must still be stripped (the surviving overload has wrong arity).
            var input =
                "namespace Test {\n" +
                "public partial class Processor {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_process_broken\")]\n" +
                "    private static partial void PInvoke_process_ABC(nint val, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_process_ok\")]\n" +
                "    private static partial void PInvoke_process_DEF(IntPtr label, nint count, IntPtr self);\n" +
                "\n" +
                "    public void Process(string label, nint count)\n" +
                "    {\n" +
                "        PInvoke_process_DEF(IntPtr.Zero, count, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public void Process(int value) => Process((nint)value);\n" +
                "}\n" +
                "}\n";
            // Only the single-param Process(nint) is stripped; Process(string, nint) survives
            var stripped = new HashSet<string> { "SBW_process_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            // The narrowing Process(int) must be stripped even though Process(string, nint) exists
            Assert.DoesNotContain("Process(int value)", result.Content);
            // The 2-param overload should survive
            Assert.Contains("Process(string label, nint count)", result.Content);
        }

        [Fact]
        public void Process_IndexerNarrowingWithDifferentArityTarget_StillStripped()
        {
            // Narrowing indexer this[int x] => this[(nint)x] where the real target
            // this[nint x] is stripped, but a different-arity this[string, nint] survives.
            // The narrowing must still be stripped.
            var input =
                "namespace Test {\n" +
                "public partial class Grid {\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_sub_get_broken\")]\n" +
                "    private static partial int PInvoke_Sub_Get_ABC(nint idx, IntPtr self);\n" +
                "\n" +
                "    [UnmanagedCallConv(CallConvs = new Type[] { typeof(CallConvCdecl) })]\n" +
                "    [LibraryImport(\"SwiftBindings\", EntryPoint = \"SBW_sub_get_ok\")]\n" +
                "    private static partial int PInvoke_Sub_Get_DEF(IntPtr label, nint idx, IntPtr self);\n" +
                "\n" +
                "    public int this[string label, nint index]\n" +
                "    {\n" +
                "        get => PInvoke_Sub_Get_DEF(IntPtr.Zero, index, IntPtr.Zero);\n" +
                "    }\n" +
                "\n" +
                "    public int this[int index0] => this[(nint)index0];\n" +
                "}\n" +
                "}\n";
            var stripped = new HashSet<string> { "SBW_sub_get_broken" };
            var result = CSharpWrapperCoGater.Process(input, stripped);

            Assert.DoesNotContain("this[int index0]", result.Content);
            Assert.Contains("this[string label, nint index]", result.Content);
        }
    }

    #endregion

}

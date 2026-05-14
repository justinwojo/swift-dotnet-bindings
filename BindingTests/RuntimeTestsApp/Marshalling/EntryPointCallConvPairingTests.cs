// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using RuntimeTestsApp.Infrastructure;

namespace RuntimeTestsApp.Marshalling;

/// <summary>
/// Cross-cutting reflection audit on the generated <c>SwiftBindingsTestLib</c>
/// surface: every P/Invoke whose entry-point name is a Swift-mangled symbol
/// (<c>$s…</c>) must declare the Swift calling convention, and every P/Invoke
/// whose entry-point name is a <c>SBW_</c> cdecl wrapper must declare the
/// cdecl convention. The two decisions are normally tied, but a desynchronised
/// emit (mangled name written into <c>EntryPoint</c> after the calling-convention
/// picker had already locked onto cdecl, or vice versa) reads register state
/// against the wrong ABI and corrupts arguments at runtime.
///
/// The audit walks every public type in the test-library assembly via
/// reflection, finds <see cref="LibraryImportAttribute"/>-decorated methods,
/// and asserts the entry-point prefix matches the
/// <see cref="UnmanagedCallConvAttribute"/> shape on the same method. A
/// regression on the emitter shows up as a failed assertion naming the
/// specific method, so the generator change that re-desynchronises the two
/// decision points is caught at unit-test time rather than via a runtime
/// crash on the consumer side.
/// </summary>
public class EntryPointCallConvPairingTests : TestBase
{
    public EntryPointCallConvPairingTests(TestResults results) : base(results) { }

    public void TestEveryMangledEntryPointDeclaresSwiftCallConv()
    {
        var assembly = typeof(TestLibFunctions).Assembly;
        var violations = new System.Collections.Generic.List<string>();
        int auditedCount = 0;

        foreach (var type in assembly.GetTypes())
        {
            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var libImport = method.GetCustomAttribute<LibraryImportAttribute>();
                if (libImport is null) continue;

                var entryPoint = libImport.EntryPoint ?? method.Name;
                var callConv = method.GetCustomAttribute<UnmanagedCallConvAttribute>();
                var convs = callConv?.CallConvs ?? Array.Empty<Type>();
                bool hasCdecl = Array.IndexOf(convs, typeof(CallConvCdecl)) >= 0;
                bool hasSwift = Array.IndexOf(convs, typeof(CallConvSwift)) >= 0;

                auditedCount++;

                if (entryPoint.StartsWith("$s", StringComparison.Ordinal))
                {
                    // Mangled symbols MUST declare exactly one call-conv: CallConvSwift.
                    // Empty / missing call-conv defaults to platform cdecl; a mixed
                    // declaration leaves the runtime free to pick either, which is
                    // undocumented behaviour. Both shapes read register state under
                    // the wrong ABI for half of the call-conv attribute payload.
                    bool exclusiveSwift = convs.Length == 1 && convs[0] == typeof(CallConvSwift);
                    if (!exclusiveSwift)
                    {
                        violations.Add(
                            $"{type.FullName}.{method.Name}: EntryPoint=\"{entryPoint}\" " +
                            "is a Swift-mangled symbol; the only correct pairing is " +
                            "[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })] " +
                            $"(observed convs: [{string.Join(", ", convs.Select(t => t.Name))}], " +
                            $"hasSwift={hasSwift}, hasCdecl={hasCdecl}). " +
                            "Anything other than exactly CallConvSwift reads register state under " +
                            "the wrong ABI for at least one call path.");
                    }
                }
                else if (entryPoint.StartsWith("SBSW_", StringComparison.Ordinal))
                {
                    // SBSW_ wrappers are emitted as @_silgen_name Swift functions whose signature
                    // can't be made C-representable (non-@objc class self, non-blittable passthrough).
                    // They MUST declare exactly one call-conv: CallConvSwift. Checked before the
                    // SBW_ branch for readability; the two prefixes do not actually collide because
                    // StartsWith("SBW_") requires the underscore as the fourth character.
                    bool exclusiveSwiftCC = convs.Length == 1 && convs[0] == typeof(CallConvSwift);
                    if (!exclusiveSwiftCC)
                    {
                        violations.Add(
                            $"{type.FullName}.{method.Name}: EntryPoint=\"{entryPoint}\" " +
                            "is an SBSW_ Swift-CC wrapper; the only correct pairing is " +
                            "[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvSwift) })] " +
                            $"(observed convs: [{string.Join(", ", convs.Select(t => t.Name))}], " +
                            $"hasSwift={hasSwift}, hasCdecl={hasCdecl}). " +
                            "Swift-CC wrapper symbols always use the Swift calling convention exclusively.");
                    }
                }
                else if (entryPoint.StartsWith("SBW_", StringComparison.Ordinal))
                {
                    // SBW_ wrappers are emitted as @_cdecl Swift functions — they MUST declare
                    // exactly one call-conv: CallConvCdecl. Same exclusivity rule as $s above.
                    bool exclusiveCdecl = convs.Length == 1 && convs[0] == typeof(CallConvCdecl);
                    if (!exclusiveCdecl)
                    {
                        violations.Add(
                            $"{type.FullName}.{method.Name}: EntryPoint=\"{entryPoint}\" " +
                            "is an SBW_ cdecl wrapper; the only correct pairing is " +
                            "[UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })] " +
                            $"(observed convs: [{string.Join(", ", convs.Select(t => t.Name))}], " +
                            $"hasCdecl={hasCdecl}, hasSwift={hasSwift}). " +
                            "Wrapper symbols always use the C calling convention exclusively.");
                    }
                }
            }
        }

        AssertTrue(auditedCount > 0,
            "Reflection audit must find at least one P/Invoke in the generated assembly — " +
            "zero matches means the test is mis-targeted, not that the surface is clean.");

        if (violations.Count > 0)
        {
            var report = string.Join("\n  ", violations);
            throw new AssertionException(
                $"EntryPoint / CallConv desynchronisation in {violations.Count} site(s):\n  {report}");
        }

        TestLogger.Info(
            $"EntryPoint / CallConv audit: {auditedCount} P/Invokes inspected, 0 desynchronised.");
    }
}

// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace BindingsGeneration.Tests;

/// <summary>
/// Holds every <c>closedConformances</c> claim in the bundled type databases to the platform SDKs
/// of the selected Xcode. A claim says a protocol outside it is one no SDK module makes the type
/// conform to, and call pinning relies on that to rule a protocol-constrained overload out, so a
/// conformance the SDK adds that the claim misses would let a wrapper bind the wrong overload. The
/// audit reads every <c>.swiftinterface</c> of each SDK: the type's own declaration, each
/// top-level <c>extension Module.Type : …</c> in any module, and the refinements of every protocol
/// reached, then requires all of them inside the claim. Each toolchain profile selects its own
/// Xcode, so each profile's unit tests audit its SDKs.
/// </summary>
public class ClosedConformanceSdkAuditTests
{
    private static readonly string[] Sdks = ["iphoneos", "macosx", "appletvos"];

    private static readonly string DatabaseDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Swift");

    [Fact]
    public async Task BundledClaims_LoadIntoTheirRecords()
    {
        var claims = BundledClaims();
        Assert.NotEmpty(claims);
        foreach (var claim in claims)
        {
            var types = new TypeDatabase();
            await types.LoadModuleDatabaseFromFile(claim.File);
            Assert.True(types.TryGetTypeRecord(SwiftTypeName.FromModuleQualifiedName(claim.Type), out var record), claim.Type);
            Assert.NotNull(record.ClosedConformances);
            Assert.Equal(claim.Protocols.OrderBy(p => p, StringComparer.Ordinal), record.ClosedConformances!.OrderBy(p => p, StringComparer.Ordinal));
        }
    }

    [SkippableFact]
    public void BundledClaims_CoverEveryConformanceTheSdksDeclare()
    {
        var sdkRoots = Sdks.Select(SdkRoot).Where(root => root != null).Cast<string>().ToList();
        Skip.If(sdkRoots.Count == 0, "no Apple platform SDK is installed (xcrun --show-sdk-path failed)");

        var failures = new List<string>();
        // Non-vacuity: some claim must see a protocol of a third module, which only the scan of
        // other modules' extensions finds. A scan that stopped reading them would pass otherwise.
        var sawThirdModuleProtocol = false;
        foreach (var root in sdkRoots)
        {
            var sdk = new SdkInterfaces(root);
            foreach (var claim in BundledClaims())
            {
                var declared = sdk.Conformances(claim.Type, out var problems);
                var typeModule = claim.Type[..claim.Type.IndexOf('.')];
                sawThirdModuleProtocol |= declared.Any(p => !p.StartsWith(typeModule + ".", StringComparison.Ordinal)
                    && !p.StartsWith("Swift.", StringComparison.Ordinal));
                failures.AddRange(problems.Select(p => $"{Path.GetFileName(root)}: {claim.Type}: {p}"));
                foreach (var protocol in declared.Where(p => !claim.Protocols.Contains(p)).OrderBy(p => p, StringComparer.Ordinal))
                    failures.Add($"{Path.GetFileName(root)}: {claim.Type} conforms to {protocol}, which its closedConformances claim omits");
            }
        }
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
        Assert.True(sawThirdModuleProtocol, "the scan found no conformance another SDK module adds to a claimed type");
    }

    [Fact]
    public void Audit_FindsAConformanceAnotherModuleAddsAndWhatItRefines()
    {
        // Positive control for the scanner, independent of any installed SDK.
        var root = Directory.CreateTempSubdirectory("closed-conformance-audit").FullName;
        try
        {
            Write(root, "Kit", """
                public struct Item : Swift::Equatable, Swift.Sendable {
                  public struct Item : Kit.Nested {
                  }
                }
                public protocol Base {
                }
                """);
            Write(root, "Other", """
                public protocol Refined : Kit.Base, ~Swift.Copyable {
                }
                public typealias Pair = Other.Refined & Swift.Hashable
                @available(iOS 18, *)
                extension Kit::Item : @retroactive Other.Pair where Self : Swift.Sendable {
                }
                extension Kit.Item.Inner : Other.Unrelated {
                }
                """);

            var found = new SdkInterfaces(root).Conformances("Kit.Item", out var problems);

            Assert.Equal(
                new[] { "Kit.Base", "Other.Refined", "Swift.Equatable", "Swift.Hashable", "Swift.Sendable" },
                found.OrderBy(p => p, StringComparer.Ordinal));
            // Swift's own protocols are not in this tree, so their refinements cannot be read.
            Assert.Contains(problems, p => p.Contains("Swift.Hashable", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Write(string root, string module, string text)
    {
        var dir = Path.Combine(root, $"{module}.swiftmodule");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "arm64-apple-ios.swiftinterface"), text);
    }

    private sealed record Claim(string File, string Type, HashSet<string> Protocols);

    private static List<Claim> BundledClaims()
    {
        var claims = new List<Claim>();
        foreach (var file in Directory.EnumerateFiles(DatabaseDirectory, "*Database.xml"))
        {
            foreach (var decl in XDocument.Load(file).Descendants("typedeclaration"))
            {
                if (decl.Attribute("closedConformances")?.Value is not { Length: > 0 } list)
                    continue;
                var type = $"{decl.Attribute("module")!.Value}.{decl.Attribute("name")!.Value}";
                var protocols = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.Ordinal);
                claims.Add(new Claim(file, type, protocols));
            }
        }
        return claims;
    }

    private static string? SdkRoot(string sdk)
    {
        var (exitCode, stdout, _) = new SystemCommandRunner().Run("xcrun", $"--sdk {sdk} --show-sdk-path");
        var path = stdout.Trim();
        if (exitCode != 0 || path.Length == 0 || !Directory.Exists(path))
            return null;
        // The versioned SDK name is a link to the SDK directory; walk the directory itself.
        return new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path;
    }

    /// <summary>
    /// The top-level declarations of an SDK's Swift interfaces that bear on conformance: type and
    /// protocol declarations with their inheritance clauses, extensions that add conformances, and
    /// protocol-composition typealiases, keyed by module. Interfaces print every name
    /// module-qualified, with <c>.</c> or (newer compilers) <c>::</c>.
    /// </summary>
    private sealed class SdkInterfaces
    {
        private const string Attributes = @"(?:@[A-Za-z_]+(?:\([^)]*\))?\s+)*";
        private const string Name = @"[A-Za-z_][A-Za-z0-9_]*";
        private const string Qualified = Name + @"(?:(?:\.|::)" + Name + ")+";

        private static readonly Regex TypeDecl = new(
            "^" + Attributes + @"public\s+(?:final\s+)?(?:struct|enum|class|actor)\s+(?<name>" + Name + @")(?:<[^>]*>)?\s*(?::\s*(?<inherits>[^{]*?))?\s*(?:where\b[^{]*)?\{",
            RegexOptions.Compiled);
        private static readonly Regex ProtocolDecl = new(
            "^" + Attributes + @"public\s+protocol\s+(?<name>" + Name + @")(?:<[^>]*>)?\s*(?::\s*(?<inherits>[^{]*?))?\s*(?:where\b(?<where>[^{]*))?\{",
            RegexOptions.Compiled);
        private static readonly Regex Extension = new(
            "^" + Attributes + @"extension\s+(?<type>" + Qualified + @")\s*:\s*(?<inherits>[^{]*?)\s*(?:where\b[^{]*)?\{",
            RegexOptions.Compiled);
        private static readonly Regex Composition = new(
            "^" + Attributes + @"public\s+typealias\s+(?<name>" + Name + @")\s*=\s*(?<parts>" + Qualified + @"(?:\s*&\s*" + Qualified + @")+)\s*$",
            RegexOptions.Compiled);
        private static readonly Regex SelfRequirement = new(@"\bSelf\s*:\s*(?<protocol>" + Qualified + ")", RegexOptions.Compiled);

        private readonly Dictionary<string, List<string>> _typeInherits = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _protocolInherits = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<string>> _compositions = new(StringComparer.Ordinal);
        private readonly List<(string Type, List<string> Protocols)> _extensions = new();

        public SdkInterfaces(string root)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                // Framework links point at the same files under Versions/; read each file once.
                AttributesToSkip = FileAttributes.ReparsePoint,
            };
            foreach (var file in Directory.EnumerateFiles(root, "*.swiftinterface", options))
            {
                var module = ModuleOf(file);
                if (module == null)
                    continue;
                foreach (var line in File.ReadLines(file))
                    Read(module, line);
            }
        }

        private void Read(string module, string line)
        {
            // Top-level declarations start in the first column; members and nested types are indented.
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                return;
            if (TypeDecl.Match(line) is { Success: true } type)
                Add(_typeInherits, $"{module}.{type.Groups["name"].Value}", Protocols(type.Groups["inherits"].Value));
            else if (ProtocolDecl.Match(line) is { Success: true } protocol)
            {
                var inherits = Protocols(protocol.Groups["inherits"].Value);
                inherits.AddRange(SelfRequirement.Matches(protocol.Groups["where"].Value).Select(m => Normalize(m.Groups["protocol"].Value)));
                Add(_protocolInherits, $"{module}.{protocol.Groups["name"].Value}", inherits);
            }
            else if (Extension.Match(line) is { Success: true } extension)
                _extensions.Add((Normalize(extension.Groups["type"].Value), Protocols(extension.Groups["inherits"].Value)));
            else if (Composition.Match(line) is { Success: true } composition)
                Add(_compositions, $"{module}.{composition.Groups["name"].Value}", Protocols(composition.Groups["parts"].Value.Replace('&', ',')));
        }

        /// <summary>
        /// Every protocol <paramref name="type"/> conforms to across the SDK, refinements included.
        /// <paramref name="problems"/> names what could not be read: a missing declaration, or a
        /// protocol whose own declaration the SDK does not hold.
        /// </summary>
        public HashSet<string> Conformances(string type, out List<string> problems)
        {
            problems = new List<string>();
            var direct = new List<string>();
            if (_typeInherits.TryGetValue(type, out var own))
                direct.AddRange(own);
            else
                problems.Add("no top-level declaration of the type in its module's interface");
            foreach (var (extended, protocols) in _extensions)
            {
                if (string.Equals(extended, type, StringComparison.Ordinal))
                    direct.AddRange(protocols);
            }

            var found = new HashSet<string>(StringComparer.Ordinal);
            var pending = new Queue<string>(direct);
            while (pending.Count > 0)
            {
                var name = pending.Dequeue();
                if (_compositions.TryGetValue(name, out var parts))
                {
                    foreach (var part in parts)
                        pending.Enqueue(part);
                    continue;
                }
                if (!found.Add(name))
                    continue;
                if (_protocolInherits.TryGetValue(name, out var refined))
                {
                    foreach (var parent in refined)
                        pending.Enqueue(parent);
                }
                else if (!_typeInherits.ContainsKey(name))
                {
                    problems.Add($"{name} has no protocol declaration in the SDK, so its refinements are unknown");
                }
            }
            return found;
        }

        private static void Add(Dictionary<string, List<string>> map, string key, List<string> values)
        {
            if (!map.TryGetValue(key, out var list))
                map[key] = list = new List<string>();
            list.AddRange(values);
        }

        /// <summary>
        /// The protocols of an inheritance clause, module-qualified with <c>.</c>; attributes such as
        /// <c>@retroactive</c> are dropped, as are suppressions (<c>~Copyable</c>), layout
        /// constraints and anything not module-qualified.
        /// </summary>
        private static List<string> Protocols(string clause)
        {
            var result = new List<string>();
            foreach (var raw in clause.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var item = Regex.Replace(raw, @"@[A-Za-z_]+(?:\([^)]*\))?\s*", "").Trim();
                if (item.StartsWith('~') || !Regex.IsMatch(item, "^" + Qualified + "$"))
                    continue;
                result.Add(Normalize(item));
            }
            return result;
        }

        private static string Normalize(string name) => name.Replace("::", ".", StringComparison.Ordinal);

        private static string? ModuleOf(string file)
        {
            for (var dir = Path.GetDirectoryName(file); dir != null; dir = Path.GetDirectoryName(dir))
            {
                if (dir.EndsWith(".swiftmodule", StringComparison.Ordinal))
                    return Path.GetFileNameWithoutExtension(dir);
            }
            return null;
        }
    }
}

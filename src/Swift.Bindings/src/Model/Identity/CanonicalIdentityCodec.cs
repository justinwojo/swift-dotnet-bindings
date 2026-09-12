// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BindingsGeneration;

/// <summary>BCL-only reader for the existing canonical identity wire format, shared with evidence consumers.</summary>
public static class CanonicalIdentityCodec
{
    public const string IdentityFormat = "RecoveryUnitId.Canonical/v1";

    // These are wire tokens, not a second declaration taxonomy. Enum parity is tested.
    public static bool IsKind(string value) => value is "Type" or "Method" or "Property" or "Operator" or "Subscript" or "Module";
    public static bool IsAccessor(string value) => value is "None" or "Getter" or "Setter" or "WillSet" or "DidSet" or "SubscriptGetter" or "SubscriptSetter";
    public static bool IsScope(string value) => value is "leaf-api" or "accessor-group" or "forward-protocol-view"
        or "managed-protocol-conformance" or "conformance-edge" or "shared-helper-bundle"
        or "type-representation" or "type-surface" or "module";

    public sealed record Declaration(string Module, string DeclPath, string Kind, string Name,
        string[] ParameterLabels, string[] ParameterTypes, string Accessor, string GenericContext,
        string Symbol, string Discriminator)
    {
        public string QualifiedPath => string.Join(".", new[] { Module, DeclPath, Name }.Where(s => s.Length > 0));
        public string Canonical
        {
            get
            {
                var sb = new StringBuilder();
                AppendEscaped(sb, Module).Append('|');
                AppendEscaped(sb, DeclPath).Append('|').Append(Kind).Append('|');
                AppendEscaped(sb, Name).Append('|');
                for (var i = 0; i < ParameterLabels.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendEscaped(sb, ParameterLabels[i]).Append(':');
                    AppendEscaped(sb, ParameterTypes[i]);
                }
                sb.Append('|').Append(Accessor).Append('|');
                AppendEscaped(sb, GenericContext).Append('|');
                AppendEscaped(sb, Symbol).Append('|');
                AppendEscaped(sb, Discriminator);
                return sb.ToString();
            }
        }
    }

    public sealed record Unit(Declaration Decl, string Scope)
    {
        public string Describe() => $"{Decl.QualifiedPath} ({Scope})";
        public string Canonical => $"{Decl.Canonical}!{Scope}";
    }

    public static bool TryParseUnit(string? canonical, out Unit? unit)
    {
        unit = null;
        if (string.IsNullOrEmpty(canonical)) return false;
        var split = canonical.LastIndexOf('!');
        if (split < 0 || !IsScope(canonical[(split + 1)..]) ||
            !TryParseDeclaration(canonical[..split], out var decl)) return false;
        unit = new Unit(decl!, canonical[(split + 1)..]);
        return true;
    }

    public static bool TryParseDeclaration(string? canonical, out Declaration? declaration)
    {
        declaration = null;
        if (canonical is null) return false;
        var fields = SplitUnescaped(canonical, '|');
        if (fields.Count != 9 || !IsKind(fields[2]) || !IsAccessor(fields[5])) return false;
        var labels = new List<string>();
        var types = new List<string>();
        if (fields[4].Length > 0)
        {
            foreach (var entry in SplitUnescaped(fields[4], ','))
            {
                var parts = SplitUnescaped(entry, ':');
                if (parts.Count != 2 || !TryUnescape(parts[0], out var label) ||
                    !TryUnescape(parts[1], out var type)) return false;
                labels.Add(label);
                types.Add(type);
            }
        }
        if (!TryUnescape(fields[0], out var module) || !TryUnescape(fields[1], out var path) ||
            !TryUnescape(fields[3], out var name) || !TryUnescape(fields[6], out var generic) ||
            !TryUnescape(fields[7], out var symbol) || !TryUnescape(fields[8], out var discriminator)) return false;
        declaration = new Declaration(module, path, fields[2], name, labels.ToArray(), types.ToArray(),
            fields[5], generic, symbol, discriminator);
        if (!string.Equals(declaration.Canonical, canonical, StringComparison.Ordinal))
        {
            declaration = null;
            return false;
        }
        return true;
    }

    public static StringBuilder AppendEscaped(StringBuilder sb, string? value)
    {
        if (string.IsNullOrEmpty(value)) return sb;
        foreach (var c in value)
        {
            if (c is '\\' or '|' or ',' or ':') sb.Append('\\');
            sb.Append(c);
        }
        return sb;
    }

    private static List<string> SplitUnescaped(string value, char separator)
    {
        var segments = new List<string>();
        var start = 0;
        var escaped = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (escaped) { escaped = false; continue; }
            if (value[i] == '\\') { escaped = true; continue; }
            if (value[i] != separator) continue;
            segments.Add(value.Substring(start, i - start));
            start = i + 1;
        }
        segments.Add(value.Substring(start));
        return segments;
    }

    private static bool TryUnescape(string value, out string result)
    {
        result = value;
        if (value.IndexOf('\\') < 0) return true;
        var sb = new StringBuilder(value.Length);
        var escaped = false;
        foreach (var c in value)
        {
            if (escaped)
            {
                if (c is not ('\\' or '|' or ',' or ':')) return false;
                sb.Append(c);
                escaped = false;
                continue;
            }
            if (c == '\\') { escaped = true; continue; }
            sb.Append(c);
        }
        if (escaped) return false;
        result = sb.ToString();
        return true;
    }
}

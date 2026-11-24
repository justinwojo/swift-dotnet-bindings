// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace RuntimeTestsApp.Infrastructure;

/// <summary>
/// The constant a binding gives a case of a non-frozen Swift enum projected as a plain C# enum of 32
/// bits or more: the 31-bit FNV-1a hash of the case's Swift name (UTF-8), the same in every binding of
/// the enum whichever SDK generated it. Tests compare against it rather than a declaration index.
/// </summary>
internal static class NameKeyedCase
{
    public static int Of(string swiftCaseName)
    {
        uint hash = 2166136261;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(swiftCaseName))
            hash = unchecked((hash ^ b) * 16777619);
        return (int)(hash & 0x7FFFFFFF);
    }
}

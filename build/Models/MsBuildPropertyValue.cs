// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

/// <summary>
/// Escaping for values passed to MSBuild's <c>-property:</c> (a.k.a. <c>-p:</c>) command-line switch.
/// </summary>
/// <remarks>
/// The switch parser splits a property value on both <c>,</c> and <c>;</c> — they are list
/// separators at the command line — so a value that legitimately contains either character is torn
/// apart and the trailing fragment is rejected as an unknown switch (MSB1006). The version floor
/// range <c>[X.Y.Z,)</c> is the live case: its comma must survive to the property. MSBuild unescapes
/// <c>%XX</c> when it reads the property, so escaping each hazardous character to its percent form
/// delivers the original string to the target unchanged. Kept dependency-free (no Nuke types) so it
/// can be link-compiled into the unit-test project and asserted directly.
/// </remarks>
public static class MsBuildPropertyValue
{
    /// <summary>
    /// Returns <paramref name="value"/> with the command-line list separators (and the percent
    /// escape character itself) replaced by their <c>%XX</c> forms. <c>%</c> is escaped first so an
    /// escape this method introduces is not itself re-escaped.
    /// </summary>
    public static string Escape(string value) =>
        value.Replace("%", "%25").Replace(";", "%3B").Replace(",", "%2C");
}

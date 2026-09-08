// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

namespace BindingsGeneration
{
    /// <summary>
    /// Emits resource consumers of the wrapper compiler's final manifest. Reading at
    /// build execution also supports generation followed by --compile-wrapper-only.
    /// </summary>
    internal static class ResourceBundleTargetsEmitter
    {
        internal static string Emit(string moduleName, string resourceRoot,
            string? dependsOnTargets = null, string? nativePackRoot = null)
        {
            var name = ConsumerTargetsEmitter.SanitizeModuleName(moduleName);
            var names = $"_SwiftResourceBundleNames_{name}";
            var files = $"_SwiftResourceBundleFiles_{name}";
            var manifest = $"{resourceRoot}_resource-bundles.txt";
            var dependency = dependsOnTargets == null ? "" : $" DependsOnTargets=\"{dependsOnTargets}\"";
            var before = "_CollectBundleResources;_CollectPackLibraryResources";
            var packItems = "";
            if (nativePackRoot != null)
            {
                before += ";_GetPackageFiles";
                packItems = $"""

                      <None Remove="@({files});{manifest}" />
                      <None Include="@({files})" Pack="true"
                            PackagePath="{nativePackRoot}%({files}.BundleName).bundle/%({files}.RecursiveDir)" />
                      <None Include="{manifest}" Pack="true" PackagePath="{nativePackRoot}" />
                """;
            }

            return $"""

                  <!-- Resources are produced after generation by wrapper compilation.
                       Read its manifest at execution, before Apple resource collection. -->
                  <Target Name="_Include{name}SwiftResourceBundles" BeforeTargets="{before}"{dependency}>
                    <ReadLinesFromFile File="{manifest}" Condition="Exists('{manifest}')">
                      <Output TaskParameter="Lines" ItemName="{names}" />
                    </ReadLinesFromFile>
                    <ItemGroup Condition="'@({names})' != ''">
                      <{files} Include="{resourceRoot}%({names}.Identity).bundle/**">
                        <BundleName>%({names}.Identity)</BundleName>
                      </{files}>
                      <BundleResource Include="@({files})">
                        <Link>%({files}.BundleName).bundle/%({files}.RecursiveDir)%({files}.Filename)%({files}.Extension)</Link>
                      </BundleResource>{packItems}
                    </ItemGroup>
                  </Target>
                """;
        }
    }
}

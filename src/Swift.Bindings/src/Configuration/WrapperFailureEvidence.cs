// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace BindingsGeneration;

/// <summary>Opt-in first-failure receipts from the production wrapper compiler, before recovery rewrites sources.</summary>
internal static class WrapperFailureEvidence
{
    internal static void Capture(string? root, string module, string target, string arguments,
        string stderr, IReadOnlyList<string> sources, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        try
        {
            var destination = Path.Combine(root, module + "-" + target);
            if (Directory.Exists(destination)) return;
            Directory.CreateDirectory(root);
            // Publish only a complete receipt; never overwrite a prior recovery round.
            var staging = Path.Combine(root, ".capture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            var hashes = new List<string>();
            for (int i = 0; i < sources.Count; i++)
            {
                var name = i + "-" + Path.GetFileName(sources[i]);
                var copy = Path.Combine(staging, name);
                File.Copy(sources[i], copy);
                hashes.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(copy))) + "  " + name);
            }
            File.WriteAllText(Path.Combine(staging, "command.txt"), "xcrun " + arguments);
            File.WriteAllText(Path.Combine(staging, "target.txt"), target);
            File.WriteAllText(Path.Combine(staging, "stderr.txt"), stderr);
            File.WriteAllText(Path.Combine(staging, "source-sha256.txt"), string.Join("\n", hashes));
            Directory.Move(staging, destination);
        }
        catch (Exception ex)
        {
            // Incomplete staging data may help diagnose the failed capture. It is never
            // presented as a complete receipt and does not affect the compiler verdict.
            logger.LogWarning("Could not retain wrapper failure evidence: {Message}", ex.Message);
        }
    }
}

// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Nuke.Common;
using Nuke.Common.IO;
using Serilog;

partial class Build
{
    static readonly TimeSpan StoreKitSdkLookupTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StoreKitCompileTimeout = TimeSpan.FromSeconds(120);
    static readonly TimeSpan StoreKitCodesignTimeout = TimeSpan.FromSeconds(30);

    [Parameter("Exact App Store Connect product id for StoreKit command-line Sandbox qualification (or STOREKIT_SANDBOX_PRODUCT_ID)")]
    readonly string? StoreKitSandboxProductId;

    string? EffectiveStoreKitSandboxProductId =>
        !string.IsNullOrWhiteSpace(StoreKitSandboxProductId)
            ? StoreKitSandboxProductId
            : Environment.GetEnvironmentVariable(StoreKitSandboxReadiness.ProductIdEnvironmentVariable);

    StoreKitSandboxReadiness.Configuration RequireStoreKitSandboxConfiguration(string platform) =>
        StoreKitSandboxReadiness.RequireConfiguration(
            EffectiveStoreKitSandboxProductId, platform, RuntimeTestsBundleId);

    StoreKitSandboxReadiness.Receipt RunStoreKitSandboxNativeControl(
        StoreKitSandboxReadiness.Configuration configuration,
        string simulatorUdid)
    {
        var controlDir = BindingTestsDir / "StoreKitSandboxControl";
        var source = controlDir / "App.swift";
        var plistTemplate = controlDir / "Info.plist";
        if (!File.Exists(source) || !File.Exists(plistTemplate))
            throw new Exception($"StoreKit Sandbox control sources are missing under {controlDir}.");

        var platform = configuration.Platform == "ios" ? ApplePlatform.IOS : ApplePlatform.TvOS;
        var target = platform.SimulatorTarget.Replace("15.0", "16.0", StringComparison.Ordinal);
        var scratch = (AbsolutePath)Path.Combine(
            Path.GetTempPath(), "swift-bindings-storekit-control", Guid.NewGuid().ToString("N"));
        var appPath = scratch / "StoreKitSandboxControl.app";
        appPath.CreateDirectory();
        try
        {
            var sdkLookup = CaptureStoreKitProcess("/usr/bin/xcrun",
                ["--sdk", platform.SimulatorSdkName, "--show-sdk-path"], StoreKitSdkLookupTimeout);
            if (sdkLookup.ExitCode != 0 || string.IsNullOrWhiteSpace(sdkLookup.Output))
                throw new Exception(
                    $"StoreKit Sandbox native-control SDK lookup failed for {platform.SimulatorSdkName}:\n{sdkLookup.Output}");
            var sdkPath = sdkLookup.Output.Trim();
            var compile = CaptureStoreKitProcess("/usr/bin/xcrun",
            [
                "swiftc", "-target", target, "-sdk", sdkPath, "-parse-as-library",
                source, "-o", appPath / "SKControl",
            ], StoreKitCompileTimeout);
            if (compile.ExitCode != 0)
                throw new Exception($"StoreKit Sandbox native control failed to compile:\n{compile.Output}");

            var plist = File.ReadAllText(plistTemplate)
                .Replace("__BUNDLE_ID__", configuration.BundleId, StringComparison.Ordinal)
                .Replace("__SUPPORTED_PLATFORM__", platform.SimulatorPlistPlatform, StringComparison.Ordinal)
                .Replace("__PLATFORM_NAME__", platform.SimulatorSdkName, StringComparison.Ordinal)
                .Replace("__MINIMUM_OS__", "16.0", StringComparison.Ordinal)
                .Replace("__DEVICE_FAMILY__", configuration.Platform == "ios" ? "1" : "3", StringComparison.Ordinal);
            File.WriteAllText(appPath / "Info.plist", plist);

            var sign = CaptureStoreKitProcess("/usr/bin/codesign",
                ["--force", "--sign", "-", appPath], StoreKitCodesignTimeout);
            if (sign.ExitCode != 0)
                throw new Exception($"StoreKit Sandbox native control failed to sign:\n{sign.Output}");

            var runToken = NewRunToken();
            SimCtl.Install(simulatorUdid, appPath);
            var output = LaunchStoreKitSandboxControl(
                simulatorUdid, configuration, runToken, TimeSpan.FromSeconds(75));
            var receipt = StoreKitSandboxReadiness.RequireFreshNativeReceipt(output, configuration, runToken);
            RetainStoreKitSandboxEvidence(configuration, simulatorUdid, receipt, output);
            Log.Information(
                "StoreKit Sandbox native readiness established on {Platform} simulator {Udid}; proceeding to managed launch.",
                configuration.Platform, simulatorUdid);
            return receipt;
        }
        finally
        {
            if (Directory.Exists(scratch))
                scratch.DeleteDirectory();
        }
    }

    string LaunchStoreKitSandboxControl(
        string simulatorUdid,
        StoreKitSandboxReadiness.Configuration configuration,
        string runToken,
        TimeSpan timeout)
    {
        var output = new ConcurrentQueue<string>();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/usr/bin/xcrun")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var argument in new[]
        {
            "simctl", "launch", "--console", "--terminate-running-process",
            simulatorUdid, configuration.BundleId,
            "--storekit-platform", configuration.Platform,
            "--storekit-bundle-id", configuration.BundleId,
            StoreKitSandboxReadiness.ProductIdArgument, configuration.ProductId,
            StoreKitSandboxReadiness.RunTokenArgument, runToken,
        })
            process.StartInfo.ArgumentList.Add(argument);

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) output.Enqueue(e.Data); };
        Log.Information(
            "StoreKit Sandbox native readiness control: platform={Platform} device={Udid} bundle={Bundle} product={Product}",
            configuration.Platform, simulatorUdid, configuration.BundleId, configuration.ProductId);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            SimCtl.Terminate(simulatorUdid, configuration.BundleId);
            throw new Exception(
                $"StoreKit Sandbox native control timed out after {timeout.TotalSeconds:0}s; " +
                "backend readiness is not established. Partial output:\n" + string.Join("\n", output));
        }
        process.WaitForExit();
        SimCtl.Terminate(simulatorUdid, configuration.BundleId);
        var text = string.Join("\n", output);
        Log.Information("=== STOREKIT NATIVE CONTROL OUTPUT ===\n{Output}", text);
        if (process.ExitCode != 0)
            throw new Exception(
                $"StoreKit Sandbox native control exited {process.ExitCode}; backend readiness is not established:\n{text}");
        return text;
    }

    void RetainStoreKitSandboxEvidence(
        StoreKitSandboxReadiness.Configuration configuration,
        string simulatorUdid,
        StoreKitSandboxReadiness.Receipt receipt,
        string output)
    {
        var evidenceDir = (AbsolutePath)Path.Combine(
            Path.GetTempPath(), "swift-bindings-storekit-sandbox-evidence",
            $"{DateTime.UtcNow:yyyyMMdd-HHmmss.fffZ}-p{Environment.ProcessId}-{configuration.Platform}");
        evidenceDir.CreateDirectory();
        File.WriteAllText(evidenceDir / "native-control.log", output);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(output))).ToLowerInvariant();
        File.WriteAllText(evidenceDir / "summary.txt",
            $"platform={configuration.Platform}\n" +
            $"simulator_udid={simulatorUdid}\n" +
            $"bundle_id={configuration.BundleId}\n" +
            $"product_id={configuration.ProductId}\n" +
            $"run_token={receipt.RunToken}\n" +
            "app_transaction_environment=sandbox\n" +
            $"products={receipt.ProductCount}\n" +
            $"current_entitlements={receipt.CurrentEntitlementsCount}\n" +
            $"all_transactions={receipt.AllTransactionsCount}\n" +
            $"native_control_sha256={hash}\n");
        Log.Information("StoreKit Sandbox native receipt retained at {Directory}", evidenceDir);
    }

    static (int ExitCode, string Output) CaptureStoreKitProcess(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            process.WaitForExit();
            var timedOutStdout = stdoutTask.GetAwaiter().GetResult();
            var timedOutStderr = stderrTask.GetAwaiter().GetResult();
            throw new TimeoutException(
                $"StoreKit native-control host command '{fileName}' timed out after " +
                $"{timeout.TotalSeconds:0}s:\n{timedOutStdout}\n{timedOutStderr}");
        }
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return (process.ExitCode, stdout + "\n" + stderr);
    }
}

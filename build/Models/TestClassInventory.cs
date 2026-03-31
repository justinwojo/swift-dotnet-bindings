// Copyright (c) 2026 Justin Wojciechowski.
// Licensed under the MIT License.

using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Parses TestClasses.g.txt (emitted by the source generator) to get the full
/// test class/method inventory. Used by the resume-on-crash orchestrator to
/// compute remaining classes and synthesize CRASHED status for unfinished methods.
/// </summary>
public class TestClassInventory
{
    /// <summary>
    /// All class names in discovery order.
    /// </summary>
    public IReadOnlyList<string> ClassNames { get; }

    /// <summary>
    /// Methods per class: ClassName -> list of MethodName.
    /// </summary>
    public IReadOnlyDictionary<string, List<string>> MethodsByClass { get; }

    private TestClassInventory(IReadOnlyList<string> classNames, IReadOnlyDictionary<string, List<string>> methodsByClass)
    {
        ClassNames = classNames;
        MethodsByClass = methodsByClass;
    }

    /// <summary>
    /// Parses a TestClasses.g.txt file. Each line is "ClassName.MethodName".
    /// Returns empty inventory if file doesn't exist.
    /// </summary>
    public static TestClassInventory Load(string filePath)
    {
        var methodsByClass = new Dictionary<string, List<string>>();
        var classOrder = new List<string>();

        if (!File.Exists(filePath))
            return new TestClassInventory(classOrder, methodsByClass);

        foreach (var line in File.ReadAllLines(filePath))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var dotIndex = trimmed.IndexOf('.');
            if (dotIndex <= 0 || dotIndex >= trimmed.Length - 1) continue;

            var className = trimmed[..dotIndex];
            var methodName = trimmed[(dotIndex + 1)..];

            if (!methodsByClass.ContainsKey(className))
            {
                methodsByClass[className] = new List<string>();
                classOrder.Add(className);
            }
            methodsByClass[className].Add(methodName);
        }

        return new TestClassInventory(classOrder, methodsByClass);
    }

    /// <summary>
    /// Returns method names for a given class, or empty list if class not found.
    /// </summary>
    public IReadOnlyList<string> GetMethods(string className)
        => MethodsByClass.TryGetValue(className, out var methods) ? methods : new List<string>();
}

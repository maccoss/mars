// Copyright (c) University of Washington 2026. Licensed under the MIT License.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace MARS.Cli;

/// <summary>
/// Small hand-rolled option parser. MARS deliberately has no command-line package
/// dependency: the assembly is meant to drop into a managed ProteoWizard tree, and every
/// package it drags along is one more thing that has to be vetted there.
/// </summary>
public sealed class CommandLineArgs
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _positional = new();
    private readonly HashSet<string> _consumed = new(StringComparer.OrdinalIgnoreCase);

    private CommandLineArgs()
    {
    }

    public string Command { get; private set; } = string.Empty;

    public IReadOnlyList<string> Positional => _positional;

    public static CommandLineArgs Parse(string[] args)
    {
        var parsed = new CommandLineArgs();
        int start = 0;

        if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
        {
            parsed.Command = args[0];
            start = 1;
        }

        for (int i = start; i < args.Length; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal) && !arg.StartsWith("-", StringComparison.Ordinal))
            {
                parsed._positional.Add(arg);
                continue;
            }

            string name = arg.TrimStart('-');
            string? value = null;

            int equals = name.IndexOf('=');
            if (equals >= 0)
            {
                value = name[(equals + 1)..];
                name = name[..equals];
            }
            else if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
            {
                value = args[++i];
            }

            if (!parsed._options.TryGetValue(name, out List<string>? values))
            {
                values = new List<string>();
                parsed._options[name] = values;
            }

            values.Add(value ?? "true");
        }

        return parsed;
    }

    public bool Has(params string[] names)
    {
        foreach (string name in names)
        {
            if (_options.ContainsKey(name))
            {
                _consumed.Add(name);
                return true;
            }
        }

        return false;
    }

    public bool Flag(params string[] names)
    {
        foreach (string name in names)
        {
            if (_options.TryGetValue(name, out List<string>? values))
            {
                _consumed.Add(name);
                return values.Count == 0 ||
                       !string.Equals(values[^1], "false", StringComparison.OrdinalIgnoreCase);
            }
        }

        return false;
    }

    public string? String(params string[] names)
    {
        foreach (string name in names)
        {
            if (_options.TryGetValue(name, out List<string>? values) && values.Count > 0)
            {
                _consumed.Add(name);
                return values[^1];
            }
        }

        return null;
    }

    public IReadOnlyList<string> Strings(params string[] names)
    {
        var all = new List<string>();
        foreach (string name in names)
        {
            if (_options.TryGetValue(name, out List<string>? values))
            {
                _consumed.Add(name);
                all.AddRange(values);
            }
        }

        return all;
    }

    public double? Double(params string[] names)
    {
        string? text = String(names);
        if (text is null) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            throw new FormatException($"Option --{names[0]} expects a number, got '{text}'.");
        return value;
    }

    public int? Int(params string[] names)
    {
        string? text = String(names);
        if (text is null) return null;
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            throw new FormatException($"Option --{names[0]} expects an integer, got '{text}'.");
        return value;
    }

    /// <summary>Options the command never asked about; almost always a typo.</summary>
    public IReadOnlyList<string> UnknownOptions() =>
        _options.Keys.Where(k => !_consumed.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();

    /// <summary>
    /// Expands file arguments and glob patterns into a sorted, de-duplicated list. Shells
    /// that do not expand wildcards (cmd.exe) and shells that do both end up here.
    /// </summary>
    public static List<string> ResolveMzMLFiles(IEnumerable<string> patterns, string? directory)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string pattern in patterns)
        {
            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                string folder = Path.GetDirectoryName(pattern) is { Length: > 0 } d ? d : ".";
                string mask = Path.GetFileName(pattern);
                if (Directory.Exists(folder))
                {
                    foreach (string match in Directory.EnumerateFiles(folder, mask))
                    {
                        if (match.EndsWith(".mzML", StringComparison.OrdinalIgnoreCase))
                            files.Add(Path.GetFullPath(match));
                    }
                }
            }
            else if (File.Exists(pattern))
            {
                files.Add(Path.GetFullPath(pattern));
            }
            else
            {
                throw new FileNotFoundException($"File not found: {pattern}");
            }
        }

        if (!string.IsNullOrEmpty(directory))
        {
            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Directory not found: {directory}");
            foreach (string match in Directory.EnumerateFiles(directory, "*.mzML"))
                files.Add(Path.GetFullPath(match));
        }

        var sorted = files.ToList();
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }
}

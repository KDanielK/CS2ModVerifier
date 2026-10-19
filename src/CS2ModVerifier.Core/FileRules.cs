namespace CS2ModVerifier.Core;

public static class FileRules
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".scr", ".com", ".msi", ".lnk", ".hta", ".jar", ".wsf",
    };

    /// <summary>
    /// Base names of the mod's own DLLs: top-level managed DLLs with a Burst companion
    /// (`Name_win_x86_64.dll`), or the only top-level managed DLL. Others are bundled libraries.
    /// </summary>
    public static IReadOnlySet<string> MainAssemblyBaseNames(IReadOnlyList<ModFile> inventory)
    {
        var baseNames = TopLevelManagedBaseNames(inventory);
        var mainBaseNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in baseNames)
        {
            if (inventory.Any(f => string.Equals(Normalize(f.RelativePath), $"{name}_win_x86_64.dll", StringComparison.OrdinalIgnoreCase)))
            {
                mainBaseNames.Add(name);
            }
        }

        if (mainBaseNames.Count == 0 && baseNames.Count == 1)
        {
            mainBaseNames.Add(baseNames[0]);
        }

        return mainBaseNames;
    }

    public static IReadOnlyList<Finding> Evaluate(IReadOnlyList<ModFile> inventory)
    {
        var findings = new List<Finding>();
        var baseNames = TopLevelManagedBaseNames(inventory);

        var expectedNativeKinds = new Dictionary<string, FileKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in baseNames)
        {
            expectedNativeKinds[$"{name}_win_x86_64.dll"] = FileKind.NativeLibrary;
            expectedNativeKinds[$"{name}_mac_x86_64.bundle"] = FileKind.MachO;
            expectedNativeKinds[$"{name}_linux_x86_64.so"] = FileKind.Elf;
        }

        var mainBaseNames = MainAssemblyBaseNames(inventory);

        foreach (var file in inventory)
        {
            var extension = Path.GetExtension(file.RelativePath);
            var fileName = Path.GetFileName(file.RelativePath);

            if (file.Kind is FileKind.NativeLibrary or FileKind.NativeExecutable or FileKind.MachO or FileKind.Elf
                && !expectedNativeKinds.ContainsKey(Normalize(file.RelativePath)))
            {
                findings.Add(new Finding(Severity.High, "UnexpectedNative",
                    $"Unexpected native file: {file.RelativePath}", file.RelativePath));
            }

            if (file.Kind == FileKind.NativeExecutable || ExecutableExtensions.Contains(extension))
            {
                findings.Add(new Finding(Severity.High, "Executable",
                    $"Executable file: {file.RelativePath}", file.RelativePath));
            }

            if (file.Kind is FileKind.ManagedAssembly or FileKind.NativeLibrary)
            {
                var peExtension = extension.ToLowerInvariant();
                if (peExtension != ".dll" && peExtension != ".exe")
                {
                    findings.Add(new Finding(Severity.High, "DisguisedFile",
                        $"PE file with a non-PE extension: {file.RelativePath}", file.RelativePath));
                }
            }

            if (expectedNativeKinds.TryGetValue(Normalize(file.RelativePath), out var expectedKind)
                && file.Kind != expectedKind)
            {
                findings.Add(new Finding(Severity.High, "DisguisedFile",
                    $"File content does not match its native name: {file.RelativePath}", file.RelativePath));
            }

            if (file.Kind == FileKind.ManagedAssembly)
            {
                var isMain = !file.RelativePath.Contains('/')
                    && file.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    && mainBaseNames.Contains(file.RelativePath[..^".dll".Length]);

                if (!isMain)
                {
                    findings.Add(new Finding(Severity.Info, "ExtraAssembly",
                        $"Additional managed assembly {fileName} (SHA-256 {file.Sha256Hex})", file.RelativePath));
                }
            }

            if (file.Kind == FileKind.Unknown)
            {
                findings.Add(new Finding(Severity.High, "UnreadablePE",
                    $"File has a PE header but could not be read as a .NET assembly: {file.RelativePath}", file.RelativePath));
            }
        }

        return findings;
    }

    private static List<string> TopLevelManagedBaseNames(IReadOnlyList<ModFile> inventory) =>
    [
        .. inventory
            .Where(f => f.Kind == FileKind.ManagedAssembly
                && !f.RelativePath.Contains('/')
                && f.RelativePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.RelativePath[..^".dll".Length]),
    ];

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}

namespace CS2ModVerifier.Core;

public static class ModScanner
{
    public static ModReport Scan(ModFolder mod)
    {
        var inventory = FileInventory.Build(mod.Path);
        var manifest = ManifestVerifier.Verify(mod, inventory);

        var assemblies = new List<AssemblyReport>();
        var findings = new List<Finding>();

        var hasCode = inventory.Any(f => f.Kind is FileKind.ManagedAssembly
            or FileKind.NativeLibrary
            or FileKind.NativeExecutable
            or FileKind.MachO
            or FileKind.Elf
            or FileKind.UiScript);

        findings.AddRange(manifest.Issues);

        if (hasCode)
        {
            foreach (var file in inventory)
            {
                if (file.Kind != FileKind.ManagedAssembly)
                {
                    continue;
                }

                var fullPath = Path.Combine(mod.Path, file.RelativePath);
                var report = AssemblyScanner.Scan(fullPath, file.RelativePath);
                if (report.Name is null && report.Mvid is null)
                {
                    findings.Add(new Finding(Severity.High, "Assembly", "Could not read as a .NET assembly", file.RelativePath));
                }

                assemblies.Add(report);
            }

            findings.AddRange(FileRules.Evaluate(inventory));

            foreach (var assembly in assemblies)
            {
                findings.AddRange(RiskRules.Evaluate(assembly));
            }

            findings = LibraryRules.Apply(inventory, assemblies, findings);
        }

        var ordered = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.File ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ModReport(mod, hasCode, inventory, manifest, assemblies, ordered);
    }
}

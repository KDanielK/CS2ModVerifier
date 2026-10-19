namespace CS2ModVerifier.Core;

public static class ManifestVerifier
{
    private sealed record ManifestEntry(string Path, long Size, string HashHex);

    public static ManifestResult Verify(ModFolder mod, IReadOnlyList<ModFile> inventory)
    {
        var manifestPath = FindManifest(mod);
        if (manifestPath is null)
        {
            return new ManifestResult(ManifestStatus.NoManifest, []);
        }

        string content;
        try
        {
            content = File.ReadAllText(manifestPath);
        }
        catch
        {
            return new ManifestResult(ManifestStatus.Unreadable, []);
        }

        string hashAlgorithm;
        ManifestEntry[] entries;
        try
        {
            (hashAlgorithm, entries) = Parse(content);
        }
        catch
        {
            return new ManifestResult(ManifestStatus.Unreadable, []);
        }

        if (!string.Equals(hashAlgorithm, "sha256", StringComparison.OrdinalIgnoreCase))
        {
            return new ManifestResult(ManifestStatus.Unsupported, []);
        }

        var issues = new List<Finding>();
        var inventoryByPath = inventory
            .GroupBy(f => Normalize(f.RelativePath), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries)
        {
            var normalized = Normalize(entry.Path);
            if (!inventoryByPath.TryGetValue(normalized, out var file))
            {
                issues.Add(new Finding(Severity.High, "Manifest", $"File listed in Paradox manifest is missing: {entry.Path}", entry.Path));
                continue;
            }

            if (file.Size != entry.Size)
            {
                issues.Add(new Finding(Severity.High, "Manifest", $"File differs from Paradox manifest (size): {entry.Path}", entry.Path));
                continue;
            }

            if (!string.Equals(file.Sha256Hex, entry.HashHex, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new Finding(Severity.High, "Manifest", "File differs from Paradox manifest", entry.Path));
            }
        }

        var listed = entries
            .Select(e => Normalize(e.Path))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in inventory)
        {
            if (!listed.Contains(Normalize(file.RelativePath)))
            {
                issues.Add(new Finding(Severity.High, "Manifest", "File not in Paradox manifest (added after download?)", file.RelativePath));
            }
        }

        var status = issues.Count == 0 ? ManifestStatus.AllMatch : ManifestStatus.Mismatch;
        return new ManifestResult(status, issues);
    }

    private static string? FindManifest(ModFolder mod)
    {
        var cpatch = Path.Combine(mod.Path, ".cpatch");
        if (!Directory.Exists(cpatch))
        {
            return null;
        }

        var candidates = new List<(string Version, string Path)>();
        foreach (var guidDir in SafeDirectories(cpatch))
        {
            foreach (var versionDir in SafeDirectories(guidDir))
            {
                var manifest = Path.Combine(versionDir, "complete", "manifest");
                if (File.Exists(manifest))
                {
                    candidates.Add((Path.GetFileName(versionDir), manifest));
                }
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (mod.Version is not null)
        {
            foreach (var candidate in candidates)
            {
                if (candidate.Version == mod.Version)
                {
                    return candidate.Path;
                }
            }
        }

        var numeric = candidates
            .Where(c => long.TryParse(c.Version, out _))
            .OrderByDescending(c => long.Parse(c.Version))
            .FirstOrDefault();

        return numeric.Path;
    }

    private static (string HashAlgorithm, ManifestEntry[] Entries) Parse(string content)
    {
        var hashAlgorithm = "sha256";
        var entries = new List<ManifestEntry>();
        var inHeader = true;

        foreach (var raw in content.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (inHeader)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    inHeader = false;
                    continue;
                }

                var colon = line.IndexOf(':');
                if (colon >= 0)
                {
                    var key = line[..colon].Trim();
                    var value = line[(colon + 1)..].Trim();
                    if (key.Equals("Hash", StringComparison.OrdinalIgnoreCase))
                    {
                        hashAlgorithm = value;
                    }
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split(',');
            if (parts.Length < 4)
            {
                throw new FormatException("Manifest entry has too few fields.");
            }

            if (!long.TryParse(parts[^3], out var size))
            {
                throw new FormatException("Manifest entry size is not a number.");
            }

            var path = string.Join(',', parts[..^3]);
            var hash = parts[^2];
            entries.Add(new ManifestEntry(path, size, Base64UrlToHex(hash)));
        }

        return (hashAlgorithm, entries.ToArray());
    }

    private static string Base64UrlToHex(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        standard = (standard.Length % 4) switch
        {
            2 => standard + "==",
            3 => standard + "=",
            _ => standard,
        };
        var bytes = Convert.FromBase64String(standard);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private static IEnumerable<string> SafeDirectories(string path)
    {
        try
        {
            return Directory.EnumerateDirectories(path).ToList();
        }
        catch
        {
            return [];
        }
    }
}

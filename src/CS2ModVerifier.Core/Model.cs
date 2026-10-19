namespace CS2ModVerifier.Core;

public enum Severity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
}

public sealed record Finding(Severity Severity, string Category, string Message, string? File);

public enum ModSource
{
    Paradox,
    Local,
}

public sealed record ModFolder(ModSource Source, string Id, string? Version, string Path);

public enum FileKind
{
    ManagedAssembly,
    NativeLibrary,
    NativeExecutable,
    MachO,
    Elf,
    UiScript,
    Asset,
    Data,
    Unknown,
}

public sealed record ModFile(string RelativePath, long Size, string Sha256Hex, FileKind Kind);

public enum ManifestStatus
{
    NoManifest,
    AllMatch,
    Mismatch,
    Unsupported,
    Unreadable,
}

public sealed record ManifestResult(ManifestStatus Status, IReadOnlyList<Finding> Issues);

public sealed record AssemblyReport(
    string RelativePath,
    string? Name,
    string? Version,
    Guid? Mvid,
    IReadOnlyList<string> AssemblyReferences,
    IReadOnlyList<string> TypeReferences,
    IReadOnlyList<string> MemberReferences,
    IReadOnlyList<string> PInvokes,
    IReadOnlyList<string> EmbeddedResources,
    IReadOnlyList<string> DefinedTypes,
    bool HasModuleInitializer,
    IReadOnlyList<string> UserStrings);

public sealed record ModReport(
    ModFolder Mod,
    bool HasCode,
    IReadOnlyList<ModFile> Files,
    ManifestResult Manifest,
    IReadOnlyList<AssemblyReport> Assemblies,
    IReadOnlyList<Finding> Findings);

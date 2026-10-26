using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CS2ModVerifier.SourceCompare;

public enum SourceTypeKind
{
    Class,
    Struct,
    Interface,
    Enum,
    Delegate,
    Record,
    RecordStruct,
}

public sealed class SourceType(string key, SourceTypeKind kind)
{
    public string Key { get; } = key;

    public SourceTypeKind Kind { get; } = kind;

    public HashSet<string> MethodKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>Method name → declared overloads, for "X?" matching and to tell "signature differs" from "not in source".</summary>
    public Dictionary<string, List<(string Key, IReadOnlyList<string> Parameters)>> MethodsByName { get; } = new(StringComparer.Ordinal);

    public bool HasInstanceConstructor { get; set; }

    public bool HasPrimaryConstructor { get; set; }

    public bool HasStaticInitialization { get; set; }

    public bool IsStatic { get; set; }

    public void AddMethod(string name, IReadOnlyList<string> parameters)
    {
        var key = $"{Key}::{name}({string.Join(",", parameters)})";
        if (!MethodKeys.Add(key))
        {
            return;
        }

        if (!MethodsByName.TryGetValue(name, out var list))
        {
            MethodsByName[name] = list = [];
        }

        list.Add((key, parameters));
    }

    /// <summary>Method name → the bodies written under that name (overloads, accessors, initializers), for tracing calls.</summary>
    public Dictionary<string, List<SourceBody>> Bodies { get; } = new(StringComparer.Ordinal);

    public void AddBody(string name, SourceBody body)
    {
        if (!Bodies.TryGetValue(name, out var list))
        {
            Bodies[name] = list = [];
        }

        // Each file is parsed with several symbol sets; keep one copy per declaration.
        if (!list.Any(b => b.File == body.File && b.Line == body.Line))
        {
            list.Add(body);
        }
    }
}

/// <summary>A method body in source: where it is, and every name used in it with the first line it appears on.</summary>
public sealed record SourceBody(string File, int Line, IReadOnlyDictionary<string, int> Identifiers);

/// <summary>
/// The types and members declared in C# source, parsed as syntax only (no compilation).
/// Source from a zip is read in memory and never extracted to disk.
/// </summary>
public sealed class SourceModel
{
    public const long MaxFileBytes = 5L * 1024 * 1024;
    public const long MaxTotalBytes = 300L * 1024 * 1024;
    public const int MaxZipEntries = 50_000;

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs" };

    private static readonly HashSet<string> ProjectExtensions = new(StringComparer.OrdinalIgnoreCase) { ".csproj", ".props", ".targets", ".projitems" };

    private static readonly CSharpParseOptions BaseOptions = new(LanguageVersion.Latest, DocumentationMode.None);

    private static readonly Regex DefineConstantsPattern = new(@"<DefineConstants[^>]*>([^<]*)</DefineConstants>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex IncludePattern = new(@"<(?:Import|Compile)\b[^>]*?\b(?:Project|Include)\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SymbolPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);

    private static readonly Regex ParentSteps = new(@"^(\.\./)+", RegexOptions.Compiled);

    private readonly HashSet<string> symbols = new(StringComparer.Ordinal) { "DEBUG" };

    private string currentFile = string.Empty;

    public Dictionary<string, SourceType> Types { get; } = new(StringComparer.Ordinal);

    public List<string> Warnings { get; } = [];

    public int FileCount { get; private set; }

    /// <summary>Preprocessor symbols found in the repository's project files, plus DEBUG.</summary>
    public IReadOnlyCollection<string> Symbols => symbols;

    /// <summary>The project file the comparison was limited to, or null when the whole repository was used.</summary>
    public string? Project { get; private set; }

    /// <param name="assemblyName">The DLL's assembly name: only the project that builds it is read. Null reads every C# file.</param>
    public static SourceModel FromDirectory(string directory, string? assemblyName = null)
    {
        var files = new List<(string Path, Func<string?> Read)>();
        foreach (var file in EnumerateFiles(directory))
        {
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            files.Add((relative, () => new FileInfo(file).Length > MaxFileBytes ? null : File.ReadAllText(file)));
        }

        return Load(files, assemblyName);
    }

    /// <param name="assemblyName">The DLL's assembly name: only the project that builds it is read. Null reads every C# file.</param>
    public static SourceModel FromZip(byte[] zip, string? assemblyName = null)
    {
        using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
        if (archive.Entries.Count > MaxZipEntries)
        {
            throw new InvalidDataException($"The archive has more than {MaxZipEntries} entries.");
        }

        // GitHub zipballs put everything in one top folder ("owner-repo-sha/"); paths are taken relative to it.
        // The declared entry length can lie (zip bombs), so every read is capped too.
        var files = archive.Entries
            .Where(e => !e.FullName.EndsWith('/'))
            .Select(e => (StripTopFolder(e.FullName), (Func<string?>)(() => ReadCapped(e, MaxFileBytes))))
            .ToList();
        return Load(files, assemblyName);
    }

    /// <summary>Source from individually downloaded files (repository-relative paths with '/').</summary>
    public static SourceModel FromFiles(IReadOnlyList<(string Path, string Text)> files, string? assemblyName = null) =>
        Load([.. files.Select(f => (f.Path, (Func<string?>)(() => f.Text)))], assemblyName);

    public void AddFile(string text, string path = "")
    {
        FileCount++;
        currentFile = path;

        // Parse without symbols, with DEBUG, and with every symbol the project files define:
        // code in any #if branch is still published source.
        CSharpParseOptions[] variants = [BaseOptions, BaseOptions.WithPreprocessorSymbols("DEBUG"), BaseOptions.WithPreprocessorSymbols(symbols)];
        foreach (var options in variants)
        {
            var root = CSharpSyntaxTree.ParseText(text, options).GetRoot();
            VisitMembers(((CompilationUnitSyntax)root).Members, string.Empty, null);
        }
    }

    private static SourceModel Load(List<(string Path, Func<string?> Read)> files, string? assemblyName)
    {
        var model = new SourceModel();
        var relevant = files.Where(f => !IsInSkippedDirectory(f.Path)).ToList();
        var projectFiles = relevant
            .Where(f => ProjectExtensions.Contains(Path.GetExtension(f.Path)))
            .Select(f => (f.Path, Text: f.Read()))
            .Where(f => f.Text is not null)
            .Select(f => (f.Path, Text: f.Text!))
            .ToList();

        // Only the project that builds the DLL: test projects or other mods in the repository can declare
        // stand-in classes with the same names, which would otherwise be merged into the real ones.
        var scope = assemblyName is null ? null : ProjectScope.Find(projectFiles, assemblyName);
        model.Project = scope?.ProjectPath;
        if (assemblyName is not null && scope is null)
        {
            model.Warnings.Add($"No project file in the repository builds {assemblyName}, so all of its C# files were compared.");
        }

        // Project files first: their symbols decide which #if branches are read. With a known project, only its own
        // file and shared .props/.targets count, and only its own references to outside code are reported.
        foreach (var (path, text) in projectFiles)
        {
            var isOtherProject = scope is not null && path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) && path != scope.ProjectPath;
            if (!isOtherProject)
            {
                model.ReadProjectFile(path, text);
            }
        }

        long total = 0;
        foreach (var (path, read) in relevant.Where(f => f.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && (scope?.Includes(f.Path) ?? true)))
        {
            var text = read();
            if (text is null)
            {
                model.Warnings.Add($"Skipped {path}: larger than {MaxFileBytes / (1024 * 1024)} MB.");
                continue;
            }

            total += text.Length;
            if (total > MaxTotalBytes)
            {
                model.Warnings.Add($"Stopped reading source after {MaxTotalBytes / (1024 * 1024)} MB.");
                break;
            }

            model.AddFile(text, path);
        }

        return model;
    }

    /// <summary>Collects preprocessor symbols, and warns when a project includes code from outside the repository.</summary>
    private void ReadProjectFile(string path, string text)
    {
        foreach (Match match in DefineConstantsPattern.Matches(text))
        {
            foreach (var symbol in match.Groups[1].Value.Split(';', ','))
            {
                var trimmed = symbol.Trim();
                if (SymbolPattern.IsMatch(trimmed))
                {
                    symbols.Add(trimmed);
                }
            }
        }

        var depth = path.Count(c => c == '/');
        foreach (Match match in IncludePattern.Matches(text))
        {
            var value = match.Groups[1].Value.Replace('\\', '/');
            if (value.Contains("$(", StringComparison.Ordinal))
            {
                continue; // SDK and tool paths such as $(CSII_TOOLPATH), not source.
            }

            var steps = ParentSteps.Match(value).Length / 3;
            var absolute = value.StartsWith('/') || (value.Length > 2 && value[1] == ':');
            var warning = $"{path} includes code from outside the repository ({match.Groups[1].Value}). That code cannot be compared.";
            if ((absolute || steps > depth) && !Warnings.Contains(warning))
            {
                Warnings.Add(warning);
            }
        }
    }

    private void VisitMembers(SyntaxList<MemberDeclarationSyntax> members, string ns, SourceType? outer)
    {
        foreach (var member in members)
        {
            switch (member)
            {
                case BaseNamespaceDeclarationSyntax namespaceDeclaration:
                    var name = namespaceDeclaration.Name.ToString();
                    VisitMembers(namespaceDeclaration.Members, ns.Length == 0 ? name : ns + "." + name, outer);
                    break;
                case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                    VisitType(member, ns, outer);
                    break;
            }
        }
    }

    private void VisitType(MemberDeclarationSyntax declaration, string ns, SourceType? outer)
    {
        var (identifier, arity, kind) = declaration switch
        {
            RecordDeclarationSyntax r => (r.Identifier.ValueText, r.TypeParameterList?.Parameters.Count ?? 0,
                r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? SourceTypeKind.RecordStruct : SourceTypeKind.Record),
            ClassDeclarationSyntax c => (c.Identifier.ValueText, c.TypeParameterList?.Parameters.Count ?? 0, SourceTypeKind.Class),
            StructDeclarationSyntax s => (s.Identifier.ValueText, s.TypeParameterList?.Parameters.Count ?? 0, SourceTypeKind.Struct),
            InterfaceDeclarationSyntax i => (i.Identifier.ValueText, i.TypeParameterList?.Parameters.Count ?? 0, SourceTypeKind.Interface),
            EnumDeclarationSyntax e => (e.Identifier.ValueText, 0, SourceTypeKind.Enum),
            DelegateDeclarationSyntax d => (d.Identifier.ValueText, d.TypeParameterList?.Parameters.Count ?? 0, SourceTypeKind.Delegate),
            _ => (string.Empty, 0, SourceTypeKind.Class),
        };

        var typeName = arity > 0 ? $"{identifier}`{arity}" : identifier;
        var key = outer is not null ? outer.Key + "+" + typeName : ns.Length == 0 ? typeName : ns + "." + typeName;
        if (!Types.TryGetValue(key, out var type))
        {
            Types[key] = type = new SourceType(key, kind);
        }

        if (declaration.Modifiers.Any(SyntaxKind.StaticKeyword))
        {
            type.IsStatic = true;
        }

        if (declaration is not TypeDeclarationSyntax typeDeclaration)
        {
            return;
        }

        if (typeDeclaration.ParameterList is { } primary)
        {
            type.HasPrimaryConstructor = true;
            type.AddMethod(".ctor", Parameters(primary));
            if (typeDeclaration is RecordDeclarationSyntax)
            {
                foreach (var parameter in primary.Parameters)
                {
                    var name = parameter.Identifier.ValueText;
                    type.AddMethod("get_" + name, []);
                    type.AddMethod("set_" + name, [TypeNames.NormalizeParameter(parameter)]);
                }
            }
        }

        foreach (var member in typeDeclaration.Members)
        {
            AddMember(type, member, ns);
        }
    }

    /// <summary>Records each body under the metadata name the compiler gives it (get_X, .ctor, ...).</summary>
    private void CaptureBodies(SourceType type, MemberDeclarationSyntax member)
    {
        var isStatic = member.Modifiers.Any(SyntaxKind.StaticKeyword);
        switch (member)
        {
            case MethodDeclarationSyntax method:
                AddBody(type, method.Identifier.ValueText, method);
                break;
            case ConstructorDeclarationSyntax constructor:
                AddBody(type, isStatic ? ".cctor" : ".ctor", constructor);
                break;
            case DestructorDeclarationSyntax destructor:
                AddBody(type, "Finalize", destructor);
                break;
            case BasePropertyDeclarationSyntax property:
                var name = property switch
                {
                    PropertyDeclarationSyntax p => p.Identifier.ValueText,
                    EventDeclarationSyntax e => e.Identifier.ValueText,
                    _ => "Item",
                };
                var arrow = property switch
                {
                    PropertyDeclarationSyntax p => p.ExpressionBody,
                    IndexerDeclarationSyntax i => i.ExpressionBody,
                    _ => null,
                };
                if (arrow is not null)
                {
                    AddBody(type, "get_" + name, arrow);
                }

                foreach (var accessor in property.AccessorList?.Accessors ?? [])
                {
                    var prefix = accessor.Keyword.Kind() switch
                    {
                        SyntaxKind.GetKeyword => "get_",
                        SyntaxKind.AddKeyword => "add_",
                        SyntaxKind.RemoveKeyword => "remove_",
                        _ => "set_",
                    };
                    AddBody(type, prefix + name, accessor);
                }

                // Property initializers run in the constructor.
                if (property is PropertyDeclarationSyntax { Initializer: { } initializer })
                {
                    AddBody(type, isStatic ? ".cctor" : ".ctor", initializer);
                }

                break;
            case FieldDeclarationSyntax field when !field.Modifiers.Any(SyntaxKind.ConstKeyword):
                foreach (var variable in field.Declaration.Variables.Where(v => v.Initializer is not null))
                {
                    AddBody(type, isStatic ? ".cctor" : ".ctor", variable.Initializer!);
                }

                break;
        }
    }

    private void AddBody(SourceType type, string name, SyntaxNode node)
    {
        var identifiers = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var token in node.DescendantTokens())
        {
            if (token.IsKind(SyntaxKind.IdentifierToken))
            {
                identifiers.TryAdd(token.ValueText, token.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
            }
        }

        type.AddBody(name, new SourceBody(currentFile, node.GetLocation().GetLineSpan().StartLinePosition.Line + 1, identifiers));
    }

    private void AddMember(SourceType type, MemberDeclarationSyntax member, string ns)
    {
        CaptureBodies(type, member);
        switch (member)
        {
            case BaseTypeDeclarationSyntax or DelegateDeclarationSyntax:
                VisitType(member, ns, type);
                break;
            case MethodDeclarationSyntax method:
                type.AddMethod(method.Identifier.ValueText, Parameters(method.ParameterList));
                break;
            case ConstructorDeclarationSyntax constructor when constructor.Modifiers.Any(SyntaxKind.StaticKeyword):
                type.HasStaticInitialization = true;
                type.AddMethod(".cctor", []);
                break;
            case ConstructorDeclarationSyntax constructor:
                type.HasInstanceConstructor = true;
                type.AddMethod(".ctor", Parameters(constructor.ParameterList));
                break;
            case DestructorDeclarationSyntax:
                type.AddMethod("Finalize", []);
                break;
            case PropertyDeclarationSyntax property:
                AddAccessors(type, property.Identifier.ValueText, [], TypeNames.Normalize(property.Type), property.AccessorList, property.ExpressionBody);
                if (property.Initializer is not null && property.Modifiers.Any(SyntaxKind.StaticKeyword))
                {
                    type.HasStaticInitialization = true;
                }

                break;
            case IndexerDeclarationSyntax indexer:
                AddAccessors(type, "Item", Parameters(indexer.ParameterList), TypeNames.Normalize(indexer.Type), indexer.AccessorList, indexer.ExpressionBody);
                break;
            case EventDeclarationSyntax eventDeclaration:
                AddEvent(type, eventDeclaration.Identifier.ValueText, TypeNames.Normalize(eventDeclaration.Type));
                break;
            case EventFieldDeclarationSyntax eventField:
                foreach (var variable in eventField.Declaration.Variables)
                {
                    AddEvent(type, variable.Identifier.ValueText, TypeNames.Normalize(eventField.Declaration.Type));
                }

                break;
            case OperatorDeclarationSyntax op:
                var parameters = Parameters(op.ParameterList);
                var operatorName = OperatorName(op.OperatorToken.ValueText, parameters.Count);
                if (!op.CheckedKeyword.IsKind(SyntaxKind.None))
                {
                    operatorName = "op_Checked" + operatorName["op_".Length..];
                }

                type.AddMethod(operatorName, parameters);
                break;
            case ConversionOperatorDeclarationSyntax conversion:
                var conversionName = conversion.ImplicitOrExplicitKeyword.IsKind(SyntaxKind.ImplicitKeyword) ? "op_Implicit" : "op_Explicit";
                if (!conversion.CheckedKeyword.IsKind(SyntaxKind.None))
                {
                    conversionName = "op_CheckedExplicit";
                }

                type.AddMethod(conversionName, Parameters(conversion.ParameterList));
                break;
            case FieldDeclarationSyntax field when field.Modifiers.Any(SyntaxKind.StaticKeyword) && !field.Modifiers.Any(SyntaxKind.ConstKeyword):
                if (field.Declaration.Variables.Any(v => v.Initializer is not null))
                {
                    type.HasStaticInitialization = true;
                }

                break;
        }
    }

    private static void AddAccessors(SourceType type, string name, IReadOnlyList<string> indexParameters, string valueType, AccessorListSyntax? accessors, ArrowExpressionClauseSyntax? expressionBody)
    {
        if (expressionBody is not null || accessors is null)
        {
            type.AddMethod("get_" + name, indexParameters);
            return;
        }

        foreach (var accessor in accessors.Accessors)
        {
            if (accessor.Keyword.IsKind(SyntaxKind.GetKeyword))
            {
                type.AddMethod("get_" + name, indexParameters);
            }
            else if (accessor.Keyword.IsKind(SyntaxKind.SetKeyword) || accessor.Keyword.IsKind(SyntaxKind.InitKeyword))
            {
                type.AddMethod("set_" + name, [.. indexParameters, valueType]);
            }
        }
    }

    private static void AddEvent(SourceType type, string name, string handlerType)
    {
        type.AddMethod("add_" + name, [handlerType]);
        type.AddMethod("remove_" + name, [handlerType]);
    }

    private static List<string> Parameters(BaseParameterListSyntax list) =>
        [.. list.Parameters.Select(TypeNames.NormalizeParameter)];

    private static string OperatorName(string token, int parameterCount) => (token, parameterCount) switch
    {
        ("+", 1) => "op_UnaryPlus",
        ("+", _) => "op_Addition",
        ("-", 1) => "op_UnaryNegation",
        ("-", _) => "op_Subtraction",
        ("*", _) => "op_Multiply",
        ("/", _) => "op_Division",
        ("%", _) => "op_Modulus",
        ("==", _) => "op_Equality",
        ("!=", _) => "op_Inequality",
        ("<", _) => "op_LessThan",
        (">", _) => "op_GreaterThan",
        ("<=", _) => "op_LessThanOrEqual",
        (">=", _) => "op_GreaterThanOrEqual",
        ("!", _) => "op_LogicalNot",
        ("~", _) => "op_OnesComplement",
        ("++", _) => "op_Increment",
        ("--", _) => "op_Decrement",
        ("true", _) => "op_True",
        ("false", _) => "op_False",
        ("&", _) => "op_BitwiseAnd",
        ("|", _) => "op_BitwiseOr",
        ("^", _) => "op_ExclusiveOr",
        ("<<", _) => "op_LeftShift",
        (">>", _) => "op_RightShift",
        (">>>", _) => "op_UnsignedRightShift",
        _ => "op_" + token,
    };

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                var extension = Path.GetExtension(file);
                if (extension.Equals(".cs", StringComparison.OrdinalIgnoreCase) || ProjectExtensions.Contains(extension))
                {
                    yield return file;
                }
            }

            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child)))
                {
                    pending.Push(child);
                }
            }
        }
    }

    private static bool IsInSkippedDirectory(string path) =>
        path.Split('/', '\\').SkipLast(1).Any(SkippedDirectories.Contains);

    private static string StripTopFolder(string zipPath)
    {
        var slash = zipPath.IndexOf('/');
        return slash < 0 ? zipPath : zipPath[(slash + 1)..];
    }

    private static string? ReadCapped(ZipArchiveEntry entry, long maxBytes)
    {
        if (entry.Length > maxBytes)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CS2ModVerifier.SourceCompare;

/// <summary>
/// Normalizes type names so that source syntax and metadata signatures produce identical strings.
/// A syntax-only parse cannot resolve namespaces, so types are compared by simple name:
/// "System.Collections.Generic.List&lt;int&gt;" and "List`1&lt;Int32&gt;" both become "List".
/// </summary>
public static class TypeNames
{
    private static readonly Dictionary<string, string> Keywords = new(StringComparer.Ordinal)
    {
        ["bool"] = "Boolean",
        ["byte"] = "Byte",
        ["sbyte"] = "SByte",
        ["char"] = "Char",
        ["decimal"] = "Decimal",
        ["double"] = "Double",
        ["float"] = "Single",
        ["int"] = "Int32",
        ["uint"] = "UInt32",
        ["long"] = "Int64",
        ["ulong"] = "UInt64",
        ["short"] = "Int16",
        ["ushort"] = "UInt16",
        ["object"] = "Object",
        ["string"] = "String",
        ["void"] = "Void",
        ["nint"] = "IntPtr",
        ["nuint"] = "UIntPtr",
        ["dynamic"] = "Object",
    };

    // `X?` on these is Nullable<X>. On anything else the syntax alone cannot tell a struct (Nullable<X>)
    // from a nullable reference (plain X), so it becomes "X?" and the comparer accepts either form.
    private static readonly HashSet<string> ValueTypeKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "sbyte", "char", "decimal", "double", "float", "int", "uint",
        "long", "ulong", "short", "ushort", "nint", "nuint",
    };

    /// <summary>Removes a generic arity suffix: "List`1" becomes "List".</summary>
    public static string StripArity(string name)
    {
        var tick = name.IndexOf('`');
        return tick < 0 ? name : name[..tick];
    }

    public static string Normalize(TypeSyntax type) => type switch
    {
        PredefinedTypeSyntax p => Keyword(p.Keyword.ValueText),
        NullableTypeSyntax n => n.ElementType is PredefinedTypeSyntax p && ValueTypeKeywords.Contains(p.Keyword.ValueText)
            ? "Nullable"
            : Normalize(n.ElementType) + "?",
        ArrayTypeSyntax a => Normalize(a.ElementType) + string.Concat(a.RankSpecifiers.Select(r => "[" + new string(',', r.Rank - 1) + "]")),
        PointerTypeSyntax p => Normalize(p.ElementType) + "*",
        FunctionPointerTypeSyntax => "FnPtr",
        TupleTypeSyntax => "ValueTuple",
        RefTypeSyntax r => Normalize(r.Type) + "&",
        ScopedTypeSyntax s => Normalize(s.Type),
        QualifiedNameSyntax q => Normalize(q.Right),
        AliasQualifiedNameSyntax a => Normalize(a.Name),
        GenericNameSyntax g => g.Identifier.ValueText,
        IdentifierNameSyntax i => Keyword(i.Identifier.ValueText),
        _ => type.ToString(),
    };

    /// <summary>A parameter's normalized type: ref, out and in become a trailing "&amp;".</summary>
    public static string NormalizeParameter(BaseParameterSyntax parameter)
    {
        var type = parameter.Type is null ? "?" : Normalize(parameter.Type);
        var byRef = parameter.Modifiers.Any(m =>
            m.IsKind(SyntaxKind.RefKeyword) || m.IsKind(SyntaxKind.OutKeyword) || m.IsKind(SyntaxKind.InKeyword));
        return byRef ? type + "&" : type;
    }

    public static string Keyword(string name) => Keywords.TryGetValue(name, out var mapped) ? mapped : name;

    /// <summary>
    /// Whether a normalized source parameter type matches a metadata one. Equal strings match; a source
    /// "X?" (struct or reference, unknown from syntax) matches metadata "X" or "Nullable" in the same position.
    /// </summary>
    public static bool ParameterMatches(string source, string metadata)
    {
        if (source == metadata)
        {
            return true;
        }

        if (!source.Contains('?'))
        {
            return false;
        }

        var pattern = "^" + System.Text.RegularExpressions.Regex.Replace(
            System.Text.RegularExpressions.Regex.Escape(source), @"([A-Za-z0-9_]+)\\\?", "(?:$1|Nullable)") + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(metadata, pattern);
    }

    /// <summary>Maps a metadata primitive type to the same simple name the source side produces.</summary>
    public static string Primitive(System.Reflection.Metadata.PrimitiveTypeCode code) => code switch
    {
        System.Reflection.Metadata.PrimitiveTypeCode.Boolean => "Boolean",
        System.Reflection.Metadata.PrimitiveTypeCode.Byte => "Byte",
        System.Reflection.Metadata.PrimitiveTypeCode.SByte => "SByte",
        System.Reflection.Metadata.PrimitiveTypeCode.Char => "Char",
        System.Reflection.Metadata.PrimitiveTypeCode.Double => "Double",
        System.Reflection.Metadata.PrimitiveTypeCode.Single => "Single",
        System.Reflection.Metadata.PrimitiveTypeCode.Int16 => "Int16",
        System.Reflection.Metadata.PrimitiveTypeCode.Int32 => "Int32",
        System.Reflection.Metadata.PrimitiveTypeCode.Int64 => "Int64",
        System.Reflection.Metadata.PrimitiveTypeCode.UInt16 => "UInt16",
        System.Reflection.Metadata.PrimitiveTypeCode.UInt32 => "UInt32",
        System.Reflection.Metadata.PrimitiveTypeCode.UInt64 => "UInt64",
        System.Reflection.Metadata.PrimitiveTypeCode.IntPtr => "IntPtr",
        System.Reflection.Metadata.PrimitiveTypeCode.UIntPtr => "UIntPtr",
        System.Reflection.Metadata.PrimitiveTypeCode.Object => "Object",
        System.Reflection.Metadata.PrimitiveTypeCode.String => "String",
        System.Reflection.Metadata.PrimitiveTypeCode.Void => "Void",
        System.Reflection.Metadata.PrimitiveTypeCode.TypedReference => "TypedReference",
        _ => code.ToString(),
    };
}

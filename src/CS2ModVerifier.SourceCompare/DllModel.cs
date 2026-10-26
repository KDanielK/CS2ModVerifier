using System.Collections.Immutable;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace CS2ModVerifier.SourceCompare;

/// <param name="OwnerTypeKey">The hand-written type this code belongs to (lambdas and state machines map to their enclosing type).</param>
/// <param name="OwnerName">The hand-written method this code belongs to: "Capture" for the lambda "&lt;Capture&gt;b__0_0".</param>
/// <param name="Calls">Outside APIs the method body calls ("System.Diagnostics.Process::Start"), and P/Invoke calls as "native:module!Name".</param>
public sealed record DllMethod(string Key, string Name, IReadOnlyList<string> Parameters, string? GeneratedRule)
{
    public string OwnerTypeKey { get; init; } = string.Empty;

    public string OwnerName { get; init; } = string.Empty;

    public IReadOnlyList<string> Calls { get; init; } = [];
}

public sealed record DllType(
    string Key,
    bool IsDelegate,
    bool IsEnum,
    bool IsRecordLike,
    string? GeneratedRule,
    IReadOnlyList<DllMethod> Methods);

/// <summary>
/// The types and method signatures of a DLL, read as metadata only. The DLL is never loaded or run.
/// </summary>
public sealed partial record DllModel(string FileName, string? AssemblyName, string? Version, string? InformationalVersion, IReadOnlyList<DllType> Types)
{
    public static DllModel Read(string dllPath)
    {
        var bytes = File.ReadAllBytes(dllPath);
        using var pe = new PEReader(ImmutableArray.Create(bytes));
        if (!pe.HasMetadata)
        {
            throw new InvalidDataException("The file is not a .NET assembly.");
        }

        var md = pe.GetMetadataReader();
        string? name = null, version = null, informational = null;
        if (md.IsAssembly)
        {
            var assembly = md.GetAssemblyDefinition();
            name = md.GetString(assembly.Name);
            version = assembly.Version.ToString();
            informational = ReadInformationalVersion(md, assembly);
        }

        var types = new List<DllType>();
        foreach (var handle in md.TypeDefinitions)
        {
            var definition = md.GetTypeDefinition(handle);
            if (md.GetString(definition.Name) == "<Module>")
            {
                continue;
            }

            types.Add(ReadType(pe, md, handle));
        }

        return new DllModel(Path.GetFileName(dllPath), name, version, informational, types);
    }

    private static DllType ReadType(PEReader pe, MetadataReader md, TypeDefinitionHandle handle)
    {
        var definition = md.GetTypeDefinition(handle);
        var key = TypeKey(md, handle);
        var generated = TypeGeneratedRule(md, handle);
        var baseType = BaseTypeName(md, definition);
        var typeParameters = GenericParameterNames(md, definition.GetGenericParameters());
        var (ownerTypeKey, ownerFromType) = Owner(md, handle);

        var methods = new List<DllMethod>();
        var isRecordLike = false;
        foreach (var methodHandle in definition.GetMethods())
        {
            var method = md.GetMethodDefinition(methodHandle);
            var metadataName = md.GetString(method.Name);
            if (metadataName == "<Clone>$")
            {
                isRecordLike = true;
            }

            // Explicit interface implementations are named "Namespace.IFoo.Bar"; source writes "Bar".
            var memberName = metadataName;
            var lastDot = metadataName.LastIndexOf('.');
            if (lastDot > 0 && metadataName != ".ctor" && metadataName != ".cctor")
            {
                memberName = metadataName[(lastDot + 1)..];
            }

            string[] parameters;
            try
            {
                var context = new GenericContext(typeParameters, GenericParameterNames(md, method.GetGenericParameters()));
                var signature = method.DecodeSignature(SignatureTypeProvider.Instance, context);
                parameters = [.. signature.ParameterTypes];
            }
            catch (BadImageFormatException)
            {
                parameters = ["?"];
            }

            // Lambdas, local functions and state machines are attributed to the method they were written in.
            var nameMatch = CompilerNamePrefix().Match(metadataName);
            var ownerName = nameMatch.Success && nameMatch.Groups[1].Value.Length > 0 ? nameMatch.Groups[1].Value : ownerFromType ?? memberName;

            methods.Add(new DllMethod(
                $"{key}::{memberName}({string.Join(",", parameters)})",
                memberName,
                parameters,
                generated ?? GeneratedCodeRules.Match(metadataName))
            {
                OwnerTypeKey = ownerTypeKey,
                OwnerName = ownerName,
                Calls = ReadCalls(pe, md, method),
            });
        }

        return new DllType(
            key,
            baseType == "System.MulticastDelegate",
            baseType == "System.Enum",
            isRecordLike,
            generated,
            methods);
    }

    /// <summary>Operand sizes of every IL opcode, from .NET's own opcode table (not typed in by hand).</summary>
    private static readonly Dictionary<ushort, OperandType> OperandTypes = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(f => f.GetValue(null))
        .OfType<OpCode>()
        .ToDictionary(o => unchecked((ushort)o.Value), o => o.OperandType);

    [GeneratedRegex(@"^<([^>]*)>")]
    private static partial Regex CompilerNamePrefix();

    /// <summary>The nearest hand-written enclosing type, and the method name a generated type carries ("&lt;LaterAsync&gt;d__2").</summary>
    private static (string TypeKey, string? MethodName) Owner(MetadataReader md, TypeDefinitionHandle handle)
    {
        string? methodName = null;
        var current = handle;
        while (true)
        {
            var definition = md.GetTypeDefinition(current);
            var name = md.GetString(definition.Name);
            var declaring = definition.GetDeclaringType();
            if (GeneratedCodeRules.Match(name) is null || declaring.IsNil)
            {
                return (TypeKey(md, current), methodName);
            }

            var match = CompilerNamePrefix().Match(name);
            if (methodName is null && match.Success && match.Groups[1].Value.Length > 0)
            {
                methodName = match.Groups[1].Value;
            }

            current = declaring;
        }
    }

    /// <summary>
    /// The outside APIs a method body calls (call, callvirt, newobj, ldftn), read by decoding its IL. Calls to the
    /// DLL's own P/Invoke declarations are listed as "native:module!Name".
    /// </summary>
    private static IReadOnlyList<string> ReadCalls(PEReader pe, MetadataReader md, MethodDefinition method)
    {
        if (method.RelativeVirtualAddress == 0)
        {
            return [];
        }

        var calls = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var il = pe.GetMethodBody(method.RelativeVirtualAddress).GetILBytes() ?? [];
            for (var i = 0; i < il.Length;)
            {
                ushort code = il[i++];
                if (code == 0xFE && i < il.Length)
                {
                    code = (ushort)(0xFE00 | il[i++]);
                }

                if (!OperandTypes.TryGetValue(code, out var operand))
                {
                    break; // Not valid IL: stop rather than misread the rest.
                }

                if (operand == OperandType.InlineMethod && i + 4 <= il.Length)
                {
                    var target = CallTarget(md, BitConverter.ToInt32(il.AsSpan(i, 4)));
                    if (target is not null)
                    {
                        calls.Add(target);
                    }
                }

                i += operand switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch when i + 4 <= il.Length => 4 + (4 * BitConverter.ToInt32(il.AsSpan(i, 4))),
                    _ => 4,
                };
            }
        }
        catch (Exception e) when (e is BadImageFormatException or ArgumentException or InvalidOperationException)
        {
            // An unreadable body: keep what was decoded.
        }

        return [.. calls];
    }

    private static string? CallTarget(MetadataReader md, int token)
    {
        var handle = MetadataTokens.EntityHandle(token);
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var reference = md.GetMemberReference((MemberReferenceHandle)handle);
                if (ParentName(md, reference.Parent) is not { } parent)
                {
                    return null;
                }

                var name = parent + "::" + md.GetString(reference.Name);
                return OverloadSensitive.Contains(name) ? WithParameters(reference, name) : name;
            case HandleKind.MethodSpecification:
                return CallTarget(md, MetadataTokens.GetToken(md.GetMethodSpecification((MethodSpecificationHandle)handle).Method));
            case HandleKind.MethodDefinition:
                var definition = md.GetMethodDefinition((MethodDefinitionHandle)handle);
                var import = definition.GetImport();
                return import.Module.IsNil
                    ? null
                    : $"native:{md.GetString(md.GetModuleReference(import.Module).Name)}!{md.GetString(definition.Name)}";
            default:
                return null;
        }
    }

    /// <summary>Calls whose risk depends on the overload: Assembly.Load(byte[]) loads code from memory, Load(string) finds one by name.</summary>
    private static readonly HashSet<string> OverloadSensitive = new(StringComparer.Ordinal) { "System.Reflection.Assembly::Load" };

    private static string WithParameters(MemberReference reference, string name)
    {
        try
        {
            var signature = reference.DecodeMethodSignature(SignatureTypeProvider.Instance, new GenericContext([], []));
            return $"{name}({string.Join(",", signature.ParameterTypes)})";
        }
        catch (BadImageFormatException)
        {
            return name; // Unknown overload: classified as the risky one.
        }
    }

    private static string? ParentName(MetadataReader md, EntityHandle parent)
    {
        switch (parent.Kind)
        {
            case HandleKind.TypeReference:
                var reference = md.GetTypeReference((TypeReferenceHandle)parent);
                var ns = md.GetString(reference.Namespace);
                var name = md.GetString(reference.Name);
                return ns.Length == 0 ? name : ns + "." + name;
            case HandleKind.TypeSpecification:
                // A generic instance such as List<int>: the generic type is enough here.
                var blob = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)parent).Signature);
                if (blob.ReadByte() == 0x15)
                {
                    blob.ReadByte();
                    var inner = blob.ReadTypeHandle();
                    return inner.Kind == HandleKind.TypeReference ? ParentName(md, inner) : null;
                }

                return null;
            default:
                return null; // The DLL's own types are covered by the signature comparison.
        }
    }

    /// <summary>Namespace.Name, nested types joined with '+', metadata arity kept ("Box`1+Inner").</summary>
    private static string TypeKey(MetadataReader md, TypeDefinitionHandle handle)
    {
        var definition = md.GetTypeDefinition(handle);
        var name = md.GetString(definition.Name);
        var declaring = definition.GetDeclaringType();
        if (!declaring.IsNil)
        {
            return TypeKey(md, declaring) + "+" + name;
        }

        var ns = md.GetString(definition.Namespace);
        return ns.Length == 0 ? name : ns + "." + name;
    }

    /// <summary>A type is generated when its own name, or a declaring type's name, matches a rule, or it is a compiler-embedded attribute.</summary>
    private static string? TypeGeneratedRule(MetadataReader md, TypeDefinitionHandle handle)
    {
        for (var current = handle; !current.IsNil; current = md.GetTypeDefinition(current).GetDeclaringType())
        {
            var definition = md.GetTypeDefinition(current);
            var rule = GeneratedCodeRules.Match(md.GetString(definition.Name));
            if (rule is not null)
            {
                return rule;
            }

            if (IsCompilerEmbedded(md, definition))
            {
                return "embedded-attribute";
            }
        }

        return null;
    }

    /// <summary>
    /// The compiler embeds helper attributes (NullableAttribute, RefSafetyRulesAttribute, ...) marked with
    /// Microsoft.CodeAnalysis.EmbeddedAttribute, which it also embeds itself.
    /// </summary>
    private static bool IsCompilerEmbedded(MetadataReader md, TypeDefinition definition)
    {
        if (md.GetString(definition.Namespace) == "Microsoft.CodeAnalysis" && md.GetString(definition.Name) == "EmbeddedAttribute")
        {
            return true;
        }

        foreach (var attributeHandle in definition.GetCustomAttributes())
        {
            if (AttributeTypeName(md, md.GetCustomAttribute(attributeHandle)) == "Microsoft.CodeAnalysis.EmbeddedAttribute")
            {
                return true;
            }
        }

        return false;
    }

    private static string? AttributeTypeName(MetadataReader md, CustomAttribute attribute)
    {
        switch (attribute.Constructor.Kind)
        {
            case HandleKind.MemberReference:
                var reference = md.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
                if (reference.Parent.Kind == HandleKind.TypeReference)
                {
                    var type = md.GetTypeReference((TypeReferenceHandle)reference.Parent);
                    return md.GetString(type.Namespace) + "." + md.GetString(type.Name);
                }

                if (reference.Parent.Kind == HandleKind.TypeDefinition)
                {
                    var type = md.GetTypeDefinition((TypeDefinitionHandle)reference.Parent);
                    return md.GetString(type.Namespace) + "." + md.GetString(type.Name);
                }

                return null;
            case HandleKind.MethodDefinition:
                var method = md.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor);
                var declaring = md.GetTypeDefinition(method.GetDeclaringType());
                return md.GetString(declaring.Namespace) + "." + md.GetString(declaring.Name);
            default:
                return null;
        }
    }

    private static string? BaseTypeName(MetadataReader md, TypeDefinition definition)
    {
        var baseType = definition.BaseType;
        if (baseType.Kind == HandleKind.TypeReference)
        {
            var reference = md.GetTypeReference((TypeReferenceHandle)baseType);
            return md.GetString(reference.Namespace) + "." + md.GetString(reference.Name);
        }

        return null;
    }

    private static ImmutableArray<string> GenericParameterNames(MetadataReader md, GenericParameterHandleCollection handles) =>
        [.. handles.Select(h => md.GetString(md.GetGenericParameter(h).Name))];

    private static string? ReadInformationalVersion(MetadataReader md, AssemblyDefinition assembly)
    {
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = md.GetCustomAttribute(handle);
            if (AttributeTypeName(md, attribute) != "System.Reflection.AssemblyInformationalVersionAttribute")
            {
                continue;
            }

            try
            {
                // Blob: prolog 0x0001, then one SerString argument.
                var reader = md.GetBlobReader(attribute.Value);
                if (reader.ReadUInt16() == 1)
                {
                    return reader.ReadSerializedString();
                }
            }
            catch (BadImageFormatException)
            {
                return null;
            }
        }

        return null;
    }
}

public sealed record GenericContext(ImmutableArray<string> TypeParameters, ImmutableArray<string> MethodParameters);

/// <summary>Turns metadata signatures into the same normalized strings as <see cref="TypeNames.Normalize"/>.</summary>
public sealed class SignatureTypeProvider : ISignatureTypeProvider<string, GenericContext>
{
    public static readonly SignatureTypeProvider Instance = new();

    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => TypeNames.Primitive(typeCode);

    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
        TypeNames.StripArity(reader.GetString(reader.GetTypeDefinition(handle).Name));

    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind) =>
        TypeNames.StripArity(reader.GetString(reader.GetTypeReference(handle).Name));

    public string GetTypeFromSpecification(MetadataReader reader, GenericContext genericContext, TypeSpecificationHandle handle, byte rawTypeKind) =>
        reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

    public string GetSZArrayType(string elementType) => elementType + "[]";

    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[" + new string(',', shape.Rank - 1) + "]";

    public string GetByReferenceType(string elementType) => elementType + "&";

    public string GetPointerType(string elementType) => elementType + "*";

    public string GetPinnedType(string elementType) => elementType;

    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType;

    public string GetGenericTypeParameter(GenericContext genericContext, int index) =>
        index < genericContext.TypeParameters.Length ? genericContext.TypeParameters[index] : "!" + index;

    public string GetGenericMethodParameter(GenericContext genericContext, int index) =>
        index < genericContext.MethodParameters.Length ? genericContext.MethodParameters[index] : "!!" + index;

    public string GetFunctionPointerType(MethodSignature<string> signature) => "FnPtr";

    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType;
}

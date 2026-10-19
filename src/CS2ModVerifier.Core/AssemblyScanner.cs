using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace CS2ModVerifier.Core;

public static class AssemblyScanner
{
    public static AssemblyReport Scan(string fullPath, string relativePath)
    {
        var assemblyReferences = new List<string>();
        var typeReferences = new List<string>();
        var memberReferences = new List<string>();
        var pInvokes = new List<string>();
        var embeddedResources = new List<string>();
        var definedTypes = new List<string>();
        var userStrings = new List<string>();
        string? name = null;
        string? version = null;
        Guid? mvid = null;
        var hasModuleInitializer = false;

        try
        {
            var bytes = ReadAllBytes(fullPath);
            using var pe = new PEReader(ImmutableArray.Create(bytes));
            if (!pe.HasMetadata)
            {
                return Build(relativePath, name, version, mvid, assemblyReferences, typeReferences,
                    memberReferences, pInvokes, embeddedResources, definedTypes, hasModuleInitializer, userStrings);
            }

            var md = pe.GetMetadataReader();

            if (md.IsAssembly)
            {
                var assembly = md.GetAssemblyDefinition();
                name = md.GetString(assembly.Name);
                version = assembly.Version.ToString();
            }

            var module = md.GetModuleDefinition();
            mvid = md.GetGuid(module.Mvid);

            foreach (var handle in md.AssemblyReferences)
            {
                var reference = md.GetAssemblyReference(handle);
                assemblyReferences.Add($"{md.GetString(reference.Name)}, {reference.Version}");
            }

            foreach (var handle in md.TypeReferences)
            {
                typeReferences.Add(GetTypeReferenceFullName(md, handle));
            }

            foreach (var handle in md.MemberReferences)
            {
                try
                {
                    memberReferences.Add(GetMemberReferenceName(md, handle));
                }
                catch
                {
                    // skip a single malformed member reference
                }
            }

            foreach (var handle in md.TypeDefinitions)
            {
                var typeDefinition = md.GetTypeDefinition(handle);
                var typeName = md.GetString(typeDefinition.Name);
                if (typeName == "<Module>")
                {
                    foreach (var methodHandle in typeDefinition.GetMethods())
                    {
                        var method = md.GetMethodDefinition(methodHandle);
                        if (md.GetString(method.Name) == ".cctor")
                        {
                            hasModuleInitializer = true;
                        }
                    }

                    continue;
                }

                definedTypes.Add(GetTypeDefinitionFullName(md, handle));
            }

            foreach (var handle in md.MethodDefinitions)
            {
                var method = md.GetMethodDefinition(handle);
                var import = method.GetImport();
                if (!import.Module.IsNil)
                {
                    var moduleReference = md.GetModuleReference(import.Module);
                    pInvokes.Add($"{md.GetString(moduleReference.Name)}!{md.GetString(import.Name)}");
                }
            }

            // Read the whole #US heap rather than decoding IL: every string literal used by
            // ldstr must live here, so nothing can be skipped by a mis-decoded instruction.
            ReadUserStrings(md, userStrings);

            foreach (var handle in md.ManifestResources)
            {
                var resource = md.GetManifestResource(handle);
                var resourceName = md.GetString(resource.Name);
                if (resource.Implementation.IsNil)
                {
                    var size = GetEmbeddedResourceSize(pe, resource);
                    embeddedResources.Add(size is int s ? $"{resourceName} ({s} bytes)" : resourceName);
                }
                else
                {
                    embeddedResources.Add(resourceName);
                }
            }
        }
        catch
        {
            // Return whatever was read before the failure.
        }

        return Build(relativePath, name, version, mvid, assemblyReferences, typeReferences,
            memberReferences, pInvokes, embeddedResources, definedTypes, hasModuleInitializer, userStrings);
    }

    private static AssemblyReport Build(
        string relativePath,
        string? name,
        string? version,
        Guid? mvid,
        List<string> assemblyReferences,
        List<string> typeReferences,
        List<string> memberReferences,
        List<string> pInvokes,
        List<string> embeddedResources,
        List<string> definedTypes,
        bool hasModuleInitializer,
        List<string> userStrings)
    {
        return new AssemblyReport(
            relativePath,
            name,
            version,
            mvid,
            Sort(assemblyReferences),
            Sort(typeReferences),
            Sort(memberReferences),
            Sort(pInvokes),
            Sort(embeddedResources),
            Sort(definedTypes),
            hasModuleInitializer,
            Sort(userStrings));
    }

    private static List<string> Sort(List<string> list)
    {
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static byte[] ReadAllBytes(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        file.CopyTo(memory);
        return memory.ToArray();
    }

    private static string GetTypeReferenceFullName(MetadataReader md, TypeReferenceHandle handle)
    {
        var reference = md.GetTypeReference(handle);
        var name = md.GetString(reference.Name);
        if (reference.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return GetTypeReferenceFullName(md, (TypeReferenceHandle)reference.ResolutionScope) + "+" + name;
        }

        var ns = md.GetString(reference.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string GetTypeDefinitionFullName(MetadataReader md, TypeDefinitionHandle handle)
    {
        var definition = md.GetTypeDefinition(handle);
        var name = md.GetString(definition.Name);
        var declaringType = definition.GetDeclaringType();
        if (!declaringType.IsNil)
        {
            return GetTypeDefinitionFullName(md, declaringType) + "+" + name;
        }

        var ns = md.GetString(definition.Namespace);
        return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
    }

    private static string GetMemberReferenceName(MetadataReader md, MemberReferenceHandle handle)
    {
        var reference = md.GetMemberReference(handle);
        var memberName = md.GetString(reference.Name);
        var parentType = ResolveParentTypeName(md, reference.Parent);
        return parentType + "::" + memberName;
    }

    private static string ResolveParentTypeName(MetadataReader md, EntityHandle parent)
    {
        return parent.Kind switch
        {
            HandleKind.TypeReference => GetTypeReferenceFullName(md, (TypeReferenceHandle)parent),
            HandleKind.TypeDefinition => GetTypeDefinitionFullName(md, (TypeDefinitionHandle)parent),
            HandleKind.TypeSpecification => GetTypeSpecificationName(md, (TypeSpecificationHandle)parent),
            _ => "?",
        };
    }

    private static string GetTypeSpecificationName(MetadataReader md, TypeSpecificationHandle handle)
    {
        try
        {
            var specification = md.GetTypeSpecification(handle);
            var reader = md.GetBlobReader(specification.Signature);
            return DecodeTypeName(md, reader);
        }
        catch
        {
            return "?";
        }
    }

    private static string DecodeTypeName(MetadataReader md, BlobReader reader)
    {
        var elementType = reader.ReadByte();
        switch (elementType)
        {
            case 0x11: // VALUETYPE
            case 0x12: // CLASS
                return DecodeTypeDefOrRef(md, (uint)reader.ReadCompressedInteger());
            case 0x15: // GENERICINST
                return DecodeTypeName(md, reader);
            case 0x1D: // SZARRAY
                return DecodeTypeName(md, reader) + "[]";
            default:
                return "?";
        }
    }

    private static string DecodeTypeDefOrRef(MetadataReader md, uint codedIndex)
    {
        var tag = codedIndex & 0x3u;
        var row = (int)(codedIndex >> 2);
        return tag switch
        {
            0 => GetTypeDefinitionFullName(md, MetadataTokens.TypeDefinitionHandle(row)),
            1 => GetTypeReferenceFullName(md, MetadataTokens.TypeReferenceHandle(row)),
            2 => GetTypeSpecificationName(md, MetadataTokens.TypeSpecificationHandle(row)),
            _ => "?",
        };
    }

    private static int? GetEmbeddedResourceSize(PEReader pe, ManifestResource resource)
    {
        try
        {
            var corHeader = pe.PEHeaders.CorHeader;
            if (corHeader is null)
            {
                return null;
            }

            var resourcesRva = corHeader.ResourcesDirectory.RelativeVirtualAddress;
            if (resourcesRva == 0)
            {
                return null;
            }

            var block = pe.GetSectionData(resourcesRva);
            var offset = checked((int)resource.Offset);
            if (offset < 0 || offset + 4 > block.Length)
            {
                return null;
            }

            var reader = block.GetReader(offset, 4);
            var length = reader.ReadInt32();
            if (length <= 0 || offset + 4 + length > block.Length)
            {
                return null;
            }

            return length;
        }
        catch
        {
            return null;
        }
    }

    private static void ReadUserStrings(MetadataReader md, List<string> userStrings)
    {
        if (md.GetHeapSize(HeapIndex.UserString) <= 1)
        {
            return;
        }

        try
        {
            var handle = MetadataTokens.UserStringHandle(1);
            while (!handle.IsNil)
            {
                var value = md.GetUserString(handle);
                if (!string.IsNullOrEmpty(value))
                {
                    userStrings.Add(value);
                }

                handle = md.GetNextHandle(handle);
            }
        }
        catch
        {
            // Keep the strings read before a malformed heap entry.
        }
    }
}

using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace CS2ModVerifier.Core;

public static class FileInventory
{
    public static IReadOnlyList<ModFile> Build(string modPath)
    {
        var result = new List<ModFile>();
        if (!Directory.Exists(modPath))
        {
            return result;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(modPath, "*", SearchOption.AllDirectories).ToList();
        }
        catch
        {
            return result;
        }

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(modPath, file).Replace('\\', '/');
            var firstSegment = relative.Split('/')[0];
            if (firstSegment.Equals(".cpatch", StringComparison.OrdinalIgnoreCase)
                || firstSegment.Equals(".metadata", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            result.Add(BuildOne(file, relative));
        }

        return result.OrderBy(f => f.RelativePath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static ModFile BuildOne(string fullPath, string relativePath)
    {
        try
        {
            using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var first = ReadFirst(stream);
            var sha256 = ComputeSha256(stream);
            var kind = Classify(stream, first, relativePath);
            return new ModFile(relativePath, length, sha256, kind);
        }
        catch
        {
            return Fallback(fullPath, relativePath);
        }
    }

    private static byte[] ReadFirst(Stream stream)
    {
        var length = (int)Math.Min(64, stream.Length);
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = stream.Read(buffer, offset, length - offset);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        stream.Position = 0;
        return buffer;
    }

    private static string ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        using var sha256 = SHA256.Create();
        var hash = sha256.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static FileKind Classify(Stream stream, byte[] first, string relativePath)
    {
        if (first.Length >= 2 && first[0] == (byte)'M' && first[1] == (byte)'Z')
        {
            try
            {
                stream.Position = 0;
                using var pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
                if (pe.HasMetadata)
                {
                    return FileKind.ManagedAssembly;
                }

                var characteristics = pe.PEHeaders.CoffHeader.Characteristics;
                return (characteristics & Characteristics.Dll) != 0
                    ? FileKind.NativeLibrary
                    : FileKind.NativeExecutable;
            }
            catch
            {
                return FileKind.Unknown;
            }
        }

        if (first.Length >= 4)
        {
            var magic = BitConverter.ToUInt32(first, 0);
            if (magic is 0xFEEDFACE or 0xFEEDFACF or 0xCEFAEDFE or 0xCFFAEDFE or 0xCAFEBABE or 0xBEBAFECA)
            {
                return FileKind.MachO;
            }

            if (first[0] == 0x7F && first[1] == (byte)'E' && first[2] == (byte)'L' && first[3] == (byte)'F')
            {
                return FileKind.Elf;
            }
        }

        var extension = Path.GetExtension(relativePath).ToLowerInvariant();
        if (extension is ".mjs" or ".js")
        {
            return FileKind.UiScript;
        }

        if (extension is ".cok" or ".cid")
        {
            return FileKind.Asset;
        }

        return FileKind.Data;
    }

    private static ModFile Fallback(string fullPath, string relativePath)
    {
        long size = 0;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch
        {
            // ignore; size stays 0
        }

        return new ModFile(relativePath, size, string.Empty, FileKind.Unknown);
    }
}

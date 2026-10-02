namespace XnbCompress.Test;

// Read explicit sample folders from the test process environment, without config files.
internal sealed record SampleSettings(string CompressedRoot, string DecompressedRoot)
{
    private const string CompressedRootVariable = "XNB_COMPRESSED_ROOT";
    private const string DecompressedRootVariable = "XNB_DECOMPRESSED_ROOT";

    internal static SampleSettings Current
    {
        get
        {
            return Load(Environment.GetEnvironmentVariable(CompressedRootVariable),
                Environment.GetEnvironmentVariable(DecompressedRootVariable));
        }
    }

    internal static SampleSettings Load(string? compressed, string? decompressed)
    {
        if (string.IsNullOrWhiteSpace(compressed) || string.IsNullOrWhiteSpace(decompressed))
        {
            throw new InvalidDataException($"Set {CompressedRootVariable} and {DecompressedRootVariable} in the test process environment.");
        }

        var compressedRoot = Path.GetFullPath(compressed);
        var decompressedRoot = Path.GetFullPath(decompressed);
        foreach (var root in new[] { compressedRoot, decompressedRoot })
        {
            if (!Directory.Exists(root))
            {
                throw new DirectoryNotFoundException($"Configured sample directory not found: {root}.");
            }
        }

        return new SampleSettings(compressedRoot, decompressedRoot);
    }
}

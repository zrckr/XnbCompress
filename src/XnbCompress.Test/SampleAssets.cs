namespace XnbCompress.Test;

// Test only saved compressed/decompressed pairs with matching relative paths.
internal static class SampleAssets
{
    public static IEnumerable<object[]> GetCases(SampleSettings? settings = null)
    {
        var (compressedRoot, decompressedRoot) = settings ?? SampleSettings.Current;

        var cases = new List<object[]>();
        foreach (var file in Directory.EnumerateFiles(compressedRoot, "*.xnb", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            // An unpaired asset cannot provide an independent saved-file comparison.
            var relative = Path.GetRelativePath(compressedRoot, file);
            if (!File.Exists(Path.Combine(decompressedRoot, relative)))
            {
                continue;
            }

            var compressed = File.ReadAllBytes(file);
            XnbLogic.Validate(compressed);
            if (!XnbLogic.IsCompressed(compressed))
            {
                continue;
            }

            cases.Add([relative]);
        }

        if (cases.Count == 0)
        {
            throw new InvalidDataException($"No compressed XNB pairs found under {compressedRoot}.");
        }

        return cases;
    }

    public static (byte[] Compressed, byte[] Decompressed) Load(string asset, SampleSettings? settings = null)
    {
        settings ??= SampleSettings.Current;
        return (File.ReadAllBytes(Path.Combine(settings.CompressedRoot, asset)),
            File.ReadAllBytes(Path.Combine(settings.DecompressedRoot, asset)));
    }
}

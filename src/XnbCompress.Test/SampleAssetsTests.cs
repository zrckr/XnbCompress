using System.Buffers.Binary;

namespace XnbCompress.Test;

// Verify pair-only discovery in temporary folders without modifying real samples.
public class SampleAssetsTests : IDisposable
{
    private const string Asset = "sample.xnb";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "xnb-pair-" + Guid.NewGuid().ToString("N"));
    private readonly SampleSettings _settings;
    private readonly byte[] _source;

    public SampleAssetsTests()
    {
        // Make a valid XNB container with a small zero payload and only its compressed file.
        var compressedRoot = Directory.CreateDirectory(Path.Combine(_root, "compressed")).FullName;
        var decompressedRoot = Directory.CreateDirectory(Path.Combine(_root, "decompressed")).FullName;
        _settings = new SampleSettings(compressedRoot, decompressedRoot);
        _source = Convert.FromHexString("584E427705000A000000");
        Array.Resize(ref _source, _source.Length + 513);
        BinaryPrimitives.WriteInt32LittleEndian(_source.AsSpan(6), _source.Length);
        File.WriteAllBytes(Path.Combine(compressedRoot, Asset), XnbLogic.Compress(_source));
    }

    [Fact]
    public void DiscoverOnlyPairedAssets()
    {
        // Keep the complete pair while ignoring a compressed asset with no counterpart.
        File.WriteAllBytes(Path.Combine(_settings.DecompressedRoot, Asset), _source);
        File.Copy(Path.Combine(_settings.CompressedRoot, Asset),
            Path.Combine(_settings.CompressedRoot, "unpaired.xnb"));
        var testCase = Assert.Single(SampleAssets.GetCases(_settings));
        Assert.Equal(Asset, Assert.Single(testCase));
    }

    [Fact]
    public void RejectCorpusWithoutPairs()
    {
        // An empty intersection must fail rather than report a successful zero-test run.
        Assert.Throws<InvalidDataException>(() => SampleAssets.GetCases(_settings));
    }

    [Fact]
    public void RejectLoadingMissingCounterpart()
    {
        // If a paired file disappears after discovery, fail instead of deriving replacement bytes.
        Assert.Throws<FileNotFoundException>(() => SampleAssets.Load(Asset, _settings));
        Assert.False(File.Exists(Path.Combine(_settings.DecompressedRoot, Asset)));
    }

    [Fact]
    public void PreferSuppliedSource()
    {
        // A present counterpart stays authoritative rather than being replaced by decoding.
        var supplied = _source.ToArray();
        supplied[^1] = 1;
        File.WriteAllBytes(Path.Combine(_settings.DecompressedRoot, Asset), supplied);
        Assert.Equal(supplied, SampleAssets.Load(Asset, _settings).Decompressed);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

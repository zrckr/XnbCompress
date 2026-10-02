using Xunit.Abstractions;

namespace XnbCompress.Test;

// Exercise paired XNB assets through every conversion chain and the native oracle.
public class XnbCompressorTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Assets => SampleAssets.GetCases();

    [Theory]
    [MemberData(nameof(Assets))]
    public void DecompressCompressed(string asset)
    {
        // Decode the saved stream and compare the complete decompressed XNB.
        var pair = SampleAssets.Load(asset);
        Assert.Equal(pair.Decompressed, XnbLogic.Decompress(pair.Compressed));
    }

    [Theory]
    [MemberData(nameof(Assets))]
    public void CompressDecompressed(string asset)
    {
        // Encode supplied bytes and compare the complete saved compressed XNB.
        var pair = SampleAssets.Load(asset);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var compressed = XnbLogic.Compress(pair.Decompressed);
        output.WriteLine($"{asset}: encode={timer.Elapsed.TotalSeconds:F3}s");
        Assert.Equal(pair.Compressed, compressed);
    }

    [Theory]
    [MemberData(nameof(Assets))]
    public void DecompressCompressDecompress(string asset)
    {
        // Check each intermediate result, not only the final round trip.
        var pair = SampleAssets.Load(asset);
        var decoded = XnbLogic.Decompress(pair.Compressed);
        Assert.Equal(pair.Decompressed, decoded);
        var encoded = XnbLogic.Compress(decoded);
        Assert.Equal(pair.Compressed, encoded);
        Assert.Equal(pair.Decompressed, XnbLogic.Decompress(encoded));
    }

    [Theory]
    [MemberData(nameof(Assets))]
    public void CompressDecompressCompress(string asset)
    {
        // Re-encoding decoded bytes must reproduce the saved stream exactly.
        var pair = SampleAssets.Load(asset);
        var encoded = XnbLogic.Compress(pair.Decompressed);
        Assert.Equal(pair.Compressed, encoded);
        var decoded = XnbLogic.Decompress(encoded);
        Assert.Equal(pair.Decompressed, decoded);
        Assert.Equal(pair.Compressed, XnbLogic.Compress(decoded));
    }

    [Theory]
    [MemberData(nameof(Assets))]
    public void ManagedCompressionMatchesNative(string asset)
    {
        // Compare fresh native bytes, saved bytes, and native decoding of managed output.
        var pair = SampleAssets.Load(asset);
        var source = XnbLogic.GetDecodedPayload(pair.Decompressed);
        var managed = XnbLogic.GetEncodedPayload(XnbLogic.Compress(pair.Decompressed));
        using var native = new XCompress();
        Assert.Equal(XnbLogic.GetEncodedPayload(pair.Compressed), managed);
        Assert.Equal(native.Compress(source), managed);
        Assert.Equal(source, native.Decompress(managed, source.Length));
    }

    [Theory]
    [MemberData(nameof(Assets))]
    public void ManagedDecompressionMatchesNative(string asset)
    {
        // Both decoders must match supplied bytes for saved and fresh native streams.
        var pair = SampleAssets.Load(asset);
        var source = XnbLogic.GetDecodedPayload(pair.Decompressed);
        var saved = XnbLogic.GetEncodedPayload(pair.Compressed);
        using var native = new XCompress();
        Assert.Equal(source, native.Decompress(saved, source.Length));
        Assert.Equal(source, XnbLogic.DecodePayload(saved, source.Length));
        var encoded = native.Compress(source);
        Assert.Equal(source, XnbLogic.DecodePayload(encoded, source.Length));
        Assert.Equal(source, native.Decompress(encoded, source.Length));
    }
}

using System.Diagnostics;
using Xunit.Abstractions;

namespace XnbCompress.Test;

// Keep timing runs separate from other test collections to avoid CPU contention.
[CollectionDefinition("Compression timing", DisableParallelization = true)]
public class CompressionTimingCollection;

[Collection("Compression timing")]
[Trait("Category", "Performance")]
public class CompressionTimingTests(ITestOutputHelper output)
{
    [Fact]
    public void CompressAssetsNative()
    {
        using var compressor = new XCompress();
        var timer = new Stopwatch();
        var assetCount = 0;
        long inputBytes = 0;
        long outputBytes = 0;

        foreach (var assetCase in SampleAssets.GetCases())
        {
            var asset = (string)assetCase[0];
            var source = XnbLogic.GetDecodedPayload(SampleAssets.Load(asset).Decompressed);
            compressor.Compress(source); // Warm up before measuring.

            timer.Start();
            var encoded = compressor.Compress(source);
            timer.Stop();

            assetCount++;
            inputBytes += source.Length;
            outputBytes += encoded.Length;
        }

        output.WriteLine($"Native: total={timer.Elapsed.TotalSeconds:F3}s; assets={assetCount}; input={inputBytes} bytes; output={outputBytes} bytes");
    }

    [Fact]
    public void CompressAssetsManaged()
    {
        var compressor = new XMemCompressor(XnbLogic.WindowExponent, XnbLogic.PartitionSize);
        var timer = new Stopwatch();
        var assetCount = 0;
        long inputBytes = 0;
        long outputBytes = 0;

        foreach (var assetCase in SampleAssets.GetCases())
        {
            var asset = (string)assetCase[0];
            var source = XnbLogic.GetDecodedPayload(SampleAssets.Load(asset).Decompressed);
            using var input = new MemoryStream(source, writable: false);
            using var encoded = new MemoryStream(checked(source.Length * 2 + (1 << XnbLogic.WindowExponent)));
            compressor.Compress(input, source.Length, encoded, int.MaxValue);
            input.Position = 0;
            encoded.SetLength(0);
            encoded.Position = 0;

            timer.Start();
            var written = compressor.Compress(input, source.Length, encoded, int.MaxValue);
            timer.Stop();

            assetCount++;
            inputBytes += source.Length;
            outputBytes += written;
        }

        output.WriteLine($"Managed: total={timer.Elapsed.TotalSeconds:F3}s; assets={assetCount}; input={inputBytes} bytes; output={outputBytes} bytes");
    }
}

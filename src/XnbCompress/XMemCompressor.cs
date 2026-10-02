using System;
using System.IO;
using XnbCompress.Encoder;

namespace XnbCompress;

public class XMemCompressor
{
    internal int MainSymbolCount => Constants.LiteralSymbolCount + _positionSlotCount * Constants.LengthCodesPerSlot;
    internal readonly int WindowSize;
    internal readonly int PartitionSize;
    private readonly int _positionSlotCount;

    public XMemCompressor(int windowExponent, int partitionSize)
    {
        if (windowExponent is < Constants.MinimumWindowExponent or > Constants.MaximumWindowExponent)
        {
            throw new ArgumentOutOfRangeException(nameof(windowExponent), "Window exponent must be between 15 and 21.");
        }

        WindowSize = 1 << windowExponent;
        PartitionSize = partitionSize;

        // Large windows add fixed-width slots once distance bits reach 17
        _positionSlotCount = windowExponent switch
        {
            20 => 42,
            21 => 50,
            _ => windowExponent * 2
        };
    }

    public int Compress(Stream inData, int inLen, Stream outData, int outLen)
    {
        if (inData == null)
        {
            throw new ArgumentNullException(nameof(inData));
        }

        if (outData == null)
        {
            throw new ArgumentNullException(nameof(outData));
        }

        if (!inData.CanRead)
        {
            throw new ArgumentException("The input stream must be readable.", nameof(inData));
        }

        if (!outData.CanWrite)
        {
            throw new ArgumentException("The output stream must be writable.", nameof(outData));
        }

        if (inLen < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inLen));
        }

        if (outLen < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(outLen));
        }

        // Read exactly the requested input, allowing streams that return
        // partial reads and leaving any following bytes for the caller.
        var source = new byte[inLen];
        var read = 0;
        while (read < source.Length)
        {
            var count = inData.Read(source.AsSpan(read));
            if (count == 0)
            {
                throw new EndOfStreamException("The input stream is shorter than inLen.");
            }

            read += count;
        }

        // Buffer complete output before checking capacity, so an undersized
        // destination does not receive a partial compressed stream.
        using var encoded = new MemoryStream();
        var session = new CompressionSession(this, source);
        using var blocks = new BlockWriter(this, source, encoded);
        session.Encode(blocks.WriteBlock);
        blocks.Complete();

        if (encoded.Length > outLen)
        {
            throw new ArgumentException("The compressed stream exceeds outLen.", nameof(outLen));
        }

        var written = checked((int)encoded.Length);
        outData.Write(encoded.GetBuffer(), 0, written);
        return written;
    }

    #region Constants

    // Block selection uses estimated tree costs and aligned frequencies
    public const int BlockTreeEstimateBits = 0x4B0;
    public const int AlignedMinimumMatchCount = 100;
    public const int AlignedHistogramDivisor = 5;

    // Match search retains history and padded lookahead
    public const int MatchKeyCount = 1 << 16;
    public const int MatchLookaheadSize = 0x1101;
    public const int WindowSearchMargin = 4;
    public const int LongMatchThreshold = 50;

    // Bound short-match searches to limit competing paths
    public const int ShortMatchSearchLimit = 0xEFD;
    public const int TwoByteFrontierDistanceLimit = 0x800;

    // Refresh costs and flush tokens at the parser thresholds
    public const int InitialCostRefreshTokenCount = 10000;
    public const int CostRefreshTokenInterval = 0x1000;
    public const int TokenBufferLimit = 0xFFF8;
    public const int MatchBufferLimit = 0x7FF8;
    public const int FirstReparseTokenLimit = 0xFE00;
    public const int FirstReparseMatchLimit = 0x7E00;

    #endregion
}

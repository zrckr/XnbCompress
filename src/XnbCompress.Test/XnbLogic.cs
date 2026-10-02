using System.Buffers.Binary;

namespace XnbCompress.Test;

// Adapt XNB containers to the public stream API without involving either native codec.
internal static class XnbLogic
{
    internal const int WindowExponent = 16;
    internal const int PartitionSize = 0x80000;
    private const int HeaderSize = 10;
    private const int CompressedHeaderSize = HeaderSize + sizeof(int);
    private const int FlagsOffset = 5;
    private const int FileLengthOffset = 6;
    private const byte LzxFlag = 0x80;
    private const byte Lz4Flag = 0x40;

    internal static void Validate(byte[] xnb)
    {
        if (xnb.Length < HeaderSize || !xnb.AsSpan(0, 3).SequenceEqual("XNB"u8) ||
            BinaryPrimitives.ReadInt32LittleEndian(xnb.AsSpan(FileLengthOffset)) != xnb.Length)
        {
            throw new InvalidDataException("Invalid XNB header or declared length.");
        }

        if ((xnb[FlagsOffset] & Lz4Flag) != 0)
        {
            throw new NotSupportedException("These tests require LZX, not LZ4.");
        }
    }

    internal static bool IsCompressed(byte[] xnb)
    {
        return (xnb[FlagsOffset] & LzxFlag) != 0;
    }

    internal static byte[] GetDecodedPayload(byte[] xnb)
    {
        Validate(xnb);
        if (IsCompressed(xnb))
        {
            throw new InvalidDataException("Expected decompressed XNB.");
        }

        return xnb.AsSpan(HeaderSize).ToArray();
    }

    internal static byte[] GetEncodedPayload(byte[] xnb)
    {
        Validate(xnb);
        if (!IsCompressed(xnb) || xnb.Length < CompressedHeaderSize)
        {
            throw new InvalidDataException("Expected LZX-compressed XNB.");
        }

        return xnb.AsSpan(CompressedHeaderSize).ToArray();
    }

    internal static byte[] Compress(byte[] xnb)
    {
        var source = GetDecodedPayload(xnb);
        using var input = new MemoryStream(source, writable: false);
        using var output = new MemoryStream();
        var encoder = new XMemCompressor(WindowExponent, PartitionSize);
        var written = encoder.Compress(input, source.Length, output, int.MaxValue);
        Assert.Equal(output.Length, written);

        var result = new byte[checked(CompressedHeaderSize + written)];
        xnb.AsSpan(0, HeaderSize).CopyTo(result);
        result[FlagsOffset] |= LzxFlag;
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(FileLengthOffset), result.Length);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(HeaderSize), source.Length);
        output.GetBuffer().AsSpan(0, written).CopyTo(result.AsSpan(CompressedHeaderSize));
        return result;
    }

    internal static byte[] Decompress(byte[] xnb)
    {
        var record = GetEncodedPayload(xnb);
        var length = BinaryPrimitives.ReadInt32LittleEndian(xnb.AsSpan(HeaderSize));
        var payload = DecodePayload(record, length);
        var result = new byte[checked(HeaderSize + payload.Length)];
        xnb.AsSpan(0, HeaderSize).CopyTo(result);
        result[FlagsOffset] &= unchecked((byte)~LzxFlag);
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(FileLengthOffset), result.Length);
        payload.CopyTo(result, HeaderSize);
        return result;
    }

    internal static byte[] DecodePayload(byte[] record, int length)
    {
        using var input = new MemoryStream(record, writable: false);
        using var output = new MemoryStream();
        var decoder = new XMemDecompressor(WindowExponent);
        Assert.Equal(0, decoder.Decompress(input, record.Length, output, length));
        Assert.Equal(length, output.Length);
        return output.ToArray();
    }
}

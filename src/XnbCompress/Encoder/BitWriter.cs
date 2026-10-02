using System;
using System.IO;

namespace XnbCompress.Encoder;

internal sealed class BitWriter
{
    private readonly Stream _byteStream;
    private uint _buffer;
    private int _bitsLeft;

    public BitWriter(Stream stream)
    {
        if (stream == null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (!stream.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(stream));
        }

        _byteStream = stream;
        _buffer = 0;
        // Bits enter at the high end of this 32-bit buffer.
        // Emit its upper 16 bits when a complete word is available,
        // then shift the remaining bits up. An empty buffer has 32 free bits.
        _bitsLeft = 32;
    }

    public void WriteBits(int count, uint value)
    {
        if (count is < 0 or > 24)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        if (count == 0)
        {
            return;
        }

        if ((value >> count) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        // At most 16 bits are added at once, so the shift below always fits.
        // Split larger fields high bits first to preserve the bit order.
        if (count > 16)
        {
            WriteBits(count - 16, value >> 16);
            WriteBits(16, value & 0xFFFF);
            return;
        }

        _buffer |= value << (_bitsLeft - count);
        _bitsLeft -= count;

        if (_bitsLeft <= 16)
        {
            // LZX writes bits most significant first within each word,
            // but stores the word's low byte before its high byte.
            _byteStream.WriteByte((byte)(_buffer >> 16));
            _byteStream.WriteByte((byte)(_buffer >> 24));
            _buffer <<= 16;
            _bitsLeft += 16;
        }
    }

    public void AlignToWord(bool forcePadding = false)
    {
        var pendingBits = 32 - _bitsLeft;
        if (pendingBits != 0)
        {
            WriteBits(16 - pendingBits, 0);
        }
        else if (forcePadding)
        {
            WriteBits(16, 0);
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        if (_bitsLeft != 32)
        {
            throw new InvalidOperationException("Raw bytes require word alignment.");
        }

        _byteStream.Write(data);
    }
}

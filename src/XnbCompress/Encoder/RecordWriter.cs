using System;
using System.Buffers.Binary;
using System.IO;

namespace XnbCompress.Encoder;

internal sealed class RecordWriter : IDisposable
{
    public BitWriter Bits { get; }
    public int DecodedLength { get; private set; }

    private readonly Stream _output;
    private readonly MemoryStream _buffer = new();
    private readonly int _inputLength;
    private int _totalDecodedLength;
    private bool _completed;

    public RecordWriter(Stream output, int inputLength)
    {
        if (output == null)
        {
            throw new ArgumentNullException(nameof(output));
        }

        if (!output.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(output));
        }

        if (inputLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(inputLength));
        }

        _output = output;
        _inputLength = inputLength;
        Bits = new BitWriter(_buffer);

        // The Intel E8 translation flag belongs to the stream, not each block.
        // Write it once; empty input produces no records.
        if (inputLength != 0)
        {
            Bits.WriteBits(1, 0);
        }
    }

    public void Advance(int decodedLength)
    {
        if (_completed)
        {
            throw new InvalidOperationException("Record output is already complete.");
        }

        if (decodedLength < 1 || decodedLength > Constants.RecordDecodedCapacity - DecodedLength ||
            decodedLength > _inputLength - _totalDecodedLength)
        {
            throw new ArgumentOutOfRangeException(nameof(decodedLength));
        }

        DecodedLength += decodedLength;
        _totalDecodedLength += decodedLength;

        // A full record can also be the last record. Total progress
        // decides which header to write without adding an empty tail.
        if (DecodedLength == Constants.RecordDecodedCapacity)
        {
            Flush();
        }
    }

    public void WriteBytes(ReadOnlySpan<byte> source)
    {
        if (_completed || source.Length > _inputLength - _totalDecodedLength)
        {
            throw new InvalidOperationException("Raw bytes exceed the remaining input.");
        }

        // Raw blocks can span records. Copy only the bytes that fit,
        // flush at the boundary, then continue with the same block.
        while (!source.IsEmpty)
        {
            var count = Math.Min(source.Length, Constants.RecordDecodedCapacity - DecodedLength);
            Bits.WriteBytes(source[..count]);
            Advance(count);
            source = source[count..];
        }
    }

    public void Complete()
    {
        if (_totalDecodedLength != _inputLength)
        {
            throw new InvalidOperationException("Record output does not cover the input.");
        }

        if (DecodedLength != 0)
        {
            Flush();
        }

        _completed = true;
    }

    private void Flush()
    {
        Bits.AlignToWord();
        var compressedLength = checked((int)_buffer.Length);
        if (compressedLength > ushort.MaxValue)
        {
            throw new InvalidOperationException("Compressed record exceeds its length field.");
        }

        var isFinal = _totalDecodedLength == _inputLength;
        if (isFinal)
        {
            _output.WriteByte(Constants.FinalRecordMarker);
            WriteBigEndian16(_output, DecodedLength);
        }

        WriteBigEndian16(_output, compressedLength);
        _output.Write(_buffer.GetBuffer(), 0, compressedLength);

        if (isFinal)
        {
            Span<byte> trailer = stackalloc byte[Constants.FinalRecordTrailerLength];
            trailer.Clear();
            _output.Write(trailer);
            _completed = true;
        }

        // Alignment emptied the bit buffer. Clear only this record's bytes and count.
        // BlockWriter retains trees and repeat offsets.
        _buffer.SetLength(0);
        _buffer.Position = 0;
        DecodedLength = 0;
    }

    private static void WriteBigEndian16(Stream output, int value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value));
        output.Write(bytes);
    }

    public void Dispose()
    {
        _buffer.Dispose();
    }
}

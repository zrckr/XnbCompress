/*
 * Based on libmsport's lzxd.c
 * Copyright 2003-2004 Stuart Caie
 * Copyright 2011 Ali Scissons
 *
 * Released under a dual MSPL/LGPL license.
 * See licenses/lzxdecoder.LICENSE for details.
 */

using System;
using System.Buffers.Binary;
using System.IO;

namespace XnbCompress.Decoder;

internal sealed class RecordReader : IDisposable
{
    public Stream Data => _buffer;
    public int EncodedLength { get; private set; }
    public int DecodedLength { get; private set; }

    private readonly Stream _input;
    private readonly MemoryStream _buffer = new(ushort.MaxValue);
    private int _remaining;

    public RecordReader(Stream input, int encodedLength)
    {
        _input = input;
        _remaining = encodedLength;
    }

    public void Read(int remainingDecoded)
    {
        Span<byte> header = stackalloc byte[5];
        ReadExactly(header[..2]);

        // Extended headers supply the decoded size; ordinary records decode 32 KiB
        var extended = header[0] == Constants.FinalRecordMarker;
        if (extended)
        {
            ReadExactly(header[2..]);
        }

        DecodedLength = extended
            ? BinaryPrimitives.ReadUInt16BigEndian(header.Slice(1, 2))
            : Constants.RecordDecodedCapacity;
        EncodedLength = BinaryPrimitives.ReadUInt16BigEndian(header.Slice(extended ? 3 : 0, 2));

        if (DecodedLength < 1 || DecodedLength > Constants.RecordDecodedCapacity ||
            DecodedLength > remainingDecoded || EncodedLength < 1 || EncodedLength > _remaining)
        {
            throw new InvalidDataException("Invalid LZX record size.");
        }

        // A bounded seekable buffer keeps bit lookahead out of the next record
        _buffer.SetLength(EncodedLength);
        ReadExactly(_buffer.GetBuffer().AsSpan(0, EncodedLength));
        _buffer.Position = 0;
    }

    public void Complete()
    {
        if (_remaining == 0)
        {
            return;
        }

        // Accept the five-byte zero trailer emitted after the final record
        if (_remaining != Constants.FinalRecordTrailerLength)
        {
            throw new InvalidDataException("Unexpected LZX record trailer.");
        }

        Span<byte> trailer = stackalloc byte[Constants.FinalRecordTrailerLength];
        ReadExactly(trailer);
        foreach (var value in trailer)
        {
            if (value != 0)
            {
                throw new InvalidDataException("Invalid LZX record trailer.");
            }
        }
    }

    private void ReadExactly(Span<byte> destination)
    {
        if (destination.Length > _remaining)
        {
            throw new InvalidDataException("Truncated LZX record.");
        }

        // Fill fields and payloads even when the input returns partial reads
        while (!destination.IsEmpty)
        {
            var count = _input.Read(destination);
            if (count == 0)
            {
                throw new EndOfStreamException("The input stream is shorter than inLen.");
            }

            _remaining -= count;
            destination = destination[count..];
        }
    }

    public void Dispose()
    {
        _buffer.Dispose();
    }
}

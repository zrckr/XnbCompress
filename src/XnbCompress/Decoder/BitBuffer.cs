/*
 * Based on libmsport's lzxd.c
 * Copyright 2003-2004 Stuart Caie
 * Copyright 2011 Ali Scissons
 *
 * Released under a dual MSPL/LGPL license.
 * See licenses/lzxdecoder.LICENSE for details.
 */

using System.IO;

namespace XnbCompress.Decoder;

internal class BitBuffer
{
    public uint Buffer { get; private set; }
    public byte Remaining { get; private set; }

    private readonly Stream _stream;

    public BitBuffer(Stream stream)
    {
        _stream = stream;
        Reset();
    }

    public void Reset()
    {
        Buffer = 0;
        Remaining = 0;
    }

    public void Ensure(byte bits)
    {
        while (Remaining < bits)
        {
            var lo = (byte)_stream.ReadByte();
            var hi = (byte)_stream.ReadByte();
            Buffer |= (uint)(((hi << 8) | lo) << (sizeof(uint) * 8 - 16 - Remaining));
            Remaining += 16;
        }
    }

    public uint Peek(byte bits)
    {
        return (Buffer >> ((sizeof(uint) * 8) - bits));
    }

    public void Remove(byte bits)
    {
        Buffer <<= bits;
        Remaining -= bits;
    }

    public uint Read(byte bits)
    {
        var ret = 0U;
        if (bits > 0)
        {
            Ensure(bits);
            ret = Peek(bits);
            Remove(bits);
        }

        return ret;
    }
}

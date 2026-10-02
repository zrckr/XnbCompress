/*
 * Based on libmsport's lzxd.c
 * Copyright 2003-2004 Stuart Caie
 * Copyright 2011 Ali Scissons
 *
 * Released under a dual MSPL/LGPL license.
 * See licenses/lzxdecoder.LICENSE for details.
 */

using System;

namespace XnbCompress.Decoder;

internal struct DecoderState
{
    public uint R0, R1, R2;
    public readonly ushort MainElements;
    public int HeaderRead;
    public BlockType BlockType;
    public uint BlockLength;
    public uint BlockRemaining;

    // Trees retain lengths across records because block headers encode deltas
    public readonly HuffmanTree MainTree;
    public readonly HuffmanTree LengthTree;
    public readonly HuffmanTree AlignedTree;

    public readonly byte[] Window;
    public readonly uint WindowSize;
    public uint WindowPosition;

    public DecoderState(int windowExponent)
    {
        this = default;
        WindowSize = (uint)(1 << windowExponent);
        Window = new byte[WindowSize];
        Array.Fill(Window, (byte)0xDC);
        R0 = R1 = R2 = 1;

        var positionSlots = windowExponent switch
        {
            20 => 42,
            21 => 50,
            _ => windowExponent * 2
        };
        MainElements = (ushort)(Constants.LiteralSymbolCount + positionSlots * Constants.LengthCodesPerSlot);
        MainTree = new HuffmanTree(XMemDecompressor.MainTreeSymbolCount, XMemDecompressor.MainTreeTableBits);
        LengthTree = new HuffmanTree(XMemDecompressor.LengthTreeSymbolCount, XMemDecompressor.LengthTableBits);
        AlignedTree = new HuffmanTree(Constants.AlignedSymbolCount, XMemDecompressor.AlignedTableBits);
    }
}

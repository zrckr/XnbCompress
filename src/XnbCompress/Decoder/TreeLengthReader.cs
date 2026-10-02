/*
 * Based on libmsport's lzxd.c
 * Copyright 2003-2004 Stuart Caie
 * Copyright 2011 Ali Scissons
 *
 * Released under a dual MSPL/LGPL license.
 * See licenses/lzxdecoder.LICENSE for details.
 */

namespace XnbCompress.Decoder;

internal sealed class TreeLengthReader
{
    private readonly HuffmanTree _pretree = new(Constants.PretreeSymbolCount, XMemDecompressor.PretreeTableBits);

    // Decode length deltas in place so later blocks can reuse the previous tree
    public void Read(HuffmanTree tree, uint first, uint last, BitBuffer bitbuf)
    {
        var lens = tree.Lengths;
        uint x, y;
        int z;
        for (x = 0; x < Constants.PretreeSymbolCount; x++)
        {
            y = bitbuf.Read(Constants.PretreeLengthBitCount);
            _pretree.Lengths[x] = (byte)y;
        }

        _pretree.Build();
        for (x = first; x < last;)
        {
            z = (int)_pretree.ReadSymbol(bitbuf);
            if (z == Constants.ShortZeroRunSymbol)
            {
                y = bitbuf.Read(4);
                y += 4;
                while (y-- != 0)
                {
                    lens[x++] = 0;
                }
            }
            else if (z == Constants.LongZeroRunSymbol)
            {
                y = bitbuf.Read(5);
                y += 20;
                while (y-- != 0)
                {
                    lens[x++] = 0;
                }
            }
            else if (z == Constants.RepeatedLengthSymbol)
            {
                y = bitbuf.Read(1);
                y += 4;
                z = (int)_pretree.ReadSymbol(bitbuf);
                z = lens[x] - z;
                if (z < 0)
                {
                    z += Constants.LengthDeltaModulo;
                }

                while (y-- != 0)
                {
                    lens[x++] = (byte)z;
                }
            }
            else
            {
                z = lens[x] - z;
                if (z < 0)
                {
                    z += Constants.LengthDeltaModulo;
                }

                lens[x++] = (byte)z;
            }
        }
    }
}

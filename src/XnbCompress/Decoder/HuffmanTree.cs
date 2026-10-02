/*
 * Based on libmsport's lzxd.c
 * Copyright 2003-2004 Stuart Caie
 * Copyright 2011 Ali Scissons
 *
 * Released under a dual MSPL/LGPL license.
 * See licenses/lzxdecoder.LICENSE for details.
 */

namespace XnbCompress.Decoder;

internal sealed class HuffmanTree
{
    public byte[] Lengths { get; }

    private readonly uint _symbolCount;
    private readonly uint _tableBits;
    private readonly ushort[] _table;

    public HuffmanTree(int symbolCount, int tableBits)
    {
        _symbolCount = (uint)symbolCount;
        _tableBits = (uint)tableBits;
        Lengths = new byte[symbolCount + XMemDecompressor.LengthTableSafety];
        _table = new ushort[(1 << tableBits) + (symbolCount << 1)];
    }

    // Rebuild lookups from current lengths; return 1 if code space is invalid
    public int Build()
    {
        ushort sym;
        uint leaf;
        byte bit_num = 1;
        uint fill;
        uint pos = 0; // The current position in the decode table.
        uint table_mask = (uint)(1 << (int)_tableBits);
        uint bit_mask = table_mask >> 1; // Don't do 0 Lengths codes.
        uint next_symbol = bit_mask; // Base of allocation for long codes.
        // Fill entries for codes short enough for a direct mapping.
        while (bit_num <= _tableBits)
        {
            for (sym = 0; sym < _symbolCount; sym++)
            {
                if (Lengths[sym] == bit_num)
                {
                    leaf = pos;
                    if ((pos += bit_mask) > table_mask)
                    {
                        return 1; // Table overrun
                    }

                    /* Fill all possible lookups of this symbol with the
                     * symbol itself.
                     */
                    fill = bit_mask;
                    while (fill-- > 0)
                    {
                        _table[leaf++] = sym;
                    }
                }
            }

            bit_mask >>= 1;
            bit_num++;
        }

        // If there are any codes longer than _tableBits
        if (pos != table_mask)
        {
            // Clear the remainder of the table.
            for (sym = (ushort)pos; sym < table_mask; sym++)
            {
                _table[sym] = 0;
            }

            // Give ourselves room for codes to grow by up to 16 more bits.
            pos <<= 16;
            table_mask <<= 16;
            bit_mask = 1 << 15;
            while (bit_num <= Constants.MaximumCodeLength)
            {
                for (sym = 0; sym < _symbolCount; sym++)
                {
                    if (Lengths[sym] == bit_num)
                    {
                        leaf = pos >> 16;
                        for (fill = 0; fill < bit_num - _tableBits; fill++)
                        {
                            // if this path hasn't been taken yet, 'allocate' two entries.
                            if (_table[leaf] == 0)
                            {
                                _table[(next_symbol << 1)] = 0;
                                _table[(next_symbol << 1) + 1] = 0;
                                _table[leaf] = (ushort)(next_symbol++);
                            }

                            // Follow the path and select either left or right for next bit.
                            leaf = (uint)(_table[leaf] << 1);
                            if (((pos >> (int)(15 - fill)) & 1) == 1)
                            {
                                leaf++;
                            }
                        }

                        _table[leaf] = sym;
                        if ((pos += bit_mask) > table_mask)
                        {
                            return 1;
                        }
                    }
                }

                bit_mask >>= 1;
                bit_num++;
            }
        }

        // full table?
        if (pos == table_mask)
        {
            return 0;
        }

        // Either erroneous table, or all elements are 0 - let's find out.
        for (sym = 0; sym < _symbolCount; sym++)
        {
            if (Lengths[sym] != 0)
            {
                return 1;
            }
        }

        return 0;
    }

    public uint ReadSymbol(BitBuffer bitbuf)
    {
        uint i, j;
        bitbuf.Ensure(16);
        if ((i = _table[bitbuf.Peek((byte)_tableBits)]) >= _symbolCount)
        {
            j = (uint)(1 << (int)((sizeof(uint) * 8) - _tableBits));
            do
            {
                j >>= 1;
                i <<= 1;
                i |= (bitbuf.Buffer & j) != 0 ? (uint)1 : 0;
                if (j == 0)
                {
                    return 0; // TODO: throw proper exception
                }
            } while ((i = _table[i]) >= _symbolCount);
        }

        j = Lengths[i];
        bitbuf.Remove((byte)j);
        return i;
    }
}

/*
 * Based on libmsport's lzxd.c
 * Copyright 2003-2004 Stuart Caie
 * Copyright 2011 Ali Scissons
 *
 * Released under a dual MSPL/LGPL license.
 * See licenses/lzxdecoder.LICENSE for details.
 */

using System;
using System.IO;
using XnbCompress.Decoder;

namespace XnbCompress;

public class XMemDecompressor
{
    private readonly uint[] _positionBase;
    private readonly byte[] _extraBits;
    private readonly TreeLengthReader _lengthReader = new();
    private DecoderState _state;
    private readonly int _windowExponent;

    public XMemDecompressor(int window)
    {
        if (window is < Constants.MinimumWindowExponent or > Constants.MaximumWindowExponent)
        {
            throw new ArgumentOutOfRangeException(nameof(window), "Window exponent must be between 15 and 21.");
        }

        _windowExponent = window;

        // Precompute slot widths and bases for decoding match distances
        _extraBits = new byte[Constants.MaximumPositionSlotCount + 2];
        for (int i = 0, j = 0; i <= Constants.MaximumPositionSlotCount; i += 2)
        {
            _extraBits[i] = _extraBits[i + 1] = (byte)j;
            if ((i != 0) && (j < Constants.MaximumDistanceExtraBits))
            {
                j++;
            }
        }

        _positionBase = new uint[Constants.MaximumPositionSlotCount + 1];
        for (int i = 0, j = 0; i <= Constants.MaximumPositionSlotCount; i++)
        {
            _positionBase[i] = (uint)j;
            j += 1 << _extraBits[i];
        }
    }

    public int Decompress(Stream inData, int inLen, Stream outData, int outLen)
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

        // Each call starts a new stream; records within it retain decoding history
        _state = new DecoderState(_windowExponent);
        using var records = new RecordReader(inData, inLen);
        var remaining = outLen;
        while (remaining != 0)
        {
            records.Read(remaining);
            var status = DecodeRecord(records.Data, records.EncodedLength, outData, records.DecodedLength);
            if (status != 0)
            {
                return status;
            }

            remaining -= records.DecodedLength;
        }

        records.Complete();
        return 0;
    }

    private int DecodeRecord(Stream inData, int inLen, Stream outData, int outLen)
    {
        var startPosition = inData.Position;
        var endPosition = startPosition + inLen;
        var buffer = new BitBuffer(inData);
        var windowPosition = _state.WindowPosition;
        var windowSize = _state.WindowSize;
        var r0 = _state.R0;
        var r1 = _state.R1;
        var r2 = _state.R2;

        buffer.Reset();
        // Consume the stream header once; translated streams are unsupported
        if (_state.HeaderRead == 0)
        {
            if (buffer.Read(1) != 0)
            {
                throw new NotSupportedException("Intel E8 translation is not supported.");
            }

            _state.HeaderRead = 1;
        }

        // Keep block and window progress across calls so records share history
        var togo = outLen;
        while (togo > 0)
        {
            // last block finished, new block expected.
            if (_state.BlockRemaining == 0)
            {
                // Skip odd raw-block padding before reading the next header
                if (_state.BlockType == BlockType.Uncompressed)
                {
                    if ((_state.BlockLength & 1) == 1)
                    {
                        inData.ReadByte(); /* realign bitstream to word */
                    }

                    buffer.Reset();
                }

                _state.BlockType = (BlockType)buffer.Read(Constants.BlockTypeBitCount);
                var i = buffer.Read(16);
                var j = buffer.Read(8);

                _state.BlockRemaining = _state.BlockLength = (uint)((i << 8) | j);
                switch (_state.BlockType)
                {
                    case BlockType.Aligned:
                        for (i = 0; i < Constants.AlignedSymbolCount; i++)
                        {
                            j = buffer.Read(Constants.AlignedLengthBitCount);
                            _state.AlignedTree.Lengths[i] = (byte)j;
                        }

                        _state.AlignedTree.Build();
                        // Rest of aligned header is same as verbatim
                        goto case BlockType.Verbatim;

                    case BlockType.Verbatim:
                        _lengthReader.Read(_state.MainTree, 0, Constants.LiteralSymbolCount, buffer);
                        _lengthReader.Read(_state.MainTree, Constants.LiteralSymbolCount, _state.MainElements,
                            buffer);
                        _state.MainTree.Build();
                        _lengthReader.Read(_state.LengthTree, 0, Constants.SecondaryLengthCount, buffer);
                        _state.LengthTree.Build();
                        break;

                    case BlockType.Uncompressed:
                        buffer.Ensure(16); // Get up to 16 pad bits into the buffer.
                        if (buffer.Remaining > 16)
                        {
                            inData.Seek(-2, SeekOrigin.Current); /* and align the bitstream! */
                        }

                        var lo = (byte)inData.ReadByte();
                        var ml = (byte)inData.ReadByte();
                        var mh = (byte)inData.ReadByte();
                        var hi = (byte)inData.ReadByte();
                        r0 = (uint)(lo | ml << 8 | mh << 16 | hi << 24);
                        lo = (byte)inData.ReadByte();
                        ml = (byte)inData.ReadByte();
                        mh = (byte)inData.ReadByte();
                        hi = (byte)inData.ReadByte();
                        r1 = (uint)(lo | ml << 8 | mh << 16 | hi << 24);
                        lo = (byte)inData.ReadByte();
                        ml = (byte)inData.ReadByte();
                        mh = (byte)inData.ReadByte();
                        hi = (byte)inData.ReadByte();
                        r2 = (uint)(lo | ml << 8 | mh << 16 | hi << 24);
                        break;

                    default:
                        return -1; // TODO throw proper exception
                }
            }

            // Buffer exhaustion check.
            if (inData.Position > endPosition)
            {
                /* It's possible to have a file where the next run is less than
                 * 16 bits in size. In this case, the READ_HUFFSYM() macro used
                 * in building the tables will exhaust the buffer, so we should
                 * allow for this, but not allow those accidentally read bits to
                 * be used (so we check that there are at least 16 bits
                 * remaining - in this boundary case they aren't really part of
                 * the compressed data.
                 */
                if (inData.Position > endPosition + 2 || buffer.Remaining < 16)
                {
                    return -1; //TODO throw proper exception
                }
            }

            int thisRun;
            while ((thisRun = (int)_state.BlockRemaining) > 0 && togo > 0)
            {
                if (thisRun > togo)
                {
                    thisRun = togo;
                }

                togo -= thisRun;
                _state.BlockRemaining -= (uint)thisRun;
                // Apply 2^x-1 mask.
                windowPosition &= windowSize - 1;
                // Runs can't straddle the window wraparound.
                if ((windowPosition + thisRun) > windowSize)
                {
                    return -1; // TODO: throw proper exception
                }

                int mainElement;
                int matchLength;
                int matchOffset;
                int lengthFooter;
                int runSource;
                int runDestination;

                switch (_state.BlockType)
                {
                    case BlockType.Verbatim:
                        while (thisRun > 0)
                        {
                            mainElement = (int)_state.MainTree.ReadSymbol(buffer);
                            if (mainElement < Constants.LiteralSymbolCount)
                            {
                                // Literal: 0 to NUM_CHARS-1.
                                _state.Window[windowPosition++] = (byte)mainElement;
                                thisRun--;
                            }
                            else
                            {
                                // Match: NUM_CHARS + ((slot<<3) | length_header (3 bits))
                                mainElement -= Constants.LiteralSymbolCount;
                                matchLength = mainElement & Constants.PrimaryLengthCount;
                                if (matchLength == Constants.PrimaryLengthCount)
                                {
                                    lengthFooter = (int)_state.LengthTree.ReadSymbol(buffer);
                                    matchLength += lengthFooter;
                                }

                                matchLength += Constants.MinimumMatchLength;
                                matchOffset = mainElement >> Constants.LengthCodeBitCount;
                                if (matchOffset > 2)
                                {
                                    // Not repeated offset.
                                    if (matchOffset != 3)
                                    {
                                        var extra = _extraBits[matchOffset];
                                        var verbatimBits = (int)buffer.Read(extra);
                                        matchOffset = (int)_positionBase[matchOffset] - 2 + verbatimBits;
                                    }
                                    else
                                    {
                                        matchOffset = 1;
                                    }

                                    // Update repeated offset LRU queue.
                                    r2 = r1;
                                    r1 = r0;
                                    r0 = (uint)matchOffset;
                                }
                                else if (matchOffset == 0)
                                {
                                    matchOffset = (int)r0;
                                }
                                else if (matchOffset == 1)
                                {
                                    matchOffset = (int)r1;
                                    r1 = r0;
                                    r0 = (uint)matchOffset;
                                }
                                else // match_offset == 2
                                {
                                    matchOffset = (int)r2;
                                    r2 = r0;
                                    r0 = (uint)matchOffset;
                                }

                                runDestination = (int)windowPosition;
                                thisRun -= matchLength;
                                // Copy any wrapped around source data
                                if (windowPosition >= matchOffset)
                                {
                                    // No wrap
                                    runSource = runDestination - matchOffset;
                                }
                                else
                                {
                                    runSource = runDestination + ((int)windowSize - matchOffset);
                                    var copyLength = matchOffset - (int)windowPosition;
                                    if (copyLength < matchLength)
                                    {
                                        matchLength -= copyLength;
                                        windowPosition += (uint)copyLength;
                                        while (copyLength-- > 0)
                                        {
                                            _state.Window[runDestination++] = _state.Window[runSource++];
                                        }

                                        runSource = 0;
                                    }
                                }

                                windowPosition += (uint)matchLength;
                                // Copy match data - no worries about destination wraps
                                while (matchLength-- > 0)
                                {
                                    _state.Window[runDestination++] = _state.Window[runSource++];
                                }
                            }
                        }

                        break;

                    case BlockType.Aligned:
                        while (thisRun > 0)
                        {
                            mainElement = (int)_state.MainTree.ReadSymbol(buffer);
                            if (mainElement < Constants.LiteralSymbolCount)
                            {
                                // Literal 0 to NUM_CHARS-1
                                _state.Window[windowPosition++] = (byte)mainElement;
                                thisRun -= 1;
                            }
                            else
                            {
                                // Match: NUM_CHARS + ((slot<<3) | length_header (3 bits))
                                mainElement -= Constants.LiteralSymbolCount;
                                matchLength = mainElement & Constants.PrimaryLengthCount;
                                if (matchLength == Constants.PrimaryLengthCount)
                                {
                                    lengthFooter = (int)_state.LengthTree.ReadSymbol(buffer);
                                    matchLength += lengthFooter;
                                }

                                matchLength += Constants.MinimumMatchLength;
                                matchOffset = mainElement >> Constants.LengthCodeBitCount;
                                if (matchOffset > 2)
                                {
                                    // Not repeated offset.
                                    var extra = _extraBits[matchOffset];
                                    matchOffset = (int)_positionBase[matchOffset] - 2;
                                    int alignedBits;
                                    if (extra > Constants.AlignedDistanceBitCount)
                                    {
                                        // Verbatim and aligned bits.
                                        extra -= Constants.AlignedDistanceBitCount;
                                        var verbatimBits = (int)buffer.Read(extra);
                                        matchOffset += (verbatimBits << Constants.AlignedDistanceBitCount);
                                        alignedBits = (int)_state.AlignedTree.ReadSymbol(buffer);
                                        matchOffset += alignedBits;
                                    }
                                    else if (extra == Constants.AlignedDistanceBitCount)
                                    {
                                        // Aligned bits only.
                                        alignedBits = (int)_state.AlignedTree.ReadSymbol(buffer);
                                        matchOffset += alignedBits;
                                    }
                                    else if (extra > 0) // extra==1, extra==2
                                    {
                                        // Verbatim bits only.
                                        var verbatimBits = (int)buffer.Read(extra);
                                        matchOffset += verbatimBits;
                                    }
                                    else // extra == 0
                                    {
                                        // ???
                                        matchOffset = 1;
                                    }

                                    // Update repeated offset LRU queue.
                                    r2 = r1;
                                    r1 = r0;
                                    r0 = (uint)matchOffset;
                                }
                                else if (matchOffset == 0)
                                {
                                    matchOffset = (int)r0;
                                }
                                else if (matchOffset == 1)
                                {
                                    matchOffset = (int)r1;
                                    r1 = r0;
                                    r0 = (uint)matchOffset;
                                }
                                else // match_offset == 2
                                {
                                    matchOffset = (int)r2;
                                    r2 = r0;
                                    r0 = (uint)matchOffset;
                                }

                                runDestination = (int)windowPosition;
                                thisRun -= matchLength;
                                // Copy any wrapped around source data
                                if (windowPosition >= matchOffset)
                                {
                                    // No wrap
                                    runSource = runDestination - matchOffset;
                                }
                                else
                                {
                                    runSource = runDestination + ((int)windowSize - matchOffset);
                                    var copyLength = matchOffset - (int)windowPosition;
                                    if (copyLength < matchLength)
                                    {
                                        matchLength -= copyLength;
                                        windowPosition += (uint)copyLength;
                                        while (copyLength-- > 0)
                                        {
                                            _state.Window[runDestination++] = _state.Window[runSource++];
                                        }

                                        runSource = 0;
                                    }
                                }

                                windowPosition += (uint)matchLength;
                                // Copy match data - no worries about destination wraps.
                                while (matchLength-- > 0)
                                {
                                    _state.Window[runDestination++] = _state.Window[runSource++];
                                }
                            }
                        }

                        break;
                    case BlockType.Uncompressed:
                        if ((inData.Position + thisRun) > endPosition)
                        {
                            return -1; // TODO: Throw proper exception
                        }

                        var tempBuffer = new byte[thisRun];
                        inData.Read(tempBuffer, 0, thisRun);
                        tempBuffer.CopyTo(_state.Window, (int)windowPosition);
                        windowPosition += (uint)thisRun;
                        break;
                    default:
                        return -1; // TODO: Throw proper exception
                }
            }
        }

        if (togo != 0)
        {
            return -1; // TODO: Throw proper exception
        }

        var startWindowPos = (int)windowPosition;
        if (startWindowPos == 0)
        {
            startWindowPos = (int)windowSize;
        }

        startWindowPos -= outLen;
        outData.Write(_state.Window, startWindowPos, outLen);
        _state.WindowPosition = windowPosition;
        _state.R0 = r0;
        _state.R1 = r1;
        _state.R2 = r2;

        return 0;
    }

    #region Constants

    // Direct lookup widths trade table space for fewer tree walks
    public const int PretreeTableBits = 6;
    public const int MainTreeTableBits = 12;
    public const int LengthTableBits = 12;
    public const int AlignedTableBits = 7;

    public const int MainTreeSymbolCount = Constants.LiteralSymbolCount +
                                           Constants.MaximumPositionSlotCount * Constants.LengthCodesPerSlot;

    // Preserve the decoder's extra secondary slot and run-length safety space
    public const int LengthTreeSymbolCount = Constants.SecondaryLengthCount + 1;
    public const int LengthTableSafety = 64;

    #endregion
}

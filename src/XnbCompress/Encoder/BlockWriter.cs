using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace XnbCompress.Encoder;

internal sealed class BlockWriter : IDisposable
{
    private readonly byte[] _source;
    private readonly RecordWriter _records;

    // Rebuild current trees in place for each block. Previous lengths
    // below are separate snapshots used when writing tree deltas.
    private readonly HuffmanTree _mainTree;
    private readonly HuffmanTree _lengthTree = new(Constants.SecondaryLengthCount);
    private readonly HuffmanTree _alignedTree = new(Constants.AlignedSymbolCount);

    // Token statistics stay separate from the trees' build workspace
    private readonly ushort[] _mainFrequencies;
    private readonly ushort[] _lengthFrequencies = new ushort[Constants.SecondaryLengthCount];
    private readonly ushort[] _alignedFrequencies = new ushort[Constants.AlignedSymbolCount];

    // Selection needs full counts rather than 16-bit tree frequencies.
    // Keep a separate histogram so large counts are not truncated.
    private readonly int[] _alignedHistogram = new int[Constants.AlignedSymbolCount];

    // Retain main-tree lengths across blocks for delta encoding
    private readonly byte[] _previousMainLengths;

    // Retain secondary-tree lengths for matches of 9..257 bytes
    private readonly byte[] _previousLengthLengths = new byte[Constants.SecondaryLengthCount];

    private RepeatOffsets _outputRepeatOffsets = new();
    private int _blockDecodedLength;
    private int _matchCount;
    private int _extraDistanceBitCount;

    public BlockWriter(XMemCompressor encoder, byte[] source, Stream output)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _mainTree = new HuffmanTree(encoder.MainSymbolCount);
        _mainFrequencies = new ushort[encoder.MainSymbolCount];
        _previousMainLengths = new byte[encoder.MainSymbolCount];
        _records = new RecordWriter(output, source.Length);
    }

    public void Complete()
    {
        _records.Complete();
    }

    public void Dispose()
    {
        _records.Dispose();
    }

    public void WriteBlock(Token[] tokens, int position, bool allowRaw)
    {
        var length = tokens.Sum(token => token.DecodedLength);
        ReadOnlySpan<byte> sourceSpan = _source.AsSpan(position, length);
        ReadOnlySpan<Token> tokensSpan = tokens.AsSpan();

        var decodedLength = BuildBlockStatistics(tokensSpan);
        if (decodedLength != sourceSpan.Length)
        {
            throw new ArgumentException("Token lengths do not match source length.", nameof(tokens));
        }

        // The complete source is available here, so a raw block can copy
        // the original bytes when compression would not make them smaller.
        var blockType = ChooseBlockType(decodedLength, allowRaw);
        switch (blockType)
        {
            case BlockType.Verbatim:
            case BlockType.Aligned:
                // A match must fit in one record; parsing chooses lengths
                // with these boundaries in mind before output begins.
                var recordLength = _records.DecodedLength;
                foreach (var token in tokensSpan)
                {
                    recordLength += token.DecodedLength;
                    if (recordLength > Constants.RecordDecodedCapacity)
                    {
                        throw new ArgumentException("Token crosses an output record boundary.", nameof(tokens));
                    }

                    if (recordLength == Constants.RecordDecodedCapacity)
                    {
                        recordLength = 0;
                    }
                }

                WriteCompressedBlock(decodedLength, tokensSpan, blockType);
                break;

            case BlockType.Uncompressed:
                WriteUncompressedBlock(sourceSpan, tokensSpan);
                break;

            default:
                throw new InvalidOperationException("Unsupported block type.");
        }
    }

    private int BuildBlockStatistics(ReadOnlySpan<Token> tokens)
    {
        Array.Clear(_mainFrequencies, 0, _mainFrequencies.Length);
        Array.Clear(_lengthFrequencies, 0, _lengthFrequencies.Length);
        Array.Clear(_alignedFrequencies, 0, _alignedFrequencies.Length);
        Array.Clear(_alignedHistogram, 0, _alignedHistogram.Length);

        _blockDecodedLength = 0;
        _matchCount = 0;
        _extraDistanceBitCount = 0;

        var decodedLength = 0;
        foreach (var token in tokens)
        {
            if (token is
                {
                    Kind: TokenKind.Match,
                    MatchLength: < Constants.MinimumMatchLength or > Constants.MaximumMatchLength
                })
            {
                throw new ArgumentOutOfRangeException(nameof(tokens));
            }

            decodedLength = checked(decodedLength + token.DecodedLength);
            if (token.Kind == TokenKind.Literal)
            {
                _mainFrequencies[token.LiteralValue]++;
                continue;
            }

            var (slot, extraBits) = TokenUtils.GetPositionSlot(token.EncodedDistance);
            var mainSymbol = TokenUtils.GetMainSymbol(token, slot);
            _mainFrequencies[mainSymbol]++;
            _matchCount++;
            _extraDistanceBitCount += extraBits;

            if (token.MatchLength >= Constants.SecondaryMinimumLength)
            {
                _lengthFrequencies[token.MatchLength - Constants.SecondaryMinimumLength]++;
            }

            // Collect aligned frequencies before choosing the block type.
            // They show how often each low-three-bit distance value occurs.
            if (token.EncodedDistance > 15)
            {
                var symbol = token.EncodedDistance & Constants.AlignedDistanceMask;
                _alignedFrequencies[symbol]++;
                _alignedHistogram[symbol]++;
            }
        }

        if (decodedLength is < 1 or > Constants.MaximumBlockLength)
        {
            throw new ArgumentOutOfRangeException(nameof(tokens));
        }

        _mainTree.Build(_mainFrequencies);
        _lengthTree.Build(_lengthFrequencies);
        _alignedTree.Build(_alignedFrequencies);

        _blockDecodedLength = decodedLength;
        return decodedLength;
    }

    private BlockType ChooseBlockType(int decodedLength, bool allowRaw = true)
    {
        if (decodedLength < 1 || decodedLength != _blockDecodedLength)
        {
            throw new ArgumentException("Block statistics do not match decoded length.", nameof(decodedLength));
        }

        // Estimate the compressed size, including a fixed cost for the trees.
        // This is an estimate rather than the actual serialized header size.
        var estimatedBits = XMemCompressor.BlockTreeEstimateBits + _extraDistanceBitCount;
        estimatedBits += EstimateTreeBitCount(_mainFrequencies, _mainTree);
        estimatedBits += EstimateTreeBitCount(_lengthFrequencies, _lengthTree);

        if (allowRaw && decodedLength <= (estimatedBits + 7) / 8)
        {
            return BlockType.Uncompressed;
        }

        var alignedTotal = 0;
        var alignedMaximum = 0;

        foreach (var count in _alignedHistogram)
        {
            alignedTotal += count;
            alignedMaximum = Math.Max(alignedMaximum, count);
        }

        // MatchCount includes all matches. The histogram includes only
        // distances with at least three extra bits to encode.
        if (_matchCount >= XMemCompressor.AlignedMinimumMatchCount &&
            alignedMaximum > (alignedTotal / XMemCompressor.AlignedHistogramDivisor))
        {
            return BlockType.Aligned;
        }

        return BlockType.Verbatim;
    }

    private static int EstimateTreeBitCount(ushort[] frequencies, HuffmanTree tree)
    {
        var bits = 0;
        var usedSymbols = 0;

        for (var symbol = 0; symbol < frequencies.Length; symbol++)
        {
            bits += frequencies[symbol] * tree.Lengths[symbol];

            if (frequencies[symbol] != 0)
            {
                usedSymbols++;
            }
        }

        // A single-symbol tree has a dummy leaf with frequency one.
        // Include its one-bit code in the size estimate.
        if (usedSymbols == 1)
        {
            bits++;
        }

        return bits;
    }

    private void WriteUncompressedBlock(ReadOnlySpan<byte> source, ReadOnlySpan<Token> tokens)
    {
        if (source.Length is < 1 or > Constants.MaximumBlockLength)
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        var decodedLength = 0;
        var offsets = _outputRepeatOffsets;

        // Raw bytes replace the parsed tokens, but their final repeat offsets
        // still need to be stored. Validate before writing or changing state.
        foreach (var token in tokens)
        {
            decodedLength = checked(decodedLength + token.DecodedLength);

            if (token.Kind == TokenKind.Match)
            {
                if (token.MatchLength is < Constants.MinimumMatchLength or > Constants.MaximumMatchLength)
                {
                    throw new ArgumentOutOfRangeException(nameof(tokens));
                }

                offsets = offsets.Apply(token.EncodedDistance);
            }
        }

        if (decodedLength != source.Length)
        {
            throw new ArgumentException("Token lengths do not match block length.", nameof(tokens));
        }

        _records.Bits.WriteBits(Constants.BlockTypeBitCount, (uint)BlockType.Uncompressed);
        _records.Bits.WriteBits(Constants.BlockLengthBitCount, (uint)source.Length);

        // End the raw header with word padding. If it is already aligned,
        // write a full zero word rather than skipping the padding.
        _records.Bits.AlignToWord(forcePadding: true);

        Span<byte> offsetBytes = stackalloc byte[12];
        BinaryPrimitives.WriteInt32LittleEndian(offsetBytes.Slice(0, 4), offsets.R0);
        BinaryPrimitives.WriteInt32LittleEndian(offsetBytes.Slice(4, 4), offsets.R1);
        BinaryPrimitives.WriteInt32LittleEndian(offsetBytes.Slice(8, 4), offsets.R2);

        _records.Bits.WriteBytes(offsetBytes);
        _records.WriteBytes(source);
        _outputRepeatOffsets = offsets;

        // Raw blocks do not write trees. Keep the previous lengths for
        // delta encoding when the next compressed block is written.
    }

    private void WriteCompressedBlock(int decodedLength, ReadOnlySpan<Token> tokens, BlockType blockType)
    {
        if (blockType != BlockType.Verbatim && blockType != BlockType.Aligned)
        {
            throw new ArgumentOutOfRangeException(nameof(blockType));
        }

        if (decodedLength is < 1 or > Constants.MaximumBlockLength)
        {
            throw new ArgumentOutOfRangeException(nameof(decodedLength));
        }

        // Statistics and trees are prepared before selecting the block type.
        // Writing uses those trees without counting or building them again.
        if (_blockDecodedLength != decodedLength)
        {
            throw new ArgumentException("Block statistics do not match decoded length.", nameof(decodedLength));
        }

        _records.Bits.WriteBits(Constants.BlockTypeBitCount, (uint)blockType);
        _records.Bits.WriteBits(Constants.BlockLengthBitCount, (uint)decodedLength);

        // Aligned lengths are stored directly in each block.
        // They do not need previous lengths or pretree deltas.
        var alignedTree = blockType == BlockType.Aligned ? _alignedTree : null;
        if (alignedTree != null)
        {
            foreach (var length in alignedTree.Lengths)
            {
                _records.Bits.WriteBits(Constants.AlignedLengthBitCount, length);
            }
        }

        // Main-tree literals and matches have separate pretree descriptions.
        // Each section uses its own previous lengths for delta encoding.
        WriteRepTree(_mainTree.Lengths.AsSpan(0, Constants.LiteralSymbolCount),
            _previousMainLengths.AsSpan(0, Constants.LiteralSymbolCount));
        WriteRepTree(_mainTree.Lengths.AsSpan(Constants.LiteralSymbolCount),
            _previousMainLengths.AsSpan(Constants.LiteralSymbolCount));
        WriteRepTree(_lengthTree.Lengths, _previousLengthLengths);

        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.Literal)
            {
                _records.Bits.WriteBits(_mainTree.Lengths[token.LiteralValue], _mainTree.Codes[token.LiteralValue]);
                _records.Advance(token.DecodedLength);
                continue;
            }

            var (slot, extraBits) = TokenUtils.GetPositionSlot(token.EncodedDistance);
            var mainSymbol = TokenUtils.GetMainSymbol(token, slot);
            _records.Bits.WriteBits(_mainTree.Lengths[mainSymbol], _mainTree.Codes[mainSymbol]);

            if (token.MatchLength >= Constants.SecondaryMinimumLength)
            {
                var lengthSymbol = token.MatchLength - Constants.SecondaryMinimumLength;
                _records.Bits.WriteBits(_lengthTree.Lengths[lengthSymbol], _lengthTree.Codes[lengthSymbol]);
            }

            // Slot bases are multiples of their widths, so masking the
            // encoded distance gives its offset within the selected slot.
            var distanceBits = (uint)(token.EncodedDistance & ((1 << extraBits) - 1));
            if (alignedTree == null || extraBits < Constants.AlignedDistanceBitCount)
            {
                if (extraBits != 0)
                {
                    _records.Bits.WriteBits(extraBits, distanceBits);
                }
            }
            else
            {
                if (extraBits > Constants.AlignedDistanceBitCount)
                {
                    _records.Bits.WriteBits(extraBits - Constants.AlignedDistanceBitCount,
                        distanceBits >> Constants.AlignedDistanceBitCount);
                }

                // With exactly three extra bits, only this symbol is needed
                var alignedSymbol = token.EncodedDistance & Constants.AlignedDistanceMask;
                _records.Bits.WriteBits(alignedTree.Lengths[alignedSymbol], alignedTree.Codes[alignedSymbol]);
            }

            _outputRepeatOffsets = _outputRepeatOffsets.Apply(token.EncodedDistance);
            _records.Advance(token.DecodedLength);
        }

        // The next block continues at the current bit position.
        // Word alignment is applied when the output record is flushed.
    }

    private void WriteRepTree(ReadOnlySpan<byte> lengths, Span<byte> previous)
    {
        if (lengths.Length != previous.Length)
        {
            throw new ArgumentException("Tree length mismatch.");
        }

        // First choose the instructions and count their symbols.
        // These counts are needed to build the pretree before writing them.
        var instructions = new List<(int Symbol, int ExtraBits, int ExtraValue, int DeltaSymbol)>();
        var frequencies = new ushort[Constants.PretreeSymbolCount];

        for (var index = 0; index < lengths.Length; index++)
        {
            var length = lengths[index];
            var followingCount = 0;

            while (index + 1 + followingCount < lengths.Length && lengths[index + 1 + followingCount] == length)
            {
                followingCount++;
            }

            // Symbols 0..16 describe the change from the previous tree
            var delta = (previous[index] - length + Constants.LengthDeltaModulo) % Constants.LengthDeltaModulo;

            // Count following entries, excluding the current entry.
            // Using that count as the run length leaves the final matching
            // entry to be handled by the next instruction.
            if (followingCount >= 4)
            {
                if (length == 0)
                {
                    // 17: 4..19 zeros (4 extra bits)
                    // 18: 20..51 zeros (5 extra bits)
                    var runLength = Math.Min(followingCount, 51);
                    var symbol = runLength < 20 ? Constants.ShortZeroRunSymbol : Constants.LongZeroRunSymbol;
                    var extraBits = symbol == Constants.ShortZeroRunSymbol ? 4 : 5;
                    var minimumRunLength = symbol == Constants.ShortZeroRunSymbol ? 4 : 20;

                    instructions.Add((symbol, extraBits, runLength - minimumRunLength, -1));
                    frequencies[symbol]++;

                    index += runLength - 1;
                }
                else
                {
                    // 19: repeat a nonzero length 4 or 5 times. The delta
                    // is a second pretree symbol, after the one extra bit.
                    var runLength = Math.Min(followingCount, 5);
                    instructions.Add((Constants.RepeatedLengthSymbol, 1, runLength - 4, delta));
                    frequencies[Constants.RepeatedLengthSymbol]++;
                    frequencies[delta]++;

                    index += runLength - 1;
                }
            }
            else
            {
                instructions.Add((delta, 0, 0, -1));
                frequencies[delta]++;
            }
        }


        // Pretree lengths are stored directly, four bits per symbol
        var pretree = new HuffmanTree(frequencies);
        for (var symbol = 0; symbol < frequencies.Length; symbol++)
        {
            _records.Bits.WriteBits(Constants.PretreeLengthBitCount, pretree.Lengths[symbol]);
        }

        foreach (var instruction in instructions)
        {
            _records.Bits.WriteBits(pretree.Lengths[instruction.Symbol], pretree.Codes[instruction.Symbol]);

            if (instruction.ExtraBits != 0)
            {
                _records.Bits.WriteBits(instruction.ExtraBits, (uint)instruction.ExtraValue);
            }

            if (instruction.DeltaSymbol >= 0)
            {
                _records.Bits.WriteBits(pretree.Lengths[instruction.DeltaSymbol], pretree.Codes[instruction.DeltaSymbol]);
            }
        }

        // The next block's deltas are relative to the lengths just written
        lengths.CopyTo(previous);
    }
}

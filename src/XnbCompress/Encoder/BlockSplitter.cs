using System;
using System.Collections.Generic;

namespace XnbCompress.Encoder;

internal sealed class BlockSplitter
{
    private const int SampleLength = 0x400;
    private const int MinimumTokenCount = 0x1800;
    private const int MaximumSplitCount = 4;
    private const int DifferenceThreshold = 0x578;
    private const int SplitThreshold = 0x6A3;

    private readonly int[] _left;
    private readonly int[] _right;

    public BlockSplitter(XMemCompressor encoder)
    {
        _left = new int[encoder.MainSymbolCount];
        _right = new int[encoder.MainSymbolCount];
    }

    public int FindPrefix(IReadOnlyList<Token> tokens, ref int splitCount)
    {
        if (tokens.Count < MinimumTokenCount || splitCount >= MaximumSplitCount)
        {
            return tokens.Count;
        }

        // Look for a sustained change in symbol distribution before
        // choosing a boundary that can benefit from a different tree.
        for (var before = 0x800; before < tokens.Count - 0x1000; before += SampleLength)
        {
            var after = before + 0x800;
            if (Difference(tokens, before, after - SampleLength) <= DifferenceThreshold ||
                Difference(tokens, before - SampleLength, after) <= DifferenceThreshold ||
                Difference(tokens, before - 0x800, after + SampleLength) <= DifferenceThreshold)
            {
                continue;
            }

            var bestScore = 0;
            var bestPosition = 0;
            for (var at = after - 0x600; at < after + 0x200; at += 0x40)
            {
                var score = Difference(tokens, at - SampleLength, at);
                if (score <= bestScore)
                {
                    continue;
                }

                bestScore = score;
                bestPosition = at;
            }

            if (bestScore <= SplitThreshold || bestPosition < 0x1000)
            {
                continue;
            }

            splitCount++;
            return bestPosition;
        }

        return tokens.Count;
    }

    private int Difference(IReadOnlyList<Token> tokens, int first, int second)
    {
        Array.Clear(_left, 0, _left.Length);
        Array.Clear(_right, 0, _right.Length);

        for (var index = 0; index < SampleLength; index++)
        {
            _left[TokenUtils.GetMainSymbol(tokens[first + index])]++;
            _right[TokenUtils.GetMainSymbol(tokens[second + index])]++;
        }

        var score = 0;
        for (var symbol = 0; symbol < _left.Length; symbol++)
        {
            var left = Bucket(_left[symbol]);
            var right = Bucket(_right[symbol]);
            score += Math.Abs(left * left - right * right);
        }

        return score;
    }

    private static int Bucket(int count)
    {
        // Compare logarithmic count buckets so common symbols do not
        // dominate the score solely through their absolute frequency.
        var bucket = count >= 256 ? 8 : 0;
        if (bucket != 0)
        {
            count >>= 8;
        }

        while (count != 0)
        {
            bucket++;
            count >>= 1;
        }

        return bucket;
    }
}

using System;

namespace XnbCompress.Encoder;

internal sealed class HuffmanTree
{
    public readonly byte[] Lengths;
    public readonly ushort[] Codes;

    // Construction workspace is owned by this tree and reused by Build
    private readonly ushort[] _frequencies;
    private readonly int[] _heap;
    private readonly int[] _leftChildren;
    private readonly int[] _rightChildren;
    private readonly int[] _leafOrder;
    private readonly int[] _lengthCounts;
    private readonly int[] _nextCode;

    public HuffmanTree(int symbolCount)
    {
        if (symbolCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(symbolCount));
        }

        Lengths = new byte[symbolCount];
        Codes = new ushort[symbolCount];
        _frequencies = new ushort[2 * symbolCount];
        _heap = new int[2 * symbolCount + 1];
        _leftChildren = new int[2 * symbolCount];
        _rightChildren = new int[2 * symbolCount];
        _leafOrder = new int[symbolCount];
        _lengthCounts = new int[Constants.MaximumCodeLength + 1];
        _nextCode = new int[Constants.MaximumCodeLength + 1];
    }

    // Convenience for the local pretree, whose frequencies are already
    // known when WriteRepTree creates it.
    public HuffmanTree(ReadOnlySpan<ushort> freqs) : this(freqs.Length)
    {
        Build(freqs);
    }

    public void Build(ReadOnlySpan<ushort> freqs)
    {
        if (freqs.Length != Lengths.Length)
        {
            throw new ArgumentException("Tree frequency count mismatch.", nameof(freqs));
        }

        var symbolCount = Lengths.Length;

        // Clear outputs even for an empty tree, so symbols used only
        // by the previous build cannot retain a length or code.
        Array.Clear(Lengths, 0, Lengths.Length);
        Array.Clear(Codes, 0, Codes.Length);
        Array.Clear(_lengthCounts, 0, _lengthCounts.Length);

        // Leaves occupy the first symbolCount entries; merged nodes follow.
        // Copy frequencies so adding dummy and merged nodes does not
        // change the caller's symbol counts.
        var frequencies = _frequencies;
        freqs.CopyTo(frequencies);

        // The heap is one-based, with the least frequent node at index 1.
        // Used heap entries and node children are overwritten during
        // each build, so their unused tails do not need clearing.
        var heap = _heap;
        var heapSize = 0;

        for (var symbol = 0; symbol < symbolCount; symbol++)
        {
            if (frequencies[symbol] != 0)
            {
                heap[++heapSize] = symbol;
            }
        }

        if (heapSize == 0)
        {
            return;
        }

        // A single used symbol needs a second leaf to obtain a code
        if (heapSize == 1)
        {
            var dummySymbol = heap[1] == 0 ? 1 : 0;
            frequencies[dummySymbol] = 1;
            heap[++heapSize] = dummySymbol;
        }

        for (var parent = heapSize / 2; parent >= 1; parent--)
        {
            DownHeap(heap, heapSize, frequencies, parent);
        }

        var leftChildren = _leftChildren;
        var rightChildren = _rightChildren;
        var leafOrder = _leafOrder;
        var leafCount = 0;
        var nextNode = symbolCount;

        // Repeatedly merge the two least frequent nodes.
        // Save leaves in removal order to assign lengths consistently,
        // including when several symbols have the same frequency.
        while (heapSize > 1)
        {
            var first = heap[1];
            if (first < symbolCount)
            {
                leafOrder[leafCount++] = first;
            }

            heap[1] = heap[heapSize--];
            DownHeap(heap, heapSize, frequencies, 1);

            var second = heap[1];
            if (second < symbolCount)
            {
                leafOrder[leafCount++] = second;
            }

            frequencies[nextNode] = checked((ushort)(frequencies[first] + frequencies[second]));
            leftChildren[nextNode] = first;
            rightChildren[nextNode] = second;

            heap[1] = nextNode++;
            DownHeap(heap, heapSize, frequencies, 1);
        }

        var lengthCounts = _lengthCounts;
        CountLengths(heap[1], 0, symbolCount, leftChildren, rightChildren, lengthCounts);
        LimitCodeLengths(lengthCounts);

        // The earliest removed (least frequent) leaves get the longest
        // codes. Use the corrected length counts, not each leaf's depth.
        var order = 0;
        for (var bits = Constants.MaximumCodeLength; bits >= 1; bits--)
        {
            for (var count = 0; count < lengthCounts[bits]; count++)
            {
                Lengths[leafOrder[order++]] = (byte)bits;
            }
        }

        if (order != leafCount)
        {
            throw new InvalidOperationException("Huffman leaf count mismatch.");
        }

        AssignCanonicalCodes(lengthCounts);
    }

    private static void DownHeap(int[] heap, int heapSize, ushort[] frequencies, int parent)
    {
        var heldNode = heap[parent];
        var child = parent * 2;

        while (child <= heapSize)
        {
            // On equal frequencies, choose the left child and leave an
            // equal parent in place. This keeps the tie order consistent.
            if (child < heapSize && frequencies[heap[child + 1]] < frequencies[heap[child]])
            {
                child++;
            }

            if (frequencies[heldNode] <= frequencies[heap[child]])
            {
                break;
            }

            heap[parent] = heap[child];
            parent = child;
            child = parent * 2;
        }

        heap[parent] = heldNode;
    }

    private static void CountLengths(int node, int depth, int symbolCount, int[] leftChildren, int[] rightChildren, int[] lengthCounts)
    {
        while (true)
        {
            if (node < symbolCount)
            {
                lengthCounts[Math.Min(depth, Constants.MaximumCodeLength)]++;
                return;
            }

            CountLengths(leftChildren[node], depth + 1, symbolCount, leftChildren, rightChildren, lengthCounts);
            node = rightChildren[node];
            depth += 1;
        }
    }

    private static void LimitCodeLengths(int[] lengthCounts)
    {
        // Measure occupied code space in units of a 16-bit code.
        // Clamping deep leaves to 16 bits can overfill that space.
        var occupiedCodeSpace = 0;
        const int fullCodeSpace = 1 << Constants.MaximumCodeLength;

        for (var bits = 1; bits <= Constants.MaximumCodeLength; bits++)
        {
            occupiedCodeSpace += lengthCounts[bits] << (Constants.MaximumCodeLength - bits);
        }

        while (occupiedCodeSpace > fullCodeSpace)
        {
            // Replace one shorter code with two codes one bit longer,
            // and remove one clamped leaf. The leaf count stays unchanged,
            // while occupied space decreases by one 16-bit code unit.
            lengthCounts[Constants.MaximumCodeLength]--;
            var shorterLength = Constants.MaximumCodeLength - 1;

            while (shorterLength > 0 && lengthCounts[shorterLength] == 0)
            {
                shorterLength--;
            }

            if (shorterLength == 0)
            {
                throw new InvalidOperationException("Cannot limit Huffman depth.");
            }

            lengthCounts[shorterLength]--;
            lengthCounts[shorterLength + 1] += 2;
            occupiedCodeSpace--;
        }
    }

    private void AssignCanonicalCodes(int[] lengthCounts)
    {
        var nextCode = _nextCode;
        Array.Clear(nextCode, 0, nextCode.Length);

        for (var bits = 2; bits <= Constants.MaximumCodeLength; bits++)
        {
            nextCode[bits] = (nextCode[bits - 1] + lengthCounts[bits - 1]) << 1;
        }

        // For equal lengths, canonical codes follow symbol order.
        // Codes are right-aligned for BitWriter.WriteBits(length, code).
        for (var symbol = 0; symbol < Lengths.Length; symbol++)
        {
            int bits = Lengths[symbol];

            if (bits != 0)
            {
                Codes[symbol] = (ushort)nextCode[bits]++;
            }
        }
    }
}

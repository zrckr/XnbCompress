using System;
using System.Runtime.CompilerServices;

namespace XnbCompress.Encoder;

internal sealed class MatchFinder
{
    public int AvailableLength { get; private set; }
    public bool CanCompact => AvailableLength - _partitionBase >= _windowSize + _partitionSize;

    private readonly int _windowSize;
    private readonly int _partitionSize;
    private readonly byte[] _source;
    private readonly byte[] _memory;
    private readonly int[] _roots = new int[XMemCompressor.MatchKeyCount];
    private readonly int[] _leftChildren;
    private readonly int[] _rightChildren;
    private readonly int[] _matchDistances = new int[Constants.MaximumMatchLength + 1];
    private int _partitionBase;

    public MatchFinder(XMemCompressor encoder, byte[] source)
    {
        _windowSize = encoder.WindowSize;
        _partitionSize = encoder.PartitionSize;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        AvailableLength = source.Length;
        _leftChildren = new int[source.Length];
        _rightChildren = new int[source.Length];
        _memory = new byte[_windowSize + _partitionSize + XMemCompressor.MatchLookaheadSize];
        Reset();
    }

    public void Reset()
    {
        // Clear search links for reparsing while retaining submitted bytes
        Array.Fill(_roots, -1);
        Array.Fill(_leftChildren, -1);
        Array.Fill(_rightChildren, -1);
        Array.Clear(_matchDistances, 0, _matchDistances.Length);
    }

    public void Submit(int start, int end)
    {
        if (start < _partitionBase || end < start || end > _source.Length ||
            end - _partitionBase > _windowSize + _partitionSize)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        Array.Copy(_source, start, _memory, start - _partitionBase, end - start);
        AvailableLength = end;
    }

    public void Compact()
    {
        if (AvailableLength - _partitionBase < _windowSize + _partitionSize)
        {
            throw new InvalidOperationException("The partition is not ready for compaction.");
        }

        // Move the retained window to the front. Tree indices remain
        // logical source positions, so only the byte-storage base moves.
        Array.Copy(_memory, _partitionSize, _memory, 0, _windowSize);
        _partitionBase += _partitionSize;
    }

    public MatchCandidates Find(int position, RepeatOffsets offsets)
    {
        // Results share scratch storage and must be consumed before
        // the next search or insertion changes that storage.
        return FindCore(position, offsets, quick: false);
    }

    public void QuickInsert(int position)
    {
        FindCore(position, default, quick: true);
    }

    public void InsertSkipped(int position, int length, int encodedDistance)
    {
        // New distance-one matches longer than 16 bytes insert only the
        // next position; other matches insert every skipped byte.
        var insertionLength = encodedDistance == 3 && length > 16 ? 2 : length;
        for (var offset = 1; offset < insertionLength; offset++)
        {
            QuickInsert(position + offset);
        }
    }

    public void Remove(int position, int expired)
    {
        var key = Read(position) | Read(position + 1) << 8;
        if (_roots[key] != position)
        {
            return;
        }

        var link = (Owner: key, Kind: LinkKind.Root);
        if (position <= expired)
        {
            SetLink(link, -1);
            _leftChildren[position] = -1;
            _rightChildren[position] = -1;
            return;
        }

        var left = _leftChildren[position];
        if (left <= expired)
        {
            left = -1;
            _leftChildren[position] = -1;
        }

        var right = _rightChildren[position];
        if (right <= expired)
        {
            right = -1;
            _rightChildren[position] = -1;
        }

        // Merge the children by descending position to retain newer
        // candidates and discard links outside the search window.
        while (true)
        {
            if (left > right)
            {
                if (left <= expired)
                {
                    left = -1;
                }

                SetLink(link, left);

                if (left < 0)
                {
                    return;
                }

                link = (left, LinkKind.Right);
                left = _rightChildren[left];
            }
            else
            {
                if (right <= expired)
                {
                    right = -1;
                }

                SetLink(link, right);

                if (right < 0)
                {
                    return;
                }

                link = (right, LinkKind.Left);
                right = _leftChildren[right];
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private byte Read(int position)
    {
        return _memory[position - _partitionBase];
    }

    private void SetLink((int Owner, LinkKind Kind) link, int position)
    {
        switch (link.Kind)
        {
            case LinkKind.Root:
                _roots[link.Owner] = position;
                break;

            case LinkKind.Left:
                _leftChildren[link.Owner] = position;
                break;

            case LinkKind.Right:
                _rightChildren[link.Owner] = position;
                break;
        }
    }

    private MatchCandidates FindCore(int position, RepeatOffsets offsets, bool quick)
    {
        if (position < 0 || position >= AvailableLength)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (!quick)
        {
            Array.Clear(_matchDistances, 0, _matchDistances.Length);
        }

        var remaining = AvailableLength - position;
        // Two leading bytes choose the root. Insert this position while
        // walking the suffix tree to find longer matches.
        var key = Read(position) | Read(position + 1) << 8;
        var candidate = _roots[key];
        _roots[key] = position;

        var expired = position - _windowSize + XMemCompressor.WindowSearchMargin;
        if (candidate < 0 || candidate <= expired)
        {
            _leftChildren[position] = -1;
            _rightChildren[position] = -1;
            return new MatchCandidates(0, _matchDistances);
        }

        var bestLength = Constants.MinimumMatchLength;
        var leftCommon = Constants.MinimumMatchLength;
        var rightCommon = Constants.MinimumMatchLength;
        var common = Constants.MinimumMatchLength;
        _matchDistances[Constants.MinimumMatchLength] = position - candidate + 2;

        var leftLink = (Owner: position, Kind: LinkKind.Left);
        var rightLink = (Owner: position, Kind: LinkKind.Right);
        var limit = quick ? XMemCompressor.LongMatchThreshold : Constants.MaximumMatchLength;

        while (candidate >= 0 && candidate > expired)
        {
            var difference = 0;
            var matched = common;

            while (matched < limit)
            {
                difference = Read(candidate + matched) - Read(position + matched);

                if (difference != 0)
                {
                    break;
                }

                matched++;
            }

            if (difference < 0)
            {
                if (leftCommon < matched)
                {
                    leftCommon = matched;

                    if (bestLength < matched)
                    {
                        SetMatchDistances(bestLength + 1, matched, position - candidate + 2);
                        bestLength = matched;

                        if (matched >= XMemCompressor.LongMatchThreshold)
                        {
                            SetLink(leftLink, _leftChildren[candidate]);
                            SetLink(rightLink, _rightChildren[candidate]);
                            break;
                        }
                    }

                    common = Math.Min(leftCommon, rightCommon);
                }

                SetLink(rightLink, candidate);
                rightLink = (candidate, LinkKind.Left);
                candidate = _leftChildren[candidate];
            }
            else
            {
                if (rightCommon < matched)
                {
                    if (bestLength < matched)
                    {
                        SetMatchDistances(bestLength + 1, matched, position - candidate + 2);
                        bestLength = matched;

                        if (matched >= XMemCompressor.LongMatchThreshold)
                        {
                            SetLink(leftLink, _leftChildren[candidate]);
                            SetLink(rightLink, _rightChildren[candidate]);
                            break;
                        }
                    }

                    common = Math.Min(leftCommon, matched);
                    rightCommon = matched;
                }

                SetLink(leftLink, candidate);
                leftLink = (candidate, LinkKind.Right);
                candidate = _rightChildren[candidate];
            }
        }

        if (candidate < 0 || candidate <= expired)
        {
            SetLink(leftLink, -1);
            SetLink(rightLink, -1);
        }

        if (quick)
        {
            return new MatchCandidates(bestLength, _matchDistances);
        }

        // Prefer repeat selectors for lengths they cover. Earlier
        // selectors keep their lengths when later repeats also match.
        var previousRepeatLength = 1;
        for (var selector = 0; selector < 3; selector++)
        {
            var distance = selector switch
            {
                0 => offsets.R0,
                1 => offsets.R1,
                _ => offsets.R2
            };

            if (distance > position)
            {
                continue;
            }

            var length = 0;
            while (length < bestLength && Read(position - distance + length) == Read(position + length))
            {
                length++;
            }

            SetMatchDistances(Math.Max(Constants.MinimumMatchLength, previousRepeatLength + 1), length, selector);
            previousRepeatLength = Math.Max(previousRepeatLength, length);
            if (selector == 0 && length > XMemCompressor.LongMatchThreshold)
            {
                break;
            }
        }

        // Search padding can extend beyond valid input. Clamp the
        // result and leave the last byte of each chunk for a literal
        var chunkRemaining = Constants.RecordDecodedCapacity - 1 - (position & Constants.RecordDecodedCapacity - 1);
        bestLength = Math.Min(bestLength, Math.Min(remaining, chunkRemaining));

        return new MatchCandidates(bestLength < Constants.MinimumMatchLength ? 0 : bestLength, _matchDistances);
    }

    private void SetMatchDistances(int firstLength, int lastLength, int encodedDistance)
    {
        for (var length = firstLength; length <= lastLength; length++)
        {
            _matchDistances[length] = encodedDistance;
        }
    }

    private enum LinkKind
    {
        Root,
        Left,
        Right
    }
}

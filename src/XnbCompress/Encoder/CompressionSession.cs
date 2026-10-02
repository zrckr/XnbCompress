using System;
using System.Collections.Generic;

namespace XnbCompress.Encoder;

internal sealed class CompressionSession
{
    private readonly List<Token> _tokens = new();
    private RepeatOffsets _repeats;
    private readonly TokenCostModel _costs;
    private readonly MatchOptimizer _optimizer;
    private readonly MatchFinder _finder;
    private readonly byte[] _source;
    private readonly int _windowSize;
    private int _nextCostRefresh;
    private bool _needsFrequencyRecount;
    private bool _firstBlock;
    private int _matchCount;
    private int _splitCount;
    private int _emittedBytes;
    private int _earliestRetainedByte;
    private readonly BlockSplitter _splitter;
    private Action<Token[], int, bool>? _writeBlock;

    public CompressionSession(XMemCompressor encoder, byte[] source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _windowSize = encoder.WindowSize;
        _costs = new TokenCostModel(encoder);
        _splitter = new BlockSplitter(encoder);
        _finder = new MatchFinder(encoder, source);
        _optimizer = new MatchOptimizer(source, _finder, _costs);
    }

    private void Reset()
    {
        _tokens.Clear();
        _repeats = new RepeatOffsets();
        _nextCostRefresh = XMemCompressor.InitialCostRefreshTokenCount;
        _needsFrequencyRecount = true;
        _firstBlock = true;
        _matchCount = 0;
        _splitCount = 0;
        _emittedBytes = 0;
        _earliestRetainedByte = 0;
        _costs.Reset();
        _optimizer.Reset();
    }

    public void Encode(Action<Token[], int, bool> writeBlock)
    {
        if (writeBlock == null)
        {
            throw new ArgumentNullException(nameof(writeBlock));
        }

        Reset();
        _writeBlock = writeBlock;

        try
        {
            for (var start = 0; start < _source.Length;)
            {
                var end = start + Math.Min(Constants.RecordDecodedCapacity, _source.Length - start);
                _finder.Submit(start, end);

                // Refresh the previous chunk's tail now that lookahead
                // contains the newly submitted bytes.
                if (start != 0)
                {
                    for (var position = start - XMemCompressor.LongMatchThreshold; position < start; position++)
                    {
                        _finder.QuickInsert(position);
                    }
                }

                ParseRange(start, end);
                _earliestRetainedByte = Math.Max(0, end - _windowSize);

                if (end - start == Constants.RecordDecodedCapacity)
                {
                    RemoveTail(end);
                    if (_finder.CanCompact)
                    {
                        // Redo the first block while its bytes are still
                        // available, then compact the retained window.
                        if (_firstBlock)
                        {
                            ReparseFirstBlock();
                            ParseRange(0, end);
                            RemoveTail(end);
                        }

                        _finder.Compact();
                    }
                }
                else if (_firstBlock)
                {
                    ReparseFirstBlock();
                    ParseRange(0, end);
                }

                // Rebuild estimates after each input chunk without
                // adding tokens to the saved frequency tally.
                _costs.RebuildCosts();
                start = end;
            }

            while (_tokens.Count != 0)
            {
                EmitPendingPrefix();
            }
        }
        finally
        {
            _writeBlock = null;
        }
    }

    private void ParseRange(int start, int end)
    {
        if (start < 0 || start > end)
        {
            throw new ArgumentOutOfRangeException(nameof(start));
        }

        // Share the submitted endpoint so returned matches stay within
        // valid input even when the matcher examines padded lookahead.
        if (end > _source.Length || end != _finder.AvailableLength)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        ReadOnlySpan<byte> source = _source.AsSpan(0, end);
        var position = start;
        while (position < end)
        {
            var candidates = _finder.Find(position, _repeats);
            if (TryEmitDirectToken(source, position, candidates.Length, candidates.Distances, out var nextPosition))
            {
                position = nextPosition;
                if (_tokens.Count >= XMemCompressor.TokenBufferLimit ||
                    (_tokens[^1].Kind == TokenKind.Match && _matchCount >= XMemCompressor.MatchBufferLimit))
                {
                    EndBlock();
                }

                continue;
            }

            var path = _optimizer.FindShortPath(position, end, _repeats, candidates);

            // Reserve the entire optimized path against both buffers
            // before committing any of its tokens.
            while (_tokens.Count + path.Tokens.Count >= XMemCompressor.TokenBufferLimit ||
                _matchCount + path.Tokens.Count >= XMemCompressor.MatchBufferLimit)
            {
                EndBlock();
            }

            _tokens.AddRange(path.Tokens);
            foreach (var token in path.Tokens)
            {
                if (token.Kind == TokenKind.Match)
                {
                    _matchCount++;
                }
            }

            _repeats = path.Repeats;
            position = path.NextPosition;

            RefreshCostsAfterPath();

            // Only optimized paths trigger the early first-block redo.
            // Literal and direct paths use the hard buffer limits above.
            if (_firstBlock && (_tokens.Count >= XMemCompressor.FirstReparseTokenLimit ||
                _matchCount >= XMemCompressor.FirstReparseMatchLimit))
            {
                ReparseFirstBlock();
                position = 0;
            }
        }
    }

    private bool TryEmitDirectToken(
        ReadOnlySpan<byte> source,
        int position,
        int matchLength,
        ReadOnlySpan<int> distances,
        out int nextPosition)
    {
        if ((uint)position >= (uint)source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (matchLength < 0 || matchLength > Constants.MaximumMatchLength || matchLength > source.Length - position)
        {
            throw new ArgumentOutOfRangeException(nameof(matchLength));
        }

        nextPosition = position;

        // With no usable match, the current byte must be emitted literally
        if (matchLength < Constants.MinimumMatchLength)
        {
            _tokens.Add(Token.Literal(source[position]));
            nextPosition++;
            return true;
        }

        // Short matches need a comparison of competing token sequences.
        // Leave their candidates intact for the optimization step.
        if (matchLength < XMemCompressor.LongMatchThreshold)
        {
            return false;
        }

        var encodedDistance = distances[matchLength];
        _finder.InsertSkipped(position, matchLength, encodedDistance);

        _tokens.Add(Token.Match(matchLength, encodedDistance));
        _matchCount++;
        _repeats = _repeats.Apply(encodedDistance);
        nextPosition += matchLength;

        // Direct tokens do not trigger a cost rebuild; those updates
        // happen after a path through the short-match optimizer.
        return true;
    }

    private void RemoveTail(int end)
    {
        var expired = end - _windowSize + XMemCompressor.LongMatchThreshold + XMemCompressor.WindowSearchMargin;
        for (var at = 1; at <= XMemCompressor.LongMatchThreshold; at++)
        {
            _finder.Remove(end - at, expired);
        }
    }

    private void ReparseFirstBlock()
    {
        var prefix = _splitter.FindPrefix(_tokens, ref _splitCount);
        _costs.Recount(_tokens, prefix);
        _costs.RebuildCosts();
        _finder.Reset();
        _tokens.Clear();
        _repeats = new RepeatOffsets();
        _nextCostRefresh = prefix;
        _matchCount = 0;
        _needsFrequencyRecount = true;
        _firstBlock = false;
    }

    private void EndBlock()
    {
        _firstBlock = false;
        _needsFrequencyRecount = true;
        EmitPendingPrefix();
        _nextCostRefresh = _tokens.Count < XMemCompressor.CostRefreshTokenInterval
            ? XMemCompressor.CostRefreshTokenInterval
            : _tokens.Count + XMemCompressor.CostRefreshTokenInterval;
    }

    private void EmitPendingPrefix()
    {
        if (_writeBlock == null)
        {
            throw new InvalidOperationException("Block output has not been configured.");
        }

        var count = _splitter.FindPrefix(_tokens, ref _splitCount);
        var prefix = new Token[count];
        _tokens.CopyTo(0, prefix, 0, count);
        var decodedLength = 0;
        var matchCount = 0;
        foreach (var token in prefix)
        {
            decodedLength += token.DecodedLength;
            if (token.Kind == TokenKind.Match)
            {
                matchCount++;
            }
        }

        _costs.Recount(_tokens, count);
        _costs.RebuildCosts();
        _writeBlock(prefix, _emittedBytes, _emittedBytes >= _earliestRetainedByte);

        // A record boundary starts a new allowance for distribution splits
        if (_emittedBytes / Constants.RecordDecodedCapacity !=
            (_emittedBytes + decodedLength) / Constants.RecordDecodedCapacity)
        {
            _splitCount = 0;
        }

        _emittedBytes += decodedLength;
        _matchCount -= matchCount;
        _tokens.RemoveRange(0, count);
    }

    private void RefreshCostsAfterPath()
    {
        if (_tokens.Count < _nextCostRefresh)
        {
            return;
        }

        // The first refresh recounts the complete pending token buffer.
        // Later refreshes add only tokens beyond the saved tally cursor.
        if (_needsFrequencyRecount)
        {
            _costs.Recount(_tokens, _tokens.Count);
            _needsFrequencyRecount = false;
        }
        else
        {
            _costs.AppendFrequencies(_tokens, _tokens.Count);
        }

        _costs.RebuildCosts();

        // Advance once per optimized path, even if that path crossed
        // several thresholds. The next path performs the next update.
        _nextCostRefresh += XMemCompressor.CostRefreshTokenInterval;
    }
}

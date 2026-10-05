using System;
using System.Collections.Generic;

namespace XnbCompress.Encoder;

internal sealed class MatchOptimizer
{
    private readonly byte[] _source;
    private readonly MatchFinder _finder;
    private readonly TokenCostModel _costs;

    // Each node describes the cheapest known path to an input position
    private ParseNode[] _nodes = Array.Empty<ParseNode>();
    private int _initializedNodes;
    private readonly List<Token> _path = new();

    public MatchOptimizer(byte[] source, MatchFinder finder, TokenCostModel costs)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _finder = finder ?? throw new ArgumentNullException(nameof(finder));
        _costs = costs ?? throw new ArgumentNullException(nameof(costs));
    }

    public void Reset()
    {
        _initializedNodes = 0;
        _path.Clear();
    }

    private void PrepareNodes(int remainingLength, RepeatOffsets repeats)
    {
        if (remainingLength < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(remainingLength));
        }

        // A search stops at the frontier limit, but its final match can extend past it.
        // Include that match and its endpoint, then reuse this bounded allocation
        // for later searches in this compression call.
        var requiredLength = Math.Min(remainingLength, XMemCompressor.ShortMatchSearchLimit + Constants.MaximumMatchLength) + 1;
        if (_nodes.Length < requiredLength)
        {
            _nodes = new ParseNode[requiredLength];
        }

        _initializedNodes = 0;
        InitializeNodesThrough(0);
        _nodes[0] = new ParseNode(0, 0, default, repeats);
    }

    private void InitializeNodesThrough(int destination)
    {
        // Reset costs before comparing paths, so stale nodes from an
        // earlier search cannot appear reachable in the current search.
        while (_initializedNodes <= destination)
        {
            _nodes[_initializedNodes].Cost = int.MaxValue;
            _initializedNodes++;
        }
    }

    public OptimizedPath FindShortPath(int position, int end, RepeatOffsets repeats, MatchCandidates candidates)
    {
        // Limit the search to submitted input, even though the source is complete
        if (end < 0 || end > _source.Length || end != _finder.AvailableLength)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        ReadOnlySpan<byte> source = _source.AsSpan(0, end);
        var matchLength = candidates.Length;
        ReadOnlySpan<int> distances = candidates.Distances;

        if ((uint)position >= (uint)source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        if (matchLength < Constants.MinimumMatchLength || matchLength >= XMemCompressor.LongMatchThreshold ||
            matchLength > source.Length - position)
        {
            throw new ArgumentOutOfRangeException(nameof(matchLength));
        }

        var remainingLength = source.Length - position;
        var nodeLimit = checked(remainingLength + 1);
        PrepareNodes(remainingLength, repeats);
        InitializeNodesThrough(matchLength);

        RelaxToken(0, Token.Literal(source[position]), nodeLimit);
        for (var length = Constants.MinimumMatchLength; length <= matchLength; length++)
        {
            RelaxToken(0, Token.Match(length, distances[length]), nodeLimit);
        }

        var furthest = matchLength;
        for (var at = 1; at < furthest; at++)
        {
            if (_nodes[at].Cost == int.MaxValue)
            {
                continue;
            }

            var nextCandidates = _finder.Find(position + at, _nodes[at].Repeats);

            // Once a long match or the search limit is reached, finish
            // with that match rather than expanding more alternatives.
            if (nextCandidates.Length > XMemCompressor.LongMatchThreshold ||
                at + nextCandidates.Length >= XMemCompressor.ShortMatchSearchLimit)
            {
                var encodedDistance = nextCandidates.Distances[nextCandidates.Length];
                _finder.InsertSkipped(position + at, nextCandidates.Length, encodedDistance);

                furthest = at + nextCandidates.Length;
                _nodes[furthest] = new ParseNode(
                    0, at, Token.Match(nextCandidates.Length, encodedDistance),
                    _nodes[at].Repeats.Apply(encodedDistance));
                break;
            }

            var reach = at + nextCandidates.Length;
            var extendsFrontier = nextCandidates.Length > Constants.MinimumMatchLength ||
                (nextCandidates.Length == Constants.MinimumMatchLength &&
                    nextCandidates.Distances[Constants.MinimumMatchLength] < XMemCompressor.TwoByteFrontierDistanceLimit);

            if (extendsFrontier && reach > furthest)
            {
                // A newly searchable frontier discards earlier fringe costs.
                // Reset these nodes even when they were already initialized.
                for (var destination = furthest + 1; destination <= reach; destination++)
                {
                    _nodes[destination].Cost = int.MaxValue;
                }

                _initializedNodes = Math.Max(_initializedNodes, reach + 1);
                furthest = reach;
            }

            RelaxToken(at, Token.Literal(source[position + at]), nodeLimit);
            for (var length = Constants.MinimumMatchLength; length <= nextCandidates.Length; length++)
            {
                RelaxToken(at, Token.Match(length, nextCandidates.Distances[length]), nodeLimit);
            }
        }

        // Follow predecessor links backwards, then restore input order.
        // Return borrowed storage so the caller can reserve block space
        // before committing any of the chosen tokens.
        _path.Clear();
        for (var at = furthest; at > 0; at = _nodes[at].Previous)
        {
            _path.Add(_nodes[at].Token);
        }

        _path.Reverse();
        return new OptimizedPath(_path, position + furthest, _nodes[furthest].Repeats);
    }

    private void RelaxToken(int at, Token token, int nodeLimit)
    {
        var destination = at + token.DecodedLength;
        if (destination >= nodeLimit)
        {
            return;
        }

        InitializeNodesThrough(destination);
        var cost = _nodes[at].Cost + _costs.GetTokenCost(token);

        // Preserve the earlier path when two alternatives have equal cost
        if (cost >= _nodes[destination].Cost)
        {
            return;
        }

        var repeats = token.Kind == TokenKind.Match
            ? _nodes[at].Repeats.Apply(token.EncodedDistance)
            : _nodes[at].Repeats;

        _nodes[destination] = new ParseNode(cost, at, token, repeats);
    }

    private struct ParseNode
    {
        public int Cost;
        public readonly int Previous;
        public readonly Token Token;
        public readonly RepeatOffsets Repeats;

        public ParseNode(int cost, int previous, Token token, RepeatOffsets repeats)
        {
            Cost = cost;
            Previous = previous;
            Token = token;
            Repeats = repeats;
        }
    }
}

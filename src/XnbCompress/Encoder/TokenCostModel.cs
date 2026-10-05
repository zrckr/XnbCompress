using System;
using System.Collections.Generic;

namespace XnbCompress.Encoder;

internal sealed class TokenCostModel
{
    private readonly byte[] _mainCosts;
    private readonly byte[] _lengthCosts = new byte[Constants.SecondaryLengthCount];

    // Parsing estimates are rebuilt independently of the trees used for output
    private readonly HuffmanTree _mainTree;
    private readonly HuffmanTree _lengthTree = new(Constants.SecondaryLengthCount);
    private readonly ushort[] _mainFrequencies;
    private readonly ushort[] _lengthFrequencies = new ushort[Constants.SecondaryLengthCount];
    private int _countedTokens;
    private Cache _cache;

    public TokenCostModel(XMemCompressor encoder)
    {
        _mainCosts = new byte[encoder.MainSymbolCount];
        _mainFrequencies = new ushort[encoder.MainSymbolCount];
        _mainTree = new HuffmanTree(encoder.MainSymbolCount);
        Reset();
    }

    public void Reset()
    {
        _countedTokens = 0;
        _cache = new Cache();

        Array.Clear(_mainFrequencies, 0, _mainFrequencies.Length);
        Array.Clear(_lengthFrequencies, 0, _lengthFrequencies.Length);

        // Before any tokens are counted, use fixed estimates for each alphabet
        Array.Fill(_mainCosts, (byte)8, 0, Constants.LiteralSymbolCount);
        Array.Fill(_mainCosts, (byte)9, Constants.LiteralSymbolCount, _mainCosts.Length - Constants.LiteralSymbolCount);
        Array.Fill(_lengthCosts, (byte)6);
        PreventFarMatches();
    }

    // ReSharper disable once UnusedMethodReturnValue.Global
    public int Recount(IReadOnlyList<Token> tokens, int end)
    {
        if (end < 0 || end > tokens.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        Array.Clear(_mainFrequencies, 0, _mainFrequencies.Length);
        Array.Clear(_lengthFrequencies, 0, _lengthFrequencies.Length);
        _countedTokens = 0;

        return AppendFrequencies(tokens, end);
    }

    public int AppendFrequencies(IReadOnlyList<Token> tokens, int end)
    {
        if (end < _countedTokens || end > tokens.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(end));
        }

        var decodedLength = 0;
        for (var index = _countedTokens; index < end; index++)
        {
            var token = tokens[index];
            var mainSymbol = TokenUtils.GetMainSymbol(token);
            _mainFrequencies[mainSymbol] = unchecked((ushort)(_mainFrequencies[mainSymbol] + 1));

            if (token is { Kind: TokenKind.Match, MatchLength: >= Constants.SecondaryMinimumLength })
            {
                var lengthSymbol = token.MatchLength - Constants.SecondaryMinimumLength;
                _lengthFrequencies[lengthSymbol] = unchecked((ushort)(_lengthFrequencies[lengthSymbol] + 1));
            }

            decodedLength += token.DecodedLength;
        }

        _countedTokens = end;
        return decodedLength;
    }

    public void RebuildCosts()
    {
        // A single used symbol needs a partner. Retain that frequency so
        // later cumulative updates start from the same two-symbol tree.
        InsertDummy(_mainFrequencies);
        InsertDummy(_lengthFrequencies);

        _mainTree.Build(_mainFrequencies);
        _lengthTree.Build(_lengthFrequencies);

        // Missing symbols still need a cost, otherwise an unused symbol
        // would look free when comparing possible token sequences.
        for (var symbol = 0; symbol < _mainCosts.Length; symbol++)
        {
            var length = _mainTree.Lengths[symbol];
            _mainCosts[symbol] = length != 0
                ? length
                : (byte)(symbol < Constants.LiteralSymbolCount ? 11 : 12);
        }

        for (var symbol = 0; symbol < _lengthCosts.Length; symbol++)
        {
            var length = _lengthTree.Lengths[symbol];
            _lengthCosts[symbol] = length != 0 ? length : (byte)8;
        }

        PreventFarMatches();
    }

    private void PreventFarMatches()
    {
        // Discourage two-byte matches whose distance needs at least 16 extra bits
        for (var symbol = Constants.LiteralSymbolCount + 34 * Constants.LengthCodesPerSlot;
            symbol < _mainCosts.Length; symbol += Constants.LengthCodesPerSlot)
        {
            _mainCosts[symbol] = 100;
        }
    }

    public int GetTokenCost(Token token)
    {
        if (token.Kind == TokenKind.Literal)
        {
            return _mainCosts[token.LiteralValue];
        }

        // Adjacent candidate lengths often share a distance. Its slot is
        // independent of coding trees, so retain it across cost rebuilds.
        if (token.EncodedDistance != _cache.Distance)
        {
            (_cache.Slot, _cache.ExtraBits) = TokenUtils.GetPositionSlot(token.EncodedDistance);
            _cache.Distance = token.EncodedDistance;
        }

        var cost = (int)_mainCosts[TokenUtils.GetMainSymbol(token, _cache.Slot)];

        // Match cost includes its distance bits and, for longer matches,
        // a second Huffman symbol describing the remaining length.
        cost += _cache.ExtraBits;
        if (token.MatchLength >= Constants.SecondaryMinimumLength)
        {
            cost += _lengthCosts[token.MatchLength - Constants.SecondaryMinimumLength];
        }

        return cost;
    }

    private static void InsertDummy(ushort[] frequencies)
    {
        var soleSymbol = -1;
        for (var symbol = 0; symbol < frequencies.Length; symbol++)
        {
            if (frequencies[symbol] == 0)
            {
                continue;
            }

            if (soleSymbol >= 0)
            {
                return;
            }

            soleSymbol = symbol;
        }

        if (soleSymbol >= 0)
        {
            frequencies[soleSymbol == 0 ? 1 : 0] = 1;
        }
    }

    private struct Cache()
    {
        public int Distance = 0;
        public int Slot = 0;
        public int ExtraBits = 0;
    }
}

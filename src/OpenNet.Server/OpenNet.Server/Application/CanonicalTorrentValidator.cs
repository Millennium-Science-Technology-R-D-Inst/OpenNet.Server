using System.Security.Cryptography;
using System.Text;

namespace OpenNet.Server.Application;

internal static class CanonicalTorrentValidator
{
    private const int CanonicalPieceLength = 1024 * 1024;
    private const int Bep52BlockLength = 16 * 1024;

    public static bool Validate(
        byte[] metainfo,
        long expectedSize,
        string expectedFileRootHex,
        string claimedInfoHashHex,
        out string error)
    {
        error = string.Empty;
        if (expectedSize <= 0)
        {
            error = "Canonical v2 metadata is not defined for empty content.";
            return false;
        }

        byte[] expectedRoot;
        try
        {
            expectedRoot = Convert.FromHexString(expectedFileRootHex);
        }
        catch (FormatException)
        {
            error = "The registered BEP 52 root is malformed.";
            return false;
        }

        if (expectedRoot.Length != 32)
        {
            error = "The registered BEP 52 root must be 32 bytes.";
            return false;
        }

        Node root;
        try
        {
            var parser = new Parser(metainfo);
            root = parser.Parse();
        }
        catch (FormatException exception)
        {
            error = "Invalid bencoded canonical torrent: " + exception.Message;
            return false;
        }

        try
        {
            if (root.Dictionary is null || root.Dictionary.Count != 2)
            {
                error = "Canonical torrent must contain exactly info and piece layers.";
                return false;
            }

            Node? info = FindAscii(root, "info");
            Node? pieceLayers = FindAscii(root, "piece layers");
            if (info?.Dictionary is null || pieceLayers?.Dictionary is null)
            {
                error = "Canonical torrent is missing info or piece layers.";
                return false;
            }

            string actualInfoHash = Convert.ToHexString(
                SHA256.HashData(
                    metainfo.AsSpan(info.Start, info.End - info.Start)))
                .ToLowerInvariant();
            if (!actualInfoHash.Equals(
                claimedInfoHashHex,
                StringComparison.OrdinalIgnoreCase))
            {
                error = "Canonical v2 info-hash does not match the bencoded info dictionary.";
                return false;
            }

            if (info.Dictionary.Count != 4
                || ReadInteger(info, "meta version") != 2
                || ReadInteger(info, "piece length") != CanonicalPieceLength
                || !ReadBytes(info, "name").AsSpan()
                    .SequenceEqual("OpenNet.Content.v1"u8))
            {
                error = "Canonical info dictionary does not match OpenNet.Content.v1.";
                return false;
            }

            Node? fileTree = FindAscii(info, "file tree");
            if (fileTree?.Dictionary is null || fileTree.Dictionary.Count != 1)
            {
                error = "Canonical file tree must contain exactly one file.";
                return false;
            }

            Entry fileEntry = fileTree.Dictionary[0];
            if (!fileEntry.Key.AsSpan().SequenceEqual("content"u8)
                || fileEntry.Value.Dictionary is not { Count: 1 } fileNode
                || fileNode[0].Key.Length != 0
                || fileNode[0].Value.Dictionary is not { Count: 2 })
            {
                error = "Canonical file path must be OpenNet.Content.v1/content.";
                return false;
            }

            Node leafNode = fileNode[0].Value;
            if (ReadInteger(leafNode, "length") != expectedSize)
            {
                error = "Canonical torrent file size does not match the registered content.";
                return false;
            }

            byte[] piecesRoot = ReadBytes(leafNode, "pieces root");
            if (!piecesRoot.AsSpan().SequenceEqual(expectedRoot))
            {
                error = "Canonical torrent pieces root does not match the registered BEP 52 identity.";
                return false;
            }

            long expectedPieces64 =
                1 + ((expectedSize - 1) / CanonicalPieceLength);
            if (expectedPieces64 > int.MaxValue)
            {
                error = "Canonical content contains too many pieces.";
                return false;
            }

            int expectedPieces = (int)expectedPieces64;
            if (expectedPieces == 1)
            {
                if (pieceLayers.Dictionary.Count != 0)
                {
                    error = "Single-piece canonical content must not carry a piece layer.";
                    return false;
                }

                return true;
            }

            if (pieceLayers.Dictionary.Count != 1)
            {
                error = "Multi-piece canonical content must carry exactly one piece layer.";
                return false;
            }

            Entry layer = pieceLayers.Dictionary[0];
            long expectedLayerBytes = (long)expectedPieces * 32;
            if (!layer.Key.AsSpan().SequenceEqual(expectedRoot)
                || layer.Value.Bytes is not { } layerBytes
                || layerBytes.LongLength != expectedLayerBytes)
            {
                error = "Canonical piece layer key or size is inconsistent.";
                return false;
            }

            var pieceRoots = new byte[expectedPieces][];
            for (int index = 0; index < expectedPieces; ++index)
            {
                pieceRoots[index] =
                    layerBytes.AsSpan(index * 32, 32).ToArray();
            }

            byte[] computedRoot = MerkleRoot(
                pieceRoots,
                PieceLayerPadHash());
            if (!computedRoot.AsSpan().SequenceEqual(expectedRoot))
            {
                error = "Canonical piece layer does not resolve to the registered BEP 52 root.";
                return false;
            }

            return true;
        }
        catch (Exception exception) when (
            exception is FormatException
            or OverflowException
            or ArgumentException)
        {
            error = "Invalid canonical torrent: " + exception.Message;
            return false;
        }
    }

    private static long ReadInteger(Node dictionary, string key)
    {
        Node? node = FindAscii(dictionary, key);
        if (node?.Integer is not long value)
        {
            throw new FormatException($"Missing integer field '{key}'.");
        }

        return value;
    }

    private static byte[] ReadBytes(Node dictionary, string key)
    {
        Node? node = FindAscii(dictionary, key);
        if (node?.Bytes is not { } value)
        {
            throw new FormatException($"Missing byte-string field '{key}'.");
        }

        return value;
    }

    private static Node? FindAscii(Node dictionary, string key)
    {
        if (dictionary.Dictionary is null)
        {
            return null;
        }

        ReadOnlySpan<byte> wanted = Encoding.ASCII.GetBytes(key);
        foreach (Entry entry in dictionary.Dictionary)
        {
            if (entry.Key.AsSpan().SequenceEqual(wanted))
            {
                return entry.Value;
            }
        }

        return null;
    }

    private static byte[] PieceLayerPadHash()
    {
        byte[] pad = new byte[32];
        for (int pieces = 1;
            pieces < CanonicalPieceLength / Bep52BlockLength;
            pieces *= 2)
        {
            pad = HashPair(pad, pad);
        }

        return pad;
    }

    private static byte[] MerkleRoot(
        IReadOnlyList<byte[]> leaves,
        byte[] initialPad)
    {
        if (leaves.Count == 0)
        {
            throw new ArgumentException("Merkle tree requires at least one leaf.");
        }

        int targetLeaves = 1;
        while (targetLeaves < leaves.Count)
        {
            targetLeaves <<= 1;
        }

        var level = leaves.Select(leaf => leaf.ToArray()).ToList();
        byte[] pad = initialPad.ToArray();
        int logicalLeaves = targetLeaves;

        while (logicalLeaves > 1)
        {
            var next = new List<byte[]>((level.Count + 1) / 2);
            int index = 0;
            for (; index + 1 < level.Count; index += 2)
            {
                next.Add(HashPair(level[index], level[index + 1]));
            }

            if (index < level.Count)
            {
                next.Add(HashPair(level[index], pad));
            }

            pad = HashPair(pad, pad);
            level = next;
            logicalLeaves >>= 1;
        }

        if (level.Count != 1)
        {
            throw new FormatException("Invalid canonical Merkle layer.");
        }

        return level[0];
    }

    private static byte[] HashPair(
        ReadOnlySpan<byte> left,
        ReadOnlySpan<byte> right)
    {
        Span<byte> input = stackalloc byte[64];
        left.CopyTo(input[..32]);
        right.CopyTo(input[32..]);
        return SHA256.HashData(input);
    }

    private sealed class Node
    {
        public required int Start { get; init; }
        public required int End { get; init; }
        public long? Integer { get; init; }
        public byte[]? Bytes { get; init; }
        public List<Node>? List { get; init; }
        public List<Entry>? Dictionary { get; init; }
    }

    private sealed record Entry(byte[] Key, Node Value);

    private sealed class Parser(byte[] data)
    {
        private int offset;
        private int nodes;

        public Node Parse()
        {
            Node root = ReadNode(0);
            if (offset != data.Length)
            {
                throw new FormatException("Trailing bytes after root value.");
            }

            return root;
        }

        private Node ReadNode(int depth)
        {
            if (depth > 64)
            {
                throw new FormatException("Bencode nesting exceeds 64 levels.");
            }

            if (++nodes > 1_000_000)
            {
                throw new FormatException("Bencode contains too many nodes.");
            }

            if (offset >= data.Length)
            {
                throw new FormatException("Unexpected end of input.");
            }

            int start = offset;
            byte token = data[offset];
            if (token == (byte)'i')
            {
                ++offset;
                long integer = ReadIntegerToken();
                return new Node
                {
                    Start = start,
                    End = offset,
                    Integer = integer
                };
            }

            if (token == (byte)'l')
            {
                ++offset;
                var list = new List<Node>();
                while (!TryConsumeEnd())
                {
                    list.Add(ReadNode(depth + 1));
                }

                return new Node
                {
                    Start = start,
                    End = offset,
                    List = list
                };
            }

            if (token == (byte)'d')
            {
                ++offset;
                var dictionary = new List<Entry>();
                byte[]? previousKey = null;
                while (!TryConsumeEnd())
                {
                    byte[] key = ReadByteString();
                    if (previousKey is not null
                        && CompareBytes(previousKey, key) >= 0)
                    {
                        throw new FormatException(
                            "Dictionary keys are not strictly sorted.");
                    }

                    previousKey = key;
                    dictionary.Add(new Entry(
                        key,
                        ReadNode(depth + 1)));
                }

                return new Node
                {
                    Start = start,
                    End = offset,
                    Dictionary = dictionary
                };
            }

            if (token is >= (byte)'0' and <= (byte)'9')
            {
                byte[] bytes = ReadByteString();
                return new Node
                {
                    Start = start,
                    End = offset,
                    Bytes = bytes
                };
            }

            throw new FormatException(
                $"Unexpected bencode token 0x{token:x2}.");
        }

        private bool TryConsumeEnd()
        {
            if (offset >= data.Length)
            {
                throw new FormatException("Unterminated container.");
            }

            if (data[offset] != (byte)'e')
            {
                return false;
            }

            ++offset;
            return true;
        }

        private long ReadIntegerToken()
        {
            int start = offset;
            while (offset < data.Length && data[offset] != (byte)'e')
            {
                ++offset;
            }

            if (offset >= data.Length)
            {
                throw new FormatException("Unterminated integer.");
            }

            ReadOnlySpan<byte> token = data.AsSpan(start, offset - start);
            ++offset;
            if (token.IsEmpty
                || token[0] == (byte)'+'
                || (token.Length > 1 && token[0] == (byte)'0')
                || (token.Length >= 2
                    && token[0] == (byte)'-'
                    && token[1] == (byte)'0')
                || (token[0] == (byte)'-' && token.Length == 1))
            {
                throw new FormatException("Non-canonical integer.");
            }

            for (int index = token[0] == (byte)'-' ? 1 : 0;
                index < token.Length;
                ++index)
            {
                if (token[index] is < (byte)'0' or > (byte)'9')
                {
                    throw new FormatException("Invalid integer.");
                }
            }

            string text = Encoding.ASCII.GetString(token);
            if (!long.TryParse(
                text,
                System.Globalization.NumberStyles.AllowLeadingSign,
                System.Globalization.CultureInfo.InvariantCulture,
                out long value))
            {
                throw new FormatException("Invalid integer.");
            }

            return value;
        }

        private byte[] ReadByteString()
        {
            int lengthStart = offset;
            while (offset < data.Length
                && data[offset] is >= (byte)'0' and <= (byte)'9')
            {
                ++offset;
            }

            if (offset == lengthStart
                || offset >= data.Length
                || data[offset] != (byte)':')
            {
                throw new FormatException("Invalid byte-string length.");
            }

            ReadOnlySpan<byte> lengthToken =
                data.AsSpan(lengthStart, offset - lengthStart);
            if (lengthToken.Length > 1
                && lengthToken[0] == (byte)'0')
            {
                throw new FormatException("Non-canonical byte-string length.");
            }

            if (!int.TryParse(
                Encoding.ASCII.GetString(lengthToken),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int length)
                || length < 0)
            {
                throw new FormatException("Invalid byte-string length.");
            }

            ++offset;
            if (length > data.Length - offset)
            {
                throw new FormatException("Byte string exceeds input.");
            }

            byte[] value = data.AsSpan(offset, length).ToArray();
            offset += length;
            return value;
        }

        private static int CompareBytes(
            ReadOnlySpan<byte> left,
            ReadOnlySpan<byte> right)
        {
            int length = Math.Min(left.Length, right.Length);
            for (int index = 0; index < length; ++index)
            {
                int comparison = left[index].CompareTo(right[index]);
                if (comparison != 0)
                {
                    return comparison;
                }
            }

            return left.Length.CompareTo(right.Length);
        }
    }
}

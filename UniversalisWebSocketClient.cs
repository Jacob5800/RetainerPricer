using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

namespace RetainerPricer;

internal sealed record UniversalisListing(uint ItemId, uint WorldId, bool IsHq, uint PricePerUnit,
    uint Quantity, string? ListingId, bool IsRemoved = false);

/// <summary>Small BSON WebSocket adapter for Universalis' read-only live listing feed.</summary>
internal sealed class UniversalisWebSocketClient : IAsyncDisposable
{
    private const int MaximumMessageBytes = 2 * 1024 * 1024;
    private readonly ClientWebSocket socket = new();
    private int subscriptionId;

    public WebSocketState State => socket.State;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        await socket.ConnectAsync(new Uri("wss://universalis.app/api/ws"), cancellationToken).ConfigureAwait(false);
    }

    public async Task SubscribeAsync(uint worldId, uint itemId, string change, CancellationToken cancellationToken)
    {
        var channel = $"listings/{change}{{world={worldId},item={itemId}}}";
        await socket.SendAsync(new ArraySegment<byte>(BsonCodec.WriteSubscribe(channel, ++subscriptionId)),
            WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task SubscribeWorldAsync(uint worldId, string change, CancellationToken cancellationToken)
    {
        var channel = $"listings/{change}{{world={worldId}}}";
        await socket.SendAsync(new ArraySegment<byte>(BsonCodec.WriteSubscribe(channel, ++subscriptionId)),
            WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UniversalisListing>?> ReceiveListingsAsync(CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(chunk), cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Binary)
                throw new InvalidOperationException("Universalis sent an unsupported live-feed message.");
            if (buffer.Length + result.Count > MaximumMessageBytes)
                throw new InvalidOperationException("Universalis sent an oversized live-feed message.");
            buffer.Write(chunk, 0, result.Count);
        } while (!result.EndOfMessage);

        var message = BsonCodec.ReadDocument(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        var eventName = String(message, "event");
        var removed = eventName.Equals("listings/remove", StringComparison.OrdinalIgnoreCase);
        if (!removed && !eventName.Equals("listings/add", StringComparison.OrdinalIgnoreCase)) return [];
        var itemId = UInt(message, "item");
        var worldId = UInt(message, "world");
        if (!message.TryGetValue("listings", out var listingsValue) || listingsValue is not List<object?> listings)
            return [];

        var resultListings = new List<UniversalisListing>(listings.Count);
        foreach (var listingValue in listings)
        {
            if (listingValue is not Dictionary<string, object?> listing) continue;
            var price = UInt(listing, "pricePerUnit");
            var quantity = UInt(listing, "quantity");
            if (price == 0 || quantity == 0 || !listing.TryGetValue("hq", out var hqValue) || hqValue is not bool isHq)
                continue;
            var listingId = listing.TryGetValue("listingID", out var idValue) ? idValue as string : null;
            resultListings.Add(new UniversalisListing(itemId, worldId, isHq, price, quantity, listingId, removed));
        }
        return resultListings;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Sniper stopped", CancellationToken.None)
                    .ConfigureAwait(false);
        }
        catch { }
        socket.Dispose();
    }

    private static uint UInt(IReadOnlyDictionary<string, object?> document, string name)
    {
        if (!document.TryGetValue(name, out var value)) return 0;
        return value switch
        {
            int number when number > 0 => (uint)number,
            long number when number is > 0 and <= uint.MaxValue => (uint)number,
            _ => 0,
        };
    }

    private static string String(IReadOnlyDictionary<string, object?> document, string name) =>
        document.TryGetValue(name, out var value) ? value as string ?? string.Empty : string.Empty;

    private static class BsonCodec
    {
        public static byte[] WriteSubscribe(string channel, int id)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
            writer.Write(0);
            WriteStringElement(writer, "event", "subscribe");
            WriteStringElement(writer, "channel", channel);
            WriteInt32Element(writer, "id", id);
            writer.Write((byte)0);
            var bytes = stream.ToArray();
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0, sizeof(int)), bytes.Length);
            return bytes;
        }

        public static Dictionary<string, object?> ReadDocument(ReadOnlySpan<byte> bytes)
        {
            var offset = 0;
            var result = ReadDocumentCore(bytes, ref offset, 0);
            if (offset != bytes.Length) throw new InvalidDataException("Trailing data in Universalis BSON message.");
            return result;
        }

        private static Dictionary<string, object?> ReadDocumentCore(ReadOnlySpan<byte> bytes, ref int offset, int depth)
        {
            if (depth > 16) throw new InvalidDataException("Universalis BSON nesting is too deep.");
            var start = offset;
            var length = ReadInt32(bytes, ref offset);
            if (length < 5 || length > bytes.Length - start) throw new InvalidDataException("Invalid Universalis BSON length.");
            var end = start + length;
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            while (offset < end - 1)
            {
                var type = ReadByte(bytes, ref offset);
                var name = ReadCString(bytes, ref offset, end);
                result[name] = ReadValue(type, bytes, ref offset, end, depth + 1);
            }
            if (offset != end - 1 || ReadByte(bytes, ref offset) != 0)
                throw new InvalidDataException("Invalid Universalis BSON terminator.");
            return result;
        }

        private static object? ReadValue(byte type, ReadOnlySpan<byte> bytes, ref int offset, int end, int depth) => type switch
        {
            0x01 => ReadDouble(bytes, ref offset),
            0x02 => ReadString(bytes, ref offset, end),
            0x03 => ReadDocumentCore(bytes, ref offset, depth),
            0x04 => ReadArray(bytes, ref offset, depth),
            0x05 => ReadBinary(bytes, ref offset),
            0x06 or 0x0A or 0x7F or 0xFF => null,
            0x07 => SkipAndNull(bytes, ref offset, 12),
            0x08 => ReadBool(bytes, ref offset),
            0x09 or 0x11 or 0x12 => ReadInt64(bytes, ref offset),
            0x0B => ReadRegex(bytes, ref offset, end),
            0x0D or 0x0E => ReadString(bytes, ref offset, end),
            0x0F => SkipCodeWithScope(bytes, ref offset, end),
            0x10 => ReadInt32(bytes, ref offset),
            0x13 => SkipAndNull(bytes, ref offset, 16),
            _ => throw new InvalidDataException($"Unsupported BSON type 0x{type:x2} from Universalis."),
        };

        private static List<object?> ReadArray(ReadOnlySpan<byte> bytes, ref int offset, int depth) =>
            ReadDocumentCore(bytes, ref offset, depth).OrderBy(pair => int.TryParse(pair.Key, out var index) ? index : int.MaxValue)
                .Select(pair => pair.Value).ToList();

        private static byte[] ReadBinary(ReadOnlySpan<byte> bytes, ref int offset)
        {
            var length = ReadInt32(bytes, ref offset);
            _ = ReadByte(bytes, ref offset);
            if (length < 0 || length > bytes.Length - offset) throw new InvalidDataException("Invalid BSON binary length.");
            var result = bytes.Slice(offset, length).ToArray();
            offset += length;
            return result;
        }

        private static bool ReadBool(ReadOnlySpan<byte> bytes, ref int offset) => ReadByte(bytes, ref offset) switch
        {
            0 => false,
            1 => true,
            _ => throw new InvalidDataException("Invalid BSON boolean."),
        };

        private static string ReadString(ReadOnlySpan<byte> bytes, ref int offset, int end)
        {
            var length = ReadInt32(bytes, ref offset);
            if (length < 1 || length > end - offset || bytes[offset + length - 1] != 0)
                throw new InvalidDataException("Invalid BSON string.");
            var value = Encoding.UTF8.GetString(bytes.Slice(offset, length - 1));
            offset += length;
            return value;
        }

        private static string ReadCString(ReadOnlySpan<byte> bytes, ref int offset, int end)
        {
            var terminator = bytes.Slice(offset, end - offset).IndexOf((byte)0);
            if (terminator < 0) throw new InvalidDataException("Invalid BSON field name.");
            var value = Encoding.UTF8.GetString(bytes.Slice(offset, terminator));
            offset += terminator + 1;
            return value;
        }

        private static string ReadRegex(ReadOnlySpan<byte> bytes, ref int offset, int end)
        {
            _ = ReadCString(bytes, ref offset, end);
            return ReadCString(bytes, ref offset, end);
        }

        private static object? SkipCodeWithScope(ReadOnlySpan<byte> bytes, ref int offset, int end)
        {
            var totalLength = ReadInt32(bytes, ref offset);
            if (totalLength < 4 || totalLength - 4 > end - offset) throw new InvalidDataException("Invalid BSON code-with-scope.");
            offset += totalLength - 4;
            return null;
        }

        private static object? SkipAndNull(ReadOnlySpan<byte> bytes, ref int offset, int count)
        {
            Ensure(bytes, offset, count);
            offset += count;
            return null;
        }

        private static double ReadDouble(ReadOnlySpan<byte> bytes, ref int offset)
        {
            Ensure(bytes, offset, sizeof(double));
            var value = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(offset, sizeof(long))));
            offset += sizeof(double);
            return value;
        }

        private static int ReadInt32(ReadOnlySpan<byte> bytes, ref int offset)
        {
            Ensure(bytes, offset, sizeof(int));
            var value = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, sizeof(int)));
            offset += sizeof(int);
            return value;
        }

        private static long ReadInt64(ReadOnlySpan<byte> bytes, ref int offset)
        {
            Ensure(bytes, offset, sizeof(long));
            var value = BinaryPrimitives.ReadInt64LittleEndian(bytes.Slice(offset, sizeof(long)));
            offset += sizeof(long);
            return value;
        }

        private static byte ReadByte(ReadOnlySpan<byte> bytes, ref int offset)
        {
            Ensure(bytes, offset, 1);
            return bytes[offset++];
        }

        private static void Ensure(ReadOnlySpan<byte> bytes, int offset, int count)
        {
            if (count < 0 || offset < 0 || offset > bytes.Length - count)
                throw new InvalidDataException("Truncated Universalis BSON message.");
        }

        private static void WriteStringElement(BinaryWriter writer, string name, string value)
        {
            writer.Write((byte)0x02);
            WriteCString(writer, name);
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write(bytes.Length + 1);
            writer.Write(bytes);
            writer.Write((byte)0);
        }

        private static void WriteInt32Element(BinaryWriter writer, string name, int value)
        {
            writer.Write((byte)0x10);
            WriteCString(writer, name);
            writer.Write(value);
        }

        private static void WriteCString(BinaryWriter writer, string value)
        {
            writer.Write(Encoding.UTF8.GetBytes(value));
            writer.Write((byte)0);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RetainerPricer;

/// <summary>Fetches one item on one world when requested. Never uploads or polls.</summary>
public sealed class UniversalisClient : IDisposable
{
    private const int MaximumResponseBytes = 1_048_576;
    private const int HistoryEntryLimit = 100;
    private const int HistoryWindowSeconds = 20 * 24 * 60 * 60;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient client;
    private readonly bool ownsClient;

    public UniversalisClient() : this(new HttpClient(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
    }) { Timeout = Timeout.InfiniteTimeSpan }, true) { }

    // The caller owns an injected client; useful for deterministic HTTP checks.
    public UniversalisClient(HttpClient client) : this(client, false) { }

    private UniversalisClient(HttpClient client, bool ownsClient)
    {
        this.client = client ?? throw new ArgumentNullException(nameof(client));
        this.ownsClient = ownsClient;
    }

    public async Task<PriceSnapshot> FetchAsync(uint worldId, uint itemId, CancellationToken ct = default)
    {
        if (worldId == 0 || itemId == 0)
            throw new ArgumentException("Select a valid item and selling world before fetching prices.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            // https://docs.universalis.app/ — current listings and sales in the previous 20 days.
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"https://universalis.app/api/v2/{worldId}/{itemId}?entries={HistoryEntryLimit}&entriesWithin={HistoryWindowSeconds}");
            request.Headers.UserAgent.ParseAdd("RetainerPricer/0.1");
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta;
                var advice = wait is { TotalSeconds: > 0 }
                    ? $" Try again in {Math.Ceiling(wait.Value.TotalSeconds):N0} seconds."
                    : " Wait a moment, then try again.";
                throw new InvalidOperationException("Universalis has temporarily limited requests." + advice);
            }
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("Universalis has no data for this item and world.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Universalis returned HTTP {(int)response.StatusCode}. Try again later.");
            if (response.Content.Headers.ContentLength > MaximumResponseBytes)
                throw new InvalidOperationException("Universalis returned too much market data. Try this item again later.");

            await using var source = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16_384];
            while (true)
            {
                var read = await source.ReadAsync(chunk, deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumResponseBytes)
                    throw new InvalidOperationException("Universalis returned too much market data. Try this item again later.");
                buffer.Write(chunk, 0, read);
            }
            return Parse(buffer.ToArray(), worldId, itemId);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Universalis did not respond in time. Try again later.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Could not reach Universalis. Check your connection and try again.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("The Universalis response was interrupted. Fetch prices again.", ex);
        }
    }

    private static PriceSnapshot Parse(byte[] json, uint worldId, uint itemId)
    {
        static InvalidOperationException Invalid(string detail) => new("Universalis returned unusable market data: " + detail);
        static uint UInt(JsonElement row, string property)
        {
            if (!row.TryGetProperty(property, out var value) || !value.TryGetUInt32(out var number))
                throw Invalid($"missing or invalid {property}.");
            return number;
        }
        static bool Bool(JsonElement row, string property)
        {
            if (!row.TryGetProperty(property, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw Invalid($"missing or invalid {property}.");
            return value.GetBoolean();
        }

        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw Invalid("expected an item response.");
            if (UInt(root, "itemID") != itemId || UInt(root, "worldID") != worldId)
                throw Invalid("the item or world does not match the request.");
            if (root.TryGetProperty("hasData", out var hasData) && hasData.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("Universalis has not received market data for this item and world yet.");
            if (!root.TryGetProperty("lastUploadTime", out var upload) || !upload.TryGetInt64(out var milliseconds) || milliseconds <= 0)
                throw Invalid("missing market upload time.");
            var observedAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            if (!root.TryGetProperty("listings", out var listings) || listings.ValueKind != JsonValueKind.Array)
                throw Invalid("missing listings.");
            var totalCount = UInt(root, "listingsCount");
            if (totalCount < listings.GetArrayLength()) throw Invalid("inconsistent listing count.");

            var results = new List<MarketListing>(listings.GetArrayLength());
            foreach (var listing in listings.EnumerateArray())
            {
                if (listing.ValueKind != JsonValueKind.Object) throw Invalid("invalid listing.");
                var price = UInt(listing, "pricePerUnit");
                var quantity = UInt(listing, "quantity");
                if (price is 0 or > PriceCalculator.MaximumPrice || quantity == 0) throw Invalid("invalid listing price or quantity.");
                if (listing.TryGetProperty("worldID", out _) && UInt(listing, "worldID") != worldId)
                    throw Invalid("a listing belongs to another world.");
                if (listing.TryGetProperty("itemID", out _) && UInt(listing, "itemID") != itemId)
                    throw Invalid("a listing belongs to another item.");
                // Universalis documents retainerID as optional. Keep the market response readable
                // when it is absent; PriceCalculator blocks a quote if an unknown owner could affect it.
                ulong retainerId = 0;
                if (listing.TryGetProperty("retainerID", out var retainer) && retainer.ValueKind != JsonValueKind.Null)
                {
                    var validRetainer = retainer.ValueKind switch
                    {
                        JsonValueKind.String => ulong.TryParse(retainer.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out retainerId),
                        JsonValueKind.Number => retainer.TryGetUInt64(out retainerId),
                        _ => false,
                    };
                    if (!validRetainer || retainerId == 0) retainerId = 0;
                }
                results.Add(new MarketListing(itemId, Bool(listing, "hq"), price, quantity, retainerId, Bool(listing, "onMannequin")));
            }

            DateTimeOffset? mostRecentSaleAt = null;
            if (root.TryGetProperty("recentHistory", out var recentHistory) && recentHistory.ValueKind != JsonValueKind.Null)
            {
                if (recentHistory.ValueKind != JsonValueKind.Array)
                    throw Invalid("invalid recent sale history.");
                foreach (var sale in recentHistory.EnumerateArray())
                {
                    if (sale.ValueKind != JsonValueKind.Object || !sale.TryGetProperty("timestamp", out var timestamp)
                        || !timestamp.TryGetInt64(out var seconds) || seconds <= 0)
                        throw Invalid("invalid recent sale timestamp.");
                    var soldAt = DateTimeOffset.FromUnixTimeSeconds(seconds);
                    if (mostRecentSaleAt is null || soldAt > mostRecentSaleAt.Value)
                        mostRecentSaleAt = soldAt;
                }
            }

            // Use upload age, never request completion time, to detect stale cached prices.
            return new(itemId, worldId, PriceSource.Universalis, observedAt, results.AsReadOnly(),
                totalCount == results.Count, mostRecentSaleAt);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Universalis returned malformed JSON. Try again later.", ex);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new InvalidOperationException("Universalis returned an invalid market timestamp. Try again later.", ex);
        }
        catch (InvalidOperationException ex) when (!ex.Message.StartsWith("Universalis", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Universalis returned malformed market fields. Try again later.", ex);
        }
    }

    public void Dispose()
    {
        if (ownsClient) client.Dispose();
    }
}

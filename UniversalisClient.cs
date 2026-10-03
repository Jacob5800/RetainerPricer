using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RetainerPricer;

/// <summary>Fetches one item for a requested world or data center. Never uploads or polls.</summary>
public sealed class UniversalisClient : IDisposable
{
    private enum ScopeKind { World, DataCenter, Region }

    private const int MaximumResponseBytes = 1_048_576;
    private const int HistoryEntryLimit = 100;
    private const int HistoryWindowSeconds = 20 * 24 * 60 * 60;
    private const int MaximumCachedItems = 512;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly object cacheLock = new();
    private readonly Dictionary<(string Scope, uint Item), PriceSnapshot> cache = [];
    private readonly SemaphoreSlim dataCenterRegionLock = new(1, 1);
    private IReadOnlyDictionary<string, string>? dataCenterRegions;

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

    public async Task<PriceSnapshot> FetchAsync(uint worldId, uint itemId, CancellationToken ct = default,
        int cacheMinutes = 5, string? dataCenterName = null, bool useRegionPrices = false)
    {
        if (worldId == 0 || itemId == 0)
            throw new ArgumentException("Select a valid item and selling world before fetching prices.");
        if (dataCenterName is { Length: 0 }) dataCenterName = null;
        if (dataCenterName is { Length: > 64 } || dataCenterName?.Contains('/') == true)
            throw new ArgumentException("The home world's data-center name is invalid.");
        if (useRegionPrices && dataCenterName is null)
            throw new ArgumentException("The home world's data center is required to identify its region.");
        ct.ThrowIfCancellationRequested();

        var cacheAgeLimit = TimeSpan.FromMinutes(Math.Clamp(cacheMinutes, 0, 60));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(RequestTimeout);
        try
        {
            string? homeRegion = null;
            string? regionalScope = null;
            if (useRegionPrices)
            {
                homeRegion = await GetRegionForDataCenterAsync(dataCenterName!, deadline.Token).ConfigureAwait(false);
                regionalScope = StringComparer.OrdinalIgnoreCase.Equals(homeRegion, "Oceania")
                    ? "Oceania (Materia)"
                    : $"{homeRegion} + Oceania (Materia)";
            }

            var scopeKey = useRegionPrices
                ? $"region:{regionalScope!.ToUpperInvariant()}:{dataCenterName!.ToUpperInvariant()}"
                : dataCenterName is null ? $"world:{worldId}" : $"dc:{dataCenterName.ToUpperInvariant()}";
            var key = (scopeKey, itemId);
            if (cacheAgeLimit > TimeSpan.Zero)
            {
                lock (cacheLock)
                {
                    if (cache.TryGetValue(key, out var cached) && cached.RetrievedAt is { } retrievedAt)
                    {
                        var age = DateTimeOffset.UtcNow - retrievedAt;
                        if (age >= TimeSpan.Zero && age <= cacheAgeLimit)
                            return cached with { WasCached = true };
                        cache.Remove(key);
                    }
                }
            }

            PriceSnapshot result;
            if (useRegionPrices)
            {
                var scopes = StringComparer.OrdinalIgnoreCase.Equals(homeRegion, "Oceania")
                    ? new[] { "Oceania" }
                    : new[] { homeRegion!, "Oceania" };
                // Require complete data from both markets so an unavailable Materia/home-region
                // response can never silently produce a misleading "lowest in region" price.
                var parts = await Task.WhenAll(scopes.Select(region =>
                    FetchScopeAsync(worldId, itemId, ScopeKind.Region, region, deadline.Token))).ConfigureAwait(false);
                result = CombineRegions(worldId, itemId, dataCenterName!, regionalScope!, parts);
            }
            else
            {
                var kind = dataCenterName is null ? ScopeKind.World : ScopeKind.DataCenter;
                var scopeName = dataCenterName ?? worldId.ToString(CultureInfo.InvariantCulture);
                result = await FetchScopeAsync(worldId, itemId, kind, scopeName, deadline.Token).ConfigureAwait(false);
            }

            result = result with { RetrievedAt = DateTimeOffset.UtcNow, WasCached = false };
            lock (cacheLock)
            {
                if (!cache.ContainsKey(key) && cache.Count >= MaximumCachedItems)
                {
                    var oldestKey = cache.MinBy(entry => entry.Value.RetrievedAt ?? DateTimeOffset.MinValue).Key;
                    cache.Remove(oldestKey);
                }
                cache[key] = result;
            }
            return result;
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

    private async Task<string> GetRegionForDataCenterAsync(string dataCenterName, CancellationToken ct)
    {
        await dataCenterRegionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (dataCenterRegions is null)
            {
                var json = await RequestJsonAsync("data-centers", "Data Center information", ct).ConfigureAwait(false);
                try
                {
                    using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("Universalis returned invalid Data Center information.");
                    var regions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in document.RootElement.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object ||
                            !entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                            !entry.TryGetProperty("region", out var region) || region.ValueKind != JsonValueKind.String)
                            throw new InvalidOperationException("Universalis returned incomplete Data Center information.");
                        var dcName = name.GetString();
                        var regionValue = region.GetString();
                        if (string.IsNullOrWhiteSpace(dcName) || string.IsNullOrWhiteSpace(regionValue))
                            throw new InvalidOperationException("Universalis returned incomplete Data Center information.");
                        regions[dcName] = regionValue;
                    }
                    if (regions.Count == 0)
                        throw new InvalidOperationException("Universalis returned no Data Center information.");
                    dataCenterRegions = regions;
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("Universalis returned malformed Data Center information.", ex);
                }
            }

            if (!dataCenterRegions.TryGetValue(dataCenterName, out var regionName))
                throw new InvalidOperationException($"Universalis could not map the home Data Center '{dataCenterName}' to a region.");
            return regionName;
        }
        finally { dataCenterRegionLock.Release(); }
    }

    private async Task<PriceSnapshot> FetchScopeAsync(uint worldId, uint itemId, ScopeKind kind,
        string scopeName, CancellationToken ct)
    {
        // https://docs.universalis.app/ — current listings and sales in the previous 20 days.
        var escapedScope = Uri.EscapeDataString(scopeName);
        var json = await RequestJsonAsync($"{escapedScope}/{itemId}?entries={HistoryEntryLimit}&entriesWithin={HistoryWindowSeconds}",
            scopeName, ct).ConfigureAwait(false);
        return Parse(json, worldId, itemId, kind, scopeName);
    }

    private async Task<byte[]> RequestJsonAsync(string path, string scopeDescription, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://universalis.app/api/v2/{path}");
        request.Headers.UserAgent.ParseAdd("RetainerPricer/0.1");
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var wait = response.Headers.RetryAfter?.Delta;
            var advice = wait is { TotalSeconds: > 0 }
                ? $" Try again in {Math.Ceiling(wait.Value.TotalSeconds):N0} seconds."
                : " Wait a moment, then try again.";
            throw new InvalidOperationException("Universalis has temporarily limited requests." + advice);
        }
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new InvalidOperationException($"Universalis has no market data for {scopeDescription}.");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Universalis returned HTTP {(int)response.StatusCode} for {scopeDescription}. Try again later.");
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidOperationException($"Universalis returned too much market data for {scopeDescription}. Try again later.");

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16_384];
        while (true)
        {
            var read = await source.ReadAsync(chunk, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidOperationException($"Universalis returned too much market data for {scopeDescription}. Try again later.");
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static PriceSnapshot CombineRegions(uint worldId, uint itemId, string homeDataCenter,
        string regionalScope, IReadOnlyList<PriceSnapshot> snapshots)
    {
        var recentSales = snapshots.Where(snapshot => snapshot.MostRecentSaleAt.HasValue)
            .Select(snapshot => snapshot.MostRecentSaleAt!.Value).ToArray();
        return new PriceSnapshot(itemId, worldId, PriceSource.Universalis,
            snapshots.Min(snapshot => snapshot.ObservedAt), snapshots.SelectMany(snapshot => snapshot.Listings).ToArray(),
            snapshots.All(snapshot => snapshot.IsComplete), recentSales.Length == 0 ? null : recentSales.Max(),
            DataCenterName: homeDataCenter, RegionName: regionalScope);
    }

    private static PriceSnapshot Parse(byte[] json, uint worldId, uint itemId, ScopeKind scopeKind, string scopeName)
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
            if (UInt(root, "itemID") != itemId)
                throw Invalid("the item does not match the request.");
            if (scopeKind == ScopeKind.World)
            {
                if (UInt(root, "worldID") != worldId)
                    throw Invalid("the world does not match the request.");
            }
            else
            {
                var property = scopeKind == ScopeKind.DataCenter ? "dcName" : "regionName";
                if (!root.TryGetProperty(property, out var scope) || scope.ValueKind != JsonValueKind.String ||
                    !StringComparer.OrdinalIgnoreCase.Equals(scope.GetString(), scopeName))
                    throw Invalid(scopeKind == ScopeKind.DataCenter
                        ? "the data center does not match the request."
                        : "the region does not match the request.");
            }
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
                if (listing.TryGetProperty("worldID", out var listingWorld) &&
                    (!listingWorld.TryGetUInt32(out var listingWorldId) || listingWorldId == 0 ||
                     (scopeKind == ScopeKind.World && listingWorldId != worldId)))
                    throw Invalid("a listing belongs to another market scope.");
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
                totalCount == results.Count, mostRecentSaleAt,
                DataCenterName: scopeKind == ScopeKind.DataCenter ? scopeName : null,
                RegionName: scopeKind == ScopeKind.Region ? scopeName : null);
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
        lock (cacheLock) cache.Clear();
        dataCenterRegionLock.Dispose();
        if (ownsClient) client.Dispose();
    }
}

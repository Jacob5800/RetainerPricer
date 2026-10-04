using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace RetainerPricer;

/// <summary>Fetches Universalis market data and sale histories. Never uploads data or opens a live feed.</summary>
public sealed class UniversalisClient : IDisposable
{
    private enum ScopeKind { World, DataCenter, Region }
    private sealed record DataCenterInfo(string Name, string Region, IReadOnlyList<uint> Worlds);

    private const int MaximumResponseBytes = 1_048_576;
    private const int MaximumHistoryResponseBytes = 32 * 1_048_576;
    private const int HistoryEntryLimit = 100;
    private const int HistoryWindowSeconds = 20 * 24 * 60 * 60;
    private const int SniperHistoryEntryLimit = 1_800;
    private const int UniversalisRequestsPerSecond = 25;
    private static readonly TimeSpan HistoryRequestTimeout = TimeSpan.FromSeconds(60);
    private static readonly SemaphoreSlim RequestRateGate = new(1, 1);
    private static readonly SemaphoreSlim RequestConcurrencyGate = new(8, 8);
    private static readonly SemaphoreSlim SniperHistoryBatchRateGate = new(1, 1);
    private static long nextRequestTimestamp;
    private static long nextSniperHistoryBatchTimestamp;
    public const int SniperHistoryBatchSize = 100;
    private const int MaximumCachedItems = 512;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private readonly HttpClient client;
    private readonly bool ownsClient;
    private readonly object cacheLock = new();
    private readonly Dictionary<(string Scope, uint Item), PriceSnapshot> cache = [];
    private readonly SemaphoreSlim dataCenterRegionLock = new(1, 1);
    private IReadOnlyDictionary<string, DataCenterInfo>? dataCenters;

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

    internal async Task<SniperMarketScope> ResolveSniperMarketScopeAsync(MarketWorld world,
        bool useDataCenter, bool useRegion, CancellationToken ct = default)
    {
        if (world.WorldId == 0) throw new ArgumentException("Select a valid home world before watching listings.");
        if (!useRegion && !useDataCenter)
            return new SniperMarketScope(world.Name, [new SniperHistoryScope(
                world.WorldId.ToString(CultureInfo.InvariantCulture), SniperHistoryScopeKind.World)], [world.WorldId]);

        if (string.IsNullOrWhiteSpace(world.DataCenterName))
            throw new InvalidOperationException("Could not identify your home world's Data Center for the selected Sniper scope.");

        var centers = await GetDataCentersAsync(ct).ConfigureAwait(false);
        if (!centers.TryGetValue(world.DataCenterName, out var homeCenter))
            throw new InvalidOperationException($"Universalis could not map the home Data Center '{world.DataCenterName}'.");

        if (!useRegion)
        {
            return new SniperMarketScope($"{homeCenter.Name} Data Center",
                [new SniperHistoryScope(homeCenter.Name, SniperHistoryScopeKind.DataCenter)],
                homeCenter.Worlds.Distinct().ToArray());
        }

        var matchingCenters = centers.Values.Where(center =>
                StringComparer.OrdinalIgnoreCase.Equals(center.Region, homeCenter.Region) ||
                !StringComparer.OrdinalIgnoreCase.Equals(homeCenter.Region, "Oceania") &&
                StringComparer.OrdinalIgnoreCase.Equals(center.Region, "Oceania"))
            .ToArray();
        var historyRegions = StringComparer.OrdinalIgnoreCase.Equals(homeCenter.Region, "Oceania")
            ? new[] { "Oceania" }
            : new[] { homeCenter.Region, "Oceania" };
        var worldIds = matchingCenters.SelectMany(center => center.Worlds).Distinct().ToArray();
        if (worldIds.Length == 0)
            throw new InvalidOperationException($"Universalis returned no worlds for the {homeCenter.Region} region scope.");
        var label = StringComparer.OrdinalIgnoreCase.Equals(homeCenter.Region, "Oceania")
            ? "Oceania (Materia)"
            : $"{homeCenter.Region} + Oceania (Materia)";
        return new SniperMarketScope(label,
            historyRegions.Select(region => new SniperHistoryScope(region, SniperHistoryScopeKind.Region)).ToArray(),
            worldIds);
    }

    internal async Task<IReadOnlyDictionary<uint, SniperSalesSnapshot>> FetchSniperSalesBatchAsync(
        SniperHistoryScope scope, IReadOnlyList<uint> itemIds, int historyDays, uint homeWorldId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(scope.Name) || itemIds.Count is 0 or > SniperHistoryBatchSize ||
            itemIds.Any(itemId => itemId == 0) || homeWorldId == 0)
            throw new ArgumentException($"Select a valid market scope, 1 to {SniperHistoryBatchSize} items, and a home world before fetching sales history.");
        historyDays = Math.Clamp(historyDays, 3, 14);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(HistoryRequestTimeout);
        try
        {
            var itemIdsPath = string.Join(',', itemIds.Distinct());
            var historyWindowSeconds = historyDays * 24 * 60 * 60;
            var escapedScope = Uri.EscapeDataString(scope.Name);
            var path = $"history/{escapedScope}/{itemIdsPath}?entriesToReturn={SniperHistoryEntryLimit}&entriesWithin={historyWindowSeconds}";
            var json = await RequestHistoryJsonAsync(path, deadline.Token).ConfigureAwait(false);
            return ParseSniperSalesBatch(json, scope, homeWorldId, itemIds, historyDays, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Universalis sales history did not respond in time.");
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException("Could not reach Universalis sales history.", ex);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException("The Universalis sales-history response was interrupted.", ex);
        }
    }

    private async Task<byte[]> RequestHistoryJsonAsync(string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://universalis.app/api/v2/{path}");
        request.Headers.UserAgent.ParseAdd("RetainerPricer/0.4");
        request.Headers.Accept.ParseAdd("application/json");
        await RequestConcurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForRequestSlotAsync(ct).ConfigureAwait(false);
            await WaitForSniperHistoryBatchSlotAsync(ct).ConfigureAwait(false);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException("Universalis has temporarily limited sales-history requests. Wait a moment, then retry.");
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new InvalidOperationException("Universalis has no sales history for this item batch and world.");
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Universalis returned HTTP {(int)response.StatusCode} for sales history.");
            if (response.Content.Headers.ContentLength > MaximumHistoryResponseBytes)
                throw new InvalidOperationException("Universalis returned too much sales history for this item batch.");

            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[16_384];
            while (true)
            {
                var read = await source.ReadAsync(chunk, ct).ConfigureAwait(false);
                if (read == 0) break;
                if (buffer.Length + read > MaximumHistoryResponseBytes)
                    throw new InvalidOperationException("Universalis returned too much sales history for this item batch.");
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }
        finally { RequestConcurrencyGate.Release(); }
    }

    private static IReadOnlyDictionary<uint, SniperSalesSnapshot> ParseSniperSalesBatch(
        byte[] json, SniperHistoryScope scope, uint homeWorldId, IReadOnlyList<uint> itemIds,
        int historyDays, DateTimeOffset retrievedAt)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("Universalis returned unusable sales history.");
            var scopeProperty = scope.Kind switch
            {
                SniperHistoryScopeKind.World => "worldID",
                SniperHistoryScopeKind.DataCenter => "dcName",
                _ => "regionName",
            };
            if (scope.Kind == SniperHistoryScopeKind.World)
            {
                if (!root.TryGetProperty(scopeProperty, out var returnedWorld) ||
                    !returnedWorld.TryGetUInt32(out var parsedWorld) ||
                    !StringComparer.Ordinal.Equals(parsedWorld.ToString(CultureInfo.InvariantCulture), scope.Name))
                    throw new InvalidOperationException("Universalis sales history belongs to another world.");
            }
            else if (!root.TryGetProperty(scopeProperty, out var returnedScope) || returnedScope.ValueKind != JsonValueKind.String ||
                     !StringComparer.OrdinalIgnoreCase.Equals(returnedScope.GetString(), scope.Name))
                throw new InvalidOperationException("Universalis sales history does not match the selected market scope.");

            var results = new Dictionary<uint, SniperSalesSnapshot>();
            if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object)
            {
                foreach (var itemId in itemIds.Distinct())
                {
                    if (!items.TryGetProperty(itemId.ToString(CultureInfo.InvariantCulture), out var entryRoot) ||
                        entryRoot.ValueKind != JsonValueKind.Object ||
                        !entryRoot.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                        continue;
                    results[itemId] = ParseSniperItem(itemId, homeWorldId, entries, retrievedAt, historyDays);
                }
            }
            else if (itemIds.Distinct().Take(2).Count() == 1 && root.TryGetProperty("entries", out var entries) &&
                     entries.ValueKind == JsonValueKind.Array)
            {
                var itemId = itemIds.Distinct().Single();
                results[itemId] = ParseSniperItem(itemId, homeWorldId, entries, retrievedAt, historyDays);
            }
            else if (itemIds.Distinct().Take(2).Count() == 1)
                throw new InvalidOperationException("Universalis returned malformed sales history.");
            return results;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Universalis returned malformed sales history.", ex);
        }
    }

    private static SniperSalesSnapshot ParseSniperItem(uint itemId, uint homeWorldId, JsonElement entries,
        DateTimeOffset retrievedAt, int historyDays)
    {
        var minimumTimestamp = retrievedAt.AddDays(-historyDays).ToUnixTimeSeconds();
        var sales = new List<SniperSale>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object ||
                !entry.TryGetProperty("timestamp", out var timestamp) || !timestamp.TryGetInt64(out var seconds) ||
                seconds < minimumTimestamp || seconds > retrievedAt.ToUnixTimeSeconds() ||
                !entry.TryGetProperty("pricePerUnit", out var price) || !price.TryGetUInt32(out var gil) || gil == 0 ||
                !entry.TryGetProperty("hq", out var hq) || hq.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                continue;
            sales.Add(new SniperSale(gil, hq.GetBoolean()));
        }

        var qualities = new List<SniperQualityBaseline>(2);
        foreach (var quality in sales.GroupBy(sale => sale.IsHq))
        {
            var ordered = quality.Select(sale => sale.PricePerUnit).OrderBy(price => price).ToArray();
            if (ordered.Length == 0) continue;
            var middle = ordered.Length / 2;
            var median = ordered.Length % 2 == 0
                ? (uint)(((ulong)ordered[middle - 1] + ordered[middle]) / 2)
                : ordered[middle];
            qualities.Add(new SniperQualityBaseline(quality.Key, ordered.Length, median));
        }
        return new SniperSalesSnapshot(itemId, homeWorldId, retrievedAt, qualities.AsReadOnly(), sales.AsReadOnly());
    }

    private async Task<string> GetRegionForDataCenterAsync(string dataCenterName, CancellationToken ct)
    {
        var centers = await GetDataCentersAsync(ct).ConfigureAwait(false);
        if (!centers.TryGetValue(dataCenterName, out var info))
            throw new InvalidOperationException($"Universalis could not map the home Data Center '{dataCenterName}' to a region.");
        return info.Region;
    }

    private async Task<IReadOnlyDictionary<string, DataCenterInfo>> GetDataCentersAsync(CancellationToken ct)
    {
        await dataCenterRegionLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (dataCenters is null)
            {
                var json = await RequestJsonAsync("data-centers", "Data Center information", ct).ConfigureAwait(false);
                try
                {
                    using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
                    if (document.RootElement.ValueKind != JsonValueKind.Array)
                        throw new InvalidOperationException("Universalis returned invalid Data Center information.");
                    var centers = new Dictionary<string, DataCenterInfo>(StringComparer.OrdinalIgnoreCase);
                    foreach (var entry in document.RootElement.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object ||
                            !entry.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                            !entry.TryGetProperty("region", out var region) || region.ValueKind != JsonValueKind.String ||
                            !entry.TryGetProperty("worlds", out var worlds) || worlds.ValueKind != JsonValueKind.Array)
                            throw new InvalidOperationException("Universalis returned incomplete Data Center information.");
                        var dcName = name.GetString();
                        var regionValue = region.GetString();
                        if (string.IsNullOrWhiteSpace(dcName) || string.IsNullOrWhiteSpace(regionValue))
                            throw new InvalidOperationException("Universalis returned incomplete Data Center information.");
                        var worldIds = worlds.EnumerateArray()
                            .Where(world => world.TryGetUInt32(out var id) && id != 0)
                            .Select(world => world.GetUInt32()).Distinct().ToArray();
                        centers[dcName] = new DataCenterInfo(dcName, regionValue, worldIds);
                    }
                    if (centers.Count == 0)
                        throw new InvalidOperationException("Universalis returned no Data Center information.");
                    dataCenters = centers;
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException("Universalis returned malformed Data Center information.", ex);
                }
            }

            return dataCenters;
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
        await RequestConcurrencyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WaitForRequestSlotAsync(ct).ConfigureAwait(false);
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
        finally { RequestConcurrencyGate.Release(); }
    }

    private static async Task WaitForRequestSlotAsync(CancellationToken ct)
    {
        await RequestRateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Stopwatch.GetTimestamp();
            var remainingTicks = nextRequestTimestamp - now;
            if (remainingTicks > 0)
                await Task.Delay(TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency), ct).ConfigureAwait(false);
            nextRequestTimestamp = Stopwatch.GetTimestamp() + Math.Max(1, Stopwatch.Frequency / UniversalisRequestsPerSecond);
        }
        finally { RequestRateGate.Release(); }
    }

    private static async Task WaitForSniperHistoryBatchSlotAsync(CancellationToken ct)
    {
        await SniperHistoryBatchRateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = Stopwatch.GetTimestamp();
            var next = Interlocked.Read(ref nextSniperHistoryBatchTimestamp);
            var remainingTicks = next - now;
            if (remainingTicks > 0)
                await Task.Delay(TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency), ct).ConfigureAwait(false);
            Interlocked.Exchange(ref nextSniperHistoryBatchTimestamp,
                Stopwatch.GetTimestamp() + Stopwatch.Frequency + Stopwatch.Frequency / 100);
        }
        finally { SniperHistoryBatchRateGate.Release(); }
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

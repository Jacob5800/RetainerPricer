namespace RetainerPricer;

internal sealed class SniperMonitor : IDisposable
{
    private const int MaximumDeals = 250;
    private readonly object gate = new();
    private readonly UniversalisClient universalis;
    private readonly PluginConfig config;
    private readonly IReadOnlyList<ItemChoice> marketableItems;
    private readonly IReadOnlyDictionary<uint, string> itemNames;
    private readonly IReadOnlyDictionary<uint, string> worldNames;
    private readonly List<SniperDeal> deals = [];
    private CancellationTokenSource? cancellation;
    private string status = "Sniper is stopped.";
    private bool isListening;

    public SniperMonitor(UniversalisClient universalis, PluginConfig config, IReadOnlyList<ItemChoice> items,
        IReadOnlyDictionary<uint, string> worldNames)
    {
        this.universalis = universalis;
        this.config = config;
        marketableItems = items;
        itemNames = items.ToDictionary(item => item.ItemId, item => item.Name);
        this.worldNames = worldNames;
    }

    public bool IsListening { get { lock (gate) return isListening; } }
    public bool IsRunning { get { lock (gate) return cancellation is not null; } }
    public string Status { get { lock (gate) return status; } }
    public IReadOnlyList<SniperDeal> Deals { get { lock (gate) return deals.ToArray(); } }
    public int MarketableItemCount => marketableItems.Count;

    public void Start(MarketWorld? world, bool useDataCenter, bool useRegion)
    {
        if (world is null)
        {
            SetStatus("Log in to start Sniper.");
            return;
        }
        var watchedItems = marketableItems;
        CancellationTokenSource run;
        lock (gate)
        {
            if (cancellation is not null) return;
            cancellation = run = new CancellationTokenSource();
            deals.Clear();
            isListening = false;
            status = $"Preparing to scan {watchedItems.Count:N0} marketable items using the selected market scope…";
        }
        _ = Task.Run(() => RunAsync(world, useDataCenter, useRegion, watchedItems, Math.Clamp(config.SniperMinimumSales14Days, 1, 1_800),
            Math.Clamp(config.SniperThresholdFraction, 0.01, 1.0), Math.Clamp(config.SniperHistoryDays, 3, 14),
            Math.Clamp(config.SniperMinimumItemPrice, 1, 999_999_999), run.Token));
    }

    public void Stop()
    {
        CancellationTokenSource? active;
        lock (gate) active = cancellation;
        try { active?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(MarketWorld world, bool useDataCenter, bool useRegion,
        IReadOnlyList<ItemChoice> watchedItems, int minimumSales, double threshold, int historyDays, int minimumItemPrice,
        CancellationToken cancellationToken)
    {
        try
        {
            var scope = await universalis.ResolveSniperMarketScopeAsync(world, useDataCenter, useRegion, cancellationToken)
                .ConfigureAwait(false);
            var watchedWorlds = scope.WorldIds.ToHashSet();
            if (watchedWorlds.Count == 0)
                throw new InvalidOperationException("The selected Sniper scope did not contain any supported worlds.");

            var baselines = new Dictionary<(uint ItemId, bool IsHq), SniperQualityBaseline>();
            var failed = new List<string>();
            var batchCount = (watchedItems.Count + UniversalisClient.SniperHistoryBatchSize - 1) /
                             UniversalisClient.SniperHistoryBatchSize;
            for (var index = 0; index < watchedItems.Count; index += UniversalisClient.SniperHistoryBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = watchedItems.Skip(index).Take(UniversalisClient.SniperHistoryBatchSize).ToArray();
                var currentBatch = index / UniversalisClient.SniperHistoryBatchSize + 1;
                SetStatus($"Scanning all marketable items · {historyDays}-day history batch {currentBatch} of {batchCount} ({batch.Length} items)…");
                try
                {
                    var batchSales = new Dictionary<uint, List<SniperSale>>();
                    foreach (var historyScope in scope.HistoryScopes)
                    {
                        var snapshots = await universalis.FetchSniperSalesBatchAsync(historyScope,
                                batch.Select(item => item.ItemId).ToArray(), historyDays, world.WorldId, cancellationToken)
                            .ConfigureAwait(false);
                        foreach (var snapshot in snapshots.Values)
                        {
                            if (!batchSales.TryGetValue(snapshot.ItemId, out var sales))
                                batchSales[snapshot.ItemId] = sales = [];
                            sales.AddRange(snapshot.Sales);
                        }
                    }
                    foreach (var (itemId, sales) in batchSales)
                    {
                        foreach (var qualitySales in sales.GroupBy(sale => sale.IsHq))
                        {
                            var orderedPrices = qualitySales.Select(sale => sale.PricePerUnit).OrderBy(price => price).ToArray();
                            if (orderedPrices.Length < minimumSales) continue;
                            var middle = orderedPrices.Length / 2;
                            var median = orderedPrices.Length % 2 == 0
                                ? (uint)(((ulong)orderedPrices[middle - 1] + orderedPrices[middle]) / 2)
                                : orderedPrices[middle];
                            baselines[(itemId, qualitySales.Key)] = new SniperQualityBaseline(qualitySales.Key, orderedPrices.Length, median);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var label = batch.Length == 1 ? batch[0].Name : $"{batch[0].Name} … {batch[^1].Name}";
                    failed.Add($"{label}: {ex.Message}");
                }
            }

            var eligible = baselines.Keys.Select(key => key.ItemId).Distinct().Count();
            if (eligible == 0 && watchedItems.Count > 0)
            {
                var reason = failed.Count > 0
                    ? $" {failed.Count} history batch(es) failed; first error: {failed[0]}"
                    : $" No marketable item reached the minimum of {minimumSales} sales in {historyDays} days.";
                SetStatus("No marketable item has a usable sales baseline." + reason + " One-gil alerts will still be monitored.");
            }

            var backoffSeconds = 2;
            while (!cancellationToken.IsCancellationRequested)
            {
                var reconnectReason = "the server closed the connection";
                try
                {
                    await using var socket = new UniversalisWebSocketClient();
                    SetStatus($"Connecting to Universalis live listings for {scope.Label}…");
                    await socket.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var worldId in watchedWorlds)
                    {
                        await socket.SubscribeWorldAsync(worldId, "add", cancellationToken).ConfigureAwait(false);
                        await socket.SubscribeWorldAsync(worldId, "remove", cancellationToken).ConfigureAwait(false);
                    }
                    backoffSeconds = 2;
                    lock (gate)
                    {
                        isListening = true;
                        status = $"Listening on {scope.Label} across {watchedWorlds.Count:N0} world(s) · all {watchedItems.Count:N0} marketable items scanned, {eligible:N0} with usable {historyDays}-day history · 1-gil alerts cover all marketable items. Purchases are manual.";
                        if (failed.Count > 0) status += $" {failed.Count} history batch(es) failed.";
                    }

                    while (!cancellationToken.IsCancellationRequested && socket.State == System.Net.WebSockets.WebSocketState.Open)
                    {
                        var listings = await socket.ReceiveListingsAsync(cancellationToken).ConfigureAwait(false);
                        if (listings is null) break;
                        foreach (var listing in listings)
                        {
                            if (listing.IsRemoved)
                            {
                                lock (gate)
                                {
                                    if (listing.ListingId is { Length: > 0 } removedId)
                                        deals.RemoveAll(previous => previous.Key == $"{listing.WorldId}:{listing.ItemId}:{removedId}" ||
                                            previous.IsOneGilAlert && previous.ItemId == listing.ItemId &&
                                            previous.WorldId == listing.WorldId && previous.IsHq == listing.IsHq);
                                    else if (listing.PricePerUnit == 1)
                                        deals.RemoveAll(previous => previous.IsOneGilAlert && previous.ItemId == listing.ItemId &&
                                            previous.WorldId == listing.WorldId && previous.IsHq == listing.IsHq);
                                    else
                                        deals.RemoveAll(previous => previous.ItemId == listing.ItemId && previous.WorldId == listing.WorldId &&
                                            previous.IsHq == listing.IsHq && previous.PricePerUnit == listing.PricePerUnit && previous.Quantity == listing.Quantity);
                                }
                                continue;
                            }
                            if (!watchedWorlds.Contains(listing.WorldId) || !itemNames.TryGetValue(listing.ItemId, out var name))
                                continue;
                            var isOneGilAlert = listing.PricePerUnit == 1;
                            var hasBaseline = baselines.TryGetValue((listing.ItemId, listing.IsHq), out var baseline);
                            if (!isOneGilAlert && (listing.PricePerUnit < minimumItemPrice || !hasBaseline ||
                                listing.PricePerUnit > baseline!.MedianSalePrice * threshold))
                                continue;
                            var listingWorldName = worldNames.TryGetValue(listing.WorldId, out var resolvedWorldName)
                                ? resolvedWorldName : listing.WorldId == world.WorldId ? world.Name : $"World {listing.WorldId}";
                            var listingKey = isOneGilAlert
                                ? $"alert:{listing.WorldId}:{listing.ItemId}:{listing.IsHq}"
                                : listing.ListingId is { Length: > 0 } id
                                    ? $"{listing.WorldId}:{listing.ItemId}:{id}"
                                    : $"{listing.WorldId}:{listing.ItemId}:{listing.IsHq}:{listing.PricePerUnit}:{listing.Quantity}";
                            var deal = new SniperDeal(listingKey, listing.ItemId, name, listing.WorldId, listingWorldName,
                                listing.IsHq, listing.PricePerUnit, listing.Quantity, baseline?.MedianSalePrice ?? 0,
                                baseline?.SaleCount ?? 0, DateTimeOffset.UtcNow, listing.ListingId, isOneGilAlert);
                            lock (gate)
                            {
                                if (isOneGilAlert)
                                    deals.RemoveAll(previous => previous.IsOneGilAlert && previous.ItemId == listing.ItemId &&
                                        previous.WorldId == listing.WorldId && previous.IsHq == listing.IsHq);
                                else
                                    deals.RemoveAll(previous => previous.Key == listingKey);
                                deals.Insert(0, deal);
                                if (deals.Count > MaximumDeals) deals.RemoveRange(MaximumDeals, deals.Count - MaximumDeals);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    reconnectReason = ex.Message;
                    lock (gate)
                    {
                        isListening = false;
                        status = $"Universalis live feed disconnected ({reconnectReason}). Reconnecting…";
                    }
                }

                if (cancellationToken.IsCancellationRequested) break;
                SetStatus($"Universalis live feed disconnected ({reconnectReason}). Reconnecting in {backoffSeconds}s…");
                await Task.Delay(TimeSpan.FromSeconds(backoffSeconds), cancellationToken).ConfigureAwait(false);
                backoffSeconds = Math.Min(backoffSeconds * 2, 30);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            SetStatus($"Sniper stopped: {ex.Message}");
        }
        finally
        {
            lock (gate)
            {
                isListening = false;
                cancellation?.Dispose();
                cancellation = null;
                if (string.IsNullOrWhiteSpace(status) || status.StartsWith("Listening", StringComparison.Ordinal))
                    status = "Sniper is stopped.";
                else if (cancellationToken.IsCancellationRequested)
                    status = "Sniper stopped.";
            }
        }
    }

    private void SetStatus(string value)
    {
        lock (gate) status = value;
    }

    public void Dispose() => Stop();
}

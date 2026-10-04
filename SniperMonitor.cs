namespace RetainerPricer;

internal sealed class SniperMonitor : IDisposable
{
    private const int MaximumDeals = 250;
    private readonly object gate = new();
    private readonly UniversalisClient universalis;
    private readonly PluginConfig config;
    private readonly IReadOnlyList<ItemChoice> marketableItems;
    private readonly IReadOnlyDictionary<uint, string> itemNames;
    private readonly List<SniperDeal> deals = [];
    private CancellationTokenSource? cancellation;
    private string status = "Sniper is stopped.";
    private bool isListening;

    public SniperMonitor(UniversalisClient universalis, PluginConfig config, IReadOnlyList<ItemChoice> items)
    {
        this.universalis = universalis;
        this.config = config;
        marketableItems = items;
        itemNames = items.ToDictionary(item => item.ItemId, item => item.Name);
    }

    public bool IsListening { get { lock (gate) return isListening; } }
    public bool IsRunning { get { lock (gate) return cancellation is not null; } }
    public string Status { get { lock (gate) return status; } }
    public IReadOnlyList<SniperDeal> Deals { get { lock (gate) return deals.ToArray(); } }
    public int MarketableItemCount => marketableItems.Count;

    public void Start(MarketWorld? world)
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
            status = $"Preparing to scan {watchedItems.Count:N0} marketable items on {world.Name}…";
        }
        _ = Task.Run(() => RunAsync(world, watchedItems, Math.Clamp(config.SniperMinimumSales14Days, 1, 1_800),
            Math.Clamp(config.SniperThresholdFraction, 0.01, 1.0), Math.Clamp(config.SniperHistoryDays, 3, 14), run.Token));
    }

    public void Stop()
    {
        CancellationTokenSource? active;
        lock (gate) active = cancellation;
        try { active?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(MarketWorld world, IReadOnlyList<ItemChoice> watchedItems, int minimumSales,
        double threshold, int historyDays, CancellationToken cancellationToken)
    {
        try
        {
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
                    var snapshots = await universalis.FetchSniperSalesBatchAsync(world.WorldId,
                            batch.Select(item => item.ItemId).ToArray(), historyDays, cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var snapshot in snapshots.Values)
                        foreach (var quality in snapshot.Qualities.Where(quality => quality.SaleCount >= minimumSales))
                            baselines[(snapshot.ItemId, quality.IsHq)] = quality;
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
                    SetStatus($"Connecting to Universalis live listings for {world.Name}…");
                    await socket.ConnectAsync(cancellationToken).ConfigureAwait(false);
                    await socket.SubscribeWorldAsync(world.WorldId, "add", cancellationToken).ConfigureAwait(false);
                    await socket.SubscribeWorldAsync(world.WorldId, "remove", cancellationToken).ConfigureAwait(false);
                    backoffSeconds = 2;
                    lock (gate)
                    {
                        isListening = true;
                        status = $"Listening on {world.Name} · all {watchedItems.Count:N0} marketable items scanned, {eligible:N0} with usable {historyDays}-day history · 1-gil alerts cover all marketable items. Purchases are manual.";
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
                                        deals.RemoveAll(previous => previous.Key == $"{listing.WorldId}:{listing.ItemId}:{removedId}");
                                    else
                                        deals.RemoveAll(previous => previous.ItemId == listing.ItemId && previous.WorldId == listing.WorldId &&
                                            previous.IsHq == listing.IsHq && previous.PricePerUnit == listing.PricePerUnit && previous.Quantity == listing.Quantity);
                                }
                                continue;
                            }
                            if (listing.WorldId != world.WorldId || !itemNames.TryGetValue(listing.ItemId, out var name))
                                continue;
                            var isOneGilAlert = listing.PricePerUnit == 1;
                            var hasBaseline = baselines.TryGetValue((listing.ItemId, listing.IsHq), out var baseline);
                            if (!isOneGilAlert && (!hasBaseline || listing.PricePerUnit > baseline!.MedianSalePrice * threshold))
                                continue;
                            var listingKey = listing.ListingId is { Length: > 0 } id
                                ? $"{world.WorldId}:{listing.ItemId}:{id}"
                                : $"{world.WorldId}:{listing.ItemId}:{listing.IsHq}:{listing.PricePerUnit}:{listing.Quantity}";
                            var deal = new SniperDeal(listingKey, listing.ItemId, name, world.WorldId, world.Name,
                                listing.IsHq, listing.PricePerUnit, listing.Quantity, baseline?.MedianSalePrice ?? 0,
                                baseline?.SaleCount ?? 0, DateTimeOffset.UtcNow, listing.ListingId, isOneGilAlert);
                            lock (gate)
                            {
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

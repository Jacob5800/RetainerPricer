namespace RetainerPricer;

internal sealed class SniperMonitor : IDisposable
{
    private const int MaximumDeals = 250;
    private readonly object gate = new();
    private readonly UniversalisClient universalis;
    private readonly PluginConfig config;
    private readonly IReadOnlyDictionary<uint, string> itemNames;
    private readonly List<SniperDeal> deals = [];
    private CancellationTokenSource? cancellation;
    private string status = "Sniper is stopped.";
    private bool isListening;

    public SniperMonitor(UniversalisClient universalis, PluginConfig config, IReadOnlyList<ItemChoice> items)
    {
        this.universalis = universalis;
        this.config = config;
        itemNames = items.ToDictionary(item => item.ItemId, item => item.Name);
    }

    public bool IsListening { get { lock (gate) return isListening; } }
    public bool IsRunning { get { lock (gate) return cancellation is not null; } }
    public string Status { get { lock (gate) return status; } }
    public IReadOnlyList<SniperDeal> Deals { get { lock (gate) return deals.ToArray(); } }

    public void Start(MarketWorld? world)
    {
        if (world is null)
        {
            SetStatus("Log in to start Sniper.");
            return;
        }
        var watchedItems = config.SniperWatchlistItemIds
            .Where(itemNames.ContainsKey).Distinct().Take(100)
            .Select(id => new ItemChoice(id, itemNames[id])).ToArray();
        if (watchedItems.Length == 0)
        {
            SetStatus("Add at least one item to the Sniper watchlist first.");
            return;
        }

        CancellationTokenSource run;
        lock (gate)
        {
            if (cancellation is not null) return;
            cancellation = run = new CancellationTokenSource();
            deals.Clear();
            isListening = false;
            status = $"Loading 14-day sales history for {watchedItems.Length} watched item(s) on {world.Name}…";
        }
        _ = Task.Run(() => RunAsync(world, watchedItems, Math.Clamp(config.SniperMinimumSales14Days, 1, 100_000),
            Math.Clamp(config.SniperThresholdFraction, 0.01, 1.0), run.Token));
    }

    public void Stop()
    {
        CancellationTokenSource? active;
        lock (gate) active = cancellation;
        try { active?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync(MarketWorld world, IReadOnlyList<ItemChoice> watchedItems, int minimumSales,
        double threshold, CancellationToken cancellationToken)
    {
        try
        {
            var baselines = new Dictionary<(uint ItemId, bool IsHq), SniperQualityBaseline>();
            var failed = new List<string>();
            for (var index = 0; index < watchedItems.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = watchedItems[index];
                SetStatus($"Loading 14-day sales history · {index + 1} of {watchedItems.Count}: {item.Name}…");
                try
                {
                    var snapshot = await universalis.FetchSniperSalesAsync(world.WorldId, item.ItemId, cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var quality in snapshot.Qualities.Where(quality => quality.SaleCount >= minimumSales))
                        baselines[(item.ItemId, quality.IsHq)] = quality;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    failed.Add($"{item.Name}: {ex.Message}");
                }
            }

            var eligible = baselines.Keys.Select(key => key.ItemId).Distinct().Count();
            if (eligible == 0)
            {
                var reason = failed.Count > 0
                    ? $" {failed.Count} history request(s) failed; first error: {failed[0]}"
                    : $" No watched item reached the minimum of {minimumSales} sales in 14 days.";
                SetStatus("Sniper couldn't build a sales baseline." + reason);
                return;
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
                    foreach (var item in watchedItems)
                    {
                        await socket.SubscribeAsync(world.WorldId, item.ItemId, "add", cancellationToken).ConfigureAwait(false);
                        await socket.SubscribeAsync(world.WorldId, item.ItemId, "remove", cancellationToken).ConfigureAwait(false);
                    }
                    backoffSeconds = 2;
                    lock (gate)
                    {
                        isListening = true;
                        status = failed.Count == 0
                            ? $"Listening to Universalis on {world.Name} · {watchedItems.Count} item(s) · {eligible} with enough recent sales. Alerts are manual-buy only."
                            : $"Listening on {world.Name}; {failed.Count} history lookup(s) failed. Alerts are manual-buy only.";
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
                            if (!baselines.TryGetValue((listing.ItemId, listing.IsHq), out var baseline) ||
                                listing.WorldId != world.WorldId || !itemNames.TryGetValue(listing.ItemId, out var name))
                                continue;
                            var limit = baseline.MedianSalePrice * threshold;
                            if (listing.PricePerUnit > limit) continue;
                            var listingKey = listing.ListingId is { Length: > 0 } id
                                ? $"{world.WorldId}:{listing.ItemId}:{id}"
                                : $"{world.WorldId}:{listing.ItemId}:{listing.IsHq}:{listing.PricePerUnit}:{listing.Quantity}";
                            var deal = new SniperDeal(listingKey, listing.ItemId, name, world.WorldId, world.Name,
                                listing.IsHq, listing.PricePerUnit, listing.Quantity, baseline.MedianSalePrice,
                                baseline.SaleCount, DateTimeOffset.UtcNow, listing.ListingId);
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

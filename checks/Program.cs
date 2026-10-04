using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using RetainerPricer;

var checks = 0;
var now = new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
var own = new HashSet<ulong> { 21, 22 };
var age = TimeSpan.FromMinutes(15);
MarketListing Row(uint price, bool hq = false, ulong retainer = 42, uint quantity = 1, bool mannequin = false) =>
    new(5333, hq, price, quantity, retainer, mannequin);
PriceSnapshot Snapshot(params MarketListing[] rows) => new(5333, 74, PriceSource.Local, now, rows);
PriceProposal Calculate(PriceSnapshot snapshot, bool hq = false, uint floor = 1) =>
    PriceCalculator.Calculate(snapshot, 5333, 74, hq, own, floor, now, age);
void Assert(bool success, string description)
{
    checks++;
    if (!success) throw new Exception("FAILED: " + description);
}
void Reject(PriceProposal proposal, string description) =>
    Assert(!proposal.CanApply && proposal.SuggestedPrice == 0 && !string.IsNullOrWhiteSpace(proposal.Error), description);

var basic = Calculate(Snapshot(Row(100, quantity: 99), Row(200), Row(90)));
Assert(basic.CanApply && basic.LowestPrice == 90 && basic.SuggestedPrice == 89 && basic.MatchingListings == 3,
    "Use lowest unit price independent of stack quantity and ordering");
var quality = Snapshot(Row(40), Row(110, true), Row(100, true));
Assert(Calculate(quality).SuggestedPrice == 39, "NQ compares NQ only");
Assert(Calculate(quality, true).SuggestedPrice == 99, "HQ compares HQ only");
var excluded = Calculate(Snapshot(Row(2, retainer: 21), Row(3, retainer: 22), Row(4, mannequin: true), Row(100)));
Assert(excluded.SuggestedPrice == 99 && excluded.MatchingListings == 1, "Exclude all owned retainers and mannequin listings");
Reject(Calculate(Snapshot(Row(1))), "Cannot undercut one gil");
Assert(Calculate(Snapshot(Row(2))).SuggestedPrice == 1, "Two gil undercuts to one gil");
Assert(Calculate(Snapshot(Row(2)), floor: 0).SuggestedPrice == 1, "Zero configured floor still uses game's minimum");
Reject(Calculate(Snapshot(Row(100)), floor: 100), "Floor conflict fails rather than falsely claiming an undercut");
Assert(Calculate(Snapshot(Row(100)), floor: 99).SuggestedPrice == 99, "An undercut exactly at floor is valid");
Reject(Calculate(Snapshot(Row(100)), floor: 1_000_000_000), "Reject floor above game's maximum");
Assert(Calculate(Snapshot(Row(999_999_999))).SuggestedPrice == 999_999_998, "Maximum valid game price undercuts without overflow");
Reject(Calculate(Snapshot()), "Empty market has no invented default price");
Reject(Calculate(Snapshot(Row(100, true))), "No matching quality requires manual price");
Reject(Calculate(Snapshot(Row(100, retainer: 21))), "Only owned listings requires manual price");
Reject(Calculate(Snapshot(Row(100)) with { ItemId = 77 }), "Reject wrong item snapshot");
Reject(Calculate(Snapshot(Row(100)) with { WorldId = 63 }), "Reject wrong world snapshot");
Reject(Calculate(Snapshot(Row(100)) with { IsComplete = false }), "Reject incomplete local or truncated Universalis listings");
Reject(Calculate(Snapshot(Row(100)) with { ObservedAt = now - age - TimeSpan.FromTicks(1) }), "Reject data just beyond maximum age");
Assert(Calculate(Snapshot(Row(100)) with { ObservedAt = now - age }).CanApply, "Accept age exactly at the configured limit");
Reject(Calculate(Snapshot(Row(100)) with { ObservedAt = now + TimeSpan.FromTicks(1) }), "Reject future timestamp");
Reject(Calculate(Snapshot(Row(100)) with { ObservedAt = default }), "Reject missing timestamp");
Reject(Calculate(Snapshot(Row(100)) with { Source = (PriceSource)99 }), "Reject unknown source");
Reject(Calculate(Snapshot(Row(100)) with { Listings = null! }), "Reject missing market rows");
Reject(Calculate(null!), "Reject missing snapshot");
foreach (var invalid in new[] { Row(0), Row(1_000_000_000), Row(100, quantity: 0), Row(100, retainer: 0), Row(100) with { ItemId = 1 }, null! })
    Reject(Calculate(Snapshot(Row(150), invalid)), "Invalid listing must not be silently ignored");
Reject(PriceCalculator.Calculate(Snapshot(Row(100)), 5333, 74, false, own, 1, now, TimeSpan.Zero), "Reject invalid maximum age");
Reject(PriceCalculator.Calculate(Snapshot(Row(100)), 5333, 74, false, null!, 1, now, age), "Reject missing own-retainer collection");

JsonObject Body()
{
    return new JsonObject
    {
        ["itemID"] = 5333, ["worldID"] = 74, ["lastUploadTime"] = now.ToUnixTimeMilliseconds(),
        ["hasData"] = true, ["listingsCount"] = 1,
        ["listings"] = new JsonArray(new JsonObject
        {
            ["pricePerUnit"] = 100, ["quantity"] = 99, ["hq"] = false,
            ["onMannequin"] = false, ["retainerID"] = "18446744073709551615",
        }),
    };
}
HttpResponseMessage JsonResponse(JsonObject body) => new(HttpStatusCode.OK)
{ Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
async Task<PriceSnapshot> Fetch(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler, CancellationToken ct = default)
{
    using var http = new HttpClient(new StubHandler(handler));
    using var client = new UniversalisClient(http);
    return await client.FetchAsync(74, 5333, ct);
}
Task<PriceSnapshot> FetchBody(JsonObject body) => Fetch((_, _) => Task.FromResult(JsonResponse(body)));
async Task RejectFetch(Func<Task<PriceSnapshot>> fetch, string expectedMessage, string description)
{
    try { await fetch(); Assert(false, description); }
    catch (InvalidOperationException e) { Assert(e.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase), description + ": " + e.Message); }
}

var fetched = await Fetch((request, _) =>
{
    Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsoluteUri == "https://universalis.app/api/v2/74/5333?entries=100&entriesWithin=1728000",
        "Single-world read-only request uses documented limits");
    Assert(request.Headers.UserAgent.ToString().StartsWith("RetainerPricer/"), "Identify application to API");
    return Task.FromResult(JsonResponse(Body()));
});
Assert(fetched.Source == PriceSource.Universalis && fetched.ItemId == 5333 && fetched.WorldId == 74, "Preserve price source and identity");
Assert(fetched.ObservedAt == now && fetched.IsComplete && fetched.Listings[0].RetainerId == ulong.MaxValue,
    "Use upload timestamp and lossless decimal string retainer IDs");
var numericId = Body();
numericId["listings"]![0]!["retainerID"] = 42ul;
Assert((await FetchBody(numericId)).Listings[0].RetainerId == 42, "Accept numeric retainer ID when supplied");
var old = Body(); old["lastUploadTime"] = (now - TimeSpan.FromDays(1)).ToUnixTimeMilliseconds();
Reject(Calculate(await FetchBody(old)), "Old server cache remains stale after a fresh HTTP request");
var truncated = Body(); truncated["listingsCount"] = 101;
Assert(!(await FetchBody(truncated)).IsComplete, "Total listing count detects truncated response");
Reject(Calculate(await FetchBody(truncated)), "Truncated HTTP snapshot is never applicable");
var empty = Body(); empty["listings"] = new JsonArray(); empty["listingsCount"] = 0;
var emptySnapshot = await FetchBody(empty);
Assert(emptySnapshot.IsComplete && emptySnapshot.Listings.Count == 0, "An empty complete market is distinct from truncated data");

var sniperItemIds = Enumerable.Range(4_000, UniversalisClient.SniperHistoryBatchSize).Select(id => (uint)id).ToArray();
var sniperItems = new JsonObject();
var saleTimestamp = DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeSeconds();
foreach (var itemId in sniperItemIds)
    sniperItems[itemId.ToString()] = new JsonObject
    {
        ["entries"] = new JsonArray(new JsonObject
        {
            ["timestamp"] = saleTimestamp, ["pricePerUnit"] = 125, ["hq"] = false,
        }),
    };
var sniperBatchBody = new JsonObject { ["worldID"] = 74, ["items"] = sniperItems };
var sniperRequestTimes = new List<DateTimeOffset>();
using (var http = new HttpClient(new StubHandler((request, _) =>
       {
           sniperRequestTimes.Add(DateTimeOffset.UtcNow);
           var uri = request.RequestUri!;
           var requestedIds = uri.AbsolutePath.Split('/').Last().Split(',');
           Assert(request.Method == HttpMethod.Get && requestedIds.Length == UniversalisClient.SniperHistoryBatchSize,
               "Sniper sends 100 item IDs in one history request");
           Assert(uri.Query.Contains("entriesToReturn=1800", StringComparison.Ordinal) &&
                  uri.Query.Contains("entriesWithin=604800", StringComparison.Ordinal),
               "Sniper history request uses the configured lookback and per-item history limit");
           return Task.FromResult(JsonResponse((JsonObject)sniperBatchBody.DeepClone()));
       })))
using (var client = new UniversalisClient(http))
{
    var firstBatch = await client.FetchSniperSalesBatchAsync(74, sniperItemIds, 7);
    var secondBatch = await client.FetchSniperSalesBatchAsync(74, sniperItemIds, 7);
    Assert(firstBatch.Count == sniperItemIds.Length && firstBatch[sniperItemIds[0]].Qualities.Single().MedianSalePrice == 125,
        "Parse all items and sale baselines from the multi-item history response");
    Assert(sniperRequestTimes.Count == 2 && sniperRequestTimes[1] - sniperRequestTimes[0] >= TimeSpan.FromSeconds(1),
        "Space consecutive Sniper history batches by at least one second");
}

foreach (var property in new[] { "itemID", "worldID", "lastUploadTime", "listings", "listingsCount" })
{
    var missing = Body(); missing.Remove(property);
    await RejectFetch(() => FetchBody(missing), "Universalis", "Reject missing " + property);
}
foreach (var property in new[] { "pricePerUnit", "quantity", "hq", "onMannequin" })
{
    var missing = Body(); missing["listings"]![0]!.AsObject().Remove(property);
    await RejectFetch(() => FetchBody(missing), "Universalis", "Reject missing listing " + property);
}
var missingRetainerId = Body(); missingRetainerId["listings"]![0]!.AsObject().Remove("retainerID");
var missingRetainerSnapshot = await FetchBody(missingRetainerId);
Assert(missingRetainerSnapshot.Listings[0].RetainerId == 0,
    "Accept the documented optional retainer ID without inventing an owner");
Reject(Calculate(missingRetainerSnapshot),
    "A missing retainer identity can be displayed but never used to price a listing");
foreach (var pair in new[] { ("worldID", 63), ("itemID", 5334) })
{
    var wrong = Body(); wrong[pair.Item1] = pair.Item2;
    await RejectFetch(() => FetchBody(wrong), "does not match", "Reject wrong API " + pair.Item1);
}
var wrongListingWorld = Body(); wrongListingWorld["listings"]![0]!["worldID"] = 63;
await RejectFetch(() => FetchBody(wrongListingWorld), "another market scope", "Reject cross-world listing in world-specific response");
foreach (var id in new string?[] { "", "legacy-hash", "0", "18446744073709551616", null })
{
    var badRetainer = Body(); badRetainer["listings"]![0]!["retainerID"] = id;
    var badRetainerSnapshot = await FetchBody(badRetainer);
    Assert(badRetainerSnapshot.Listings[0].RetainerId == 0,
        "Preserve unusable retainer identities as unknown");
    Reject(Calculate(badRetainerSnapshot),
        "Do not price using unknown or hashed retainer identities");
}
var missingHistory = Body(); missingHistory["hasData"] = false;
await RejectFetch(() => FetchBody(missingHistory), "not received", "Distinguish never-uploaded item");
var malformedTime = Body(); malformedTime["lastUploadTime"] = long.MaxValue;
await RejectFetch(() => FetchBody(malformedTime), "timestamp", "Reject overflowing timestamp");
var countMismatch = Body(); countMismatch["listingsCount"] = 0;
await RejectFetch(() => FetchBody(countMismatch), "listing count", "Reject inconsistent count");
var wrongType = Body(); wrongType["worldID"] = "74";
await RejectFetch(() => FetchBody(wrongType), "malformed", "Wrong JSON types produce actionable errors");
await RejectFetch(() => Fetch((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{ Content = new StringContent("{broken") })), "malformed JSON", "Reject malformed JSON");
await RejectFetch(() => Fetch((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)
{ Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)) } })), "30 seconds", "Report rate limit retry time without retry loops");
await RejectFetch(() => Fetch((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound))), "no market data", "Report missing data");
await RejectFetch(() => Fetch((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))), "503", "Report server failure");
await RejectFetch(() => Fetch((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("network"))), "Could not reach", "Report network failure cleanly");
await RejectFetch(() => Fetch((_, _) => Task.FromException<HttpResponseMessage>(new TaskCanceledException())), "in time", "Report request timeout");
await RejectFetch(() => Fetch((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
{ Content = new StringContent(new string(' ', 1_048_577)) })), "too much market data", "Bound oversized response");
using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    try { await Fetch((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct), cancellation.Token); Assert(false, "Propagate caller cancellation"); }
    catch (OperationCanceledException) { Assert(true, "Propagate caller cancellation without displaying service error"); }
}

Console.WriteLine($"PASS: {checks} standalone pricing and HTTP checks. No game interaction or live network requests.");

sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        handler(request, cancellationToken);
}

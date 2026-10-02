# Standalone pricing checks

Run from the project directory:

```powershell
dotnet run --project checks\RetainerPricer.Checks.csproj --configuration Release
```

On 2026-10-02, all **77 checks passed** using .NET 10. These checks use synthetic market snapshots and an injected HTTP handler; they do not access FFXIV or send network requests.

Coverage includes unit prices across different stack sizes, exact HQ/NQ matching, own-retainer and mannequin exclusion, no competitors, the 1-gil boundary, minimum-price conflicts, maximum asking price, stale/future/missing timestamps, incomplete markets, invalid rows, missing retainer identities, API identity mismatch, truncated responses, malformed JSON/fields, rate limits, network errors, timeout, cancellation, and oversized responses.

The client follows the [Universalis API documentation](https://docs.universalis.app/): a world-specific `/api/v2/{worldId}/{itemId}?listings=100&entries=0` request. `lastUploadTime` is interpreted as Unix milliseconds and `listingsCount` is compared with the number of returned listings. Incomplete data cannot produce an applicable price proposal. Market data can still change after it is observed; the live retainer integration needs its own final identity/price checks.

This suite does not verify native game callbacks, retainer selection, response-hook timing, or applying a price in the client. The local response reader and retainer compatibility view still require manual validation on a supported game build; native operations are disabled when the verified client layout or selector signature is unavailable.

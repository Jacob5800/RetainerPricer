using System;
using System.Collections.Generic;

namespace RetainerPricer;

public static class PriceCalculator
{
    public const uint MaximumPrice = 999_999_999;
    public static readonly TimeSpan RequiredSaleHistoryWindow = TimeSpan.FromDays(20);

    public static PriceProposal Calculate(
        PriceSnapshot snapshot,
        uint itemId,
        uint worldId,
        bool isHq,
        IReadOnlySet<ulong> ownRetainerIds,
        uint minimumPrice,
        DateTimeOffset now,
        TimeSpan? maxAge,
        string? dataCenterName = null,
        PriceStrategy strategy = PriceStrategy.UndercutByOne)
    {
        static PriceProposal Fail(string message, uint lowest = 0, int count = 0) => new(lowest, 0, count, message);

        if (snapshot is null || itemId == 0 || worldId == 0)
            return Fail("Select an item and selling world, then fetch prices.");
        if (snapshot.ItemId != itemId || snapshot.WorldId != worldId ||
            !StringComparer.OrdinalIgnoreCase.Equals(snapshot.DataCenterName, dataCenterName))
            return Fail("These prices belong to another item or market scope. Fetch prices again.");
        if (snapshot.Source is not (PriceSource.Local or PriceSource.Universalis))
            return Fail("The price source is not recognized. Fetch prices again.");
        if (!Enum.IsDefined(strategy))
            return Fail("The pricing strategy is not recognized. Choose a pricing strategy and fetch prices again.");
        if (!snapshot.IsComplete)
            return Fail("The market results are incomplete. Refresh price data before applying.");
        if (snapshot.Listings is null || ownRetainerIds is null)
            return Fail("Market or retainer data is missing. Fetch prices again.");
        if (maxAge is { } maximumAge && maximumAge <= TimeSpan.Zero)
            return Fail("The maximum price age must be greater than zero.");
        if (snapshot.ObservedAt == default || snapshot.ObservedAt > now)
            return Fail("The market timestamp is missing or in the future. Fetch prices again.");
        if (maxAge is { } ageLimit && now - snapshot.ObservedAt > ageLimit)
            return Fail("These market prices are too old. Refresh prices before applying.");
        var hasRecentSale = snapshot.MostRecentSaleAt is { } mostRecentSaleAt
            && mostRecentSaleAt <= now
            && now - mostRecentSaleAt <= RequiredSaleHistoryWindow;
        if (minimumPrice > MaximumPrice)
            return Fail("The minimum price exceeds the game's maximum asking price.");

        uint lowest = uint.MaxValue;
        var matches = 0;
        foreach (var listing in snapshot.Listings)
        {
            // Reject corrupt data instead of silently hiding a possibly cheaper listing.
            if (listing is null || listing.ItemId != itemId || listing.PricePerUnit is 0 or > MaximumPrice ||
                listing.Quantity == 0)
                return Fail("A market listing is invalid. Refresh prices before applying.");
            if (listing.IsHq != isHq || listing.OnMannequin)
                continue;
            if (listing.RetainerId == 0)
                return Fail("A matching listing did not include its retainer ID, so your own stock cannot be excluded safely. No price will be applied.");
            if (ownRetainerIds.Contains(listing.RetainerId)) continue;

            lowest = Math.Min(lowest, listing.PricePerUnit);
            matches++;
        }

        if (matches == 0)
        {
            if (snapshot.Source == PriceSource.Universalis)
                return Fail(hasRecentSale
                    ? "Universalis has no current competing listings of the same quality. No price will be applied."
                    : "Universalis has no sale history from the last 20 days and no current competing listings of the same quality. No price will be applied.");
            return Fail("No competing listings of the same quality were found. No automatic price was applied.");
        }
        if (snapshot.Source == PriceSource.Universalis && !hasRecentSale)
            return Fail("Universalis has no sale history for this item in the last 20 days. No price will be applied.", lowest, matches);
        if (strategy == PriceStrategy.UndercutByOne && lowest == 1)
            return Fail("The lowest competing price is already 1 gil and cannot be undercut.", lowest, matches);

        var suggested = strategy == PriceStrategy.MatchLowest ? lowest : lowest - 1;
        var floor = Math.Max(1u, minimumPrice);
        if (suggested < floor)
            return Fail($"The selected pricing rule would go below your minimum of {floor:N0} gil. No price will be applied.", lowest, matches);

        return new(lowest, suggested, matches, null);
    }
}

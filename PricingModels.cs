using System;
using System.Collections.Generic;

namespace RetainerPricer;

public enum PriceSource { Universalis, Local }

public sealed record MarketListing(
    uint ItemId,
    bool IsHq,
    uint PricePerUnit,
    uint Quantity,
    ulong RetainerId,
    bool OnMannequin = false);

public sealed record PriceSnapshot(
    uint ItemId,
    uint WorldId,
    PriceSource Source,
    DateTimeOffset ObservedAt,
    IReadOnlyList<MarketListing> Listings,
    bool IsComplete = true);

public sealed record PriceProposal(uint LowestPrice, uint SuggestedPrice, int MatchingListings, string? Error)
{
    public bool CanApply => Error is null;
}

namespace RetainerPricer;

public sealed record SniperQualityBaseline(bool IsHq, int SaleCount, uint MedianSalePrice);

internal enum SniperHistoryScopeKind { World, DataCenter, Region }

internal sealed record SniperHistoryScope(string Name, SniperHistoryScopeKind Kind);

internal sealed record SniperMarketScope(string Label, IReadOnlyList<SniperHistoryScope> HistoryScopes,
    IReadOnlyList<uint> WorldIds);

public sealed record SniperSale(uint PricePerUnit, bool IsHq);

public sealed record SniperSalesSnapshot(uint ItemId, uint WorldId, DateTimeOffset RetrievedAt,
    IReadOnlyList<SniperQualityBaseline> Qualities, IReadOnlyList<SniperSale> Sales);

public sealed record SniperDeal(string Key, uint ItemId, string ItemName, uint WorldId, string WorldName,
    bool IsHq, uint PricePerUnit, uint Quantity, uint MedianSalePrice, int SalesInHistoryWindow,
    DateTimeOffset DetectedAt, string? ListingId, bool IsOneGilAlert = false);

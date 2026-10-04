namespace RetainerPricer;

public sealed record SniperQualityBaseline(bool IsHq, int SaleCount, uint MedianSalePrice);

public sealed record SniperSalesSnapshot(uint ItemId, uint WorldId, DateTimeOffset RetrievedAt,
    IReadOnlyList<SniperQualityBaseline> Qualities);

public sealed record SniperDeal(string Key, uint ItemId, string ItemName, uint WorldId, string WorldName,
    bool IsHq, uint PricePerUnit, uint Quantity, uint MedianSalePrice, int SalesInHistoryWindow,
    DateTimeOffset DetectedAt, string? ListingId, bool IsOneGilAlert = false);

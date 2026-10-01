using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace RetainerPricer;

/// <summary>
/// Compatibility view for AgentRetainer fields absent from some API 15 SDK snapshots.
/// Offsets are copied from the explicit AgentRetainer layout in FFXIVClientStructs, not guessed.
/// FFXIVClientStructs source: https://github.com/aers/FFXIVClientStructs/blob/main/FFXIVClientStructs/FFXIV/Client/UI/Agent/AgentRetainer.cs
/// If its declared size differs, every field read through this overlay is disabled.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x68D0)]
internal unsafe struct RetainerAgentView
{
    internal const int ExpectedAgentSize = 0x68D0;
    [FieldOffset(0x58)] public InventoryType SellInventoryType;
    [FieldOffset(0x5C)] public int SellInventorySlot;
    [FieldOffset(0x4B78)] public int SellPriceLimit;
    [FieldOffset(0x4B7C)] public int SellUnitPrice;
    [FieldOffset(0x4B80)] public int SellQuantity;
    [FieldOffset(0x4B88)] public int SellListCount;
    [FieldOffset(0x4B90)] public SellListEntries SellList;
    [FieldOffset(0x688C)] public uint SellListAddonId;
    [FieldOffset(0x6890)] public uint SellAddonId;

    public static bool IsSupported => sizeof(AgentRetainer) == ExpectedAgentSize;
    public static RetainerAgentView* For(AgentRetainer* agent) => IsSupported && agent != null ? (RetainerAgentView*)agent : null;

    [InlineArray(20)]
    public struct SellListEntries
    {
        private SellListEntry _first;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x168)]
    public struct SellListEntry
    {
        [FieldOffset(0x00)] public uint ItemId;
        [FieldOffset(0x70)] public int Quantity;
        [FieldOffset(0x148)] public ushort InventorySlot;
    }
}

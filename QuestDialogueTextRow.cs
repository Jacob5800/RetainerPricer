using Lumina.Excel;
using Lumina.Text.ReadOnly;

namespace RetainerPricer;

[Sheet("PleaseSpecifyTheSheetExplicitly")]
public readonly struct QuestDialogueTextRow : IExcelRow<QuestDialogueTextRow>
{
    private readonly ExcelPage page;
    private readonly uint offset;

    public uint RowId { get; }
    public ExcelPage ExcelPage => page;
    public uint RowOffset => offset;
    public ReadOnlySeString Key => page.ReadString(offset, offset);
    public ReadOnlySeString Value => page.ReadString(offset + 4, offset);

    public QuestDialogueTextRow(ExcelPage page, uint offset, uint row)
        => (this.page, this.offset, RowId) = (page, offset, row);

    static QuestDialogueTextRow IExcelRow<QuestDialogueTextRow>.Create(ExcelPage page, uint offset, uint row)
        => new(page, offset, row);
}

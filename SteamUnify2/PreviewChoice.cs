using System.ComponentModel;

namespace SteamUnify;

internal sealed class PreviewChoice : INotifyPropertyChanged
{
    private readonly ChangeLine original;
    private readonly Action changed;
    private bool name;
    private bool group;
    public PreviewChoice(ChangeLine line, Action changed)
    {
        original = line;
        this.changed = changed;
        name = CanBulkPickName;
        group = HasGroup;
    }
    public int Id => original.Row.Id;
    public string Title => original.Title;
    public string Name => original.NameEdit;
    public string Collection => original.GroupEdit;
    public bool HasName => original.Edit.Name != null;
    public bool HasGroup => original.Edit.Collection != null;
    public bool Machine => HasName && original.Row.Suggested.Source == "机器翻译";
    public bool NeedsNameApproval => HasName && original.Row.MachineReview;
    public bool CanBulkPickName => HasName && !NeedsNameApproval;
    public bool CanToggleGame => CanBulkPickName || HasGroup || name;
    public void SetBulkName(bool picked) => Set(picked && (CanBulkPickName || name), group);
    public bool PickName
    {
        get => name;
        set => Set(value && HasName, group);
    }
    public bool PickGroup
    {
        get => group;
        set => Set(name, value && HasGroup);
    }
    public bool? PickGame
    {
        get => !name && !group ? false : name == HasName && group == HasGroup ? true : null;
        set => Set(value == true && (CanBulkPickName || name), value == true && HasGroup);
    }
    private void Set(bool newName, bool newGroup)
    {
        if (newName == name && newGroup == group) return;
        name = newName;
        group = newGroup;
        PropertyChanged?.Invoke(this, new(nameof(PickName)));
        PropertyChanged?.Invoke(this, new(nameof(PickGroup)));
        PropertyChanged?.Invoke(this, new(nameof(PickGame)));
        PropertyChanged?.Invoke(this, new(nameof(CanToggleGame)));
        changed();
    }
    public ChangeLine? Selected => name || group
        ? new(original.Row,original.Edit with { Name = name ? original.Edit.Name : null, Collection = group ? original.Edit.Collection : null })
        : null;
    public event PropertyChangedEventHandler? PropertyChanged;
}

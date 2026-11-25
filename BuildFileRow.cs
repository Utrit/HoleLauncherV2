using Avalonia.Media;

namespace HoleLauncher;

internal sealed record BuildFileRow(string Title, IReadOnlyList<BuildFileRow> Children, string? Status = null,
    string? Description = null, IBrush? Accent = null, ModIcon? Icon = null)
{
    private static readonly IBrush StrongAccent = Brush.Parse("#E77B72");
    private static readonly IBrush SoftAccent = Brush.Parse("#58AEEC");
    private static readonly IBrush OptionalAccent = Brush.Parse("#8FCB82");

    public bool HasFile => Status is not null;
    public bool IsFolder => !HasFile;
    public bool Compact { get; init; }
    public bool CanSelect { get; init; }
    public bool Selected { get; set; }
    public string? SelectionKey { get; init; }
    public BuildFileRow[] Dependencies { get; init; } = [];
    public bool HasDescription => !Compact && !string.IsNullOrWhiteSpace(Description);
    public int IconSize => Compact ? 22 : 40;

    public static BuildFileRow Leaf(BuildFile file, LocalFileState state, ModIcon? icon)
    {
        var status = state switch
        {
            LocalFileState.Current => "Актуален",
            LocalFileState.Outdated => "Нужно обновить",
            LocalFileState.Unavailable => "Не удалось проверить",
            LocalFileState.Missing => "Не установлен",
            _ => "Проверяем…"
        };
        var accent = file.Type switch
        {
            "Strong" => StrongAccent,
            "Soft" => SoftAccent,
            _ => OptionalAccent
        };
        return new(file.DisplayName, [], status, file.Card?.Description, accent, icon);
    }
}

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
    public bool CanOverride { get; init; }
    public bool Unmanaged { get; init; }
    public string ManagementMark => Unmanaged ? "✕" : "✓";
    public IBrush ManagementColor => Unmanaged ? StrongAccent : OptionalAccent;
    public string ManagementHint => Unmanaged ? "Вернуть управление лаунчеру" : "Отключить управление лаунчером";
    public BuildFileRow[] Dependencies { get; init; } = [];
    public bool HasDescription => !Compact && !string.IsNullOrWhiteSpace(Description);
    public int IconSize => Compact ? 22 : 40;

    private static string StatusText(LocalFileState state) => state switch
    {
        LocalFileState.Current => "Актуален",
        LocalFileState.Outdated => "Нужно обновить",
        LocalFileState.Unavailable => "Не удалось проверить",
        LocalFileState.Missing => "Не установлен",
        _ => "Проверяем…"
    };

    public static BuildFileRow Leaf(BuildFile file, LocalFileState state, ModIcon? icon)
    {
        var accent = file.Type switch
        {
            "Strong" => StrongAccent,
            "Soft" => SoftAccent,
            _ => OptionalAccent
        };
        return new(file.DisplayName, [], StatusText(state), file.Card?.Description, accent, icon);
    }
}

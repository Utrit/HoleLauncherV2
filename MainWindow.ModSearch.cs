using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace HoleLauncher;

public sealed partial class MainWindow
{
    private readonly List<ModMatch> modMatches = [];
    private int modMatchIndex;

    private sealed record ModMatch(BuildFileRow Row, BuildFileRow[] Parents);

    private void UpdateModMatches(BuildFileRow[] rows)
    {
        modMatches.Clear();
        if (ModSearchBar?.IsVisible != true)
            return;

        Collect(rows, []);
        modMatchIndex = Math.Clamp(modMatchIndex, 0, Math.Max(0, modMatches.Count - 1));
        ShowModMatchCount();

        void Collect(IEnumerable<BuildFileRow> siblings, BuildFileRow[] parents)
        {
            foreach (var row in siblings)
            {
                if (row.IsFolder)
                {
                    Collect(row.Children, [.. parents, row]);
                    continue;
                }

                if (row.SearchMatch)
                    modMatches.Add(new(row, parents));
                foreach (var dependency in row.Dependencies)
                    if (dependency.SearchMatch)
                        modMatches.Add(new(row, parents));
            }
        }
    }

    private void ShowModMatchCount()
    {
        ModSearchCount.Text = string.IsNullOrWhiteSpace(ModSearch.Text) ? "" :
            modMatches.Count == 0 ? "Нет совпадений" : $"{modMatchIndex + 1} / {modMatches.Count}";
        PreviousModMatch.IsEnabled = NextModMatch.IsEnabled = modMatches.Count > 0;
    }

    private void PreviousModMatchClicked(object? sender, RoutedEventArgs e) => MoveModMatch(-1);

    private void NextModMatchClicked(object? sender, RoutedEventArgs e) => MoveModMatch(1);

    private void CloseModSearchClicked(object? sender, RoutedEventArgs e) => CloseModSearch();

    private void CloseModSearch()
    {
        ModSearchBar.IsVisible = false;
        ModSearch.Clear();
        (TreeViewMode.IsChecked == true ? (Control)FileTree : FileList).Focus();
    }

    private void MoveModMatch(int direction)
    {
        if (modMatches.Count == 0)
            return;

        modMatchIndex = (modMatchIndex + direction + modMatches.Count) % modMatches.Count;
        ShowModMatchCount();
        ScrollToModMatch();
    }

    private void ScrollToModMatch()
    {
        if (modMatches.Count == 0)
            return;

        var match = modMatches[modMatchIndex];
        Dispatcher.UIThread.Post(() =>
        {
            if (!ModSearchBar.IsVisible || modMatches.Count == 0 ||
                !ReferenceEquals(modMatches[modMatchIndex], match))
                return;

            if (TreeViewMode.IsChecked != true)
            {
                FileList.SelectedItem = match.Row;
                FileList.ScrollIntoView(match.Row);
                return;
            }

            FileTree.UpdateLayout();
            ItemsControl branch = FileTree;
            foreach (var parent in match.Parents)
            {
                if (branch.ContainerFromItem(parent) is not TreeViewItem node)
                    return;
                node.IsExpanded = true;
                FileTree.UpdateLayout();
                branch = node;
            }

            FileTree.SelectedItem = match.Row;
            branch.ContainerFromItem(match.Row)?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}

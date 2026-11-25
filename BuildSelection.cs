namespace HoleLauncher;

internal sealed class BuildSelection(PublishedBuild build, IReadOnlySet<string>? selected = null, bool serverMode = false)
{
    private readonly BuildFile[] files = build.Files.Where(file => IsForSide(file, serverMode)).ToArray();

    public static bool IsForSide(BuildFile file, bool serverMode) =>
        (serverMode ? file.ServerSide : file.ClientSide) != false;

    public static string Key(BuildFile file) => file.ProjectId ?? file.Path;

    public IEnumerable<BuildFile> Dependencies(BuildFile file) =>
        files.Where(candidate => candidate.ProjectId is { } project &&
            (file.Dependencies ?? []).Contains(project, StringComparer.Ordinal));

    public IEnumerable<BuildFile> Roots()
    {
        var dependencies = files.SelectMany(Dependencies).ToHashSet();
        return files.Where(file => !dependencies.Contains(file) || selected?.Contains(Key(file)) == true);
    }

    public BuildFile[] Installation()
    {
        var included = new HashSet<BuildFile>();
        foreach (var file in files.Where(file => !file.IsOptional || selected?.Contains(Key(file)) == true))
            Include(file);
        return files.Where(included.Contains).ToArray();

        void Include(BuildFile file)
        {
            if (!included.Add(file))
                return;
            foreach (var dependency in Dependencies(file))
                Include(dependency);
        }
    }
}

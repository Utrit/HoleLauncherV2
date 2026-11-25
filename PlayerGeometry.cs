using System.Numerics;
using System.Text.Json;
using Avalonia.Platform;

namespace HoleLauncher;

internal sealed record PlayerGeometry(PlayerGeometry.Cube[] Classic, PlayerGeometry.Cube[] Slim, PlayerGeometry.Cape CapeModel)
{


    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    internal readonly record struct Cube(string Bone, Vector3 Origin, Vector3 Size, int U, int V, float Inflate, bool Mirror);

    internal readonly record struct Cape(Cube[] Cubes, Vector3 Pivot, float RotationY);

    public static async Task<PlayerGeometry> LoadAsync()
    {
        var classic = ReadAsync("humanoid.custom.geo.json");
        var mobs = ReadAsync("mobs.json");
        await Task.WhenAll(classic, mobs);
        using var classicDocument = await classic;
        using var mobsDocument = await mobs;
        var root = mobsDocument.RootElement;
        var capeModel = root.GetProperty("geometry.cape");
        var capeBone = capeModel.GetProperty("bones").EnumerateArray().First(bone => bone.GetProperty("name").GetString() == "cape");
        var cape = new Cape(ReadCubes(capeModel), ReadVector(capeBone.GetProperty("pivot")),
            capeBone.GetProperty("rotation")[1].GetSingle());
        return new(ReadCubes(classicDocument.RootElement.GetProperty("minecraft:geometry")[0]),
            ReadCubes(root.GetProperty("geometry.humanoid.customSlim")), cape);
    }

    public Cube[] ForModel(string model) => model == "slim" ? Slim : Classic;

    private static async Task<JsonDocument> ReadAsync(string path)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://HoleLauncher/Assets/Models/{path}"));
        return await JsonDocument.ParseAsync(stream, JsonOptions);
    }

    private static Cube[] ReadCubes(JsonElement model)
    {
        var cubes = new List<Cube>();
        foreach (var bone in model.GetProperty("bones").EnumerateArray())
        {
            if (!bone.TryGetProperty("cubes", out var entries))
                continue;
            var name = bone.GetProperty("name").GetString()!;
            foreach (var entry in entries.EnumerateArray())
            {
                var uv = entry.GetProperty("uv");
                var mirror = entry.TryGetProperty("mirror", out var cubeMirror)
                    ? cubeMirror.GetBoolean()
                    : bone.TryGetProperty("mirror", out var boneMirror) && boneMirror.GetBoolean();
                cubes.Add(new(name, ReadVector(entry.GetProperty("origin")), ReadVector(entry.GetProperty("size")),
                    uv[0].GetInt32(), uv[1].GetInt32(),
                    entry.TryGetProperty("inflate", out var inflate) ? inflate.GetSingle() : 0, mirror));
            }
        }
        return cubes.ToArray();
    }

    private static Vector3 ReadVector(JsonElement value) => new(value[0].GetSingle(), value[1].GetSingle(), value[2].GetSingle());
}

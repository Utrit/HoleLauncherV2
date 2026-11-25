using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;


namespace HoleLauncher;

public sealed class SkinPreview : Control
{
    private const float InitialYaw = -0.45f;
    private const float InitialPitch = 0.12f;
    private const float DragSensitivity = 0.012f;
    private float yaw = InitialYaw;
    private float pitch = InitialPitch;
    private Point? dragPoint;
    private const int TextureWidth = 64;
    private const int ModernTextureHeight = 64;
    private const int LegacyTextureHeight = 32;
    private const float CapeRestAngle = -0.17f;
    private byte[]? texture;
    private byte[]? cape;
    private static readonly Lazy<byte[]> DefaultTexture = new(() =>
    {
        using var source = AssetLoader.Open(new Uri("avares://HoleLauncher/Assets/Textures/steve.png"));
        using var buffer = new MemoryStream();
        source.CopyTo(buffer);
        return buffer.ToArray();
    });
    private PlayerGeometry? geometry;
    private Task<PlayerGeometry>? loading;
    private string model = "classic";

    internal async Task LoadGeometryAsync()
    {
        if (geometry is not null)
            return;

        try
        {
            geometry = await (loading ??= PlayerGeometry.LoadAsync());
            InvalidateVisual();
        }
        finally
        {
            loading = null;
        }
    }

    public void SetModel(string value)
    {
        model = value;
        InvalidateVisual();
    }

    public void SetSkin(byte[]? png)
    {
        texture = png;
        InvalidateVisual();
    }

    public void SetCape(byte[]? png)
    {
        cape = png;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (geometry is { } loaded)
            context.Custom(new PlayerDrawOperation(new Rect(Bounds.Size), texture ?? DefaultTexture.Value, loaded.ForModel(model), cape, loaded.CapeModel, yaw, pitch));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        dragPoint = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (dragPoint is not { } previous)
            return;
        var point = e.GetPosition(this);
        yaw = (yaw + (float)(point.X - previous.X) * DragSensitivity) % MathF.Tau;
        pitch = Math.Clamp(pitch - (float)(point.Y - previous.Y) * DragSensitivity, -0.7f, 0.7f);
        dragPoint = point;
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) => e.Pointer.Capture(null);

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e) => dragPoint = null;

    private sealed class PlayerDrawOperation(Rect bounds, byte[]? png, PlayerGeometry.Cube[] cubes, byte[]? capePng, PlayerGeometry.Cape capeModel, float yaw, float pitch) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;
        public bool HitTest(Point point) => bounds.Contains(point);
        public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
        public void Dispose() { }

        private readonly record struct Face(Vector3 Origin, Vector3 Right, Vector3 Down, SKRect Texture, SKColor Color, bool Mirror, bool Cape);

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature(typeof(ISkiaSharpApiLeaseFeature)) is not ISkiaSharpApiLeaseFeature feature)
                return;

            using var lease = feature.Lease();
            var canvas = lease.SkCanvas;
            using var decoded = png is null ? null : SKBitmap.Decode(png);
            var skin = decoded is { Width: TextureWidth, Height: LegacyTextureHeight or ModernTextureHeight } ? decoded : null;
            var faces = new List<Face>();
            foreach (var cube in cubes)
            {
                var texture = cube;
                if (skin?.Height == LegacyTextureHeight && cube.V >= LegacyTextureHeight)
                {
                    var counterpart = cube.Bone switch { "leftArm" => "rightArm", "leftLeg" => "rightLeg", _ => null };
                    if (counterpart is null)
                        continue;
                    var source = Array.Find(cubes, candidate => candidate.Bone == counterpart);
                    texture = cube with { U = source.U, V = source.V, Mirror = true };
                }
                if (skin is null && cube.Inflate > 0)
                    continue;

                var color = cube.Bone switch
                {
                    "head" => new SKColor(192, 140, 108),
                    "leftLeg" or "rightLeg" => new SKColor(48, 62, 101),
                    _ => new SKColor(55, 135, 156)
                };
                var origin = cube.Origin;
                AddBox(faces, origin.X, 16 - origin.Y - cube.Size.Y, -origin.Z - cube.Size.Z,
                    (int)cube.Size.X, (int)cube.Size.Y, (int)cube.Size.Z, texture.U, texture.V,
                    color, cube.Inflate, skin?.Height == LegacyTextureHeight && texture.Mirror);
            }

            using var decodedCape = capePng is null ? null : SKBitmap.Decode(capePng);
            var capeTexture = decodedCape is { Width: TextureWidth, Height: LegacyTextureHeight } ? decodedCape : null;
            if (capeTexture is not null)
            {
                var pivot = new Vector3(capeModel.Pivot.X, 16 - capeModel.Pivot.Y, -capeModel.Pivot.Z);
                var pose = Matrix4x4.CreateRotationY(-capeModel.RotationY * MathF.PI / 180f, pivot) *
                    Matrix4x4.CreateRotationX(CapeRestAngle, pivot);
                foreach (var cube in capeModel.Cubes)
                {
                    var origin = cube.Origin;
                    AddBox(faces, origin.X, 16 - origin.Y - cube.Size.Y, -origin.Z - cube.Size.Z,
                        (int)cube.Size.X, (int)cube.Size.Y, (int)cube.Size.Z, cube.U, cube.V,
                        SKColors.White, cube.Inflate, cube.Mirror, pose);
                }
            }

            var rotation = Matrix4x4.CreateRotationY(yaw) * Matrix4x4.CreateRotationX(pitch);
            using var frame = SkinRasterizer.Render(bounds.Width, bounds.Height,
                faces.Select(face => new SkinRasterizer.Face(
                    Vector3.Transform(face.Origin, rotation),
                    Vector3.TransformNormal(face.Right, rotation),
                    Vector3.TransformNormal(face.Down, rotation),
                    face.Texture, face.Color, face.Mirror, face.Cape)),
                skin, capeTexture);
            if (frame is not null)
            {
                using var image = SKImage.FromBitmap(frame);
                canvas.DrawImage(image, new SKRect(0, 0, (float)bounds.Width, (float)bounds.Height),
                    new SKSamplingOptions(SKFilterMode.Nearest));
            }
        }

        private static void AddBox(List<Face> faces, float x, float y, float z,
            int width, int height, int depth, int u, int v, SKColor color, float shell, bool mirror, Matrix4x4? capePose = null)
        {
            var left = x - shell;
            var top = y - shell;
            var back = z - shell;
            var w = width + shell * 2;
            var h = height + shell * 2;
            var d = depth + shell * 2;
            var right = left + w;
            var bottom = top + h;
            var front = back + d;
            Add(new(left, top, front), new(w, 0, 0), new(0, h, 0), u + depth, v + depth, width, height);
            Add(new(right, top, back), new(-w, 0, 0), new(0, h, 0), u + 2 * depth + width, v + depth, width, height);
            Add(new(left, top, back), new(0, 0, d), new(0, h, 0), (mirror ? u + depth + width : u), v + depth, depth, height);
            Add(new(right, top, front), new(0, 0, -d), new(0, h, 0), (mirror ? u : u + depth + width), v + depth, depth, height);
            Add(new(left, top, back), new(w, 0, 0), new(0, 0, d), u + depth, v, width, depth);
            Add(new(left, bottom, front), new(w, 0, 0), new(0, 0, -d), u + depth + width, v, width, depth);

            void Add(Vector3 origin, Vector3 r, Vector3 down, int tx, int ty, int tw, int th)
            {
                if (capePose is { } pose)
                {
                    origin = Vector3.Transform(origin, pose);
                    r = Vector3.TransformNormal(r, pose);
                    down = Vector3.TransformNormal(down, pose);
                }
                faces.Add(new(origin, r, down, new SKRect(tx, ty, tx + tw, ty + th), color, mirror, capePose.HasValue));
            }
        }
    }
}

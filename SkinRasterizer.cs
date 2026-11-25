using System.Numerics;
using SkiaSharp;

namespace HoleLauncher;

internal static class SkinRasterizer
{
    private const int MaxSide = 720;
    private const float EdgeEpsilon = 0.0001f;

    internal readonly record struct Face(Vector3 Origin, Vector3 Right, Vector3 Down,
        SKRect Texture, SKColor Color, bool Mirror, bool Cape);

    public static SKBitmap? Render(double width, double height, IEnumerable<Face> source, SKBitmap? skin, SKBitmap? cape)
    {
        if (width <= 0 || height <= 0)
            return null;

        var resolution = Math.Min(1, MaxSide / Math.Max(width, height));
        var w = (int)Math.Max(1, Math.Ceiling(width * resolution));
        var h = (int)Math.Max(1, Math.Ceiling(height * resolution));
        var scale = MathF.Min(w / 23f, h / 38f);
        var pixels = new SKColor[w * h];
        var depth = new float[pixels.Length];
        Array.Fill(depth, float.NegativeInfinity);
        var skinPixels = skin?.Pixels;
        var capePixels = cape?.Pixels;
        var faces = source.Where(f => Vector3.Cross(f.Right, f.Down).Z > EdgeEpsilon)
            .OrderBy(f => f.Origin.Z + (f.Right.Z + f.Down.Z) * 0.5f).ToArray();

        foreach (var face in faces)
            Draw(face, translucent: false);
        foreach (var face in faces)
            Draw(face, translucent: true);

        return new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul) { Pixels = pixels };

        void Draw(Face face, bool translucent)
        {
            var origin = new Vector2(w * 0.5f + face.Origin.X * scale, h * 0.5f + face.Origin.Y * scale);
            var right = new Vector2(face.Right.X, face.Right.Y) * scale;
            var down = new Vector2(face.Down.X, face.Down.Y) * scale;
            var det = right.X * down.Y - right.Y * down.X;
            if (det <= EdgeEpsilon)
                return;

            var min = origin + Vector2.Min(Vector2.Zero, right) + Vector2.Min(Vector2.Zero, down);
            var max = origin + Vector2.Max(Vector2.Zero, right) + Vector2.Max(Vector2.Zero, down);
            var texture = face.Cape ? capePixels : skinPixels;
            var stride = face.Cape ? cape?.Width ?? 0 : skin?.Width ?? 0;
            var left = Math.Max(0, (int)MathF.Floor(min.X));
            var top = Math.Max(0, (int)MathF.Floor(min.Y));
            var lastX = Math.Min(w, (int)MathF.Ceiling(max.X));
            var lastY = Math.Min(h, (int)MathF.Ceiling(max.Y));
            for (var y = top; y < lastY; y++)
            for (var x = left; x < lastX; x++)
            {
                var dx = x + 0.5f - origin.X;
                var dy = y + 0.5f - origin.Y;
                var u = (dx * down.Y - dy * down.X) / det;
                var v = (dy * right.X - dx * right.Y) / det;
                if (u is < 0 or >= 1 || v is < 0 or >= 1)
                    continue;

                var offset = y * w + x;
                var z = face.Origin.Z + u * face.Right.Z + v * face.Down.Z;
                if (z <= depth[offset])
                    continue;

                var color = face.Color;
                if (texture is not null)
                {
                    var sx = Math.Min((int)face.Texture.Right - 1, (int)(face.Texture.Left + (face.Mirror ? 1 - u : u) * face.Texture.Width));
                    var sy = Math.Min((int)face.Texture.Bottom - 1, (int)(face.Texture.Top + v * face.Texture.Height));
                    if ((uint)sx >= (uint)stride || (uint)(sy * stride + sx) >= (uint)texture.Length)
                        continue;
                    color = texture[sy * stride + sx];
                }

                if (color.Alpha == 0 || translucent != (color.Alpha < 255))
                    continue;

                if (translucent)
                    pixels[offset] = Blend(color, pixels[offset]);
                else
                {
                    pixels[offset] = color;
                    depth[offset] = z;
                }
            }
        }
    }

    private static SKColor Blend(SKColor source, SKColor destination)
    {
        var a = source.Alpha / 255f;
        var visibleBackground = destination.Alpha / 255f * (1 - a);
        var total = a + visibleBackground;
        byte Mix(byte front, byte behind) => (byte)MathF.Round((front * a + behind * visibleBackground) / total);
        return new(Mix(source.Red, destination.Red), Mix(source.Green, destination.Green),
            Mix(source.Blue, destination.Blue), (byte)MathF.Round(total * 255));
    }
}

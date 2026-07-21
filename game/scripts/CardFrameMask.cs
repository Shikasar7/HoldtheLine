using Godot;

namespace HoldTheLine.Game;

/// <summary>
/// Extracts the central transparent illustration aperture from each painted faction frame. The mask is
/// sampled in card coordinates, so irregular frame thickness and ornaments become the crop instead of a
/// shared percentage rectangle.
/// </summary>
internal static class CardFrameMask
{
    internal sealed record Data(Texture2D Texture, Rect2 Bounds);

    private static readonly Dictionary<ulong, Data> Cache = new();
    private static readonly Shader MaskShader = new()
    {
        Code = """
            shader_type canvas_item;
            render_mode unshaded;
            uniform sampler2D aperture_mask : filter_linear, repeat_disable;
            uniform vec4 card_rect;

            void fragment() {
                vec4 art = texture(TEXTURE, UV);
                vec2 card_uv = card_rect.xy + UV * card_rect.zw;
                float inside = texture(aperture_mask, card_uv).a;
                COLOR = vec4(art.rgb, art.a * inside);
            }
            """,
    };

    internal static Data Get(Texture2D frame)
    {
        ulong id = frame.GetInstanceId();
        if (Cache.TryGetValue(id, out var cached)) return cached;
        var built = Build(frame);
        Cache[id] = built;
        return built;
    }

    internal static TextureRect BuildArt(Texture2D art, Texture2D frame, CardArtFrame framing, Vector2 cardSize)
    {
        var mask = Get(frame);
        var aperturePos = mask.Bounds.Position * cardSize;
        var apertureSize = mask.Bounds.Size * cardSize;
        float cover = Mathf.Max(apertureSize.X / art.GetWidth(), apertureSize.Y / art.GetHeight());
        var drawSize = new Vector2(art.GetWidth(), art.GetHeight()) * cover * framing.Zoom;
        var drawPos = aperturePos + (apertureSize - drawSize) / 2f
            + new Vector2(apertureSize.X * 0.5f * framing.OffsetX, apertureSize.Y * 0.5f * framing.OffsetY);

        var node = BattleTheme.Art(art, drawPos, drawSize, TextureRect.StretchModeEnum.Scale);
        var material = new ShaderMaterial { Shader = MaskShader };
        material.SetShaderParameter("aperture_mask", mask.Texture);
        material.SetShaderParameter("card_rect", new Vector4(
            drawPos.X / cardSize.X,
            drawPos.Y / cardSize.Y,
            drawSize.X / cardSize.X,
            drawSize.Y / cardSize.Y));
        node.Material = material;
        return node;
    }

    private static Data Build(Texture2D frame)
    {
        var source = frame.GetImage();
        source.Convert(Image.Format.Rgba8);
        int width = source.GetWidth(), height = source.GetHeight(), centerX = width / 2;
        int minRun = Mathf.RoundToInt(width * 0.4f);
        var left = new int[height];
        var right = new int[height];
        Array.Fill(left, -1);
        Array.Fill(right, -1);

        // On each row, find the transparent run containing the card centre. Large runs belong to the
        // illustration window; tiny runs are holes in emblems or lower-panel ornaments.
        for (int y = 0; y < height; y++)
        {
            if (source.GetPixel(centerX, y).A >= 0.5f) continue;
            int l = centerX, r = centerX;
            while (l > 0 && source.GetPixel(l - 1, y).A < 0.5f) l--;
            while (r < width - 1 && source.GetPixel(r + 1, y).A < 0.5f) r++;
            if (r - l + 1 < minRun) continue;
            left[y] = l;
            right[y] = r;
        }

        int seed = Mathf.Clamp(Mathf.RoundToInt(height * 0.4f), 0, height - 1);
        if (left[seed] < 0)
        {
            int bestDistance = height;
            for (int y = 0; y < height; y++)
            {
                int distance = Mathf.Abs(y - seed);
                if (left[y] >= 0 && distance < bestDistance)
                {
                    seed = y;
                    bestDistance = distance;
                }
            }
        }

        int top = seed, bottom = seed;
        while (top > 0 && left[top - 1] >= 0) top--;
        while (bottom < height - 1 && left[bottom + 1] >= 0) bottom++;
        int detectedTop = top;

        var validLeft = new List<int>();
        var validRight = new List<int>();
        for (int y = top; y <= bottom; y++)
            if (left[y] >= 0)
            {
                validLeft.Add(left[y]);
                validRight.Add(right[y]);
            }

        // Clamp occasional open decorative rows to robust side limits, preventing the mask from escaping
        // through a pipe/rivet gap that connects the window transparency to the outside transparency.
        validLeft.Sort();
        validRight.Sort();
        int robustLeft = validLeft.Count > 0 ? validLeft[Mathf.Clamp((int)(validLeft.Count * 0.05f), 0, validLeft.Count - 1)] : Mathf.RoundToInt(width * 0.165f);
        int robustRight = validRight.Count > 0 ? validRight[Mathf.Clamp((int)(validRight.Count * 0.95f), 0, validRight.Count - 1)] : Mathf.RoundToInt(width * 0.84f);

        // Iron Vow and Wildpack have a large opaque crest/skull hanging into an otherwise open upper
        // aperture. Their centre scan only becomes "wide" below that ornament, which used to create a
        // false empty band. Extend the mask behind the ornament; the painted frame above naturally hides
        // the covered portion. Other frames already begin above 18% and need no extension.
        if (detectedTop > Mathf.RoundToInt(height * 0.18f))
            top = Mathf.Max(0, detectedTop - Mathf.RoundToInt(height * 0.105f));

        var maskImage = Image.CreateEmpty(width, height, false, Image.Format.Rgba8);
        maskImage.Fill(Colors.Transparent);
        for (int y = top; y <= bottom; y++)
        {
            int l = y < detectedTop || left[y] < 0 ? robustLeft : Mathf.Max(left[y], robustLeft);
            int r = y < detectedTop || right[y] < 0 ? robustRight : Mathf.Min(right[y], robustRight);
            for (int x = l; x <= r; x++) maskImage.SetPixel(x, y, Colors.White);
        }

        var bounds = new Rect2(
            (float)robustLeft / width,
            (float)top / height,
            (float)(robustRight - robustLeft + 1) / width,
            (float)(bottom - top + 1) / height);
        return new Data(ImageTexture.CreateFromImage(maskImage), bounds);
    }
}

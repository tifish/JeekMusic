using SkiaSharp;

namespace JeekMusic.Covers;

/// <summary>封面图片的解码、缩放、转 JPEG（SkiaSharp，Avalonia 本来就带着它，兼容 Native AOT）。</summary>
public static class CoverImages
{
    /// <summary>保存的封面最大边长；不大于它的 JPEG 原样保存（同移动端 CoverStore.MAX_SAVED_PX）。</summary>
    public const int MaxSavedPx = 1200;

    /// <summary>缩放到最长边不超过 <paramref name="size"/>，编码成 JPEG；不是图片时返回 null。</summary>
    public static byte[]? Scaled(byte[] bytes, int size, int quality = 90)
    {
        using var bitmap = DecodeScaled(bytes, size);
        if (bitmap is null) return null;
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
        return data?.ToArray();
    }

    /// <summary>不大于 <see cref="MaxSavedPx"/> 的 JPEG 原样保存；其他格式或过大的图片重新编码。不是图片时返回 null。</summary>
    public static byte[]? ToJpeg(byte[] bytes)
    {
        using var codec = Codec(bytes);
        if (codec is null) return null;
        if (codec.EncodedFormat == SKEncodedImageFormat.Jpeg && Math.Max(codec.Info.Width, codec.Info.Height) <= MaxSavedPx) return bytes;
        return Scaled(bytes, MaxSavedPx, 92);
    }

    /// <summary>是不是能解开的图片。</summary>
    public static bool IsImage(byte[] bytes)
    {
        using var codec = Codec(bytes);
        return codec is not null && codec.Info.Width > 0;
    }

    static SKCodec? Codec(byte[] bytes)
    {
        try
        {
            using var data = SKData.CreateCopy(bytes);
            return SKCodec.Create(data);
        }
        catch (Exception) { return null; }
    }

    static SKBitmap? DecodeScaled(byte[] bytes, int size)
    {
        try
        {
            using var codec = Codec(bytes);
            if (codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 ||
                (long)codec.Info.Width * codec.Info.Height > 64_000_000) return null;
            var dimensions = codec.GetScaledDimensions(Math.Min(1f, (float)size / Math.Max(codec.Info.Width, codec.Info.Height)));
            using var original = SKBitmap.Decode(codec, new SKImageInfo(dimensions.Width, dimensions.Height));
            if (original is null || original.Width <= 0 || original.Height <= 0) return null;
            var scale = (float)size / Math.Max(original.Width, original.Height);
            if (scale >= 1f) return original.Copy();
            var info = new SKImageInfo(Math.Max(1, (int)(original.Width * scale)), Math.Max(1, (int)(original.Height * scale)));
            return original.Resize(info, new SKSamplingOptions(SKCubicResampler.Mitchell));
        }
        catch (Exception) { return null; }
    }
}

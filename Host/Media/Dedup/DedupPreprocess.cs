// Image loading + preprocessing for the duplicate detector, mirroring the python `imagededup` pipeline
// (as ported/cross-validated in ImageDedupCli). One deliberate difference from the CLI: the decoder is
// Magick.NET (already shipped by LiteBox for thumbs/WebP) instead of ImageSharp — pixel values can differ
// by a hair from the CLI, which is fine: LiteBox only compares ITS OWN fingerprints with each other, and
// the dup-param fingerprint (engine version salt) namespaces the cached results.
//
// Key rule inherited from imagededup: inputs of ANY size/aspect-ratio are STRETCHED to the target size
// (no aspect preservation) — that is precisely what makes fingerprints comparable across dimensions.

#nullable enable

using System;
using ImageMagick;

namespace LbApiHost.Host.Media.Dedup;

internal static class DedupPreprocess
{
    /// <summary>
    /// Hash path (dhash / phash): grayscale (BT.601, like PIL 'L') BEFORE the resize (imagededup's order),
    /// then Lanczos stretch-resize to (width x height). Returns a [height, width] matrix of 0..255 levels.
    /// </summary>
    public static double[,] LoadGrayResized(string path, int width, int height)
    {
        using var img = new MagickImage(path);
        if (img.HasAlpha) { img.BackgroundColor = MagickColors.White; img.Alpha(AlphaOption.Remove); }
        img.Grayscale(PixelIntensityMethod.Rec601Luma);
        img.FilterType = FilterType.Lanczos;
        img.Resize(new MagickGeometry((uint)width, (uint)height) { IgnoreAspectRatio = true });

        var g = new double[height, width];
        using var pixels = img.GetPixels();
        // Single-channel export (grayscale): one byte per pixel, row-major.
        byte[]? bytes = pixels.ToByteArray(0, 0, (uint)width, (uint)height, "R");
        if (bytes == null) throw new InvalidOperationException("pixel export failed: " + path);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                g[y, x] = bytes[y * width + x];
        return g;
    }

    /// <summary>
    /// CNN path — imagededup / torchvision MobileNetV3 parity:
    ///   Resize((256,256), bilinear, stretch) -> CenterCrop(224) -> scale [0,1] -> Normalize(ImageNet).
    /// Returns a CHW float buffer [3*224*224] ready to wrap in a [1,3,224,224] tensor.
    /// </summary>
    public static float[] LoadCnnInput(string path)
    {
        // ImageNet, RGB order (like torchvision).
        ReadOnlySpan<float> mean = stackalloc float[] { 0.485f, 0.456f, 0.406f };
        ReadOnlySpan<float> std = stackalloc float[] { 0.229f, 0.224f, 0.225f };

        using var img = new MagickImage(path);
        if (img.HasAlpha) { img.BackgroundColor = MagickColors.White; img.Alpha(AlphaOption.Remove); }
        img.FilterType = FilterType.Triangle;                 // bilinear = torchvision default
        img.Resize(new MagickGeometry(256, 256) { IgnoreAspectRatio = true });
        img.Crop(new MagickGeometry(16, 16, 224, 224));       // center crop 224 from 256
        img.ResetPage();

        using var pixels = img.GetPixels();
        byte[]? rgb = pixels.ToByteArray(0, 0, 224, 224, "RGB");
        if (rgb == null) throw new InvalidOperationException("pixel export failed: " + path);

        const int hw = 224 * 224;
        var t = new float[3 * hw];
        for (int i = 0; i < hw; i++)
        {
            int s = i * 3;
            t[i] = (rgb[s] / 255f - mean[0]) / std[0];              // R plane
            t[hw + i] = (rgb[s + 1] / 255f - mean[1]) / std[1];     // G plane
            t[2 * hw + i] = (rgb[s + 2] / 255f - mean[2]) / std[2]; // B plane
        }
        return t;
    }
}

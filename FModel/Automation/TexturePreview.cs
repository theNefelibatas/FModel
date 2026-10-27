using System;
using SkiaSharp;

namespace FModel.Automation;

internal static class TexturePreview
{
    public static SKBitmap Create(SKBitmap image, int size, string channel)
    {
        using var channels = channel == "rgba" ? null : new SKBitmap(image.Width, image.Height, SKColorType.Rgba8888, SKAlphaType.Opaque);
        if (channels != null)
        {
            // Select data channels before filtering: premultiplication discards RGB beneath zero alpha.
            for (var y = 0; y < image.Height; y++)
                for (var x = 0; x < image.Width; x++)
                {
                    var color = image.GetPixel(x, y);
                    var value = channel switch { "r" => color.Red, "g" => color.Green, "b" => color.Blue, "a" => color.Alpha,
                        "rgb" => (byte) 0, _ => throw new ArgumentException("Unknown texture channel.") };
                    channels.SetPixel(x, y, channel == "rgb" ? new SKColor(color.Red, color.Green, color.Blue) : new SKColor(value, value, value));
                }
        }
        var ratio = Math.Min(1d, (double) size / Math.Max(image.Width, image.Height));
        var preview = new SKBitmap(Math.Max(1, (int) (image.Width * ratio)), Math.Max(1, (int) (image.Height * ratio)));
        using var pixels = preview.PeekPixels();
        if ((channels ?? image).ScalePixels(pixels, SKFilterQuality.Medium)) return preview;
        preview.Dispose();
        throw new InvalidOperationException("Could not resize the texture preview.");
    }
}

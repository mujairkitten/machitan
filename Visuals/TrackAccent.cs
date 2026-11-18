using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Color = Windows.UI.Color;

namespace Machitan.Visuals;

/// <summary>
/// The accent colour of a cover: the strongest hue in it (pixels weighted by how colourful and
/// bright they are, near-greys and near-blacks ignored), lightened enough to read on black.
/// Null when the picture has no real colour (a black-and-white cover), so the caller uses its
/// neutral colour instead of an invented one.
/// </summary>
internal static class TrackAccent
{
    private const uint SampleSide = 40;
    private const int Bins = 36;

    internal static async Task<Color?> FromStreamAsync(IRandomAccessStream stream)
    {
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = SampleSide,
            ScaledHeight = SampleSide,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        PixelDataProvider data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        return Pick(data.DetachPixelData());
    }

    private static Color? Pick(byte[] pixels)
    {
        var weight = new double[Bins];
        var red = new double[Bins];
        var green = new double[Bins];
        var blue = new double[Bins];
        int count = pixels.Length / 4;
        if (count == 0)
        {
            return null;
        }

        double total = 0;
        for (int i = 0; i + 3 < pixels.Length; i += 4)
        {
            double b = pixels[i] / 255.0;
            double g = pixels[i + 1] / 255.0;
            double r = pixels[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b));
            double min = Math.Min(r, Math.Min(g, b));
            double saturation = max <= 0 ? 0 : (max - min) / max;
            if (max < 0.18 || saturation < 0.2)
            {
                continue;
            }

            double hue = Hue(r, g, b, max, min);
            int bin = Math.Clamp((int)(hue / (360.0 / Bins)), 0, Bins - 1);
            double w = saturation * max;
            weight[bin] += w;
            red[bin] += r * w;
            green[bin] += g * w;
            blue[bin] += b * w;
            total += w;
        }

        // Too little colour to call it an accent.
        if (total < count * 0.02)
        {
            return null;
        }

        // The strongest hue, with its neighbours, so a hue on a bin edge is not split in two.
        int best = 0;
        double bestWeight = -1;
        for (int i = 0; i < Bins; i++)
        {
            double around = weight[i] + (0.5 * weight[(i + 1) % Bins]) + (0.5 * weight[(i + Bins - 1) % Bins]);
            if (around > bestWeight)
            {
                bestWeight = around;
                best = i;
            }
        }

        double sumWeight = 0;
        double sumR = 0;
        double sumG = 0;
        double sumB = 0;
        foreach (int i in new[] { (best + Bins - 1) % Bins, best, (best + 1) % Bins })
        {
            sumWeight += weight[i];
            sumR += red[i];
            sumG += green[i];
            sumB += blue[i];
        }

        if (sumWeight <= 0)
        {
            return null;
        }

        return Readable(sumR / sumWeight, sumG / sumWeight, sumB / sumWeight);
    }

    private static double Hue(double r, double g, double b, double max, double min)
    {
        double delta = max - min;
        if (delta <= 0)
        {
            return 0;
        }

        double hue = max == r ? 60 * (((g - b) / delta) % 6)
            : max == g ? 60 * (((b - r) / delta) + 2)
            : 60 * (((r - g) / delta) + 4);
        return hue < 0 ? hue + 360 : hue;
    }

    /// <summary>Keeps the hue, makes it clearly coloured and light enough for text and bars on black.</summary>
    private static Color Readable(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double lightness = (max + min) / 2;
        double delta = max - min;
        double saturation = delta <= 0 ? 0 : delta / (1 - Math.Abs((2 * lightness) - 1));
        double hue = Hue(r, g, b, max, min);

        saturation = Math.Clamp(saturation, 0.45, 0.9);
        lightness = Math.Clamp(lightness, 0.6, 0.75);

        double c = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        double x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
        double m = lightness - (c / 2);
        (double r1, double g1, double b1) = hue switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Microsoft.UI.ColorHelper.FromArgb(
            0xFF,
            (byte)Math.Round(Math.Clamp(r1 + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(g1 + m, 0, 1) * 255),
            (byte)Math.Round(Math.Clamp(b1 + m, 0, 1) * 255));
    }
}

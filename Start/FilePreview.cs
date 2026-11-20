using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Machitan.Core;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Machitan.Start;

/// <summary>
/// A file's card in search, like Windows' search shows it: name, date and type, Windows'
/// thumbnail of the file, the action strip right below the picture, then where it is, its
/// size in pixels (pictures and videos) and its file size. Everything is read from the file
/// and Windows' thumbnail cache; a newer file replaces a load still running.
/// </summary>
internal sealed class FilePreview : Grid
{
    private const double PictureHeightDip = 140;
    private const double ThumbnailDip = 260;

    private readonly ResultActions _actions;
    private readonly Func<double> _scale;
    private readonly TextBlock _name;
    private readonly TextBlock _subtitle;
    private readonly Image _picture;
    private readonly Border _stripHost;
    private readonly StackPanel _details;
    private int _version;

    /// <param name="actions">Builds the file's action strip.</param>
    /// <param name="scale">The window's scale (for a sharp thumbnail).</param>
    internal FilePreview(ResultActions actions, Func<double> scale)
    {
        _actions = actions;
        _scale = scale;
        Padding = new Thickness(12, 10, 12, 12);
        RowSpacing = 6;
        for (int i = 0; i < 5; i++)
        {
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        _name = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        _subtitle = new TextBlock { FontSize = 12, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis };
        _picture = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = PictureHeightDip - 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var pictureBox = new Grid
        {
            Height = PictureHeightDip,
            CornerRadius = new CornerRadius(Visuals.Radii.Medium),
            Background = new SolidColorBrush(ColorHelper.FromArgb(0x18, 0x80, 0x80, 0x80)),
        };
        pictureBox.Children.Add(_picture);
        _stripHost = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
        _details = new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 0) };

        SetRow(_subtitle, 1);
        SetRow(pictureBox, 2);
        SetRow(_stripHost, 3);
        SetRow(_details, 4);
        Children.Add(_name);
        Children.Add(_subtitle);
        Children.Add(pictureBox);
        Children.Add(_stripHost);
        Children.Add(_details);
    }

    /// <summary>The file shown now, or null.</summary>
    internal string? FilePath { get; private set; }

    internal void Show(string path)
    {
        if (string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        FilePath = path;
        int version = ++_version;
        _name.Text = Path.GetFileName(path);
        AutomationProperties.SetName(this, Fmt.F(Strings.PreviewOf, _name.Text));
        _subtitle.Text = string.Empty;
        _picture.Source = null;
        _stripHost.Child = _actions.FileStrip(path);
        _details.Children.Clear();
        AddDetail(Strings.FileLocation, Path.GetDirectoryName(path) ?? string.Empty);
        _ = FillAsync(path, version);
    }

    internal void Clear()
    {
        FilePath = null;
        _version++;
        _name.Text = string.Empty;
        _subtitle.Text = string.Empty;
        _picture.Source = null;
        _stripHost.Child = null;
        _details.Children.Clear();
    }

    private async Task FillAsync(string path, int version)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                _subtitle.Text = Strings.FileGone;
                return;
            }

            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            if (version != _version)
            {
                return;
            }

            string dot = " " + (char)0xB7 + " ";
            _subtitle.Text = Fmt.F(Strings.FileModified, info.LastWriteTime.ToString("d MMM yyyy", CultureInfo.CurrentCulture)) + dot + file.DisplayType;

            uint pixels = (uint)Math.Ceiling(ThumbnailDip * _scale());
            using (StorageItemThumbnail? thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, pixels, ThumbnailOptions.ResizeThumbnail))
            {
                if (version != _version)
                {
                    return;
                }

                if (thumbnail is not null && thumbnail.Size > 0)
                {
                    var bitmap = new BitmapImage();
                    await bitmap.SetSourceAsync(thumbnail);
                    if (version != _version)
                    {
                        return;
                    }

                    _picture.Source = bitmap;
                }
            }

            string? dimensions = null;
            string type = file.ContentType ?? string.Empty;
            if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                ImageProperties image = await file.Properties.GetImagePropertiesAsync();
                dimensions = Dimensions(image.Width, image.Height);
            }
            else if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
            {
                VideoProperties video = await file.Properties.GetVideoPropertiesAsync();
                dimensions = Dimensions(video.Width, video.Height);
            }

            if (version != _version)
            {
                return;
            }

            if (dimensions is not null)
            {
                AddDetail(Strings.FileDimensions, dimensions);
            }

            AddDetail(Strings.FileSize, SizeText(info.Length));
        }
        catch (Exception ex)
        {
            Log.Warn("Search: the preview of a file could not be read.", ex);
        }
    }

    private void AddDetail(string label, string value)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock { Text = label, FontSize = 12, Opacity = 0.7 };
        var text = new TextBlock
        {
            Text = value,
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        ToolTipService.SetToolTip(text, value);
        SetColumn(text, 1);
        row.Children.Add(name);
        row.Children.Add(text);
        _details.Children.Add(row);
    }

    private static string? Dimensions(uint width, uint height)
    {
        return width > 0 && height > 0
            ? width.ToString(CultureInfo.CurrentCulture) + " x " + height.ToString(CultureInfo.CurrentCulture)
            : null;
    }

    /// <summary>Like File Explorer: bytes, KB, MB, GB, with up to three figures ("4,26 MB" where a comma is the decimal mark).</summary>
    private static string SizeText(long bytes)
    {
        if (bytes < 1024)
        {
            return Fmt.F(Strings.FileBytes, bytes);
        }

        string[] units = { "KB", "MB", "GB", "TB" };
        double value = bytes / 1024.0;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value < 10 ? "0.00" : value < 100 ? "0.0" : "0";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + units[unit];
    }
}

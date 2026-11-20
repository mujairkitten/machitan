using System;
using System.Collections.Generic;
using Machitan.Config;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Start;

/// <summary>One button next to Start's power button: its id in settings, name, icon, and what it opens.</summary>
/// <param name="Target">What opens: a link, a program or a shell folder; null for Machitan's own settings.</param>
internal sealed record StartFolder(
    string Id,
    string Name,
    Func<ShellSettings, bool> On,
    Action<ShellSettings, bool> Set,
    string? Target);

/// <summary>
/// Start's buttons next to the power button, as in Windows' Start > Folders, plus one for
/// Machitan's own settings. Each can be turned on, and their order changed, in Settings > Start.
/// </summary>
internal static class StartFolders
{
    internal const string MachitanId = "Machitan";

    /// <summary>
    /// "Window Wrench" (16 px, regular) from Microsoft's Fluent UI System Icons, which Segoe Fluent
    /// Icons does not include. MIT License, Copyright (c) 2020 Microsoft Corporation; see
    /// THIRD-PARTY-NOTICES.md. Source: github.com/microsoft/fluentui-system-icons,
    /// assets/Window Wrench/SVG/ic_fluent_window_wrench_16_regular.svg.
    /// </summary>
    private const string WindowWrench16 = "M2 4.5C2 3.11929 3.11929 2 4.5 2H11.5C12.8807 2 14 3.11929 14 4.5V5.62359C13.7856 5.31096 13.441 5.08092 13.0072 5.03041C12.8331 5.01014 12.658 5 12.4828 5H13V4.5C13 3.67157 12.3284 3 11.5 3H4.5C3.67157 3 3 3.67157 3 4.5V5H12.4828C11.4788 5 10.4727 5.33306 9.64707 6H3V11.5C3 12.3284 3.67157 13 4.5 13H5.49203C5.26705 13.3031 5.11922 13.6454 5.04855 14H4.5C3.11929 14 2 12.8807 2 11.5V4.5ZM13.2422 6.36103C13.2221 6.22476 13.1382 6.10415 13 6.05024C12.9669 6.03734 12.9307 6.0283 12.8916 6.02374C11.8603 5.90365 10.7861 6.23917 9.99496 7.0303C9.00271 8.02255 8.72716 9.46009 9.16831 10.6997L6.44156 13.4264C6.27428 13.5936 6.15454 13.7904 6.08231 14C5.90032 14.528 6.02008 15.137 6.44156 15.5584C7.0303 16.1472 7.98485 16.1472 8.57359 15.5584L11.3003 12.8317C12.5399 13.2728 13.9775 12.9973 14.9697 12.005C15.7608 11.2139 16.0964 10.1397 15.9763 9.10845C15.9336 8.74177 15.4952 8.63685 15.2341 8.89788L14.259 9.87301C13.6703 10.4618 12.7157 10.4618 12.127 9.87301C11.5382 9.28427 11.5382 8.32972 12.127 7.74098L13.1021 6.76585C13.1782 6.68974 13.2232 6.59854 13.2397 6.50647C13.2485 6.45753 13.2491 6.40834 13.2422 6.36103Z";

    private static readonly FontFamily FluentFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private static readonly StartFolder[] All =
    {
        new("Settings", Strings.FolderSettings, s => s.FolderSettings, (s, v) => s.FolderSettings = v, "ms-settings:"),
        new("Explorer", Strings.FolderExplorer, s => s.FolderExplorer, (s, v) => s.FolderExplorer = v, "explorer.exe"),
        new("Documents", Strings.FolderDocuments, s => s.FolderDocuments, (s, v) => s.FolderDocuments = v, "shell:Personal"),
        new("Downloads", Strings.FolderDownloads, s => s.FolderDownloads, (s, v) => s.FolderDownloads = v, "shell:Downloads"),
        new("Music", Strings.FolderMusic, s => s.FolderMusic, (s, v) => s.FolderMusic = v, "shell:My Music"),
        new("Pictures", Strings.FolderPictures, s => s.FolderPictures, (s, v) => s.FolderPictures = v, "shell:My Pictures"),
        new("Videos", Strings.FolderVideos, s => s.FolderVideos, (s, v) => s.FolderVideos = v, "shell:My Video"),
        new("Network", Strings.FolderNetwork, s => s.FolderNetwork, (s, v) => s.FolderNetwork = v, "shell:NetworkPlacesFolder"),
        new("Personal", Strings.FolderPersonal, s => s.FolderPersonal, (s, v) => s.FolderPersonal = v, "shell:Profile"),
        new(MachitanId, Strings.FolderMachitan, s => s.FolderMachitan, (s, v) => s.FolderMachitan = v, null),
    };

    /// <summary>Every button, in the order set in Settings.</summary>
    internal static List<StartFolder> InOrder(ShellSettings settings)
    {
        var ordered = new List<StartFolder>(All.Length);
        foreach (string id in ShellSettings.ParseFolderOrder(settings.FolderOrder))
        {
            StartFolder? folder = Array.Find(All, f => f.Id == id);
            if (folder is not null)
            {
                ordered.Add(folder);
            }
        }

        return ordered;
    }

    /// <summary>
    /// The button's icon at <paramref name="size"/> DIP: Windows' own glyph for each folder;
    /// Machitan's settings get the Window Wrench icon, in the text colour like the others.
    /// </summary>
    internal static FrameworkElement Icon(StartFolder folder, double size)
    {
        if (folder.Id == MachitanId)
        {
            var path = new PathIcon
            {
                Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), WindowWrench16),
                Width = 16,
                Height = 16,
            };
            return new Viewbox
            {
                Width = size,
                Height = size,
                Child = path,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        int glyph = folder.Id switch
        {
            "Settings" => 0xE713,
            "Explorer" => 0xEC50,
            "Documents" => 0xE8A5,
            "Downloads" => 0xE896,
            "Music" => 0xE8D6,
            "Pictures" => 0xE91B,
            "Videos" => 0xE714,
            "Network" => 0xE968,
            _ => 0xE77B,
        };
        return new FontIcon { Glyph = ((char)glyph).ToString(), FontFamily = FluentFont, FontSize = size };
    }
}

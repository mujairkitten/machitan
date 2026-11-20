using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Start;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Flyouts;

/// <summary>
/// The results of the bar's search box (Box mode), next to the bar and lined up with the box,
/// while you type in the box itself. Same search as Start: app names, best fit first (see
/// SearchRank), "files:" for files in your own folders, "apps:" for apps only. The first
/// result is selected; Up and Down move the selection, Enter opens it (the box sends those
/// keys here), and a click opens any result. It never takes the keyboard: the typing stays in
/// the bar's box. The bar closes it (Esc, a click elsewhere, opening a result).
/// A pointed app shows a "..." button (or a right-click) for Windows' Start menu entries and
/// the app's jump list tasks. Files get a second box beside the list: the selected or pointed
/// file's preview with its actions right below the picture.
/// </summary>
public sealed partial class SearchResultsFlyout : FlyoutWindow
{
    private const double WidthDip = 360;
    private const double InnerPadDip = 8;
    private const double HeaderHeightDip = 28;
    private const double RowHeightDip = 44;
    private const double NoteHeightDip = 40;
    private const double IconDip = 24;
    private const int MaxApps = 8;
    private const int MaxFiles = 8;
    private const double PreviewWidthDip = 300;
    private const double PreviewHeightDip = 330;
    private const double GapDip = 8;

    /// <summary>Same as Start's list icons, so the app list is built at one size.</summary>
    private const double CatalogIconDip = 32;

    private static readonly string FileGlyph = ((char)0xE8A5).ToString();

    private readonly StackPanel _panel;
    private readonly TextBlock _header;
    private readonly TextBlock _note;
    private readonly Grid _listBox;
    private readonly Grid _previewBox;
    private readonly FilePreview _preview;
    private readonly ResultActions _actions;
    private readonly List<(Grid Row, string Target)> _results = new();
    private int _menusOpen;
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    private Action? _opened;
    private CancellationTokenSource? _fileSearch;
    private string _query = string.Empty;
    private bool _filesMode;
    private int _selected = -1;
    private int _anchorLeft;

    internal SearchResultsFlyout(ShellHost host)
        : base(host, "Search results", takesFocus: false)
    {
        _header = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0.7,
            Height = HeaderHeightDip,
            Padding = new Thickness(8, 6, 8, 0),
        };
        _note = new TextBlock
        {
            FontSize = 13,
            Opacity = 0.7,
            Height = NoteHeightDip,
            Padding = new Thickness(8, 10, 8, 0),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _panel = new StackPanel { Padding = new Thickness(InnerPadDip) };
        _actions = new ResultActions(host, Hwnd, CloseAfterAction, () => Host.Catalog.RefreshIfStale(CatalogPixels(), IsDark));
        _preview = new FilePreview(_actions, Scale);

        _listBox = MakeBox(_panel, BoxRadius);
        _listBox.Width = WidthDip;
        _previewBox = MakeBox(_preview, BoxRadius);
        _previewBox.Width = PreviewWidthDip;
        _previewBox.Height = PreviewHeightDip;
        _previewBox.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_previewBox, 1);

        var layout = new Grid { ColumnSpacing = GapDip, HorizontalAlignment = HorizontalAlignment.Left };
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.Children.Add(_listBox);
        layout.Children.Add(_previewBox);
        Root.Children.Add(layout);

        Host.Catalog.Changed += OnCatalogChanged;
        Closed += (_, _) =>
        {
            Host.Catalog.Changed -= OnCatalogChanged;
            _fileSearch?.Cancel();
        };
    }

    /// <summary>
    /// Shows the results for <paramref name="text"/> next to the bar, its left edge at
    /// <paramref name="anchorLeft"/> (physical pixels: the search box's left edge). Empty text
    /// closes it. <paramref name="opened"/> runs after a result was opened.
    /// </summary>
    internal void ShowFor(string text, int anchorLeft, Action opened)
    {
        _opened = opened;
        _anchorLeft = anchorLeft;
        if (text.Trim().Length == 0)
        {
            HideFlyout();
            return;
        }

        Host.Catalog.RefreshIfStale(CatalogPixels(), IsDark);
        ParseQuery(text);
        Rebuild();
    }

    private int CatalogPixels() => IconService.PickSize((int)Math.Ceiling(CatalogIconDip * Scale()));

    /// <summary>True while an app's "..." menu is open (the bar keeps the search going meanwhile).</summary>
    internal bool MenuOpen => _menusOpen > 0;

    /// <summary>True when the window (or a menu it opened) is this panel's own.</summary>
    internal bool Owns(nint window)
    {
        return window != 0 && (window == Hwnd || User32.GetAncestor(window, User32.GA_ROOTOWNER) == Hwnd);
    }

    /// <summary>Raised when an app's "..." menu closed.</summary>
    internal event Action? MenuClosed;

    /// <summary>After an action that opens something: close, as opening a result does.</summary>
    private void CloseAfterAction()
    {
        HideFlyout();
        _opened?.Invoke();
    }

    /// <summary>Up (-1) or Down (+1) from the bar's search box.</summary>
    internal void MoveSelection(int delta)
    {
        if (!IsOpen || _results.Count == 0)
        {
            return;
        }

        Select(Math.Clamp(_selected + delta, 0, _results.Count - 1));
    }

    /// <summary>Enter in the bar's search box: opens the selected result. False when there is none.</summary>
    internal bool OpenSelected()
    {
        if (!IsOpen || _selected < 0 || _selected >= _results.Count)
        {
            return false;
        }

        Open(_results[_selected].Target);
        return true;
    }

    private protected override void OnHiding()
    {
        _fileSearch?.Cancel();
        _fileSearch = null;
    }

    private protected override void OnSettingsApplied(ShellSettings settings)
    {
        if (IsOpen)
        {
            Select(_selected);
        }
    }

    private void OnCatalogChanged()
    {
        // The app list arrived or changed while the results are shown.
        if (IsOpen && !_filesMode)
        {
            Rebuild();
        }
    }

    private void ParseQuery(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("files:", StringComparison.OrdinalIgnoreCase))
        {
            _filesMode = true;
            _query = trimmed.Substring("files:".Length).Trim();
        }
        else if (trimmed.StartsWith("apps:", StringComparison.OrdinalIgnoreCase))
        {
            _filesMode = false;
            _query = trimmed.Substring("apps:".Length).Trim();
        }
        else
        {
            _filesMode = false;
            _query = trimmed;
        }
    }

    private void Rebuild()
    {
        _panel.Children.Clear();
        _results.Clear();
        _selected = -1;
        ShowPreview(null);

        if (_filesMode)
        {
            _header.Text = Strings.SearchFiles;
            _panel.Children.Add(_header);
            if (_query.Length < 2)
            {
                _fileSearch?.Cancel();
                ShowNote(Strings.SearchFilesShort);
            }
            else
            {
                ShowNote(Strings.SearchSearchingFiles);
                _ = RunFileSearchAsync(_query);
            }

            ShowSized();
            return;
        }

        _header.Text = Strings.SearchApps;
        _panel.Children.Add(_header);

        if (!Host.Catalog.IsReady)
        {
            ShowNote(Strings.SearchReadingApps);
            ShowSized();
            return;
        }

        var ranked = new List<(int Rank, int Index)>();
        IReadOnlyList<AppEntry> apps = Host.Catalog.Apps;
        for (int i = 0; i < apps.Count; i++)
        {
            int rank = _query.Length == 0 ? SearchRank.NoMatch : SearchRank.Of(apps[i].Name, _query);
            if (rank != SearchRank.NoMatch)
            {
                ranked.Add((rank, i));
            }
        }

        ranked.Sort((a, b) => a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Index.CompareTo(b.Index));
        for (int i = 0; i < ranked.Count && i < MaxApps; i++)
        {
            AppEntry app = apps[ranked[i].Index];
            AddResult(MakeAppRow(app), app.LaunchTarget, file: false);
        }

        if (_results.Count == 0)
        {
            ShowNote(Strings.SearchNoAppMatch);
        }

        Select(0);
        ShowSized();
    }

    private async Task RunFileSearchAsync(string query)
    {
        _fileSearch?.Cancel();
        var cancel = new CancellationTokenSource();
        _fileSearch = cancel;

        List<string> files;
        try
        {
            files = await Task.Run(() => FileSearch.Find(query, MaxFiles, cancel.Token));
        }
        catch (Exception ex)
        {
            Log.Warn("Bar search: the file search failed.", ex);
            files = new List<string>();
        }

        // A newer search, or the box closing, replaces this one.
        if (cancel.IsCancellationRequested || !IsOpen || !_filesMode || query != _query)
        {
            return;
        }

        _panel.Children.Clear();
        _panel.Children.Add(_header);
        foreach (string file in files)
        {
            AddResult(MakeFileRow(file), file, file: true);
        }

        if (files.Count == 0)
        {
            ShowNote(Strings.SearchNoFileMatch);
        }

        Select(0);
        ShowSized();
    }

    private void ShowNote(string text)
    {
        _note.Text = text;
        if (!_panel.Children.Contains(_note))
        {
            _panel.Children.Add(_note);
        }
    }

    /// <summary>A result row: its main button opens it; pointing at a file previews it.</summary>
    private void AddResult(Grid row, string target, bool file)
    {
        if (row.Children[0] is Button main)
        {
            main.Click += (_, _) => Open(target);
        }

        int index = _results.Count;
        _results.Add((row, target));
        _panel.Children.Add(row);
        if (file)
        {
            row.PointerEntered += (_, _) => Select(index);
        }
    }

    /// <summary>The file's preview box beside the list, or none.</summary>
    private void ShowPreview(string? path)
    {
        if (path is null)
        {
            _preview.Clear();
            _previewBox.Visibility = Visibility.Collapsed;
            return;
        }

        _preview.Show(path);
        _previewBox.Visibility = Visibility.Visible;
    }

    private void Open(string target)
    {
        HideFlyout();
        Launcher.Start(target);
        _opened?.Invoke();
    }

    /// <summary>Marks one result as the one Enter opens (a soft background), or none.</summary>
    private void Select(int index)
    {
        _selected = _results.Count == 0 ? -1 : Math.Clamp(index, 0, _results.Count - 1);
        Windows.UI.Color mark = IsDark
            ? ColorHelper.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)
            : ColorHelper.FromArgb(0x14, 0x00, 0x00, 0x00);
        for (int i = 0; i < _results.Count; i++)
        {
            _results[i].Row.Background = new SolidColorBrush(i == _selected ? mark : Colors.Transparent);
        }

        if (_filesMode)
        {
            bool wasShown = _previewBox.Visibility == Visibility.Visible;
            ShowPreview(_selected >= 0 ? _results[_selected].Target : null);
            if (IsOpen && wasShown != (_previewBox.Visibility == Visibility.Visible))
            {
                ShowSized();
            }
        }
    }

    private void ShowSized()
    {
        double height = 2 * InnerPadDip;
        foreach (UIElement child in _panel.Children)
        {
            height += child switch
            {
                Grid => RowHeightDip,
                _ when ReferenceEquals(child, _header) => HeaderHeightDip,
                _ => NoteHeightDip,
            };
        }

        _listBox.Height = height;
        bool preview = _previewBox.Visibility == Visibility.Visible;
        double contentWidth = preview ? WidthDip + GapDip + PreviewWidthDip : WidthDip;
        double contentHeight = preview ? Math.Max(height, PreviewHeightDip) : height;

        // Both boxes sit on the bar's side of the window.
        VerticalAlignment toBar = Host.Settings.Current.DockEdge == DockEdge.Top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        _listBox.VerticalAlignment = toBar;
        _previewBox.VerticalAlignment = toBar;

        double scale = Scale();
        int pad = (int)Math.Round(PadDip * scale);
        int gap = (int)Math.Round(GapToBarDip * scale);
        int width = (int)Math.Round((contentWidth + (2 * PadDip)) * scale);
        int windowHeight = (int)Math.Round((contentHeight + (2 * PadDip)) * scale);
        PresentAt(PopupPlacement.BesideBarFrom(Host, _anchorLeft - pad, width, windowHeight, pad, gap));
    }

    /// <summary>An app row: the app, and on pointing a "..." button for its menu (or the uninstall question).</summary>
    private Grid MakeAppRow(AppEntry app)
    {
        var content = new Grid { ColumnSpacing = 12 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var name = new TextBlock
        {
            Text = app.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(name, 1);
        content.Children.Add(MakeAppIcon(app));
        content.Children.Add(name);

        Button main = MakeRow(content, app.Name);
        var more = new Button
        {
            Content = new FontIcon { Glyph = ((char)0xE712).ToString(), FontFamily = IconFont, FontSize = 14 },
            Width = 32,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 6, 0),
            CornerRadius = new CornerRadius(SmallRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            IsTabStop = false,
            Opacity = 0,
        };
        AutomationProperties.SetName(more, Fmt.F(Strings.MoreOptionsFor, app.Name));
        ToolTipService.SetToolTip(more, Strings.MoreOptions);
        var holder = new Border { Child = more };
        Grid row = MakeRowGrid(main, holder);

        FrameworkElement? question = null;
        void Ask()
        {
            main.Visibility = Visibility.Collapsed;
            more.Visibility = Visibility.Collapsed;
            Grid asking = _actions.UninstallQuestion(app, () =>
            {
                if (question is not null)
                {
                    row.Children.Remove(question);
                }

                question = null;
                main.Visibility = Visibility.Visible;
                more.Visibility = Visibility.Visible;
            });
            Grid.SetColumnSpan(asking, 2);
            question = asking;
            row.Children.Add(asking);
        }

        more.Click += (_, _) => ShowAppMenu(app, Ask, more, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
        main.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            ShowAppMenu(app, Ask, main, e.TryGetPosition(main, out Windows.Foundation.Point at)
                ? new FlyoutShowOptions { Position = at }
                : new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft });
        };
        row.PointerEntered += (_, _) => more.Opacity = 1;
        row.PointerExited += (_, _) => more.Opacity = 0;
        return row;
    }

    /// <summary>The app's menu (Windows' Start entries and its jump list tasks).</summary>
    private void ShowAppMenu(AppEntry app, Action ask, FrameworkElement target, FlyoutShowOptions options)
    {
        try
        {
            MenuFlyout menu = _actions.AppMenu(app, ask, IconService.PickSize((int)Math.Ceiling(16 * Scale())));
            menu.Opening += (_, _) => _menusOpen++;
            menu.Closed += (_, _) =>
            {
                _menusOpen = Math.Max(0, _menusOpen - 1);
                MenuClosed?.Invoke();
            };
            menu.ShowAt(target, options);
        }
        catch (Exception ex)
        {
            Log.Error("Bar search: the menu for " + app.Name + " could not be opened.", ex);
        }
    }

    private Grid MakeFileRow(string path)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(new FontIcon
        {
            Glyph = FileGlyph,
            FontFamily = IconFont,
            FontSize = 18,
            Width = IconDip,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = WidthDip - (2 * InnerPadDip) - IconDip - 40 };
        words.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis });
        words.Children.Add(new TextBlock
        {
            Text = Path.GetDirectoryName(path) ?? string.Empty,
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        content.Children.Add(words);
        return MakeRowGrid(MakeRow(content, Path.GetFileName(path)), null);
    }

    private static Button MakeRow(UIElement content, string name)
    {
        var button = new Button
        {
            Content = content,
            Height = RowHeightDip,
            Padding = new Thickness(8, 0, 8, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(SmallRadius),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            IsTabStop = false,
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    /// <summary>The row: the main button first (it opens the result), then the action strip's place.</summary>
    private static Grid MakeRowGrid(Button main, Border? strip)
    {
        var row = new Grid
        {
            Height = RowHeightDip,
            CornerRadius = new CornerRadius(SmallRadius),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(main);
        if (strip is not null)
        {
            Grid.SetColumn(strip, 1);
            row.Children.Add(strip);
        }

        return row;
    }

    /// <summary>The app's own icon from the app list (as Start shows it), or its first letter.</summary>
    private FrameworkElement MakeAppIcon(AppEntry app)
    {
        var image = new Image
        {
            Width = IconDip,
            Height = IconDip,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
        };

        if (_icons.TryGetValue(app.Key, out ImageSource? cached))
        {
            image.Source = cached;
            return image;
        }

        try
        {
            if (app.Icon is IconPixels pixels)
            {
                ImageSource source = StartMenuWindow.ToBitmap(pixels);
                _icons[app.Key] = source;
                image.Source = source;
                return image;
            }

            if (app.LogoBytes is byte[] bytes)
            {
                _ = SetLogoAsync(image, app.Key, bytes);
                return image;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Bar search: an app icon could not be shown.", ex);
        }

        return new TextBlock
        {
            Text = app.Name.Length > 0 ? app.Name.Substring(0, 1).ToUpperInvariant() : "?",
            Width = IconDip,
            FontSize = IconDip * 0.62,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private async Task SetLogoAsync(Image image, string key, byte[] bytes)
    {
        try
        {
            if (!_icons.TryGetValue(key, out ImageSource? source))
            {
                source = await StartMenuWindow.LoadBitmapAsync(bytes, IconService.PickSize((int)Math.Ceiling(IconDip * Scale())));
                _icons[key] = source;
            }

            image.Source = source;
        }
        catch (Exception ex)
        {
            Log.Warn("Bar search: a store-app logo could not be shown.", ex);
        }
    }
}

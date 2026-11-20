using System;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace Machitan.Bar;

/// <summary>
/// Box mode's search box: you type in the bar itself, and the results show in a flyout next to
/// the bar, lined up with the box (see SearchResultsFlyout). The bar never takes the keyboard
/// on its own (it must not pull the focus from the app being used); a click in the box is the
/// one time it does. Up / Down pick a result, Enter opens it, Esc clears the box and gives the
/// keyboard back to the window that had it; a click anywhere else ends the search too.
/// </summary>
public sealed partial class BarWindow
{
    private TextBox? _searchBox;
    private bool _typing;
    private bool _typingNoted;
    private bool _resultsMenuWatched;

    /// <summary>
    /// The search box, built like Start's: a capsule with a light fill and a bare text field
    /// inside it (no border or fill of its own), with the same inner margin. A press anywhere
    /// in the capsule starts typing.
    /// </summary>
    private FrameworkElement BuildSearchBox()
    {
        double height = Math.Max(PillHeightDip() - 14, 28);
        var capsule = new Grid
        {
            Width = SearchBoxWidthDip,
            Height = height,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(Radii.Capsule(height)),
            Background = new SolidColorBrush(IsDarkTheme()
                ? ColorHelper.FromArgb(24, 255, 255, 255)
                : ColorHelper.FromArgb(20, 0, 0, 0)),
        };

        var box = new TextBox
        {
            Margin = new Thickness(6, 0, 6, 0),
            MinHeight = 0,
            PlaceholderText = Strings.BarSearchPlaceholder,
            IsSpellCheckEnabled = false,
            IsTextPredictionEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (string key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
                 })
        {
            box.Resources[key] = clear;
        }

        AutomationProperties.SetName(box, Strings.SearchBoxName);
        capsule.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => BeginTyping()), handledEventsToo: true);
        box.TextChanged += (_, _) => OnSearchTextChanged();
        box.PreviewKeyDown += OnSearchPreviewKeyDown;
        capsule.Children.Add(box);
        _searchBox = box;
        return capsule;
    }

    /// <summary>A click in the box: the bar takes the keyboard so the typing lands in the box.</summary>
    private void BeginTyping()
    {
        if (_typing || _searchBox is null || _closing)
        {
            return;
        }

        if (_host.Custom.IsOn)
        {
            // Custom mode: a click on the box picks its bubble instead.
            _host.Custom.Select(TargetOf(_searchInApps ? BarLayoutStore.AppsId : BarLayoutStore.StartId));
            return;
        }

        try
        {
            if (!_resultsMenuWatched)
            {
                _resultsMenuWatched = true;
                _host.SearchResults.MenuClosed += OnResultsMenuClosed;
            }

            _host.CloseOtherPopups(_host.SearchResults);
            if (!WindowActivator.BringToFront(_hwnd) && User32.GetForegroundWindow() != _hwnd)
            {
                Log.Warn("Bar search: Windows did not give the bar the keyboard; opening Start's search instead.");
                OpenSearch();
                return;
            }

            _typing = true;
            _searchBox.Focus(FocusState.Pointer);
            if (!_typingNoted)
            {
                _typingNoted = true;
                Log.Info("Bar search: typing in the bar's search box.");
            }

            // Text left from before (it is cleared when a search ends, but be sure).
            if (_searchBox.Text.Length > 0)
            {
                OnSearchTextChanged();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bar search: the search box could not take the typing.", ex);
        }
    }

    private void OnSearchTextChanged()
    {
        if (!_typing || _searchBox is null)
        {
            return;
        }

        try
        {
            _host.SearchResults.ShowFor(_searchBox.Text, SearchBoxLeftPx(), () => EndTyping(restoreFocus: false));
        }
        catch (Exception ex)
        {
            Log.Error("Bar search: the results could not be shown.", ex);
        }
    }

    private void OnSearchPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_typing)
        {
            return;
        }

        try
        {
            switch (e.Key)
            {
                case VirtualKey.Down:
                    e.Handled = true;
                    _host.SearchResults.MoveSelection(1);
                    break;

                case VirtualKey.Up:
                    e.Handled = true;
                    _host.SearchResults.MoveSelection(-1);
                    break;

                case VirtualKey.Enter:
                    // Opening a result ends the search (the flyout calls back).
                    e.Handled = true;
                    _host.SearchResults.OpenSelected();
                    break;

                case VirtualKey.Escape:
                    e.Handled = true;
                    EndTyping(restoreFocus: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bar search: a key could not be handled.", ex);
        }
    }

    /// <summary>
    /// Ends a search: results closed, box cleared. With <paramref name="restoreFocus"/> (Esc),
    /// the window that had the keyboard before gets it back, unless it was minimised since.
    /// </summary>
    private void EndTyping(bool restoreFocus)
    {
        if (!_typing)
        {
            return;
        }

        _typing = false;
        try
        {
            _host.HideSearchResults();
            if (_searchBox is not null)
            {
                _searchBox.Text = string.Empty;
            }

            if (restoreFocus && User32.GetForegroundWindow() == _hwnd)
            {
                nint previous = _host.Tasks.PreviousForeground;
                if (previous != 0 && !User32.IsIconic(previous))
                {
                    WindowActivator.BringToFront(previous);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bar search: ending the search failed.", ex);
        }
    }

    /// <summary>The bar lost the keyboard (a click elsewhere, another window opened): the search ends.</summary>
    private void OnBarActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && !_host.SearchResults.MenuOpen)
        {
            EndTyping(restoreFocus: false);
        }
    }

    /// <summary>
    /// A result's "..." menu closed. A click on another window ends the search; otherwise the
    /// typing goes on in the box (the bar takes the keyboard back if the menu had it).
    /// </summary>
    private void OnResultsMenuClosed()
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!_typing || _searchBox is null)
            {
                return;
            }

            nint front = User32.GetForegroundWindow();
            if (front == _hwnd || (front != 0 && User32.GetAncestor(front, User32.GA_ROOTOWNER) == _hwnd))
            {
                return;
            }

            if (front == 0 || _host.SearchResults.Owns(front))
            {
                WindowActivator.BringToFront(_hwnd);
                _searchBox.Focus(FocusState.Programmatic);
                return;
            }

            EndTyping(restoreFocus: false);
        });
    }

    /// <summary>The search box's left edge on screen (physical pixels).</summary>
    private int SearchBoxLeftPx()
    {
        if (_searchBox is null || !User32.GetWindowRect(_hwnd, out RECT window))
        {
            return 0;
        }

        Windows.Foundation.Point corner = _searchBox.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        return window.Left + (int)Math.Round(corner.X * Scale());
    }
}

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Graphics;

namespace Machitan;

public sealed partial class SettingsWindow : Window
{
    private MachitanSettings _settings;
    private bool _isInitializing = true;

    public SettingsWindow(MachitanSettings settings)
    {
        this.InitializeComponent();
        _settings = settings.Clone();

        AppWindow.Resize(new SizeInt32(480, 760));

        ThemeCombo.SelectedIndex = (int)_settings.Theme;
        TaskbarModeCombo.SelectedIndex = (int)_settings.TaskbarMode;
        DockEdgeCombo.SelectedIndex = (int)_settings.DockEdge;
        DockAlignmentCombo.SelectedIndex = (int)_settings.DockAlignment;
        AcrylicToggle.IsOn = _settings.EnableAcrylic;
        OpacitySlider.Value = _settings.Opacity;
        BarSizeSlider.Value = _settings.BarSize;
        IconSizeSlider.Value = _settings.IconSize;
        UpdateValueLabels();

        ThemeCombo.SelectionChanged += SettingsChangedHandler;
        TaskbarModeCombo.SelectionChanged += SettingsChangedHandler;
        DockEdgeCombo.SelectionChanged += SettingsChangedHandler;
        DockAlignmentCombo.SelectionChanged += SettingsChangedHandler;
        AcrylicToggle.Toggled += SettingsChangedHandler;
        OpacitySlider.ValueChanged += SettingsChangedHandler;
        BarSizeSlider.ValueChanged += SettingsChangedHandler;
        IconSizeSlider.ValueChanged += SettingsChangedHandler;
        _isInitializing = false;
    }

    private void SettingsChangedHandler(object sender, RoutedEventArgs e)
    {
        if (_isInitializing || ThemeCombo.SelectedIndex < 0 || TaskbarModeCombo.SelectedIndex < 0 ||
            DockEdgeCombo.SelectedIndex < 0 || DockAlignmentCombo.SelectedIndex < 0)
        {
            return;
        }

        _settings.Theme = (ShellTheme)ThemeCombo.SelectedIndex;
        _settings.TaskbarMode = (TaskbarMode)TaskbarModeCombo.SelectedIndex;
        _settings.DockEdge = (DockEdge)DockEdgeCombo.SelectedIndex;
        _settings.DockAlignment = (DockAlignment)DockAlignmentCombo.SelectedIndex;
        _settings.EnableAcrylic = AcrylicToggle.IsOn;
        _settings.Opacity = OpacitySlider.Value;
        _settings.BarSize = BarSizeSlider.Value;
        _settings.IconSize = IconSizeSlider.Value;
        _settings.Normalize();

        UpdateValueLabels();
        MachitanSettingsStore.Save(_settings);
        App.MainWindow?.ApplySettings(_settings);
    }

    private void UpdateValueLabels()
    {
        if (OpacityValue == null || BarSizeValue == null || IconSizeValue == null ||
            OpacitySlider == null || BarSizeSlider == null || IconSizeSlider == null)
        {
            return;
        }

        OpacityValue.Text = OpacitySlider.Value.ToString("P0");
        BarSizeValue.Text = $"{BarSizeSlider.Value:0} px";
        IconSizeValue.Text = $"{IconSizeSlider.Value:0} px";
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _isInitializing = true;
        _settings = new MachitanSettings();

        ThemeCombo.SelectedIndex = (int)_settings.Theme;
        TaskbarModeCombo.SelectedIndex = (int)_settings.TaskbarMode;
        DockEdgeCombo.SelectedIndex = (int)_settings.DockEdge;
        DockAlignmentCombo.SelectedIndex = (int)_settings.DockAlignment;
        AcrylicToggle.IsOn = _settings.EnableAcrylic;
        OpacitySlider.Value = _settings.Opacity;
        BarSizeSlider.Value = _settings.BarSize;
        IconSizeSlider.Value = _settings.IconSize;
        _isInitializing = false;

        SettingsChangedHandler(this, new RoutedEventArgs());
    }

    private void OpacitySlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdateValueLabels();
    private void BarSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdateValueLabels();
    private void IconSizeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdateValueLabels();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

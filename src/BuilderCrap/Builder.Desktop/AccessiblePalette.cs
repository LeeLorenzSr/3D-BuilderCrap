using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Builder.Desktop;

/// <summary>Application chrome only. Model materials and persisted View choices are separate.</summary>
public sealed class AccessiblePalette : ResourceDictionary
{
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    public AccessiblePalette()
    {
        Refresh();
        SystemParameters.StaticPropertyChanged += SystemChanged;
        dispatcher.ShutdownStarted += (_, _) => SystemParameters.StaticPropertyChanged -= SystemChanged;
    }
    private void SystemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (dispatcher.CheckAccess()) Refresh();
        else if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(Refresh);
    }
    private void Refresh()
    {
        var contrast = SystemParameters.HighContrast;
        void Brush(string key, string normal, Color system)
        {
            var brush = new SolidColorBrush(contrast ? system : (Color)ColorConverter.ConvertFromString(normal));
            brush.Freeze(); this[key] = brush;
        }
        Brush("Ink", "#25333D", SystemColors.WindowTextColor);
        Brush("MutedInk", "#596975", SystemColors.WindowTextColor);
        Brush("Line", "#D6DDE2", SystemColors.WindowTextColor);
        Brush("Accent", "#28658C", SystemColors.HighlightColor);
        Brush("AccentText", "#FFFFFF", SystemColors.HighlightTextColor);
        Brush("HoverInk", "#25333D", SystemColors.HighlightTextColor);
        Brush("StatusInk", "#344D5F", SystemColors.WindowTextColor);
        Brush("ErrorInk", "#A53025", SystemColors.WindowTextColor);
        // Preserve the established light theme; every chrome color has a Windows contrast role.
        foreach (var hex in new[]{"#F6F7F8","#FFFFFF","#FFFFFFFF","#FAFBFC","#F8FAFB","#EDF1F4","#EEF8FAFB","#EAF6F8FA","#E8FFFFFF","#F7FFFFFF","#F6F9FB","#FFF4DB"})
            Brush("Chrome"+hex[1..],hex,SystemColors.WindowColor);
        foreach (var hex in new[]{"#ECF2F6","#DCE8EF","#F0F4F6","#EDF1F4"})
            Brush("Hover"+hex[1..],hex,SystemColors.HighlightColor);
        foreach (var hex in new[]{"#8BA8BB","#B9C5CE","#C9D3DB","#9EBBCC","#D9B66F"})
            Brush("Edge"+hex[1..],hex,SystemColors.WindowTextColor);
        foreach (var hex in new[]{"#F5F6F7","#F4F6F7","#E1E5E8","#DEE4E8"})
            Brush("Disabled"+hex[1..],hex,SystemColors.WindowColor);
        Brush("DisabledInk","#84919A",SystemColors.GrayTextColor);
        Brush("SelectionBackground","#DCEAF3",SystemColors.HighlightColor);
        Brush("SelectionInk","#204F70",SystemColors.HighlightTextColor);
        Brush("WarningInk","#814B13",SystemColors.WindowTextColor);
        Brush("LimitInk","#8A5B16",SystemColors.WindowTextColor);
        Brush("OverlayInk","#53616B",SystemColors.WindowTextColor);
    }
}

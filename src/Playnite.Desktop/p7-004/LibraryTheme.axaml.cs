using System.ComponentModel;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Playnite.Core.Plugins;

namespace Playnite.Desktop.Shell;

public sealed class LibraryTheme : Styles
{
    public LibraryTheme() => AvaloniaXamlLoader.Load(this);
}

internal sealed record LibraryPluginBadgeItem(
    string Id,
    string Label,
    PluginBadgeTone Tone,
    string? Tooltip = null,
    string? LinkUrl = null)
{
    private static readonly IBrush PositiveBg = new ImmutableSolidColorBrush(Color.FromArgb(0xCC, 0x16, 0x35, 0x22));
    private static readonly IBrush PositiveBorder = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x78, 0xFF, 0xA0));
    private static readonly IBrush AccentBg = new ImmutableSolidColorBrush(Color.FromArgb(0xD8, 0x18, 0x30, 0x3A));
    private static readonly IBrush AccentBorder = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x55, 0xCD, 0xFF));
    private static readonly IBrush WarningBg = new ImmutableSolidColorBrush(Color.FromArgb(0xD8, 0x3A, 0x30, 0x14));
    private static readonly IBrush WarningBorder = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFC, 0xA1));
    private static readonly IBrush DangerBg = new ImmutableSolidColorBrush(Color.FromArgb(0xD8, 0x3F, 0x18, 0x18));
    private static readonly IBrush DangerBorder = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));
    private static readonly IBrush NeutralBg = new ImmutableSolidColorBrush(Color.FromArgb(0xD8, 0x21, 0x21, 0x21));
    private static readonly IBrush NeutralBorder = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x77, 0x77, 0x77));
    private static readonly IBrush MutedBg = new ImmutableSolidColorBrush(Color.FromArgb(0xCC, 0x19, 0x19, 0x19));
    private static readonly IBrush MutedBorder = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0x55, 0x55, 0x55));
    private static readonly IBrush LightText = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF5));
    private static readonly IBrush MutedText = new ImmutableSolidColorBrush(Color.FromArgb(0xFF, 0xC4, 0xC4, 0xC4));

    public IBrush Background => Tone switch
    {
        PluginBadgeTone.Positive => PositiveBg,
        PluginBadgeTone.Accent => AccentBg,
        PluginBadgeTone.Warning => WarningBg,
        PluginBadgeTone.Danger => DangerBg,
        PluginBadgeTone.Muted => MutedBg,
        _ => NeutralBg,
    };

    public IBrush BorderBrush => Tone switch
    {
        PluginBadgeTone.Positive => PositiveBorder,
        PluginBadgeTone.Accent => AccentBorder,
        PluginBadgeTone.Warning => WarningBorder,
        PluginBadgeTone.Danger => DangerBorder,
        PluginBadgeTone.Muted => MutedBorder,
        _ => NeutralBorder,
    };

    public IBrush Foreground => Tone == PluginBadgeTone.Muted ? MutedText : LightText;
}

/// <summary>
/// View item bound to <see cref="Avalonia.Controls.ListBoxItem.DataContext"/> so card and row
/// templates can show asynchronously decoded cover bitmaps while preserving <c>TextBlock</c> content
/// and <see cref="LibraryRow"/> tags.
/// </summary>
internal sealed class LibraryCardItem(LibraryRow row) : INotifyPropertyChanged
{
    private Bitmap? coverBitmap;
    private IReadOnlyList<LibraryPluginBadgeItem> pluginBadges = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public LibraryRow Row { get; } = row;

    public string Initials => Row.Initials;

    public string CardSubtitle => Row.CardSubtitle;

    public bool IsInstalled => Row.IsInstalled;

    public bool Favorite => Row.Favorite;

    public string? GroupLabel => Row.GroupLabel;

    public bool HasGroupLabel => !string.IsNullOrEmpty(Row.GroupLabel);

    public string SourceDisplay => Row.SourceDisplay;

    public bool HasSourceBadge => !string.IsNullOrEmpty(Row.SourceDisplay)
        && !string.Equals(Row.SourceDisplay, "Library", StringComparison.Ordinal);

    public Bitmap? CoverBitmap => coverBitmap;

    public bool HasCoverBitmap => coverBitmap is not null;

    public IReadOnlyList<LibraryPluginBadgeItem> PluginBadges => pluginBadges;

    public bool HasPluginBadges => pluginBadges.Count > 0;

    public void SetCoverBitmap(Bitmap? bitmap)
    {
        if (ReferenceEquals(coverBitmap, bitmap))
        {
            return;
        }

        coverBitmap = bitmap;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverBitmap)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasCoverBitmap)));
    }

    public void SetPluginBadges(IReadOnlyList<LibraryPluginBadgeItem> badges)
    {
        ArgumentNullException.ThrowIfNull(badges);
        if (pluginBadges.SequenceEqual(badges))
        {
            return;
        }

        pluginBadges = badges;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PluginBadges)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPluginBadges)));
    }
}

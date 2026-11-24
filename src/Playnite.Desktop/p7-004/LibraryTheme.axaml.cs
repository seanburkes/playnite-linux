using System.ComponentModel;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Styling;

namespace Playnite.Desktop.Shell;

public sealed class LibraryTheme : Styles
{
    public LibraryTheme() => AvaloniaXamlLoader.Load(this);
}

/// <summary>
/// View item bound to <see cref="Avalonia.Controls.ListBoxItem.DataContext"/> so card and row
/// templates can show asynchronously decoded cover bitmaps while preserving <c>TextBlock</c> content
/// and <see cref="LibraryRow"/> tags.
/// </summary>
internal sealed class LibraryCardItem(LibraryRow row) : INotifyPropertyChanged
{
    private Bitmap? coverBitmap;

    public event PropertyChangedEventHandler? PropertyChanged;

    public LibraryRow Row { get; } = row;

    public string Initials => Row.Initials;

    public string CardSubtitle => Row.CardSubtitle;

    public bool IsInstalled => Row.IsInstalled;

    public bool Favorite => Row.Favorite;

    public string? GroupLabel => Row.GroupLabel;

    public bool HasGroupLabel => !string.IsNullOrEmpty(Row.GroupLabel);

    public Bitmap? CoverBitmap => coverBitmap;

    public bool HasCoverBitmap => coverBitmap is not null;

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
}

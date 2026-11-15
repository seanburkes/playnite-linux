using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Playnite.Core.Models;

// Adapted from legacy/source/PlayniteSDK/Collections/ObservableObject.cs.
public abstract class ObservableObject : INotifyPropertyChanged
{
    internal bool SuppressNotifications { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        if (!SuppressNotifications)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    protected void SetValue<T>(ref T property, T value, [CallerMemberName] string? propertyName = null)
    {
        property = value;
        OnPropertyChanged(propertyName);
    }

    protected void SetValue<T>(ref T property, T value, params string[] propertyNames)
    {
        property = value;
        foreach (var name in propertyNames)
        {
            OnPropertyChanged(name);
        }
    }
}

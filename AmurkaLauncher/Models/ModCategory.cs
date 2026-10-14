using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AmurkaLauncher.Models;

public class ModCategory : INotifyPropertyChanged
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Required { get; set; }

    public bool IsSelected
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    public int TotalCount
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CountDisplay));
            }
        }
    }

    public int InstalledCount
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CountDisplay));
            }
        }
    }

    public string CountDisplay => InstalledCount > 0 ? $"{InstalledCount}/{TotalCount}" : TotalCount.ToString();

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

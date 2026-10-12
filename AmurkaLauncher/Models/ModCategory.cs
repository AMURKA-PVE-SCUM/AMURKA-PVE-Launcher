using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace AmurkaLauncher.Models;

public class ModCategory : INotifyPropertyChanged
{
    private bool _isSelected;
    private int _totalCount;
    private int _installedCount;

    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public bool Required { get; set; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public int TotalCount
    {
        get => _totalCount;
        set
        {
            if (_totalCount != value)
            {
                _totalCount = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CountDisplay));
            }
        }
    }

    public int InstalledCount
    {
        get => _installedCount;
        set
        {
            if (_installedCount != value)
            {
                _installedCount = value;
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

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace RootDock.App.Services;

/// <summary>
/// ObservableCollection que troca todo o conteúdo com um único aviso (Reset). Com 20 mil arquivos,
/// Clear + Add um a um disparava 20 mil eventos e a lista inteira era reprocessada a cada um.
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}

using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace PullWatch;

public sealed class NotificationCenterViewModel : ObservableObject
{
    private readonly ObservableCollection<NotificationViewModel> _items = [];

    public NotificationCenterViewModel()
    {
        Items = new ReadOnlyObservableCollection<NotificationViewModel>(_items);
        _items.CollectionChanged += OnItemsChanged;
    }

    public ReadOnlyObservableCollection<NotificationViewModel> Items { get; }

    public bool HasNotifications => _items.Count > 0;

    public NotificationViewModel ShowOrUpdate(string id, NotificationContent content)
    {
        var existing = _items.FirstOrDefault(item => item.Id == id);

        if (existing is not null)
        {
            existing.Update(content);
            return existing;
        }

        var notification = new NotificationViewModel(id, Dismiss);
        notification.Update(content);
        _items.Add(notification);
        return notification;
    }

    public bool Dismiss(string id)
    {
        var notification = _items.FirstOrDefault(item => item.Id == id);

        if (notification is null)
        {
            return false;
        }

        Dismiss(notification);
        return true;
    }

    private void Dismiss(NotificationViewModel notification)
    {
        _items.Remove(notification);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasNotifications));
    }
}

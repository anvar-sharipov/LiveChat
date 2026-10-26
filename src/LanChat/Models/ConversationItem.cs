using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LanChat.Models;

/// <summary>Строка в списке диалогов слева: общий чат либо переписка с одним участником.</summary>
public sealed class ConversationItem : INotifyPropertyChanged
{
    public bool IsGroup { get; init; }
    public required string Name { get; init; }

    /// <summary>Для личных диалогов — в сети ли сейчас собеседник (для группового чата всегда true).</summary>
    public bool IsOnline { get; init; } = true;

    private int _unread;
    public int Unread
    {
        get => _unread;
        set
        {
            if (_unread == value) return;
            _unread = value;
            OnChanged();
            OnChanged(nameof(HasUnread));
        }
    }

    public bool HasUnread => Unread > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

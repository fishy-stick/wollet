using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Wollet.Client;

internal abstract class ObservableState : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }

    protected void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

// All callers use the owning UI context. Operation bodies own error reporting.
internal sealed class AsyncCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_running && canExecute();
    public async void Execute(object? parameter) => await ExecuteAsync();

    public async Task ExecuteAsync()
    {
        if (!CanExecute(null)) return;
        _running = true;
        Refresh();
        try { await execute(); }
        finally { _running = false; Refresh(); }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

internal enum StatusTone { Neutral, Success, Error }

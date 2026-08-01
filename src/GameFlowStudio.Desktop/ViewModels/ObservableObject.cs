using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace GameFlowStudio.Desktop.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => execute(parameter);
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class AsyncRelayCommand(
    Func<object?, Task> execute,
    Func<object?, bool>? canExecute = null,
    Action<Exception>? errorHandler = null,
    bool disableWhileRunning = true) : ICommand
{
    private bool _running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) =>
        (!disableWhileRunning || !_running) && (canExecute?.Invoke(parameter) ?? true);
    public async void Execute(object? parameter)
    {
        // 即使执行期间保持按钮的可用外观，也始终阻止同一异步命令重入。
        if (_running || !(canExecute?.Invoke(parameter) ?? true)) return;
        _running = true;
        if (disableWhileRunning) CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await execute(parameter);
        }
        catch (Exception exception)
        {
            if (errorHandler is null) throw;
            errorHandler(exception);
        }
        finally
        {
            _running = false;
            if (disableWhileRunning) CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

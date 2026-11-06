using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;

namespace Nexora.UI;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> run, Func<object?, bool>? can = null) : ICommand
{
    public RelayCommand(Action run, Func<bool>? can = null) : this(_ => run(), can is null ? null : _ => can()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => can?.Invoke(p) ?? true;
    public void Execute(object? p) => run(p);
}

/// <summary>Async command that disables itself while running and reports failures in a message box instead of crashing.</summary>
public sealed class AsyncCommand(Func<object?, Task> run, Func<object?, bool>? can = null) : ICommand
{
    private bool _busy;

    public AsyncCommand(Func<Task> run, Func<bool>? can = null) : this(_ => run(), can is null ? null : _ => can()) { }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? p) => !_busy && (can?.Invoke(p) ?? true);

    public async void Execute(object? p)
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try { await run(p); }
        catch (Exception e) { Dialogs.Error(e.Message); }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }
}

public static class Dialogs
{
    public static void Error(string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, Shared.NexoraInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Warning);

    public static void Info(string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, Shared.NexoraInfo.ProductName, MessageBoxButton.OK, MessageBoxImage.Information);

    public static bool Confirm(string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, Shared.NexoraInfo.ProductName, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
}

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace B_VIP.Desktop.Mvvm
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected bool SetProperty<T>(ref T field, T value, Action onChanged, [CallerMemberName] string? propertyName = null)
        {
            if (!SetProperty(ref field, value, propertyName))
            {
                return false;
            }

            onChanged();
            return true;
        }
    }

    public sealed class RelayCommand : ICommand
    {
        private readonly Action<object?> _execute;
        private readonly Predicate<object?>? _canExecute;

        public RelayCommand(Action<object?> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute is null ? null : _ => canExecute())
        {
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public void Execute(object? parameter) => _execute(parameter);

        public void RaiseCanExecuteChanged() => UiThread.Raise(CanExecuteChanged, this);
    }

    /// <summary>
    /// Command notifications must reach the controls on the thread that owns them. Work that finishes
    /// on a worker thread would otherwise re-evaluate a bound button from that thread, which throws
    /// a cross-thread <see cref="InvalidOperationException"/> straight out of the dispatcher and takes
    /// the process down. Notifications raised off the UI thread are therefore posted to it, and a
    /// dispatcher that is already shutting down simply drops them.
    /// </summary>
    internal static class UiThread
    {
        public static void Raise(EventHandler? handler, object sender)
        {
            if (handler is null)
            {
                return;
            }

            Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
            {
                try
                {
                    dispatcher.BeginInvoke(DispatcherPriority.Background, handler, sender, EventArgs.Empty);
                }
                catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
                {
                    // The window is closing; nobody is left to re-evaluate the command.
                }

                return;
            }

            handler(sender, EventArgs.Empty);
        }

        /// <summary>Runs <paramref name="action"/> on the UI thread, inline when already there.</summary>
        public static void Invoke(Action action)
        {
            Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess())
            {
                action();
                return;
            }

            try
            {
                dispatcher.Invoke(action, DispatcherPriority.DataBind);
            }
            catch (Exception ex) when (ex is TaskCanceledException or InvalidOperationException)
            {
                // The dispatcher is shutting down; the update is moot.
            }
        }
    }

    /// <summary>Command that reports an in-flight state so the UI can disable repeated clicks.</summary>
    public sealed class AsyncRelayCommand : ICommand
    {
        private readonly Func<object?, Task> _execute;
        private readonly Predicate<object?>? _canExecute;
        private bool _isRunning;

        public AsyncRelayCommand(Func<object?, Task> execute, Predicate<object?>? canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute is null ? null : _ => canExecute())
        {
        }

        public event EventHandler? CanExecuteChanged;

        public bool IsRunning
        {
            get => _isRunning;
            private set
            {
                if (_isRunning == value)
                {
                    return;
                }

                _isRunning = value;
                RaiseCanExecuteChanged();
            }
        }

        public bool CanExecute(object? parameter) => !IsRunning && (_canExecute?.Invoke(parameter) ?? true);

        public async void Execute(object? parameter)
        {
            if (!CanExecute(parameter))
            {
                return;
            }

            IsRunning = true;
            try
            {
                await _execute(parameter);
            }
            finally
            {
                // The work may have completed on a worker thread (a module that finished without
                // returning to the dispatcher), so the button must be re-evaluated on the UI thread.
                IsRunning = false;
            }
        }

        public void RaiseCanExecuteChanged() => UiThread.Raise(CanExecuteChanged, this);
    }

    public interface INavigationHost
    {
        void NavigateTo(string moduleKey);
    }

    /// <summary>Single source of truth for the shell navigation: current view model and history.</summary>
    public sealed class NavigationService : ObservableObject, INavigationHost
    {
        private readonly Dictionary<string, Func<ViewModelBase>> _factories = new(StringComparer.OrdinalIgnoreCase);
        private readonly Stack<string> _backStack = new();
        private ViewModelBase? _current;
        private string _currentKey = string.Empty;

        public event Action<string>? Navigated;

        public ViewModelBase? Current
        {
            get => _current;
            private set => SetProperty(ref _current, value);
        }

        public string CurrentKey
        {
            get => _currentKey;
            private set => SetProperty(ref _currentKey, value);
        }

        public bool CanGoBack => _backStack.Count > 0;

        public void Register(string moduleKey, Func<ViewModelBase> factory) => _factories[moduleKey] = factory;

        public void NavigateTo(string moduleKey)
        {
            if (!_factories.TryGetValue(moduleKey, out Func<ViewModelBase>? factory))
            {
                return;
            }

            if (!string.Equals(moduleKey, _currentKey, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(_currentKey))
            {
                _backStack.Push(_currentKey);
            }

            ViewModelBase? previous = _current;
            ViewModelBase viewModel = factory();
            Current = viewModel;
            CurrentKey = moduleKey;
            if (previous is not null && !ReferenceEquals(previous, viewModel))
            {
                _ = previous.OnNavigatedFromAsync();
            }

            _ = viewModel.OnNavigatedToAsync();
            Navigated?.Invoke(moduleKey);
        }

        public void GoBack()
        {
            if (_backStack.Count == 0)
            {
                return;
            }

            string key = _backStack.Pop();
            if (_factories.TryGetValue(key, out Func<ViewModelBase>? factory))
            {
                ViewModelBase? previous = _current;
                ViewModelBase viewModel = factory();
                Current = viewModel;
                CurrentKey = key;
                if (previous is not null)
                {
                    _ = previous.OnNavigatedFromAsync();
                }

                _ = viewModel.OnNavigatedToAsync();
                Navigated?.Invoke(key);
            }
        }
    }

    public abstract class ViewModelBase : ObservableObject
    {
        private bool _isBusy;
        private string? _statusMessage;
        private string? _errorMessage;

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnBusyChanged();
                }
            }
        }

        /// <summary>
        /// Called after <see cref="IsBusy"/> changes so a screen can refresh the availability of the
        /// commands that must not run twice.
        /// </summary>
        protected virtual void OnBusyChanged()
        {
        }

        public string? StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public string? ErrorMessage
        {
            get => _errorMessage;
            set => SetProperty(ref _errorMessage, value, () => OnPropertyChanged(nameof(HasError)));
        }

        public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

        public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

        /// <summary>
        /// Called once when another screen replaces this one, so a screen can drop event
        /// subscriptions and timers instead of keeping itself alive behind the scenes.
        /// </summary>
        public virtual Task OnNavigatedFromAsync() => Task.CompletedTask;
    }
}

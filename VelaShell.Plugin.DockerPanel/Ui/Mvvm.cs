using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;

namespace VelaShell.Plugin.DockerPanel.Ui;

/// <summary>
/// 最小可观察基类。
/// <para>
/// 刻意**不引 ReactiveUI / CommunityToolkit**:插件的第三方依赖是随插件目录分发的,
/// 为了两个接口拖进一整棵依赖树不值当;更要紧的是插件 ALC 里那份 <c>RxApp</c>
/// 与宿主的是两个独立实例,它的主线程调度器不会自动挂到 Avalonia 的调度器上,
/// 于是命令的可用性变化会在后台线程上触发绑定更新。要的只是
/// <see cref="INotifyPropertyChanged" /> 与两个命令类型,自己写一百行更稳。
/// </para>
/// </summary>
public abstract class ObservableObject : INotifyPropertyChanged
{
    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>值变化时赋值并通知。</summary>
    /// <typeparam name="T">属性类型。</typeparam>
    /// <param name="field">后备字段。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">属性名(自动填充)。</param>
    /// <returns>是否真的变了。</returns>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        RaisePropertyChanged(propertyName);
        return true;
    }

    /// <summary>手动触发一次属性变更通知(派生属性用)。</summary>
    /// <param name="propertyName">属性名。</param>
    protected void RaisePropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (PropertyChanged is not { } handler)
        {
            return;
        }
        // 绑定只能在 UI 线程更新。加载逻辑大多跑在后台线程(远程执行是要等网络的),
        // 统一在这里封送,免得每个调用点都记得 Dispatcher —— 漏一个就是一次随机的崩溃。
        if (Dispatcher.UIThread.CheckAccess())
        {
            handler(this, new(propertyName));
        }
        else
        {
            Dispatcher.UIThread.Post(() => handler(this, new(propertyName)));
        }
    }
}

/// <summary>无参异步命令。执行期间自动禁用自己,避免重复点击叠加请求。</summary>
/// <param name="execute">命令体。</param>
/// <param name="canExecute">可用性判定;为 null 即恒可用。</param>
public sealed class AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null) : ICommand
{
    private bool _running;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && (canExecute?.Invoke() ?? true);

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter))
        {
            return;
        }
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute().ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 命令体自己负责把失败呈现到界面上(Status)。这里只保证异常不逃出 async void ——
            // 那会直接带走宿主进程,而这只是一次点击。
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>重新求值可用性。</summary>
    public void RaiseCanExecuteChanged() => Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

/// <summary>带参异步命令(列表行上的按钮、页签切换这类)。</summary>
/// <typeparam name="T">参数类型。</typeparam>
/// <param name="execute">命令体。</param>
public sealed class AsyncCommand<T>(Func<T, Task> execute) : ICommand
{
    private bool _running;

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged;

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => !_running && parameter is T;

    /// <inheritdoc />
    public async void Execute(object? parameter)
    {
        if (parameter is not T typed || _running)
        {
            return;
        }
        _running = true;
        RaiseCanExecuteChanged();
        try
        {
            await execute(typed).ConfigureAwait(true);
        }
        catch (Exception)
        {
            // 同 AsyncCommand。
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    /// <summary>重新求值可用性。</summary>
    public void RaiseCanExecuteChanged() => Dispatcher.UIThread.Post(() => CanExecuteChanged?.Invoke(this, EventArgs.Empty));
}

using System;
using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace WinNotch;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // UI 스레드에서 처리되지 않은 예외
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;

        // UI 스레드가 아닌 곳(백그라운드 Task 등)에서 처리되지 않은 예외
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // async void 메서드 등에서 관찰되지 않은 Task 예외
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"처리되지 않은 예외 발생:\n\n{e.Exception}",
            "WinNotch - 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true; // 일단 강제로 살려서 원인 메시지를 보게 함
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"치명적 예외 발생:\n\n{e.ExceptionObject}",
            "WinNotch - 치명적 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        MessageBox.Show(
            $"백그라운드 작업 예외:\n\n{e.Exception}",
            "WinNotch - Task 오류",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.SetObserved();
    }
}
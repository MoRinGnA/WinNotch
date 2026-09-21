using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Foundation.Metadata;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using System.Runtime.InteropServices.WindowsRuntime;

namespace WinNotch.Services
{
    public class NotificationEventArgs : EventArgs
    {
        public string AppName { get; }
        public string Title { get; }
        public string Body { get; }
        // We can optionally extract icon if needed, but for simplicity, text first.

        public NotificationEventArgs(string appName, string title, string body)
        {
            AppName = appName;
            Title = title;
            Body = body;
        }
    }

    public class NotificationService : IDisposable
    {
        private UserNotificationListener? _listener;
        public event EventHandler<NotificationEventArgs>? NotificationReceived;
        private bool _isListening = false;

        public async Task<bool> InitializeAsync()
        {
            if (!ApiInformation.IsTypePresent("Windows.UI.Notifications.Management.UserNotificationListener"))
            {
                Debug.WriteLine("UserNotificationListener is not supported on this OS.");
                return false;
            }

            _listener = UserNotificationListener.Current;
            
            // Request access (this might prompt the user in Windows Settings)
            UserNotificationListenerAccessStatus accessStatus = await _listener.RequestAccessAsync();

            if (accessStatus != UserNotificationListenerAccessStatus.Allowed)
            {
                Debug.WriteLine($"Notification access denied: {accessStatus}");
                return false;
            }

            try
            {
                _listener.NotificationChanged += Listener_NotificationChanged;
                _isListening = true;
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to subscribe to NotificationChanged: {ex.Message}");
                return false;
            }
        }

        private async void Listener_NotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args)
        {
            if (args.ChangeKind == UserNotificationChangedKind.Added)
            {
                try
                {
                    var notification = _listener?.GetNotification(args.UserNotificationId);
                    if (notification != null)
                    {
                        var appInfo = notification.AppInfo;
                        string appName = appInfo?.DisplayInfo?.DisplayName ?? "Unknown App";

                        // Get text elements
                        var bindings = notification.Notification.Visual.Bindings;
                        var textElements = bindings.FirstOrDefault()?.GetTextElements();

                        if (textElements != null && textElements.Count > 0)
                        {
                            string title = textElements[0].Text;
                            string body = textElements.Count > 1 ? string.Join("\n", textElements.Skip(1).Select(t => t.Text)) : string.Empty;

                            // Skip empty notifications
                            if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(body))
                            {
                                NotificationReceived?.Invoke(this, new NotificationEventArgs(appName, title, body));
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error processing notification: {ex.Message}");
                }
            }
        }

        public void Dispose()
        {
            if (_isListening && _listener != null)
            {
                _listener.NotificationChanged -= Listener_NotificationChanged;
                _isListening = false;
            }
        }
    }
}

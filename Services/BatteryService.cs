using System;
using System.Windows.Forms;
using System.Windows.Threading;

namespace WinNotch.Services
{
    public class BatteryStatusArgs : EventArgs
    {
        public float BatteryPercent { get; }
        public bool IsCharging { get; }

        public BatteryStatusArgs(float percent, bool isCharging)
        {
            BatteryPercent = percent;
            IsCharging = isCharging;
        }
    }

    public class BatteryService : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private float _lastPercent = -1;
        private bool _lastChargingStatus = false;

        public event EventHandler<BatteryStatusArgs>? BatteryStatusChanged;

        public BatteryService()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(5) // Check every 5 seconds
            };
            _timer.Tick += Timer_Tick;
        }

        public void Start()
        {
            _timer.Start();
            CheckBatteryStatus(); // Initial check
        }

        public void Stop()
        {
            _timer.Stop();
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            CheckBatteryStatus();
        }

        private void CheckBatteryStatus()
        {
            var powerStatus = SystemInformation.PowerStatus;
            float currentPercent = powerStatus.BatteryLifePercent;
            bool currentCharging = powerStatus.PowerLineStatus == PowerLineStatus.Online;

            // BatteryLifePercent is 1.0 = 100%, 0.5 = 50%. Sometimes returns 255 if unknown.
            if (currentPercent > 1.0f) currentPercent = 1.0f; 

            if (Math.Abs(_lastPercent - currentPercent) > 0.001 || _lastChargingStatus != currentCharging)
            {
                _lastPercent = currentPercent;
                _lastChargingStatus = currentCharging;
                BatteryStatusChanged?.Invoke(this, new BatteryStatusArgs(currentPercent, currentCharging));
            }
        }

        public void ForceUpdate()
        {
            if (_lastPercent >= 0)
            {
                BatteryStatusChanged?.Invoke(this, new BatteryStatusArgs(_lastPercent, _lastChargingStatus));
            }
        }

        public void Dispose()
        {
            _timer.Stop();
        }
    }
}

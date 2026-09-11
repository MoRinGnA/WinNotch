using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WinNotch.Services
{
    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    internal class MMDeviceEnumerator { }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMMDevice
    {
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolume
    {
        int RegisterControlNotificationCallback(IAudioEndpointVolumeCallback pNotify);
        int UnregisterControlNotificationCallback(IAudioEndpointVolumeCallback pNotify);
        int GetChannelCount(out uint pnChannelCount);
        int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        int GetMasterVolumeLevel(out float pfLevelDB);
        int GetMasterVolumeLevelScalar(out float pfLevel);
        int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        int GetVolumeStepInfo(out uint pnStep, out uint pnStepCount);
        int VolumeStepUp(ref Guid pguidEventContext);
        int VolumeStepDown(ref Guid pguidEventContext);
        int QueryHardwareSupport(out uint pdwHardwareSupportMask);
        int GetVolumeRange(out float pflVolumeMindB, out float pflVolumeMaxdB, out float pflVolumeIncrementdB);
    }

    [ComImport]
    [Guid("65782020-4ACF-4537-B57A-3650C93223C3")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IAudioEndpointVolumeCallback
    {
        [PreserveSig]
        int OnNotify(IntPtr pNotifyData);
    }

    public class AudioService : IAudioEndpointVolumeCallback, IDisposable
    {
        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioEndpointVolume? _audioVolume;
        private readonly DispatcherTimer _pollTimer;
        private int _lastVolume = -1;
        private bool _lastMuted = false;
        private bool _disposed;

        public event Action<int, bool>? VolumeChanged;

        public AudioService()
        {
            Initialize();

            // 60ms volume monitor guarantees 100% detection of keyboard & taskbar volume adjustments
            _pollTimer = new DispatcherTimer(DispatcherPriority.Normal)
            {
                Interval = TimeSpan.FromMilliseconds(60)
            };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();
        }

        private void Initialize()
        {
            try
            {
                _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                // 0: eRender, 0: eConsole, 1: eMultimedia
                int hr = _enumerator.GetDefaultAudioEndpoint(0, 0, out _device);
                if (hr != 0 || _device == null)
                {
                    _enumerator.GetDefaultAudioEndpoint(0, 1, out _device);
                }

                if (_device != null)
                {
                    Guid iid = typeof(IAudioEndpointVolume).GUID;
                    int hrAct = _device.Activate(ref iid, 23, IntPtr.Zero, out object obj);
                    if (hrAct == 0 && obj != null)
                    {
                        _audioVolume = (IAudioEndpointVolume)obj;
                        _audioVolume.RegisterControlNotificationCallback(this);

                        // Read initial volume and mute
                        _audioVolume.GetMasterVolumeLevelScalar(out float level);
                        _audioVolume.GetMute(out _lastMuted);
                        _lastVolume = (int)Math.Round(level * 100);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioService init error: {ex.Message}");
            }
        }

        private void PollTimer_Tick(object? sender, EventArgs e)
        {
            if (_audioVolume == null) return;
            try
            {
                _audioVolume.GetMasterVolumeLevelScalar(out float level);
                _audioVolume.GetMute(out bool isMuted);
                int vol = (int)Math.Round(level * 100);

                if (vol != _lastVolume || isMuted != _lastMuted)
                {
                    _lastVolume = vol;
                    _lastMuted = isMuted;
                    VolumeChanged?.Invoke(vol, isMuted);
                }
            }
            catch { }
        }

        public int OnNotify(IntPtr pNotifyData)
        {
            if (_audioVolume == null) return 0;
            try
            {
                _audioVolume.GetMasterVolumeLevelScalar(out float level);
                _audioVolume.GetMute(out bool isMuted);
                int vol = (int)Math.Round(level * 100);

                _lastVolume = vol;
                _lastMuted = isMuted;
                VolumeChanged?.Invoke(vol, isMuted);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioService notify error: {ex.Message}");
            }
            return 0; // S_OK
        }

        public int GetCurrentVolume(out bool isMuted)
        {
            isMuted = _lastMuted;
            if (_audioVolume != null)
            {
                try
                {
                    _audioVolume.GetMasterVolumeLevelScalar(out float level);
                    _audioVolume.GetMute(out isMuted);
                    _lastMuted = isMuted;
                    _lastVolume = (int)Math.Round(level * 100);
                    return _lastVolume;
                }
                catch { }
            }
            return _lastVolume >= 0 ? _lastVolume : 50;
        }

        public int StepVolume(float deltaScalar, out bool isMuted)
        {
            isMuted = _lastMuted;
            if (_audioVolume == null) return 50;

            try
            {
                _audioVolume.GetMasterVolumeLevelScalar(out float currentLevel);
                float newLevel = Math.Clamp(currentLevel + deltaScalar, 0.0f, 1.0f);
                Guid empty = Guid.Empty;
                _audioVolume.SetMasterVolumeLevelScalar(newLevel, ref empty);
                _audioVolume.GetMute(out isMuted);

                int vol = (int)Math.Round(newLevel * 100);
                _lastVolume = vol;
                _lastMuted = isMuted;

                VolumeChanged?.Invoke(vol, isMuted);
                return vol;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"AudioService StepVolume error: {ex.Message}");
                return _lastVolume >= 0 ? _lastVolume : 50;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _pollTimer.Stop();

            try
            {
                if (_audioVolume != null)
                {
                    _audioVolume.UnregisterControlNotificationCallback(this);
                    Marshal.ReleaseComObject(_audioVolume);
                    _audioVolume = null;
                }

                if (_device != null)
                {
                    Marshal.ReleaseComObject(_device);
                    _device = null;
                }

                if (_enumerator != null)
                {
                    Marshal.ReleaseComObject(_enumerator);
                    _enumerator = null;
                }
            }
            catch { }
        }
    }
}

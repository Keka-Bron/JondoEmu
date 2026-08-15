using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Jondo.Unity.Launcher.UI
{
    /// <summary>
    /// Background music of the launcher (launcher_assets/theme.mp3).
    ///
    /// The web interface used an &lt;audio&gt; tag. Here playback goes through Media Foundation
    /// (MFPlay), because the theme file, despite its .mp3 extension, is really a WebM container
    /// with Opus audio: the browser detected that from the content, but the classic Windows APIs
    /// (MCI) cannot open it. MCI stays as a fallback in case the theme is ever replaced by a real
    /// MP3 or WAV and Media Foundation is not available.
    /// </summary>
    internal sealed class MusicPlayer : IDisposable
    {
        private const string MciAlias = "jondoLauncherMusic";

        // Callbacks on a thread of their own: that way we do not depend on the window pumping
        // messages for Media Foundation to complete its asynchronous operations.
        private const uint FreeThreadedOption = 0x1;

        // Slots in the IMFPMediaPlayer method table (the first three belong to IUnknown).
        private const int SlotPlay = 3;
        private const int SlotPause = 4;
        private const int SlotStop = 5;
        private const int SlotGetState = 13;
        private const int SlotShutdown = 38;

        // MFP_MEDIAPLAYER_STATE values.
        private const int StateStopped = 1;

        [DllImport("mfplay.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int MFPCreateMediaPlayer(
            string? url,
            [MarshalAs(UnmanagedType.Bool)] bool startPlayback,
            uint options,
            IntPtr callback,
            IntPtr videoWindow,
            out IntPtr player);

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "mciSendStringW")]
        private static extern int mciSendString(string command, StringBuilder? response, int size, IntPtr window);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int SimpleMethod(IntPtr instance);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int MethodWithOutput(IntPtr instance, out int value);

        private IntPtr _player;
        private bool _mciOpen;
        private bool _disposed;

        /// <summary>Tells whether there is any audio engine able to play the theme.</summary>
        public bool Available => _player != IntPtr.Zero || _mciOpen;

        /// <summary>Tells whether the music is supposed to be playing right now.</summary>
        public bool Playing { get; private set; }

        public MusicPlayer(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;

            try
            {
                // Created already playing: MFPlay prepares the media item asynchronously and
                // starts as soon as it is ready.
                if (MFPCreateMediaPlayer(path, true, FreeThreadedOption, IntPtr.Zero, IntPtr.Zero, out IntPtr player) == 0
                    && player != IntPtr.Zero)
                {
                    _player = player;
                    Playing = true;
                    return;
                }
            }
            catch (DllNotFoundException)
            {
                // System without Media Foundation: fall back to MCI.
            }
            catch (EntryPointNotFoundException)
            {
            }

            OpenWithMci(path);
        }

        private void OpenWithMci(string path)
        {
            try
            {
                mciSendString($"close {MciAlias}", null, 0, IntPtr.Zero);
                int error = mciSendString($"open \"{path}\" type mpegvideo alias {MciAlias}", null, 0, IntPtr.Zero);
                if (error != 0) error = mciSendString($"open \"{path}\" alias {MciAlias}", null, 0, IntPtr.Zero);
                _mciOpen = error == 0;
            }
            catch
            {
                _mciOpen = false;
            }
        }

        /// <summary>Starts or resumes playback.</summary>
        public void Play()
        {
            if (_player != IntPtr.Zero)
            {
                Invoke(SlotPlay);
                Playing = true;
            }
            else if (_mciOpen)
            {
                if (mciSendString($"play {MciAlias} repeat", null, 0, IntPtr.Zero) != 0)
                {
                    mciSendString($"play {MciAlias}", null, 0, IntPtr.Zero);
                }
                Playing = true;
            }
        }

        /// <summary>Pauses without losing the current position.</summary>
        public void Pause()
        {
            if (_player != IntPtr.Zero) Invoke(SlotPause);
            else if (_mciOpen) mciSendString($"pause {MciAlias}", null, 0, IntPtr.Zero);
            Playing = false;
        }

        /// <summary>Stops the music and rewinds it (used when launching the game client).</summary>
        public void Stop()
        {
            if (_player != IntPtr.Zero)
            {
                Invoke(SlotStop);
            }
            else if (_mciOpen)
            {
                mciSendString($"stop {MciAlias}", null, 0, IntPtr.Zero);
                mciSendString($"seek {MciAlias} to start", null, 0, IntPtr.Zero);
            }
            Playing = false;
        }

        /// <summary>
        /// Restarts the theme once it reaches the end. The window timer calls this, because no
        /// engine loops the audio reliably on its own.
        /// </summary>
        public void KeepLooping()
        {
            if (!Playing) return;

            if (_player != IntPtr.Zero)
            {
                try
                {
                    var getState = Method<MethodWithOutput>(SlotGetState);
                    if (getState(_player, out int state) == 0 && state == StateStopped)
                    {
                        Invoke(SlotPlay);
                    }
                }
                catch { }
                return;
            }

            if (!_mciOpen) return;

            var response = new StringBuilder(64);
            if (mciSendString($"status {MciAlias} mode", response, response.Capacity, IntPtr.Zero) != 0) return;
            if (response.ToString().Trim().Equals("stopped", StringComparison.OrdinalIgnoreCase))
            {
                mciSendString($"seek {MciAlias} to start", null, 0, IntPtr.Zero);
                mciSendString($"play {MciAlias}", null, 0, IntPtr.Zero);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Playing = false;

            if (_player != IntPtr.Zero)
            {
                try
                {
                    Invoke(SlotStop);
                    Invoke(SlotShutdown);
                    Marshal.Release(_player);
                }
                catch { }
                _player = IntPtr.Zero;
            }

            if (_mciOpen)
            {
                mciSendString($"stop {MciAlias}", null, 0, IntPtr.Zero);
                mciSendString($"close {MciAlias}", null, 0, IntPtr.Zero);
                _mciOpen = false;
            }
        }

        /// <summary>Calls a parameterless method of the player's COM interface.</summary>
        private void Invoke(int slot)
        {
            try { Method<SimpleMethod>(slot)(_player); }
            catch { }
        }

        /// <summary>Fetches a method from the virtual table of the COM object.</summary>
        private T Method<T>(int slot) where T : Delegate
        {
            IntPtr table = Marshal.ReadIntPtr(_player);
            IntPtr function = Marshal.ReadIntPtr(table, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(function);
        }
    }
}

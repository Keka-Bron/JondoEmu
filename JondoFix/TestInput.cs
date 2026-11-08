using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using MelonLoader;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace JondoFix
{
    /// <summary>
    /// Pointer and keyboard for the test harness (tools/JondoPlay), fed from inside the client.
    ///
    /// The harness plays several clients at once while the person at the computer keeps working,
    /// so it can neither move the real pointer nor bring a window forward. Windows' own posted
    /// messages do not reach the game: Unity's Input System reads its devices natively and
    /// ignores them. What does reach it is what the Input System itself is handed, so this
    /// class adds a mouse and a keyboard of its own, makes them the current ones, switches the
    /// real ones off (whatever the person types elsewhere must not land in a test client) and
    /// queues state events on them, exactly as the native backend would.
    ///
    /// It only wakes up when the client was started with JONDO_INPUT_PORT: a local port the
    /// harness talks to, one line per command, one line back when it has been played.
    /// Without that variable nothing here runs and the client behaves as always.
    /// </summary>
    internal static class TestInput
    {
        private sealed class Request
        {
            public string Line;
            public string Reply = "error: not run";
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
        }

        /// <summary>One thing to do on the main thread, and how many frames to let pass after it.</summary>
        private readonly struct Step
        {
            public readonly Action Run;
            public readonly int Frames;
            public Step(Action run, int frames) { Run = run; Frames = frames; }
        }

        private static readonly ConcurrentQueue<Request> Incoming = new ConcurrentQueue<Request>();
        private static readonly Queue<Step> Steps = new Queue<Step>();
        private static MelonLogger.Instance _log;
        private static int _port;
        private static int _waitFrames;
        private static float _nextDeviceCheck;
        private static bool _wasFocused;

        private static Mouse _mouse;
        private static Keyboard _keyboard;
        private static Vector2 _position;
        private static ushort _buttons;
        private static ushort _clickCount;
        private static float _lastPress;
        private static readonly HashSet<int> _keysDown = new HashSet<int>();
        private static IntPtr _buffer;

        public static bool Active => _port > 0;

        public static void Start(MelonLogger.Instance log)
        {
            _log = log;
            if (!int.TryParse(Environment.GetEnvironmentVariable("JONDO_INPUT_PORT"), out _port) || _port <= 0 || _port > 65535)
            {
                _port = 0;
                return;
            }
            _buffer = Marshal.AllocHGlobal(256);
            var thread = new Thread(Listen) { IsBackground = true, Name = "JondoTestInput" };
            thread.Start();
            log.Msg($"[JondoFix] Test input listening on 127.0.0.1:{_port}");
        }

        private static void Listen()
        {
            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, _port);
                listener.Start();
            }
            catch (Exception ex)
            {
                _log.Error($"[JondoFix] Test input could not listen on {_port}: {ex.Message}");
                return;
            }
            while (true)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception) { continue; }
                new Thread(() => Serve(client)) { IsBackground = true }.Start();
            }
        }

        private static void Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                try
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        var request = new Request { Line = line };
                        Incoming.Enqueue(request);
                        writer.WriteLine(request.Done.Wait(20000) ? request.Reply : "error: the client did not play it in time");
                    }
                }
                catch (Exception) { }
            }
        }

        /// <summary>From the mod's OnUpdate, on the main thread: devices kept ready, commands played frame by frame.</summary>
        public static void Tick()
        {
            if (_port == 0) return;
            try { KeepDevices(); }
            catch (Exception ex) { _log.Warning($"[JondoFix] Test input devices: {ex.Message}"); }

            while (Incoming.TryDequeue(out var request)) Plan(request);

            if (_waitFrames > 0) { _waitFrames--; return; }
            while (Steps.Count > 0)
            {
                var step = Steps.Dequeue();
                try { step.Run(); }
                catch (Exception ex) { _log.Warning($"[JondoFix] Test input step: {ex.Message}"); }
                if (step.Frames > 0) { _waitFrames = step.Frames - 1; return; }
            }
        }

        /// <summary>
        /// The harness's own mouse and keyboard, current, and the real ones off. Looked at again
        /// every few seconds: a device plugged in later, or the system's mouse coming back after
        /// a focus change, is switched off too.
        /// </summary>
        private static void KeepDevices()
        {
            // The person brought this client forward to use it: their mouse and keyboard work in
            // it at once, and go off again when they leave it, so that what they type elsewhere
            // does not land here. The harness never activates a client, so focus is theirs.
            bool focused = Application.isFocused;
            bool focusChanged = focused != _wasFocused;
            _wasFocused = focused;
            if (_mouse != null && !focusChanged && Time.unscaledTime < _nextDeviceCheck) return;
            _nextDeviceCheck = Time.unscaledTime + 3f;

            var settings = InputSystem.settings;
            if (settings != null && settings.backgroundBehavior != InputSettings.BackgroundBehavior.IgnoreFocus)
                settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
            Application.runInBackground = true;

            if (_mouse == null)
            {
                _mouse = InputSystem.AddDevice("Mouse", "JondoTestMouse", null).Cast<Mouse>();
                _keyboard = InputSystem.AddDevice("Keyboard", "JondoTestKeyboard", null).Cast<Keyboard>();
                _position = new Vector2(Screen.width / 2f, Screen.height / 2f);
                _log.Msg($"[JondoFix] Test input devices added: mouse {_mouse.deviceId}, keyboard {_keyboard.deviceId}");
            }

            var devices = InputSystem.devices;
            for (int i = 0; i < devices.Count; i++)
            {
                var device = devices[i];
                if (device == null || device.deviceId == _mouse.deviceId || device.deviceId == _keyboard.deviceId) continue;
                bool pointer = device.TryCast<Pointer>() != null, keys = device.TryCast<Keyboard>() != null;
                if (!pointer && !keys) continue;
                if (focused && !device.enabled)
                {
                    InputSystem.EnableDevice(device);
                    _log.Msg($"[JondoFix] Test input: real device {device.name} on (the client is in use)");
                }
                else if (!focused && device.enabled)
                {
                    InputSystem.DisableDevice(device, false);
                    _log.Msg($"[JondoFix] Test input: real device {device.name} switched off");
                }
            }
            if (focused) return;
            if (Mouse.current == null || Mouse.current.deviceId != _mouse.deviceId) _mouse.MakeCurrent();
            if (Keyboard.current == null || Keyboard.current.deviceId != _keyboard.deviceId) _keyboard.MakeCurrent();
        }

        private static void Then(Action run, int frames = 0) => Steps.Enqueue(new Step(run, frames));

        private static void Reply(Request request, string text) => Then(() => { request.Reply = text; request.Done.Set(); });

        /// <summary>A command turned into steps. Coordinates are the window's: (0, 0) top left, in pixels.</summary>
        private static void Plan(Request request)
        {
            string[] words = request.Line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string verb = words[0].ToLowerInvariant();
            int Int(int index, int otherwise = 0) => index < words.Length && int.TryParse(words[index], out int v) ? v : otherwise;
            bool right = Array.Exists(words, w => w.Equals("right", StringComparison.OrdinalIgnoreCase));
            try
            {
                switch (verb)
                {
                    case "ping":
                        Then(() =>
                        {
                            request.Reply = $"ok {Screen.width} {Screen.height} focused={Application.isFocused} frame={Time.frameCount}"
                                            + $" mouse={(Mouse.current != null && _mouse != null && Mouse.current.deviceId == _mouse.deviceId)}";
                            request.Done.Set();
                        });
                        return;
                    case "ui":
                    case "find":
                    case "pick":
                    {
                        string rest = request.Line.TrimStart().Length > verb.Length ? request.Line.TrimStart().Substring(verb.Length).Trim() : "";
                        Then(() =>
                        {
                            try
                            {
                                request.Reply = "ok " + (verb == "ui" ? Interface.Texts(rest)
                                                       : verb == "find" ? Interface.Find(rest)
                                                       : Interface.Pick(Int(1), Int(2)));
                            }
                            catch (Exception ex) { request.Reply = "error: " + ex.Message; }
                            request.Done.Set();
                        });
                        return;
                    }
                    case "move":
                        Then(() => Pointer(Int(1), Int(2), _buttons), 2);
                        break;
                    case "click":
                    {
                        int x = Int(1), y = Int(2), times = Math.Max(1, Int(right ? 4 : 3, 1));
                        ushort bit = (ushort)(right ? 2 : 1);
                        Then(() => Pointer(x, y, _buttons), 3);
                        for (int i = 0; i < times; i++)
                        {
                            Then(() => Pointer(x, y, (ushort)(_buttons | bit), press: true), 3);
                            Then(() => Pointer(x, y, (ushort)(_buttons & ~bit)), 3);
                        }
                        break;
                    }
                    case "down":
                    case "up":
                    {
                        int x = Int(1), y = Int(2);
                        ushort bit = (ushort)(right ? 2 : 1);
                        Then(() => Pointer(x, y, verb == "down" ? (ushort)(_buttons | bit) : (ushort)(_buttons & ~bit), press: verb == "down"), 3);
                        break;
                    }
                    case "drag":
                    {
                        int x1 = Int(1), y1 = Int(2), x2 = Int(3), y2 = Int(4);
                        Then(() => Pointer(x1, y1, _buttons), 3);
                        Then(() => Pointer(x1, y1, (ushort)(_buttons | 1), press: true), 3);
                        for (int i = 1; i <= 10; i++)
                        {
                            int k = i;
                            Then(() => Pointer(x1 + (x2 - x1) * k / 10, y1 + (y2 - y1) * k / 10, _buttons), 1);
                        }
                        Then(() => Pointer(x2, y2, (ushort)(_buttons & ~1)), 3);
                        break;
                    }
                    case "scroll":
                    {
                        int x = Int(1), y = Int(2), notches = Int(3);
                        Then(() => Pointer(x, y, _buttons), 2);
                        Then(() => Pointer(x, y, _buttons, scroll: notches * 120f), 2);
                        break;
                    }
                    case "key":
                    {
                        var keys = new List<int>();
                        foreach (string name in (words.Length > 1 ? words[1] : "").Split('+', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int key = KeyOf(name);
                            if (key <= 0) { request.Reply = $"error: unknown key {name}"; request.Done.Set(); return; }
                            keys.Add(key);
                        }
                        Then(() => { foreach (int k in keys) _keysDown.Add(k); Keys(); }, 3);
                        Then(() => { foreach (int k in keys) _keysDown.Remove(k); Keys(); }, 3);
                        break;
                    }
                    case "type":
                    {
                        string text = request.Line.TrimStart().Length > 5 ? request.Line.TrimStart().Substring(5) : "";
                        foreach (char c in text)
                        {
                            char ch = c;
                            Then(() => InputSystem.QueueTextEvent(_keyboard, ch, -1.0), 1);
                        }
                        Then(() => { }, 2);
                        break;
                    }
                    default:
                        request.Reply = $"error: unknown command {verb}";
                        request.Done.Set();
                        return;
                }
                Reply(request, "ok");
            }
            catch (Exception ex)
            {
                request.Reply = "error: " + ex.Message;
                request.Done.Set();
            }
        }

        // Unity's FourCCs: four characters packed big end first.
        private static int FourCC(string s) => (s[0] << 24) | (s[1] << 16) | (s[2] << 8) | s[3];

        /// <summary>
        /// The event header the native side would write: type, size, device and time (the
        /// layout of NativeInputEvent in this Unity: type 0, size 4, device 6, time 8, id 16).
        /// </summary>
        private static void Header(string type, int size, int deviceId)
        {
            for (int i = 0; i < size; i++) Marshal.WriteByte(_buffer, i, 0);
            Marshal.WriteInt32(_buffer, 0, FourCC(type));
            Marshal.WriteInt16(_buffer, 4, (short)size);
            Marshal.WriteInt16(_buffer, 6, (short)deviceId);
            Marshal.WriteInt64(_buffer, 8, BitConverter.DoubleToInt64Bits(InputState.currentTime));
        }

        private static unsafe void Queue() => InputSystem.QueueEvent(new InputEventPtr((InputEvent*)_buffer));

        /// <summary>A MouseState sent as a state event: position (bottom-left origin), delta, scroll, buttons, clicks.</summary>
        private static void Pointer(int x, int y, ushort buttons, bool press = false, float scroll = 0f)
        {
            if (_mouse == null) return;
            var position = new Vector2(x, Screen.height - y);
            if (press)
            {
                _clickCount = (ushort)(Time.unscaledTime - _lastPress < 0.4f ? _clickCount + 1 : 1);
                _lastPress = Time.unscaledTime;
            }
            const int stateSize = 30, size = 24 + stateSize;
            Header("STAT", size, _mouse.deviceId);
            Marshal.WriteInt32(_buffer, 20, FourCC("MOUS"));
            WriteFloat(24, position.x);
            WriteFloat(28, position.y);
            WriteFloat(32, position.x - _position.x);
            WriteFloat(36, position.y - _position.y);
            WriteFloat(40, 0f);
            WriteFloat(44, scroll);
            Marshal.WriteInt16(_buffer, 48, (short)buttons);
            Marshal.WriteInt16(_buffer, 50, 0);
            Marshal.WriteInt16(_buffer, 52, (short)_clickCount);
            Queue();
            _position = position;
            _buttons = buttons;
        }

        /// <summary>The keyboard's whole state: one bit per key held.</summary>
        private static void Keys()
        {
            if (_keyboard == null) return;
            int bytes = KeyboardState.kSizeInBytes, size = 24 + bytes;
            Header("STAT", size, _keyboard.deviceId);
            Marshal.WriteInt32(_buffer, 20, FourCC("KEYS"));
            foreach (int key in _keysDown)
            {
                int at = 24 + key / 8;
                if (at < size) Marshal.WriteByte(_buffer, at, (byte)(Marshal.ReadByte(_buffer, at) | (1 << (key % 8))));
            }
            Queue();
        }

        private static void WriteFloat(int offset, float value) => Marshal.WriteInt32(_buffer, offset, BitConverter.SingleToInt32Bits(value));

        /// <summary>"enter", "esc", "ctrl", "a", "1", "f5" or any name of Unity's Key.</summary>
        private static int KeyOf(string name)
        {
            string n = name.Trim().ToLowerInvariant();
            switch (n)
            {
                case "return": n = "enter"; break;
                case "esc": n = "escape"; break;
                case "ctrl": case "control": n = "leftctrl"; break;
                case "shift": n = "leftshift"; break;
                case "alt": n = "leftalt"; break;
                case "up": n = "uparrow"; break;
                case "down": n = "downarrow"; break;
                case "left": n = "leftarrow"; break;
                case "right": n = "rightarrow"; break;
                case "del": n = "delete"; break;
            }
            if (n.Length == 1 && n[0] >= '0' && n[0] <= '9') n = "digit" + n;
            return Enum.TryParse(n, true, out Key key) ? (int)key : 0;
        }
    }
}

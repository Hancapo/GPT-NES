using Silk.NET.SDL;

namespace NesEmu.App;

internal sealed unsafe class SdlGamepadBackend : IDisposable
{
    private const int DeviceRefreshIntervalMilliseconds = 1000;
    private static readonly uint RequiredInitFlags = Sdl.InitGamecontroller | Sdl.InitJoystick | Sdl.InitEvents;
    private readonly object _sync = new();
    private readonly Dictionary<int, nint> _controllers = [];
    private readonly Dictionary<int, string> _controllerNames = [];

    private Sdl? _sdl;
    private bool _disposeRequested;
    private bool _initializationAttempted;
    private bool _initialized;
    private long _nextDeviceRefreshTick;

    public IReadOnlyList<ControllerDeviceInfo> GetAvailableControllers()
    {
        lock (_sync)
        {
            RefreshControllersLocked(forceRefresh: true);

            return _controllers
                .OrderBy(entry => entry.Key)
                .Select(entry => new ControllerDeviceInfo(
                    CreateControllerId(entry.Key),
                    _controllerNames.TryGetValue(entry.Key, out var name)
                        ? name
                        : $"Gamepad {entry.Key}",
                    ControllerDeviceSource.Gamepad,
                    entry.Key))
                .ToArray();
        }
    }

    public bool TryReadAutoGamepad(out SdlGamepadState state)
    {
        lock (_sync)
        {
            RefreshControllersLocked(forceRefresh: false);

            foreach (var instanceId in _controllers.Keys.OrderBy(value => value))
            {
                if (TryReadStateLocked(instanceId, out state))
                {
                    return true;
                }
            }
        }

        state = default;
        return false;
    }

    public bool TryReadSelectedGamepad(string? controllerId, out SdlGamepadState state)
    {
        lock (_sync)
        {
            RefreshControllersLocked(forceRefresh: false);

            if (!TryParseControllerId(controllerId, out var instanceId))
            {
                state = default;
                return false;
            }

            if (!_controllers.ContainsKey(instanceId))
            {
                RefreshControllersLocked(forceRefresh: true);
            }

            return TryReadStateLocked(instanceId, out state);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposeRequested)
            {
                return;
            }

            CloseAllControllersLocked();
            _disposeRequested = true;
        }
    }

    private void RefreshControllersLocked(bool forceRefresh)
    {
        if (!EnsureInitializedLocked())
        {
            CloseAllControllersLocked();
            _controllerNames.Clear();
            return;
        }

        _sdl!.PumpEvents();
        _sdl.GameControllerUpdate();
        PruneDetachedControllersLocked();

        var now = Environment.TickCount64;
        if (!forceRefresh && now < _nextDeviceRefreshTick)
        {
            return;
        }

        ScanForControllersLocked();
        _nextDeviceRefreshTick = now + DeviceRefreshIntervalMilliseconds;
    }

    private bool EnsureInitializedLocked()
    {
        if (_disposeRequested)
        {
            return false;
        }

        if (_initialized)
        {
            return true;
        }

        if (_initializationAttempted)
        {
            return false;
        }

        _initializationAttempted = true;

        try
        {
            _sdl = Sdl.GetApi();
            if ((_sdl.WasInit(RequiredInitFlags) & RequiredInitFlags) != RequiredInitFlags
                && _sdl.InitSubSystem(RequiredInitFlags) != 0)
            {
                _sdl = null;
                return false;
            }

            _initialized = true;
            return true;
        }
        catch (DllNotFoundException)
        {
            _sdl = null;
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            _sdl = null;
            return false;
        }
        catch (TypeInitializationException)
        {
            _sdl = null;
            return false;
        }
        catch (BadImageFormatException)
        {
            _sdl = null;
            return false;
        }
    }

    private void ScanForControllersLocked()
    {
        if (_sdl is null)
        {
            return;
        }

        var count = _sdl.NumJoysticks();
        for (var index = 0; index < count; index++)
        {
            if (_sdl.IsGameController(index) != SdlBool.True)
            {
                continue;
            }

            var name = _sdl.GameControllerNameForIndexS(index);
            var controller = _sdl.GameControllerOpen(index);
            if (controller is null)
            {
                continue;
            }

            var joystick = _sdl.GameControllerGetJoystick(controller);
            if (joystick is null)
            {
                _sdl.GameControllerClose(controller);
                continue;
            }

            var instanceId = _sdl.JoystickInstanceID(joystick);
            if (_controllers.TryGetValue(instanceId, out var existingHandle))
            {
                if (existingHandle != (nint)controller)
                {
                    _sdl.GameControllerClose(controller);
                }
            }
            else
            {
                _controllers[instanceId] = (nint)controller;
            }

            _controllerNames[instanceId] = string.IsNullOrWhiteSpace(name)
                ? $"Gamepad {index + 1}"
                : name;
        }
    }

    private bool TryReadStateLocked(int instanceId, out SdlGamepadState state)
    {
        if (_sdl is null
            || !_controllers.TryGetValue(instanceId, out var handle))
        {
            state = default;
            return false;
        }

        var controller = (GameController*)handle;
        if (_sdl.GameControllerGetAttached(controller) != SdlBool.True)
        {
            CloseControllerLocked(instanceId);
            state = default;
            return false;
        }

        var buttons = GamepadButtons.None;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.A) != 0) buttons |= GamepadButtons.A;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.B) != 0) buttons |= GamepadButtons.B;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.X) != 0) buttons |= GamepadButtons.X;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.Y) != 0) buttons |= GamepadButtons.Y;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.Back) != 0) buttons |= GamepadButtons.View;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.Start) != 0) buttons |= GamepadButtons.Menu;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.Leftshoulder) != 0) buttons |= GamepadButtons.LeftShoulder;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.Rightshoulder) != 0) buttons |= GamepadButtons.RightShoulder;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.DpadUp) != 0) buttons |= GamepadButtons.DPadUp;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.DpadDown) != 0) buttons |= GamepadButtons.DPadDown;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.DpadLeft) != 0) buttons |= GamepadButtons.DPadLeft;
        if (_sdl.GameControllerGetButton(controller, GameControllerButton.DpadRight) != 0) buttons |= GamepadButtons.DPadRight;

        state = new SdlGamepadState(
            buttons,
            NormalizeStick(_sdl.GameControllerGetAxis(controller, GameControllerAxis.Leftx)),
            NormalizeStick(_sdl.GameControllerGetAxis(controller, GameControllerAxis.Lefty)),
            NormalizeStick(_sdl.GameControllerGetAxis(controller, GameControllerAxis.Rightx)),
            NormalizeStick(_sdl.GameControllerGetAxis(controller, GameControllerAxis.Righty)),
            NormalizeTrigger(_sdl.GameControllerGetAxis(controller, GameControllerAxis.Triggerleft)),
            NormalizeTrigger(_sdl.GameControllerGetAxis(controller, GameControllerAxis.Triggerright)));

        return true;
    }

    private void PruneDetachedControllersLocked()
    {
        if (_sdl is null || _controllers.Count == 0)
        {
            return;
        }

        var detachedIds = new List<int>();
        foreach (var entry in _controllers)
        {
            var controller = (GameController*)entry.Value;
            if (_sdl.GameControllerGetAttached(controller) != SdlBool.True)
            {
                detachedIds.Add(entry.Key);
            }
        }

        foreach (var instanceId in detachedIds)
        {
            CloseControllerLocked(instanceId);
        }
    }

    private void CloseControllerLocked(int instanceId)
    {
        if (_sdl is not null && _controllers.Remove(instanceId, out var handle))
        {
            _sdl.GameControllerClose((GameController*)handle);
        }

        _controllerNames.Remove(instanceId);
    }

    private void CloseAllControllersLocked()
    {
        if (_sdl is not null)
        {
            foreach (var handle in _controllers.Values)
            {
                _sdl.GameControllerClose((GameController*)handle);
            }
        }

        _controllers.Clear();
        _controllerNames.Clear();
    }

    private static string CreateControllerId(int instanceId) => $"gamepad:{instanceId}";

    private static bool TryParseControllerId(string? controllerId, out int instanceId)
    {
        const string prefix = "gamepad:";
        if (controllerId is not null
            && controllerId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(controllerId[prefix.Length..], out instanceId))
        {
            return true;
        }

        instanceId = -1;
        return false;
    }

    private static double NormalizeStick(short value)
    {
        return value < 0
            ? Math.Max(value / 32768.0, -1.0)
            : Math.Min(value / 32767.0, 1.0);
    }

    private static double NormalizeTrigger(short value)
    {
        return value <= 0
            ? 0.0
            : Math.Min(value / 32767.0, 1.0);
    }
}

internal readonly record struct SdlGamepadState(
    GamepadButtons Buttons,
    double LeftThumbstickX,
    double LeftThumbstickY,
    double RightThumbstickX,
    double RightThumbstickY,
    double LeftTrigger,
    double RightTrigger);

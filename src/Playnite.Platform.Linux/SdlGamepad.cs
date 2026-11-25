using System.Runtime.InteropServices;

namespace Playnite.Platform.Linux;

public enum GamepadButton : byte
{
    Accept = 0,
    Back = 1,
    LeftShoulder = 9,
    RightShoulder = 10,
    Up = 11,
    Down = 12,
    Left = 13,
    Right = 14,
}

/// <summary>SDL3 gamepad input. Open, poll and dispose on the same UI thread.</summary>
public sealed class SdlGamepad : IDisposable
{
    private const string Library = "libSDL3.so.0";
    private const uint GamepadSubsystem = 0x2000;
    private const uint ButtonDown = 0x651;
    private const uint Added = 0x653;
    private const uint Removed = 0x654;
    private readonly Dictionary<uint, nint> controllers = [];
    private bool disposed;

    private SdlGamepad() { }

    public int ConnectedCount => controllers.Count;

    public static SdlGamepad? TryOpen()
    {
        try
        {
            return Init(GamepadSubsystem) ? new SdlGamepad() : null;
        }
        catch (DllNotFoundException)
        {
            return null;
        }
    }

    public void Poll(Action<GamepadButton> pressed)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        for (var remaining = 256; remaining > 0 && !disposed && PollEvent(out var input); remaining--)
        {
            switch (input.Type)
            {
                case Added when !controllers.ContainsKey(input.Device):
                    var handle = OpenGamepad(input.Device);
                    if (handle != 0)
                    {
                        controllers.Add(input.Device, handle);
                    }
                    break;
                case Removed when controllers.Remove(input.Device, out var removed):
                    CloseGamepad(removed);
                    break;
                case ButtonDown when controllers.ContainsKey(input.Device)
                    && Enum.IsDefined((GamepadButton)input.Button):
                    pressed((GamepadButton)input.Button);
                    break;
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (var handle in controllers.Values)
        {
            CloseGamepad(handle);
        }
        controllers.Clear();
        Quit(GamepadSubsystem);
    }

    // SDL_Event reserves 128 bytes. The gamepad device and button events share the ID offset.
    [StructLayout(LayoutKind.Explicit, Size = 128)]
    private struct SdlEvent
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint Device;
        [FieldOffset(20)] public byte Button;
    }

    [DllImport(Library, EntryPoint = "SDL_InitSubSystem", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool Init(uint flags);

    [DllImport(Library, EntryPoint = "SDL_QuitSubSystem", CallingConvention = CallingConvention.Cdecl)]
    private static extern void Quit(uint flags);

    [DllImport(Library, EntryPoint = "SDL_PollEvent", CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool PollEvent(out SdlEvent input);

    [DllImport(Library, EntryPoint = "SDL_OpenGamepad", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint OpenGamepad(uint id);

    [DllImport(Library, EntryPoint = "SDL_CloseGamepad", CallingConvention = CallingConvention.Cdecl)]
    private static extern void CloseGamepad(nint controller);
}

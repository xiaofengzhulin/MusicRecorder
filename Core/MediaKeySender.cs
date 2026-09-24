using System.IO;
using System.Runtime.InteropServices;

namespace MusicRecorder.Core;

/// <summary>发送系统媒体键（作为播放器无法被 SMTC 控制时的兜底）。</summary>
public static class MediaKeySender
{
    private const int INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_MEDIA_NEXT_TRACK = 0xB0;
    private const ushort VK_MEDIA_PREV_TRACK = 0xB1;
    private const ushort VK_MEDIA_PLAY_PAUSE = 0xB3;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void SendPreviousTrack() => Send(VK_MEDIA_PREV_TRACK);
    public static void SendNextTrack() => Send(VK_MEDIA_NEXT_TRACK);
    public static void SendPlayPause() => Send(VK_MEDIA_PLAY_PAUSE);

    private static void Send(ushort vk)
    {
        try
        {
            var inputs = new INPUT[2];
            inputs[0].type = INPUT_KEYBOARD;
            inputs[0].U.ki = new KEYBDINPUT { wVk = vk };
            inputs[1].type = INPUT_KEYBOARD;
            inputs[1].U.ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP };

            var size = Marshal.SizeOf<INPUT>();
            var sent = SendInput((uint)inputs.Length, inputs, size);
            Log.Info($"发送媒体键 0x{vk:X2}，SendInput 返回 {sent}");
        }
        catch (Exception ex)
        {
            Log.Warn($"发送媒体键失败：{ex.Message}");
        }
    }
}

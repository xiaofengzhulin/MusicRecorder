using System.IO;
using System.Text;

namespace MusicRecorder.Core;

/// <summary>
/// 简易 ID3v2.3 标签写入（用于 MediaFoundation 兜底编码路径；Lame 路径自带标签写入）。
/// </summary>
public static class Id3Writer
{
    public static void WriteTag(string mp3Path, string title, string artist, string album, string comment)
    {
        try
        {
            if (!File.Exists(mp3Path)) return;

            var frames = new List<byte>();
            AddTextFrame(frames, "TIT2", title);
            AddTextFrame(frames, "TPE1", artist);
            AddTextFrame(frames, "TALB", album);
            AddTextFrame(frames, "TSSE", "MusicRecorder");
            if (!string.IsNullOrWhiteSpace(comment)) AddTextFrame(frames, "TCOM", comment);

            if (frames.Count == 0) return;

            var header = new byte[10];
            header[0] = (byte)'I'; header[1] = (byte)'D'; header[2] = (byte)'3';
            header[3] = 3;    // version 2.3
            header[4] = 0;    // revision
            header[5] = 0;    // flags
            WriteSyncSafe(header, 6, frames.Count);

            var audio = File.ReadAllBytes(mp3Path);
            using var fs = new FileStream(mp3Path, FileMode.Create, FileAccess.Write, FileShare.None);
            fs.Write(header, 0, header.Length);
            fs.Write(frames.ToArray(), 0, frames.Count);
            fs.Write(audio, 0, audio.Length);
        }
        catch (Exception ex)
        {
            Log.Warn($"写入 ID3 标签失败：{ex.Message}");
        }
    }

    private static void AddTextFrame(List<byte> buffer, string id, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        var text = Encoding.Unicode.GetBytes(value); // UTF-16LE
        var payload = new byte[1 + text.Length + 2];
        payload[0] = 0x01;                            // encoding: UTF-16 with BOM
        payload[1] = 0xFF; payload[2] = 0xFE;         // BOM
        Array.Copy(text, 0, payload, 3, text.Length);

        buffer.AddRange(Encoding.ASCII.GetBytes(id));
        var size = payload.Length;
        buffer.Add((byte)((size >> 24) & 0xFF));
        buffer.Add((byte)((size >> 16) & 0xFF));
        buffer.Add((byte)((size >> 8) & 0xFF));
        buffer.Add((byte)(size & 0xFF));
        buffer.Add(0); buffer.Add(0);                 // flags
        buffer.AddRange(payload);
    }

    private static void WriteSyncSafe(byte[] target, int offset, int value)
    {
        target[offset + 0] = (byte)((value >> 21) & 0x7F);
        target[offset + 1] = (byte)((value >> 14) & 0x7F);
        target[offset + 2] = (byte)((value >> 7) & 0x7F);
        target[offset + 3] = (byte)(value & 0x7F);
    }
}

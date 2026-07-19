using System;
using System.Collections.Generic;
using System.Text;

namespace Ambi.Utils;

public sealed class ShopSaveData
{
    public int Version { get; set; }
    public string SelectedSkin { get; set; } = "Default (gmod)";
    public int SelectedColor { get; set; }
    public List<string> OwnedSkins { get; set; } = new();
    public List<int> OwnedColors { get; set; } = new();
}

public static class DataFormat
{
    private static readonly byte[] EncryptionKey = Encoding.UTF8.GetBytes("hw_save_key2026");

    /// <summary>
    /// Encrypts a string using XOR and a checksum
    /// </summary>
    public static string Encode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        var result = string.Empty;
        var data = Encoding.UTF8.GetBytes(text);

        for (int i = 0; i < data.Length; i++)
            data[i] ^= EncryptionKey[i % EncryptionKey.Length];

        var originalBytes = Encoding.UTF8.GetBytes(text);
        byte checksum = 0;
        foreach (var b in originalBytes)
            checksum ^= b;

        // Prepend the encrypted checksum
        var newChecksum = new byte[data.Length + 1];
        newChecksum[0] = (byte)(checksum ^ EncryptionKey[0]);
        Array.Copy(data, 0, newChecksum, 1, data.Length);

        result = Convert.ToBase64String(newChecksum);

        return result;
    }

    /// <summary>
    /// Decrypts a string
    /// </summary>
    public static string Decode(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
            return string.Empty;

        try
        {
            var withChecksum = Convert.FromBase64String(encrypted);
            if (withChecksum.Length < 2)
                return null;

            // Extract the checksum
            byte storedChecksum = (byte)(withChecksum[0] ^ EncryptionKey[0]);

            // Extract and decrypt the data
            var data = new byte[withChecksum.Length - 1];
            Array.Copy(withChecksum, 1, data, 0, data.Length);

            for (int i = 0; i < data.Length; i++)
                data[i] ^= EncryptionKey[i % EncryptionKey.Length];

            var result = Encoding.UTF8.GetString(data);

            // Verify the checksum
            var bytes = Encoding.UTF8.GetBytes(result);
            byte calculatedChecksum = 0;
            foreach (var b in bytes)
                calculatedChecksum ^= b;

            if (calculatedChecksum != storedChecksum)
            {
                Log.Warning("[DataFormat] Checksum mismatch, data may be corrupted");
                return null;
            }

            return result;
        }
        catch (Exception ex)
        {
            Log.Error($"[DataFormat] {ex.Message}");

            return null;
        }
    }
}

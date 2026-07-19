using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Ambi.Utils;

public static class DataStore
{
    /// <summary>
    /// Saves data to an encrypted file
    /// </summary>
    public static void Save<T>(T data, string fileName)
    {
        try
        {
            // Serialize the object to JSON
            string jsonData = JsonSerializer.Serialize(data, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            // Encrypt the data
            string encryptedData = DataFormat.Encode(jsonData);

            // Save via the s&box FileSystem API
            FileSystem.Data.WriteAllText($"{fileName}.dat", encryptedData);

            Log.Info($"[DataStore] Save {fileName}.dat");
        }
        catch (Exception ex)
        {
            Log.Error($"[DataStore] Error: {ex.Message}");
        }
    }

    /// <summary>
    /// Loads data from an encrypted file
    /// </summary>
    public static T Load<T>(string fileName) where T : new()
    {
        try
        {
            string filePath = $"{fileName}.dat";

            // Check if the file exists
            if (!FileSystem.Data.FileExists(filePath))
            {
                Log.Warning($"[DataStore] Save file not found: {filePath}");
                return new T();
            }

            // Read the encrypted data
            string encryptedData = FileSystem.Data.ReadAllText(filePath);

            // Decrypt the data
            string jsonData = DataFormat.Decode(encryptedData);

            if (string.IsNullOrEmpty(jsonData))
            {
                Log.Error("[DataStore] Failed to decrypt data");
                return new T();
            }

            // Deserialize JSON into an object
            T result = JsonSerializer.Deserialize<T>(jsonData);

            Log.Info($"[DataStore] Data loaded successfully: {filePath}");
            return result ?? new T();
        }
        catch (Exception ex)
        {
            Log.Error($"DataStore.Load: {ex.Message}");
            return new T();
        }
    }

    /// <summary>
    /// Checks if a save file exists
    /// </summary>
    public static bool Exists(string fileName)
    {
        return FileSystem.Data.FileExists($"{fileName}.dat");
    }

    /// <summary>
    /// Deletes a save file
    /// </summary>
    public static void Delete(string fileName)
    {
        try
        {
            string filePath = $"{fileName}.dat";
            if (FileSystem.Data.FileExists(filePath))
            {
                FileSystem.Data.DeleteFile(filePath);
                Log.Info($"[DataStore] Save file deleted: {filePath}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"DataStore.Delete: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets a list of all save files
    /// </summary>
    public static string[] GetAllSaves()
    {
        try
        {
            var files = FileSystem.Data.FindFile("", "*.dat");
            return files.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error($"DataStore.GetAllSaves: {ex.Message}");
            return Array.Empty<string>();
        }
    }
}

using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace OutfitToggleGenerator
{
    internal static class WardrobeAtomicFile
    {
        internal static void WriteText(string path, string text, bool backup = false)
        {
            WriteBytes(path, new UTF8Encoding(false).GetBytes(text), backup);
        }
        internal static void WriteBytes(string path, byte[] bytes, bool backup = false)
        {
            path = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                // Never delete the destination first: a failed save must preserve the old file.
                if (File.Exists(path)) File.Replace(temporary, path, backup ? path + ".bak" : null);
                else File.Move(temporary, path);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { /* Preserve the original save failure. */ }
            }
        }
        internal static string HashFile(string path)
        {
            if (!File.Exists(path)) return string.Empty;
            using (var hash = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
    }
}

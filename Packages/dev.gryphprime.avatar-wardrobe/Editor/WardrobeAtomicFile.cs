using System;
using System.IO;
using System.Text;
using System.Security.Cryptography;

namespace OutfitToggleGenerator
{
    internal static class WardrobeAtomicFile
    {
        // Missing is only a new store when neither durable copy exists. Validate before
        // repairing, and never rotate a corrupt primary over the known-good backup.
        internal static string ReadRecoverableText(string path, Action<string> validate)
        {
            if (!File.Exists(path) && !File.Exists(path + ".bak")) return null;
            Exception primary;
            try
            {
                var text = File.ReadAllText(path);
                validate(text);
                return text;
            }
            catch (Exception error) { primary = error; }
            try
            {
                var recovered = File.ReadAllText(path + ".bak");
                validate(recovered);
                if (File.Exists(path)) File.Copy(path, path + ".corrupt-" + Guid.NewGuid().ToString("N"));
                WriteText(path, recovered);
                UnityEngine.Debug.LogWarning("[Avatar Wardrobe] Recovered settings from " + path + ".bak");
                return recovered;
            }
            catch (Exception backup)
            {
                throw new IOException("Avatar Wardrobe settings could not be recovered. Writes are blocked; restore " + path,
                    new AggregateException(primary, backup));
            }
        }

        internal static void RestoreText(string path, string snapshot, Action<string> validate)
        {
            if (snapshot != null)
            {
                validate(snapshot);
                ReadRecoverableText(path, validate);
                WriteText(path, snapshot, true);
                return;
            }
            // Undoing the creation of a store deliberately restores absence. Retain
            // recovery evidence without leaving a .bak that would resurrect it.
            var suffix = ".removed-" + Guid.NewGuid().ToString("N");
            if (File.Exists(path + ".bak")) File.Move(path + ".bak", path + ".bak" + suffix);
            if (File.Exists(path)) File.Move(path, path + suffix);
        }

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

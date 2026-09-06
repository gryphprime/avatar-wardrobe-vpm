using System;
using System.Collections.Generic;

namespace OutfitToggleGenerator
{
    internal static class WardrobeHttpPolicy
    {
        internal static readonly HashSet<string> Reads = new HashSet<string>(StringComparer.Ordinal)
        {
            "/api/state", "/api/families", "/api/family", "/api/installed", "/api/nameResult",
            "/api/shops", "/api/diag", "/api/thumb", "/api/upload_result", "/api/upload_status",
            "/api/batch_thumb_img", "/api/batch_unassigned", "/api/batch_state", "/api/batch_job", "/api/batch_export", "/api/presets"
        };
        internal static bool SameOrigin(string value, int port)
        {
            Uri uri;
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == "http" &&
                   string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) && uri.Port == port;
        }
        internal static bool IsRead(string path, string operation, string retry)
        {
            if (path == "/api/thumb" && retry == "1") return false;
            if (Reads.Contains(path)) return true;
            // These older endpoints have explicit read/write operation variants.
            return (path == "/api/batch_blendshape" || path == "/api/batch_item" || path == "/api/batch_faceemo") &&
                   (string.IsNullOrEmpty(operation) || operation == "get");
        }
    }
}

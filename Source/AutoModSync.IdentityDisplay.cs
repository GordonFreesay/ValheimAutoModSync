using System;
using System.Text;

namespace ValheimAutoModSync
{
    // Intent: Centralizes player/admin display formatting for the public server signing fingerprint without changing the full fingerprint used for cryptographic pinning.
    internal static class AutoModSyncIdentityDisplay
    {
        // Intent: Validates that a server identity is exactly one lowercase/uppercase SHA-256 hexadecimal fingerprint before it is formatted for display.
        internal static bool IsValidFingerprint(string fingerprint)
        {
            if (String.IsNullOrEmpty(fingerprint) || fingerprint.Length != 64) return false;
            int i;
            for (i = 0; i < fingerprint.Length; i++)
            {
                char c = fingerprint[i];
                bool digit = c >= '0' && c <= '9';
                bool lower = c >= 'a' && c <= 'f';
                bool upper = c >= 'A' && c <= 'F';
                if (!digit && !lower && !upper) return false;
            }
            return true;
        }

        // Intent: Derives a compact 64-bit human comparison code from the public fingerprint while full 256-bit pinning continues internally.
        // Security: this code is display-only; no trust, signature, or equality decision is ever made from the shortened value.
        internal static string VerificationCode(string fingerprint)
        {
            if (!IsValidFingerprint(fingerprint)) return "UNAVAILABLE";
            string value = fingerprint.Substring(0, 16).ToUpperInvariant();
            return value.Substring(0, 4) + "-" + value.Substring(4, 4) + "-" +
                   value.Substring(8, 4) + "-" + value.Substring(12, 4);
        }

        // Intent: Formats the complete public fingerprint only for explicit technical/admin output; normal player UI uses VerificationCode instead.
        internal static string FullFingerprint(string fingerprint)
        {
            if (!IsValidFingerprint(fingerprint)) return fingerprint ?? "";
            StringBuilder result = new StringBuilder(71);
            int i;
            for (i = 0; i < 64; i += 8)
            {
                if (i > 0) result.Append('-');
                result.Append(fingerprint.Substring(i, 8).ToUpperInvariant());
            }
            return result.ToString();
        }
    }
}

// SecretProtector.cs
// Version: 1.0.0
// Changelog:
//   1.0.0 - Initial version. Encrypts secrets in appsettings.Local.json (MQTT
//           password, webhook Authorization header) with Windows DPAPI,
//           machine scope, stored as "dpapi:<base64>". Plain values still work.

using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace RtcCallMonitor.Configuration
{
    public static class SecretProtector
    {
        public const string Prefix = "dpapi:";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RtcCallMonitor.Secrets.v1");

        public static bool IsProtected(string value) => value != null && value.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>
        /// Encrypts for this PC (machine scope, so the service account can decrypt).
        /// Off Windows the value is returned as it is.
        /// </summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain) || IsProtected(plain)) return plain;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return plain;
            return Prefix + Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(plain), protect: true));
        }

        /// <summary>Decrypts a "dpapi:" value; anything else is returned unchanged.</summary>
        public static string Unprotect(string value)
        {
            if (!IsProtected(value)) return value;
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new InvalidOperationException("This setting was encrypted on Windows and can only be read there.");
            }
            var bytes = Convert.FromBase64String(value.Substring(Prefix.Length));
            return Encoding.UTF8.GetString(Dpapi(bytes, protect: false));
        }

        // --- DPAPI via crypt32 (no extra package needed) ---

        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;
        private const int CRYPTPROTECT_LOCAL_MACHINE = 0x4;

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, ref DATA_BLOB pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static byte[] Dpapi(byte[] input, bool protect)
        {
            var inBlob = ToBlob(input);
            var entropyBlob = ToBlob(Entropy);
            var outBlob = new DATA_BLOB();
            try
            {
                var flags = CRYPTPROTECT_UI_FORBIDDEN | CRYPTPROTECT_LOCAL_MACHINE;
                var ok = protect
                    ? CryptProtectData(ref inBlob, "RtcCallMonitor", ref entropyBlob, IntPtr.Zero, IntPtr.Zero, flags, ref outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, flags, ref outBlob);
                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(inBlob.pbData);
                Marshal.FreeHGlobal(entropyBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }

        private static DATA_BLOB ToBlob(byte[] data)
        {
            var blob = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(Math.Max(1, data.Length)) };
            Marshal.Copy(data, 0, blob.pbData, data.Length);
            return blob;
        }
    }
}

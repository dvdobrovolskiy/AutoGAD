// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Dmitriy Dobrovolskiy dima@dobrovolskiy.com

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AutoGAD
{
    /// <summary>
    /// DPAPI wrapper (crypt32.dll) used to keep the Anthropic API key out of plain text on disk.
    /// Raw P/Invoke rather than the NuGet ProtectedData package — no extra assemblies to load inside AutoCAD.
    /// Blobs are tied to the current Windows user: another account (or another machine) cannot decrypt them.
    /// </summary>
    internal static class DataProtection
    {
        private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
            IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        /// <summary>Encrypt a string for the current user; returns base64 of the DPAPI blob.</summary>
        public static string Protect(string plainText)
        {
            if (plainText == null) throw new ArgumentNullException(nameof(plainText));
            return Convert.ToBase64String(Transform(Encoding.UTF8.GetBytes(plainText), true));
        }

        /// <summary>Decrypt a base64 DPAPI blob produced by <see cref="Protect"/>.</summary>
        public static string Unprotect(string base64)
        {
            if (string.IsNullOrEmpty(base64)) throw new ArgumentNullException(nameof(base64));
            return Encoding.UTF8.GetString(Transform(Convert.FromBase64String(base64), false));
        }

        private static byte[] Transform(byte[] input, bool encrypt)
        {
            var inBlob = new DATA_BLOB();
            var outBlob = new DATA_BLOB();
            try
            {
                inBlob.cbData = input.Length;
                inBlob.pbData = Marshal.AllocHGlobal(input.Length);
                Marshal.Copy(input, 0, inBlob.pbData, input.Length);

                bool ok = encrypt
                    ? CryptProtectData(ref inBlob, "AutoGAD API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                        CRYPTPROTECT_UI_FORBIDDEN, out outBlob);

                if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                if (inBlob.pbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.pbData);
                if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
            }
        }
    }
}

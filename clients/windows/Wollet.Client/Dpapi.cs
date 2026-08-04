using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Wollet.Client;

internal static class Dpapi
{
    private const uint CryptProtectUiForbidden = 0x1;
    private const uint CryptProtectLocalMachine = 0x4;

    public static string Protect(string value)
    {
        var plaintext = Encoding.UTF8.GetBytes(value);
        try
        {
            return Convert.ToBase64String(ProtectBytes(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public static string Unprotect(string value)
    {
        byte[] protectedBytes;
        try
        {
            protectedBytes = Convert.FromBase64String(value);
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("本地设备凭据格式无效", exception);
        }

        var plaintext = UnprotectBytes(protectedBytes);
        try
        {
            return Encoding.UTF8.GetString(plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static byte[] ProtectBytes(byte[] plaintext)
    {
        var input = CreateBlob(plaintext);
        try
        {
            if (!CryptProtectData(
                    ref input,
                    "Wollet device credential",
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden | CryptProtectLocalMachine,
                    out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法保护设备凭据");
            }

            return ConsumeBlob(ref output, clearBeforeFree: false);
        }
        finally
        {
            ClearAndFree(ref input);
        }
    }

    private static byte[] UnprotectBytes(byte[] protectedBytes)
    {
        var input = CreateBlob(protectedBytes);
        try
        {
            if (!CryptUnprotectData(
                    ref input,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out var output))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取设备凭据");
            }

            return ConsumeBlob(ref output, clearBeforeFree: true);
        }
        finally
        {
            ClearAndFree(ref input);
        }
    }

    private static DataBlob CreateBlob(byte[] value)
    {
        var blob = new DataBlob
        {
            Length = value.Length,
            Data = Marshal.AllocHGlobal(value.Length),
        };
        Marshal.Copy(value, 0, blob.Data, value.Length);
        return blob;
    }

    private static byte[] ConsumeBlob(ref DataBlob blob, bool clearBeforeFree)
    {
        try
        {
            var value = new byte[blob.Length];
            Marshal.Copy(blob.Data, value, 0, blob.Length);
            return value;
        }
        finally
        {
            if (clearBeforeFree && blob.Data != IntPtr.Zero && blob.Length > 0)
            {
                Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
            }

            if (blob.Data != IntPtr.Zero)
            {
                _ = LocalFree(blob.Data);
            }

            blob = default;
        }
    }

    private static void ClearAndFree(ref DataBlob blob)
    {
        if (blob.Data != IntPtr.Zero)
        {
            if (blob.Length > 0)
            {
                Marshal.Copy(new byte[blob.Length], 0, blob.Data, blob.Length);
            }

            Marshal.FreeHGlobal(blob.Data);
        }

        blob = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        IntPtr description,
        IntPtr optionalEntropy,
        IntPtr reserved,
        IntPtr prompt,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

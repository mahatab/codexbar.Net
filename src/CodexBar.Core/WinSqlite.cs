using System.Runtime.InteropServices;
using System.Text;

namespace CodexBar.Core;

/// <summary>
/// Minimal read-only SQLite access through <c>winsqlite3.dll</c>, which ships with Windows 10 and 11.
/// Used only to read one value from Cursor's state database, so no SQLite package is needed.
/// </summary>
public static class WinSqlite
{
    private const string Library = "winsqlite3";
    private const int Ok = 0;
    private const int Row = 100;
    private const int OpenReadOnly = 0x1;
    private const int TypeText = 3;
    private const int TypeBlob = 4;

    /// <summary>
    /// Runs a query and returns the first column of the first row as raw bytes, or null when there is no row.
    /// Throws <see cref="InvalidOperationException"/> when the database cannot be opened or queried.
    /// </summary>
    public static byte[]? ReadSingleValue(string databasePath, string sql)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("SQLite access is only available on Windows.");
        }

        IntPtr database = IntPtr.Zero;
        IntPtr statement = IntPtr.Zero;
        try
        {
            var result = sqlite3_open_v2(Utf8(databasePath), out database, OpenReadOnly, IntPtr.Zero);
            if (result != Ok)
            {
                throw new InvalidOperationException($"SQLite could not open the database (code {result}).");
            }

            // The owning app may be writing; wait briefly rather than fail on a momentary lock.
            sqlite3_busy_timeout(database, 250);
            result = sqlite3_prepare_v2(database, Utf8(sql), -1, out statement, IntPtr.Zero);
            if (result != Ok)
            {
                throw new InvalidOperationException($"SQLite could not prepare the query (code {result}).");
            }

            result = sqlite3_step(statement);
            if (result != Row)
            {
                return result == 101 ? null : throw new InvalidOperationException($"SQLite query failed (code {result}).");
            }

            var type = sqlite3_column_type(statement, 0);
            if (type is not (TypeText or TypeBlob))
            {
                return null;
            }

            var pointer = sqlite3_column_blob(statement, 0);
            var length = sqlite3_column_bytes(statement, 0);
            if (pointer == IntPtr.Zero || length <= 0)
            {
                return [];
            }

            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new InvalidOperationException("Windows' built-in SQLite (winsqlite3.dll) is not available.", error);
        }
        finally
        {
            if (statement != IntPtr.Zero)
            {
                sqlite3_finalize(statement);
            }

            if (database != IntPtr.Zero)
            {
                sqlite3_close(database);
            }
        }
    }

    /// <summary>Decodes a stored string: plain UTF-8, or the UTF-16LE that VS Code-style stores sometimes use.</summary>
    public static string DecodeText(byte[] bytes)
    {
        var looksUtf16 = bytes.Length >= 2 && bytes.Length % 2 == 0;
        for (var index = 0; looksUtf16 && index < bytes.Length; index += 2)
        {
            looksUtf16 = bytes[index] is > 0 and < 128 && bytes[index + 1] == 0;
        }

        return (looksUtf16 ? Encoding.Unicode : Encoding.UTF8).GetString(bytes);
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");

#pragma warning disable SA1300, IDE1006 // Native names.
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_open_v2(byte[] filename, out IntPtr database, int flags, IntPtr vfs);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_busy_timeout(IntPtr database, int milliseconds);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_prepare_v2(IntPtr database, byte[] sql, int length, out IntPtr statement, IntPtr tail);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_column_type(IntPtr statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern int sqlite3_close(IntPtr database);
#pragma warning restore SA1300, IDE1006
}

using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace AiVisionStudio.Services;

/// <summary>Read the original SQLite history using the SQLite runtime shipped with Windows.</summary>
public static class HmiDatabase
{
    public static IReadOnlyList<IReadOnlyDictionary<string, object>> Read(string path, string table, DateTime? from = null, DateTime? to = null)
    {
        if (!File.Exists(path)) return Array.Empty<IReadOnlyDictionary<string, object>>();
        nint database = 0, statement = 0;
        try
        {
            Check(sqlite3_open_v2(path, out database, 1, null), database);
            Check(sqlite3_busy_timeout(database, 1000), database);
            var listing = string.IsNullOrWhiteSpace(table);
            var sql = listing ? "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name <> 'TableDesc' ORDER BY name"
                : "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
            if (!listing && (from.HasValue || to.HasValue))
            {
                var conditions = new List<string>();
                if (from.HasValue) conditions.Add("InsertTime >= ?1");
                if (to.HasValue) conditions.Add("InsertTime <= ?2");
                sql += " WHERE " + string.Join(" AND ", conditions);
            }
            Check(sqlite3_prepare_v2(database, sql, -1, out statement, 0), database);
            if (!listing && from.HasValue) Check(sqlite3_bind_text(statement, 1, from.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), -1, new nint(-1)), database);
            if (!listing && to.HasValue) Check(sqlite3_bind_text(statement, 2, to.Value.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), -1, new nint(-1)), database);
            var result = new List<IReadOnlyDictionary<string, object>>();
            var columns = sqlite3_column_count(statement);
            int code;
            while ((code = sqlite3_step(statement)) == 100)
            {
                var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < columns; i++)
                {
                    var name = Marshal.PtrToStringUTF8(sqlite3_column_name(statement, i))!;
                    row[name] = sqlite3_column_type(statement, i) switch
                    {
                        1 => sqlite3_column_int64(statement, i),
                        2 => sqlite3_column_double(statement, i),
                        3 => Marshal.PtrToStringUTF8(sqlite3_column_text(statement, i)),
                        5 => null,
                        _ => "[binary]"
                    };
                }
                result.Add(row);
            }
            if (code != 101) Check(code, database);
            return result;
        }
        finally
        {
            if (statement != 0) sqlite3_finalize(statement);
            if (database != 0) sqlite3_close(database);
        }
    }
    private static void Check(int code, nint database)
    {
        if (code != 0) throw new InvalidDataException("历史数据库：" + (database == 0 ? code.ToString() : Marshal.PtrToStringUTF8(sqlite3_errmsg(database))));
    }
    private const string Dll = "winsqlite3.dll";
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out nint database, int flags, [MarshalAs(UnmanagedType.LPUTF8Str)] string vfs);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_busy_timeout(nint database, int milliseconds);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_prepare_v2(nint database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint statement, nint tail);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_bind_text(nint statement, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, int bytes, nint destroy);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_step(nint statement);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_count(nint statement);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_column_name(nint statement, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_column_type(nint statement, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern long sqlite3_column_int64(nint statement, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern double sqlite3_column_double(nint statement, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_column_text(nint statement, int index);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern nint sqlite3_errmsg(nint database);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_finalize(nint statement);
    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)] private static extern int sqlite3_close(nint database);
}

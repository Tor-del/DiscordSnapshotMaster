using System.Runtime.InteropServices;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace DiscordSnapshot;

internal sealed class Sqlite : IDisposable
{
    private const int Row = 100;
    private const int Done = 101;
    private const int Null = 5;
    private nint handle;

    public Sqlite(string file, bool create, bool readOnly)
    {
        // READONLY, READWRITE and CREATE are the documented sqlite3_open_v2 flags.
        var flags = readOnly ? 1 : create ? 2 | 4 : 2;
        var result = Native.Open(file, out handle, flags, 0);
        try
        {
            if (result == 14) throw new IOException(Strings.Get("ArchiveOpenFailed"));
            Check(result);
            Script("PRAGMA foreign_keys = ON; PRAGMA trusted_schema = OFF;");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Check(int result)
    {
        if (result is 0 or Row or Done) return;
        var message = result is 5 or 6 ? Strings.Get("ArchiveBusy") : Marshal.PtrToStringUTF8(Native.Error(handle));
        throw new IOException(message ?? Strings.Get("SqliteError", result));
    }

    public void Script(string sql) => Check(Native.Exec(handle, sql, 0, 0, 0));

    public void Transaction(Action write)
    {
        Script("BEGIN IMMEDIATE");
        try
        {
            write();
            Script("COMMIT");
        }
        catch
        {
            // SQLite can roll back automatically on I/O errors or when the disk fills up.
            if (Native.AutoCommit(handle) == 0) Native.Exec(handle, "ROLLBACK", 0, 0, 0);
            throw;
        }
    }

    private nint Prepare(string sql, object?[] values)
    {
        Check(Native.Prepare(handle, sql, -1, out var statement, 0));
        try
        {
            for (var index = 0; index < values.Length; index++)
            {
                var parameter = index + 1;
                var result = values[index] switch
                {
                    null => Native.BindNull(statement, parameter),
                    string text => Native.BindText(statement, parameter, System.Text.Encoding.UTF8.GetBytes(text + '\0'),
                        System.Text.Encoding.UTF8.GetByteCount(text), -1),
                    int number => Native.BindInteger(statement, parameter, number),
                    long number => Native.BindInteger(statement, parameter, number),
                    bool value => Native.BindInteger(statement, parameter, value ? 1 : 0),
                    _ => throw new ArgumentException("Unsupported SQLite parameter type."),
                };
                Check(result);
            }
            return statement;
        }
        catch
        {
            Native.Finalize(statement);
            throw;
        }
    }

    public void Execute(string sql, params object?[] values)
    {
        var statement = Prepare(sql, values);
        try { Check(Native.Step(statement)); }
        finally { Native.Finalize(statement); }
    }

    public IEnumerable<T> Query<T>(string sql, Func<Columns, T> read, params object?[] values)
    {
        var statement = Prepare(sql, values);
        try
        {
            int result;
            while ((result = Native.Step(statement)) == Row) yield return read(new Columns(statement));
            Check(result);
        }
        finally { Native.Finalize(statement); }
    }

    internal readonly struct Columns(nint statement)
    {
        public string? Text(int column)
        {
            if (Native.ColumnType(statement, column) == Null) return null;
            var pointer = Native.ColumnText(statement, column);
            return Marshal.PtrToStringUTF8(pointer, Native.ColumnBytes(statement, column));
        }

        public long Number(int column) => Native.ColumnInteger(statement, column);
        public long? OptionalNumber(int column) => Native.ColumnType(statement, column) == Null ? null : Number(column);
        public bool Flag(int column) => Number(column) != 0;
    }

    public void Dispose()
    {
        if (handle == 0) return;
        Native.Close(handle);
        handle = 0;
    }

    private static class Native
    {
        private const string Library = "winsqlite3.dll";
        private const CallingConvention Convention = CallingConvention.Cdecl;

        [DllImport(Library, EntryPoint = "sqlite3_open_v2", CallingConvention = Convention)]
        public static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string filename, out nint db, int flags, nint vfs);
        [DllImport(Library, EntryPoint = "sqlite3_close_v2", CallingConvention = Convention)]
        public static extern int Close(nint db);
        [DllImport(Library, EntryPoint = "sqlite3_errmsg", CallingConvention = Convention)]
        public static extern nint Error(nint db);
        [DllImport(Library, EntryPoint = "sqlite3_get_autocommit", CallingConvention = Convention)]
        public static extern int AutoCommit(nint db);
        [DllImport(Library, EntryPoint = "sqlite3_exec", CallingConvention = Convention)]
        public static extern int Exec(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, nint callback, nint state, nint error);
        [DllImport(Library, EntryPoint = "sqlite3_prepare_v2", CallingConvention = Convention)]
        public static extern int Prepare(nint db, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql, int bytes, out nint statement, nint tail);
        [DllImport(Library, EntryPoint = "sqlite3_step", CallingConvention = Convention)]
        public static extern int Step(nint statement);
        [DllImport(Library, EntryPoint = "sqlite3_finalize", CallingConvention = Convention)]
        public static extern int Finalize(nint statement);
        [DllImport(Library, EntryPoint = "sqlite3_bind_null", CallingConvention = Convention)]
        public static extern int BindNull(nint statement, int index);
        [DllImport(Library, EntryPoint = "sqlite3_bind_int64", CallingConvention = Convention)]
        public static extern int BindInteger(nint statement, int index, long value);
        [DllImport(Library, EntryPoint = "sqlite3_bind_text", CallingConvention = Convention)]
        public static extern int BindText(nint statement, int index, byte[] text, int length, nint destructor);
        [DllImport(Library, EntryPoint = "sqlite3_column_type", CallingConvention = Convention)]
        public static extern int ColumnType(nint statement, int column);
        [DllImport(Library, EntryPoint = "sqlite3_column_int64", CallingConvention = Convention)]
        public static extern long ColumnInteger(nint statement, int column);
        [DllImport(Library, EntryPoint = "sqlite3_column_text", CallingConvention = Convention)]
        public static extern nint ColumnText(nint statement, int column);
        [DllImport(Library, EntryPoint = "sqlite3_column_bytes", CallingConvention = Convention)]
        public static extern int ColumnBytes(nint statement, int column);
    }
}

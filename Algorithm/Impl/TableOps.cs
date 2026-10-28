using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Project-local SQLite tables. The Windows SQLite API is used without a vendor kernel.</summary>
internal sealed class ProjectDatabase : IDisposable
{
    private IntPtr _db;
    public ProjectDatabase(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var code=Native.sqlite3_open16(path,out _db);
        if(code!=0){var error=Error();Dispose();throw new IOException(error);}
        Native.sqlite3_busy_timeout(_db,5000);
    }
    private string Error()=>Marshal.PtrToStringUni(Native.sqlite3_errmsg16(_db))??"SQLite 操作失败";
    public static string Quote(string name)
        => !string.IsNullOrWhiteSpace(name)&&!name.Contains('\0')&&name.Length<=255?'"'+name.Replace("\"","\"\"")+'"':throw new ArgumentException("数据表或列名称无效");
    public List<Dictionary<string,object>> Execute(string sql,params object[] values)
    {
        var code=Native.sqlite3_prepare16_v2(_db,sql,-1,out var statement,IntPtr.Zero);
        if(code!=0)throw new IOException(Error());
        try
        {
            for(int i=0;i<values.Length;i++)
            {
                var value=values[i];int bound;
                if(value==null)bound=Native.sqlite3_bind_null(statement,i+1);
                else if(value is bool b)bound=Native.sqlite3_bind_int64(statement,i+1,b?1:0);
                else if(value is byte or short or int or long)bound=Native.sqlite3_bind_int64(statement,i+1,Convert.ToInt64(value));
                else if(value is double or float or decimal)
                {
                    var d=Convert.ToDouble(value);if(!double.IsFinite(d))throw new ArgumentException("数据库数值必须有限");
                    bound=Native.sqlite3_bind_double(statement,i+1,d);
                }
                else if(value is string s)bound=Native.sqlite3_bind_text16(statement,i+1,s,s.Length*2,new IntPtr(-1));
                else throw new ArgumentException("数据列需要标量，不能写入 "+value.GetType().Name);
                if(bound!=0)throw new IOException(Error());
            }
            var rows=new List<Dictionary<string,object>>();
            while((code=Native.sqlite3_step(statement))==100)
            {
                var row=new Dictionary<string,object>(StringComparer.OrdinalIgnoreCase);
                for(int i=0;i<Native.sqlite3_column_count(statement);i++)
                    row[Marshal.PtrToStringUni(Native.sqlite3_column_name16(statement,i))!]=Native.sqlite3_column_type(statement,i) switch {
                        1=>Native.sqlite3_column_int64(statement,i),2=>Native.sqlite3_column_double(statement,i),5=>null,
                        _=>Marshal.PtrToStringUni(Native.sqlite3_column_text16(statement,i))};
                rows.Add(row);
            }
            if(code!=101)throw new IOException(Error());
            return rows;
        }
        finally{Native.sqlite3_finalize(statement);}
    }
    public void Dispose(){if(_db!=IntPtr.Zero){Native.sqlite3_close(_db);_db=IntPtr.Zero;}}
    private static class Native
    {
        private const string Library="winsqlite3.dll";
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]internal static extern int sqlite3_open16(string path,out IntPtr db);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_close(IntPtr db);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_busy_timeout(IntPtr db,int milliseconds);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr sqlite3_errmsg16(IntPtr db);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]internal static extern int sqlite3_prepare16_v2(IntPtr db,string sql,int bytes,out IntPtr statement,IntPtr tail);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_bind_null(IntPtr statement,int index);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_bind_int64(IntPtr statement,int index,long value);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_bind_double(IntPtr statement,int index,double value);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl,CharSet=CharSet.Unicode)]internal static extern int sqlite3_bind_text16(IntPtr statement,int index,string value,int bytes,IntPtr destructor);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_step(IntPtr statement);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_finalize(IntPtr statement);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_column_count(IntPtr statement);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr sqlite3_column_name16(IntPtr statement,int index);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern int sqlite3_column_type(IntPtr statement,int index);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern long sqlite3_column_int64(IntPtr statement,int index);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern double sqlite3_column_double(IntPtr statement,int index);
        [DllImport(Library,CallingConvention=CallingConvention.Cdecl)]internal static extern IntPtr sqlite3_column_text16(IntPtr statement,int index);
    }
}

public sealed class CreateTableOp : IOperator
{
    public string Driver=>"Create Table";
    public bool Run(OpContext ctx,ParamBag p)
    {
        var table=p.Str("Table Name");var columns=p.StringList("_input_list");
        if(columns.Count==0||columns.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=columns.Count)throw new ArgumentException("数据表列为空或重复");
        var definitions=columns.Select(name=>{
            var type=Convert.ToString(p.Group(name)?.GetValueOrDefault("Column Type"))?.ToUpperInvariant()??"TEXT";
            if(type is not ("BIGINT" or "INTEGER" or "REAL" or "DATETIME" or "TEXT" or "BOOLEAN"))throw new ArgumentException("不支持的数据列类型："+type);
            return ProjectDatabase.Quote(name)+" "+(name.Equals("Id",StringComparison.OrdinalIgnoreCase)?"INTEGER PRIMARY KEY AUTOINCREMENT":type);
        });
        var path=ctx.ResolveOutputPath("data.db");using var db=new ProjectDatabase(path);
        db.Execute("CREATE TABLE IF NOT EXISTS "+ProjectDatabase.Quote(table)+" ("+string.Join(",",definitions)+")");
        ctx.Vars.Set("Table",table);ctx.Vars.Set("Database",path);ctx.Log("创建数据表："+table,false);return true;
    }
}

public sealed class WriteTableOp : IOperator
{
    public string Driver=>"Write Table";
    public bool Run(OpContext ctx,ParamBag p)
    {
        var table=p.Str("Table Name");var quoted=ProjectDatabase.Quote(table);var names=p.StringList("_input_list");
        if(names.Count==0||names.Distinct(StringComparer.OrdinalIgnoreCase).Count()!=names.Count)throw new ArgumentException("写表列为空或重复");
        var values=new List<object>();
        foreach(var name in names)
        {
            var column=p.Group(name)??throw new ArgumentException("写表列未配置："+name);
            var sqlType=C3.N(column,"Sqltype",0);
            var type=sqlType==1?Convert.ToString(column.GetValueOrDefault("Type Number")):Convert.ToString(column.GetValueOrDefault("Type Bool"));
            var field=type?.ToLowerInvariant() switch {"bind"=>"Value","float"=>"Float Value","int"=>"Int Value","bool"=>"Bool Value","string"=>"String Value",_=>throw new ArgumentException("写表数据类型无效："+name)};
            var value=ControlValues.Read(p,name+"."+field);
            if(value==null)throw new InvalidOperationException("写表输入为空："+name+"."+field);
            if(type=="bind"&&value is not (byte or short or int or long or float or double or decimal))throw new ArgumentException("写表数值绑定类型无效："+name);
            values.Add(type switch {"float"=>Convert.ToDouble(value,CultureInfo.InvariantCulture),"int"=>Convert.ToInt64(value,CultureInfo.InvariantCulture),"bool"=>ControlValues.Bool(value),_=>Convert.ToString(value)});
        }
        var path=ctx.ResolveOutputPath("data.db");using var db=new ProjectDatabase(path);
        db.Execute("BEGIN IMMEDIATE");
        try
        {
            var schema=db.Execute("PRAGMA table_info("+quoted+")");
            if(schema.Count==0)throw new InvalidOperationException("数据表不存在，请先创建："+table);
            foreach(var name in names)if(!schema.Any(c=>string.Equals(Convert.ToString(c["name"]),name,StringComparison.OrdinalIgnoreCase)))throw new InvalidOperationException("数据表列不存在："+name);
            var columns=names.Select(ProjectDatabase.Quote).ToList();var expressions=names.Select(_=>"?").ToList();
            if(schema.Any(c=>Equals(Convert.ToString(c["name"]),"InsertTime"))&&!names.Contains("InsertTime",StringComparer.OrdinalIgnoreCase))
            {columns.Add("\"InsertTime\"");expressions.Add("?");values.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture));}
            if(schema.Any(c=>Equals(Convert.ToString(c["name"]),"Id"))&&!names.Contains("Id",StringComparer.OrdinalIgnoreCase))
            {columns.Add("\"Id\"");expressions.Add("(SELECT COALESCE(MAX(\"Id\"),0)+1 FROM "+quoted+")");}
            ctx.CancellationToken.ThrowIfCancellationRequested();
            db.Execute("INSERT INTO "+quoted+" ("+string.Join(",",columns)+") VALUES ("+string.Join(",",expressions)+")",values.ToArray());
            var id=db.Execute("SELECT last_insert_rowid() AS Id")[0]["Id"];
            db.Execute("COMMIT");ctx.Vars.Set("Id",id);ctx.Vars.Set("RowsWritten",1);ctx.Vars.Set("Database",path);
        }
        catch{db.Execute("ROLLBACK");throw;}
        ctx.Log($"写入数据表：{table}，{names.Count} 列，1 行",false);return true;
    }
}

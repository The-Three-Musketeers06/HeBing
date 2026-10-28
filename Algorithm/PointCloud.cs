using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AiVisionStudio.Algorithm;

/// <summary>点云（订阅原版 PCD 数据：x/y/z + 强度）。</summary>
public class PointCloud
{
    public List<float> X = new();
    public List<float> Y = new();
    public List<float> Z = new();
    public List<float> I = new();
    /// <summary>Optional per-point display scalar, separate from geometry and intensity.</summary>
    public float[] DisplayScalars;
    /// <summary>Optional point visibility mask; null displays every point.</summary>
    public int[] DisplayIndices;
    /// <summary>Optional packed source sRGB values, 0xRRGGBB, one for each point.</summary>
    public uint[] ColorsRgb;
    public string DisplayScalarName = "Z";
    /// <summary>Optional immutable row/column mapping. Missing cells use point index -1.</summary>
    public CloudRaster Raster { get; set; }
    /// <summary>Acquisition row/column topology, independent of physical XY axes.</summary>
    public CloudPointGrid OrderedGrid { get; set; }
    public double? DisplayMin, DisplayMax;

    public int Count => X.Count;

    public void Add(float x, float y, float z, float i = 0)
    {
        X.Add(x); Y.Add(y); Z.Add(z); I.Add(i);
    }

    public void AddRange(PointCloud other)
    {
        X.AddRange(other.X);
        Y.AddRange(other.Y);
        Z.AddRange(other.Z);
        I.AddRange(other.I);
        DisplayScalars = null;
        DisplayIndices = null;
        ColorsRgb = null;
        DisplayMin = DisplayMax = null;
        DisplayScalarName = "Z";
        Raster = null;
        OrderedGrid = null;
    }

    public PointCloud Clone()
    {
        var c = new PointCloud();
        c.X.AddRange(X); c.Y.AddRange(Y); c.Z.AddRange(Z); c.I.AddRange(I);
        c.DisplayScalars = DisplayScalars?.ToArray();
        c.DisplayIndices = DisplayIndices?.ToArray();
        c.ColorsRgb = ColorsRgb?.ToArray();
        c.DisplayScalarName = DisplayScalarName;
        c.Raster = Raster;
        c.OrderedGrid = OrderedGrid;
        c.DisplayMin = DisplayMin; c.DisplayMax = DisplayMax;
        return c;
    }

    public (float x, float y, float z) At(int i) => (X[i], Y[i], Z[i]);

    public (float min, float max) ZRange()
    {
        if (Count == 0) return (0, 0);
        float lo = float.MaxValue, hi = float.MinValue;
        foreach (var z in Z) { if (z < lo) lo = z; if (z > hi) hi = z; }
        return (lo, hi);
    }

    /// <summary>点云包围盒。</summary>
    public (double x1, double y1, double x2, double y2, double z1, double z2) Bounds()
    {
        double x1 = double.MaxValue, y1 = double.MaxValue, z1 = double.MaxValue;
        double x2 = double.MinValue, y2 = double.MinValue, z2 = double.MinValue;
        for (int i = 0; i < Count; i++)
        {
            if (X[i] < x1) x1 = X[i]; if (X[i] > x2) x2 = X[i];
            if (Y[i] < y1) y1 = Y[i]; if (Y[i] > y2) y2 = Y[i];
            if (Z[i] < z1) z1 = Z[i]; if (Z[i] > z2) z2 = Z[i];
        }
        if (Count == 0) return (0, 0, 0, 0, 0, 0);
        return (x1, y1, x2, y2, z1, z2);
    }

    public (float x, float y, float z) Centroid()
    {
        if (Count == 0) return (0, 0, 0);
        double sx = 0, sy = 0, sz = 0;
        for (int i = 0; i < Count; i++) { sx += X[i]; sy += Y[i]; sz += Z[i]; }
        return ((float)(sx / Count), (float)(sy / Count), (float)(sz / Count));
    }
}

/// <summary>Raster cells map to actual points; absent cells never become fake Z=0 points.</summary>
public sealed record CloudRaster(int Width, int Height, double OriginX, double OriginY,
    double ResolutionX, double ResolutionY, int[] PointIndices);
public sealed record CloudPointGrid(int Width, int Height, int[] PointIndices);

public sealed record DepthImageGeometry(PointCloud Cloud, CloudRaster Raster, PointCloud InputCloud = null);

/// <summary>PCD 读写（支持 ascii / binary，FIELDS 里找 x/y/z，多余字段跳过）。</summary>
public static class PcdIO
{
    public static PointCloud Load(string path)
    {
        if (!File.Exists(path)) return new PointCloud();
        var bytes = File.ReadAllBytes(path);
        if (Path.GetExtension(path).ToLowerInvariant() is ".txt" or ".xyz" or ".csv")
        {
            // Some legacy projects use .txt for a PCD payload; content takes precedence.
            var prefix = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096)).TrimStart();
            if (!prefix.StartsWith("# .PCD") && !prefix.StartsWith("VERSION ") && !prefix.StartsWith("FIELDS ") && !prefix.Contains("\nFIELDS ")) return LoadXyz(path);
        }
        return Load(bytes);
    }

    /// <summary>Plain XYZ/XYZI, with an optional coordinate header. Invalid records never become synthetic points.</summary>
    private static PointCloud LoadXyz(string path)
    {
        var cloud = new PointCloud();
        int row = 0;
        int[] columns = null;
        foreach (var source in File.ReadLines(path))
        {
            row++;
            var line = source.Trim().TrimStart('\uFEFF');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//")) continue;
            var values = line.Split(new[] { ' ', '\t', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (columns == null && cloud.Count == 0 && values.Any(v => v.Equals("X", StringComparison.OrdinalIgnoreCase)))
            {
                columns = new[] { "X", "Y", "Z", "Intensity" }.Select(axis => Array.FindIndex(values, v => v.Equals(axis, StringComparison.OrdinalIgnoreCase))).ToArray();
                if (columns.Take(3).Any(i => i < 0)) throw new InvalidDataException($"XYZ 第 {row} 行缺少 X/Y/Z 列");
                if (columns[3] < 0) columns[3] = Array.FindIndex(values, v => v.Equals("I", StringComparison.OrdinalIgnoreCase));
                continue;
            }
            columns ??= new[] { 0, 1, 2, values.Length == 4 ? 3 : -1 };
            if (values.Length < 3 || columns.Any(i => i >= values.Length)) throw new InvalidDataException($"XYZ 第 {row} 行列数不足");
            var point = new float[4];
            for (int k = 0; k < 4; k++)
                if (columns[k] >= 0 && (!float.TryParse(values[columns[k]], NumberStyles.Float, CultureInfo.InvariantCulture, out point[k]) || !float.IsFinite(point[k])))
                    throw new InvalidDataException($"XYZ 第 {row} 行包含无效坐标或强度");
            cloud.Add(point[0], point[1], point[2], point[3]);
        }
        if (cloud.Count == 0) throw new InvalidDataException("XYZ 文件中没有有效点");
        return cloud;
    }

    /// <summary>Parse an in-memory PCD payload (including the original local renderer HTTP response).</summary>
    public static PointCloud Load(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var cloud = new PointCloud();
        var header = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var dataPos = 0;
        while (dataPos < bytes.Length)
        {
            var end = Array.IndexOf(bytes, (byte)'\n', dataPos);
            if (end < 0) end = bytes.Length;
            var line = Encoding.ASCII.GetString(bytes, dataPos, end - dataPos).Trim();
            dataPos = end < bytes.Length ? end + 1 : end;
            if (dataPos > 1024 * 1024) throw new InvalidDataException("PCD header exceeds 1 MiB.");
            if (line.Length == 0 || line[0] == '#') continue;
            var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            header[parts[0]] = parts.Skip(1).ToArray();
            if (parts[0].Equals("DATA", StringComparison.OrdinalIgnoreCase)) break;
        }
        if (!header.TryGetValue("DATA", out var format) || format.Length != 1)
            throw new InvalidDataException("PCD DATA header is missing or invalid.");
        if (!header.TryGetValue("FIELDS", out var fields) || fields.Length == 0)
            throw new InvalidDataException("PCD FIELDS header is missing.");

        var sizes = Numbers("SIZE", 4);
        var counts = Numbers("COUNT", 1);
        var types = header.TryGetValue("TYPE", out var ts) ? ts : Enumerable.Repeat("F", fields.Length).ToArray();
        if (types.Length != fields.Length) throw new InvalidDataException("PCD TYPE and FIELDS lengths differ.");
        var offsets = new int[fields.Length];
        var tokenOffsets = new int[fields.Length];
        int pointSize = 0, tokenCount = 0;
        for (int k = 0; k < fields.Length; k++)
        {
            if (counts[k] <= 0 || sizes[k] <= 0) throw new InvalidDataException("PCD SIZE/COUNT must be positive.");
            if (!(types[k] == "F" && sizes[k] is 4 or 8) &&
                !(types[k] is "I" or "U" && sizes[k] is 1 or 2 or 4 or 8))
                throw new InvalidDataException($"Unsupported PCD field encoding: {types[k]}{sizes[k]}.");
            offsets[k] = pointSize;
            tokenOffsets[k] = tokenCount;
            pointSize = checked(pointSize + sizes[k] * counts[k]);
            tokenCount = checked(tokenCount + counts[k]);
        }
        int FieldIndex(string name) => Array.FindIndex(fields, f => f.Equals(name, StringComparison.OrdinalIgnoreCase));
        int ix = FieldIndex("x"), iy = FieldIndex("y"), iz = FieldIndex("z"), ii = FieldIndex("intensity");
        if (ii < 0) ii = FieldIndex("i");
        var rgbField = FieldIndex("rgb");
        if (rgbField < 0) rgbField = FieldIndex("rgba");
        if (rgbField >= 0 && (sizes[rgbField] != 4 || counts[rgbField] != 1))
            throw new InvalidDataException("PCD packed RGB/RGBA must have SIZE 4 and COUNT 1.");
        var colors = rgbField >= 0 ? new List<uint>() : null;
        if (ix < 0 || iy < 0 || iz < 0) throw new InvalidDataException("PCD requires x, y and z fields.");
        int? pointCount = null;
        if (header.TryGetValue("POINTS", out var ps)) pointCount = Count(ps, "POINTS");
        else if (header.TryGetValue("WIDTH", out var ws) && header.TryGetValue("HEIGHT", out var hs))
            pointCount = checked(Count(ws, "WIDTH") * Count(hs, "HEIGHT"));
        int organizedWidth = header.TryGetValue("WIDTH", out var widthParts) ? Count(widthParts, "WIDTH") : 0;
        int organizedHeight = header.TryGetValue("HEIGHT", out var heightParts) ? Count(heightParts, "HEIGHT") : 0;
        int[] organized = organizedHeight > 1 && organizedWidth > 0 && (long)organizedWidth * organizedHeight == pointCount && pointCount <= 16_000_000 ? Enumerable.Repeat(-1, pointCount.Value).ToArray() : null;

        if (format[0].Equals("ascii", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StringReader(Encoding.ASCII.GetString(bytes, dataPos, bytes.Length - dataPos));
            string line;
            int rows = 0;
            while ((line = reader.ReadLine()) != null && (!pointCount.HasValue || rows < pointCount.Value))
            {
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < tokenCount) throw new InvalidDataException($"PCD row {rows + 1} is truncated.");
                float Value(int field)
                {
                    if (!float.TryParse(parts[tokenOffsets[field]], NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                        throw new InvalidDataException($"Invalid PCD number in row {rows + 1}.");
                    return value;
                }
                var x = Value(ix); var y = Value(iy); var z = Value(iz);
                if (Valid(x, y, z))
                {
                    if (organized != null) organized[rows] = cloud.Count;
                    cloud.Add(x, y, z, ii >= 0 ? Value(ii) : 0);
                    if (colors != null)
                    {
                        uint rgb;
                        if (types[rgbField] == "F") rgb = BitConverter.SingleToUInt32Bits(Value(rgbField));
                        else if (!uint.TryParse(parts[tokenOffsets[rgbField]], NumberStyles.Integer, CultureInfo.InvariantCulture, out rgb))
                            throw new InvalidDataException("Invalid PCD packed RGB value.");
                        colors.Add(rgb & 0x00ffffff);
                    }
                }
                rows++;
            }
            if (pointCount.HasValue && rows != pointCount.Value) throw new InvalidDataException("PCD ASCII payload is truncated.");
            cloud.ColorsRgb = colors?.ToArray();
            if (organized != null) cloud.OrderedGrid = new(organizedWidth, organizedHeight, organized);
            return cloud;
        }

        var payload = bytes;
        var start = dataPos;
        var compressed = format[0].Equals("binary_compressed", StringComparison.OrdinalIgnoreCase);
        if (compressed)
        {
            if (bytes.Length - dataPos < 8) throw new InvalidDataException("PCD compressed header is truncated.");
            var compressedSize = BitConverter.ToUInt32(bytes, dataPos);
            var expandedSize = BitConverter.ToUInt32(bytes, dataPos + 4);
            if (compressedSize > bytes.Length - dataPos - 8 || expandedSize > 512 * 1024 * 1024)
                throw new InvalidDataException("PCD compressed payload length is invalid.");
            if (expandedSize % pointSize != 0 || (pointCount.HasValue && (long)pointCount.Value * pointSize != expandedSize))
                throw new InvalidDataException("PCD compressed payload does not match POINTS/SIZE/COUNT.");
            pointCount ??= (int)expandedSize / pointSize;
            payload = ExpandLzf(bytes.AsSpan(dataPos + 8, (int)compressedSize), (int)expandedSize);
            start = 0;
        }
        else if (!format[0].Equals("binary", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsupported PCD DATA format: {format[0]}.");
        pointCount ??= (payload.Length - start) / pointSize;
        if ((long)pointCount.Value * pointSize > payload.Length - start)
            throw new InvalidDataException("PCD binary payload is truncated.");
        for (int k = 0; k < pointCount.Value; k++)
        {
            float Value(int field)
            {
                var offset = compressed ? offsets[field] * pointCount.Value + k * sizes[field] * counts[field]
                                        : start + k * pointSize + offsets[field];
                return ReadNumber(payload, offset, sizes[field], types[field]);
            }
            var x = Value(ix); var y = Value(iy); var z = Value(iz);
            if (Valid(x, y, z))
            {
                if (organized != null) organized[k] = cloud.Count;
                cloud.Add(x, y, z, ii >= 0 ? Value(ii) : 0);
                if (colors != null)
                {
                    var offset = compressed ? offsets[rgbField] * pointCount.Value + k * sizes[rgbField]
                        : start + k * pointSize + offsets[rgbField];
                    colors.Add(BitConverter.ToUInt32(payload, offset) & 0x00ffffff);
                }
            }
        }
        cloud.ColorsRgb = colors?.ToArray();
        if (organized != null) cloud.OrderedGrid = new(organizedWidth, organizedHeight, organized);
        return cloud;

        int[] Numbers(string key, int fallback)
        {
            if (!header.TryGetValue(key, out var values)) return Enumerable.Repeat(fallback, fields.Length).ToArray();
            if (values.Length != fields.Length) throw new InvalidDataException($"PCD {key} and FIELDS lengths differ.");
            return values.Select(v => int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n : throw new InvalidDataException($"Invalid PCD {key}.")).ToArray();
        }
    }

    private static int Count(string[] values, string key)
    {
        if (values.Length != 1 || !int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n < 0)
            throw new InvalidDataException($"Invalid PCD {key}.");
        return n;
    }

    private static bool Valid(float x, float y, float z)
        => float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) &&
           Math.Abs(x) < 1e30f && Math.Abs(y) < 1e30f && Math.Abs(z) < 1e30f;

    private static float ReadNumber(byte[] bytes, int offset, int size, string type) => (type, size) switch
    {
        ("F", 4) => BitConverter.ToSingle(bytes, offset),
        ("F", 8) => (float)BitConverter.ToDouble(bytes, offset),
        ("U", 1) => bytes[offset],
        ("U", 2) => BitConverter.ToUInt16(bytes, offset),
        ("U", 4) => BitConverter.ToUInt32(bytes, offset),
        ("U", 8) => BitConverter.ToUInt64(bytes, offset),
        ("I", 1) => (sbyte)bytes[offset],
        ("I", 2) => BitConverter.ToInt16(bytes, offset),
        ("I", 4) => BitConverter.ToInt32(bytes, offset),
        ("I", 8) => BitConverter.ToInt64(bytes, offset),
        _ => throw new InvalidDataException($"Unsupported PCD field encoding: {type}{size}.")
    };

    private static byte[] ExpandLzf(ReadOnlySpan<byte> source, int outputLength)
    {
        var output = new byte[outputLength];
        int input = 0, target = 0;
        while (input < source.Length)
        {
            int control = source[input++];
            if (control < 32)
            {
                int length = control + 1;
                if (input + length > source.Length || target + length > output.Length)
                    throw new InvalidDataException("Invalid PCD LZF literal.");
                source.Slice(input, length).CopyTo(output.AsSpan(target));
                input += length; target += length;
            }
            else
            {
                int length = control >> 5;
                int distance = (control & 31) << 8;
                if (length == 7)
                {
                    if (input >= source.Length) throw new InvalidDataException("Truncated PCD LZF length.");
                    length += source[input++];
                }
                if (input >= source.Length) throw new InvalidDataException("Truncated PCD LZF distance.");
                distance += source[input++];
                int reference = target - distance - 1;
                length += 2;
                if (reference < 0 || target + length > output.Length)
                    throw new InvalidDataException("Invalid PCD LZF back reference.");
                for (int k = 0; k < length; k++) output[target++] = output[reference++];
            }
        }
        if (target != output.Length) throw new InvalidDataException("PCD LZF output length differs from header.");
        return output;
    }

    /// <summary>保存为 binary PCD（x y z intensity）。</summary>
    public static void Save(string path, PointCloud c)
    {
        using var fs = new FileStream(path, FileMode.Create);
        var head = "# .PCD v0.7 - Point Cloud Data file format\n" +
                   "VERSION 0.7\nFIELDS x y z intensity\n" +
                   "SIZE 4 4 4 4\nTYPE F F F F\nCOUNT 1 1 1 1\n" +
                   $"WIDTH {c.Count}\nHEIGHT 1\nVIEWPOINT 0 0 0 1 0 0 0\n" +
                   $"POINTS {c.Count}\nDATA binary\n";
        var hb = Encoding.ASCII.GetBytes(head);
        fs.Write(hb, 0, hb.Length);
        var buf = new byte[16];
        for (int i = 0; i < c.Count; i++)
        {
            BitConverter.GetBytes(c.X[i]).CopyTo(buf, 0);
            BitConverter.GetBytes(c.Y[i]).CopyTo(buf, 4);
            BitConverter.GetBytes(c.Z[i]).CopyTo(buf, 8);
            BitConverter.GetBytes(c.I.Count > i ? c.I[i] : 0f).CopyTo(buf, 12);
            fs.Write(buf, 0, 16);
        }
    }
}

/// <summary>点云池：IM 编号 -> 点云（与 ImagePool 编号空间独立）。</summary>
public class CloudPool
{
    internal Action<int> CheckWrite { get; set; }
    internal event Action<int> VisualWritten;
    internal event Action<int> VisualRead;
    private readonly Dictionary<int, PointCloud> _clouds = new();
    private readonly Dictionary<int, IReadOnlyList<PointSet3D>> _pointSets = new();

    public void Set(int index, PointCloud cloud)
    {
        CheckWrite?.Invoke(index);
        _pointSets.Remove(index);
        _clouds[index] = cloud ?? new PointCloud();
        VisualWritten?.Invoke(index);
    }

    public PointCloud Get(int index) { VisualRead?.Invoke(index); return _clouds.TryGetValue(index, out var c) ? c : null; }
    public void Remove(int index) { if (_clouds.ContainsKey(index)) CheckWrite?.Invoke(index); _clouds.Remove(index); _pointSets.Remove(index); }

    public void SetPointSets(int im, IReadOnlyList<PointSet3D> sets)
    {
        _pointSets[im] = sets.ToArray();
        //某些检测控件只更新点集，也需要保存显示结果
        VisualWritten?.Invoke(im);
    }


    public PointSet3D GetPointSet(int im, int index)
        => _pointSets.TryGetValue(im, out var sets) && index >= 0 && index < sets.Count ? sets[index] : null;
    internal IReadOnlyList<PointSet3D> GetPointSets(int im) => _pointSets.GetValueOrDefault(im);

    public void Clear() { _clouds.Clear(); _pointSets.Clear(); }

    public IEnumerable<KeyValuePair<int, PointCloud>> All => _clouds;
}

using System.Collections;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AiVisionStudio.Views;

internal static class NativeOverlayData
{
    public static double Number(IReadOnlyDictionary<string, object> data, string key, double fallback = double.NaN)
    {
        if (data.TryGetValue(key, out var value) && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)) return number;
        if (double.IsFinite(fallback)) return fallback;
        throw new FormatException("Missing overlay coordinate: " + key);
    }
    public static IReadOnlyDictionary<string, object> Group(IReadOnlyDictionary<string, object> data, string key)
        => data.TryGetValue(key, out var value) && value is IReadOnlyDictionary<string, object> group ? group : throw new FormatException("Missing overlay group: " + key);
    public static Vector3D Vector(IReadOnlyDictionary<string, object> data, string key = null, double defaultZ = 0)
    {
        if (key != null) data = Group(data, key);
        return new Vector3D(Number(data, "X"), Number(data, "Y"), Number(data, "Z", defaultZ));
    }
    public static Vector3D Unit(Vector3D value)
    {
        if (value.Length < 1e-12) throw new FormatException("Zero direction in overlay");
        value.Normalize(); return value;
    }
    public static bool Flag(IReadOnlyDictionary<string, object> data, string key, bool fallback = false)
        => data.TryGetValue(key, out var value) ? value is bool boolean ? boolean : bool.TryParse(value?.ToString(), out var result) ? result : fallback : fallback;
    public static Vector3D[] Points(IReadOnlyDictionary<string, object> data, double defaultZ = 0)
        => data.TryGetValue("Points", out var raw) && raw is IEnumerable items
            ? items.Cast<object>().OfType<IReadOnlyDictionary<string, object>>().Select(p => Vector(p, defaultZ: defaultZ)).ToArray()
            : throw new FormatException("Missing overlay points");
    public static string Text(IReadOnlyDictionary<string, object> data)
    {
        if (!data.TryGetValue("_items", out var raw) || raw is not IEnumerable items)
            return data.TryGetValue("Text", out var text) ? Convert.ToString(text, CultureInfo.InvariantCulture) ?? "" : "";
        return string.Concat(items.Cast<object>().Select(item =>
        {
            if (item is not IReadOnlyDictionary<string, object> part || !part.TryGetValue("_value", out var value)) return "";
            if (part.TryGetValue("_precision", out var precision) && double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                return number.ToString("F" + (Convert.ToInt32(precision) > 0 ? Math.Clamp(Convert.ToInt32(precision), 1, 15) : 3), CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        }));
    }
    public static Color Color(IReadOnlyDictionary<string, object> data, string fallback = "green", string key = "_color")
    {
        var text = data.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) : null;
        return ParseColor(string.IsNullOrWhiteSpace(text) ? fallback : text);
    }
    public static Color ParseColor(string text)
    {
        if (text.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
        {
            var parts = text[(text.IndexOf('(') + 1)..text.IndexOf(')')].Split(',');
            var alpha = parts.Length == 4 ? (byte)Math.Round(Math.Clamp(double.Parse(parts[3], CultureInfo.InvariantCulture), 0, 1) * 255) : (byte)255;
            return System.Windows.Media.Color.FromArgb(alpha, byte.Parse(parts[0].Trim()), byte.Parse(parts[1].Trim()), byte.Parse(parts[2].Trim()));
        }
        return (Color)ColorConverter.ConvertFromString(text);
    }
    public static Vector3D Euler(Vector3D point, Vector3D degrees)
    {
        // Three.js Euler XYZ means matrix Rx * Ry * Rz (rightmost rotation first).
        var x = degrees.X * Math.PI / 180; var y = degrees.Y * Math.PI / 180; var z = degrees.Z * Math.PI / 180;
        point = new Vector3D(point.X * Math.Cos(z) - point.Y * Math.Sin(z), point.X * Math.Sin(z) + point.Y * Math.Cos(z), point.Z);
        point = new Vector3D(point.X * Math.Cos(y) + point.Z * Math.Sin(y), point.Y, -point.X * Math.Sin(y) + point.Z * Math.Cos(y));
        return new Vector3D(point.X, point.Y * Math.Cos(x) - point.Z * Math.Sin(x), point.Y * Math.Sin(x) + point.Z * Math.Cos(x));
    }
    public static (Vector3D U, Vector3D V) Basis(Vector3D normal)
    {
        normal = Unit(normal);
        // Quaternion rotating +Z onto the normal, matching Three's setFromUnitVectors.
        if (normal.Z < -.999999) return (new Vector3D(-1, 0, 0), new Vector3D(0, 1, 0));
        var axis = new Vector3D(-normal.Y, normal.X, 0);
        var w = 1 + normal.Z;
        var norm = Math.Sqrt(axis.LengthSquared + w * w); axis /= norm; w /= norm;
        Vector3D Rotate(Vector3D p) => p + 2 * Vector3D.CrossProduct(axis, Vector3D.CrossProduct(axis, p) + w * p);
        return (Rotate(new Vector3D(1, 0, 0)), Rotate(new Vector3D(0, 1, 0)));
    }
}

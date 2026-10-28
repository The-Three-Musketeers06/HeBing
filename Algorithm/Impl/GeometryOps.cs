using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

internal static class Params2
{
    public static double Nested(ParamBag p, string group, string sub, string key, double def)
    {
        var g = p.Group(group);
        if (g == null || !g.TryGetValue(sub, out var v)) return def;
        if (v is not Dictionary<string, object> d || !d.TryGetValue(key, out var x) || x == null) return def;
        return double.TryParse(x.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var r) ? r : def;
    }

    public static bool GroupBool(ParamBag p, string group, string key, bool def)
    {
        var g = p.Group(group);
        if (g == null || !g.TryGetValue(key, out var v) || v == null) return def;
        return v is bool b ? b : bool.TryParse(v.ToString(), out var r) && r;
    }
}

public class LineFindOp : IOperator
{
    public string Driver => "Basic Line Find";
    public bool Run(OpContext context, ParamBag parameters) => HalconGeometry2D.Run(context, parameters, Driver);
}

public class CircleFindOp : IOperator
{
    public string Driver => "Basic Circle Find";
    public bool Run(OpContext context, ParamBag parameters) => HalconGeometry2D.Run(context, parameters, Driver);
}

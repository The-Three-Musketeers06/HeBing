using System.Diagnostics;
using OpenCvSharp;
using ZXingCpp;

namespace AiVisionStudio.Algorithm.Impl;

public sealed class MatrixCodeOptions
{
    public string DecodeType { get; set; } = "Auto";
    public string Polarity { get; set; } = "Dark On Light";
    public int MaximumCodes { get; set; }
    public int TimeoutMilliseconds { get; set; } = 2000;
    public int ErodeIterations { get; set; }
    public int KernelSize { get; set; } = 3;
    public string CodeShape { get; set; } = "Auto";
    public double MinimumModuleWidth { get; set; } = -1;
    public string DmSquareModules { get; set; } = "IndexDmAuto";
    public string DmRectangleModules { get; set; } = "IndexDmRectAuto";
    public string QrModules { get; set; } = "IndexQrAuto";
}

public sealed class MatrixCode
{
    public string Content { get; init; }
    public string CodeType { get; init; }
    public byte[] Bytes { get; init; }
    public string SymbologyIdentifier { get; init; }
    public Point[] Corners { get; init; }
    public int Angle { get; init; }
    public bool Mirrored { get; init; }
    public bool Inverted { get; init; }
    public int ModuleRows { get; init; }
    public int ModuleColumns { get; init; }
}

public sealed class MatrixCodeResult
{
    public List<MatrixCode> Codes { get; } = new();
    public bool TimedOut { get; internal set; }
    public bool LimitReached { get; internal set; }
    public double ElapsedMilliseconds { get; internal set; }
}

/// <summary>Local QR / ECC200 DataMatrix decoding, with source-pixel geometry and spatial deduplication.</summary>
public static class MatrixCodeEngine
{
    public static MatrixCodeResult Read(Mat source, MatrixCodeOptions options, Mat mask = null, CancellationToken cancellation = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (source == null || source.IsDisposed || source.Empty()) throw new ArgumentException("二维码识别：输入图像为空，请先加载图像。");
        if (options.MaximumCodes < 0 || options.MaximumCodes > 10000) throw new ArgumentException("最大读取数量应为 0～10000，0 表示不限。");
        if (options.TimeoutMilliseconds < 0) throw new ArgumentException("超时时间不能小于 0。");
        if (options.ErodeIterations < 0 || options.ErodeIterations > 20 || options.KernelSize is not (3 or 5 or 7 or 9 or 11))
            throw new ArgumentException("腐蚀次数应为 0～20，内核应为 3、5、7、9 或 11。");
        var formats = options.DecodeType switch
        {
            "Auto" => BarcodeFormats.Parse("QRCode,DataMatrix"), "QRCode" => BarcodeFormats.Parse("QRCode"),
            "DataMatrix" => BarcodeFormats.Parse("DataMatrix"), _ => throw new ArgumentException("仅支持自动、QRCode、DataMatrix。")
        };
        if (options.Polarity is not ("Dark On Light" or "Light On Dark" or "Both")) throw new ArgumentException("二维码极性参数无效。");
        if (options.CodeShape is not ("Auto" or "Square" or "Rectangle")) throw new ArgumentException("码形状参数无效。");
        if (!double.IsFinite(options.MinimumModuleWidth) || options.MinimumModuleWidth != -1 && options.MinimumModuleWidth <= 0)
            throw new ArgumentException("最小单元宽度应为 -1（自动）或大于 0 的像素值。");
        if (mask != null && (mask.IsDisposed || mask.Empty() || mask.Type() != MatType.CV_8UC1 || mask.Size() != source.Size()))
            throw new ArgumentException("二维码区域掩膜必须与输入图像等大，且为 8 位单通道图像。");
        var result = new MatrixCodeResult();
        var watch = Stopwatch.StartNew();
        void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (options.TimeoutMilliseconds > 0 && watch.ElapsedMilliseconds >= options.TimeoutMilliseconds) throw new TimeoutException();
        }
        try
        {
            Check();
            using var gray = Img.ToGray8(source);
            using var selected = mask?.Clone() ?? new Mat(source.Size(), MatType.CV_8UC1, Scalar.White);
            var roi = Cv2.BoundingRect(selected);
            if (roi.Width < 8 || roi.Height < 8 || Cv2.CountNonZero(selected) < 64) throw new ArgumentException("读取区域为空或过小，请重新画选。");
            using var crop = new Mat(gray, roi);
            using var normalized = crop.Clone();
            using var localMask = new Mat(selected, roi);
            using var outside = new Mat(); Cv2.Compare(localMask, 0, outside, CmpTypes.EQ);
            var inverted = options.Polarity == "Light On Dark";
            if (inverted) Cv2.BitwiseNot(normalized, normalized);
            if (options.ErodeIterations > 0)
            {
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(options.KernelSize, options.KernelSize));
                Cv2.Erode(normalized, normalized, kernel, iterations: options.ErodeIterations);
            }
            normalized.SetTo(Scalar.White, outside);
            using var reader = new BarcodeReader
            {
                Formats = formats, TryHarder = true, TryRotate = true, TryInvert = options.Polarity == "Both",
                TryDownscale = true, MaxNumberOfSymbols = 255, ReturnErrors = false, TextMode = TextMode.Plain
            };
            // Several spatial batches are needed beyond the native per-call limit (255).
            foreach (var scale in new[] { 1, 2 })
            {
                if (scale == 2 && Math.Max(roi.Width, roi.Height) > 1600) break;
                foreach (var binary in new[] { false, true })
                {
                    Check();
                    using var prepared = new Mat();
                    if (scale == 1) normalized.CopyTo(prepared);
                    else Cv2.Resize(normalized, prepared, new Size(normalized.Width * scale, normalized.Height * scale), 0, 0, InterpolationFlags.Cubic);
                    if (binary) Cv2.Threshold(prepared, prepared, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
                    // Do not let a later retry rediscover already decoded symbols.
                    foreach (var existing in result.Codes) Erase(prepared, existing, roi, scale);
                    while (true)
                    {
                        Check();
                        var decoded = reader.From(new ZXingCpp.ImageView(prepared.Data, prepared.Width, prepared.Height, ImageFormat.Lum, checked((int)prepared.Step())));
                        var consumed = 0;
                        try
                        {
                            Check();
                            foreach (var item in decoded)
                            {
                                Check(); if (!item.IsValid) continue;
                                var pos = item.Position;
                                var isDm = item.Symbology == BarcodeFormat.DataMatrix;
                                var version = item.Extra("Version").Trim('"');
                                var dimensions = version.Split('x');
                                var rows = 0; var columns = 0;
                                if (isDm && dimensions.Length == 2) { int.TryParse(dimensions[0], out rows); int.TryParse(dimensions[1], out columns); }
                                else if (!isDm && int.TryParse(version, out var qrVersion)) rows = columns = 17 + qrVersion * 4;
                                var corners = new[] { pos.TopLeft, pos.TopRight, pos.BottomRight, pos.BottomLeft }
                                    .Select(p => new Point(roi.X + (int)Math.Round(p.X / (double)scale), roi.Y + (int)Math.Round(p.Y / (double)scale))).ToArray();
                                var code = new MatrixCode
                                {
                                    Content = item.Text, CodeType = isDm ? "DataMatrix" : "QRCode",
                                    Bytes = item.Bytes, SymbologyIdentifier = item.SymbologyIdentifier, Corners = corners,
                                    Angle = item.Orientation, Mirrored = item.IsMirrored, Inverted = inverted ^ item.IsInverted,
                                    ModuleRows = rows, ModuleColumns = columns
                                };
                                Erase(prepared, code, roi, scale); consumed++;
                                if (!Accept(code, options) || !Inside(code, selected) || result.Codes.Any(existing => SameLocation(existing, code))) continue;
                                result.Codes.Add(code);
                            }
                        }
                        finally { foreach (var item in decoded) item.Dispose(); }
                        if (options.MaximumCodes > 0 && result.Codes.Count >= options.MaximumCodes) goto Complete;
                        if (decoded.Length < 255 || consumed == 0) break;
                    }
                }
            }
        Complete:
            Check();
            result.Codes.Sort((a, b) =>
            {
                var ay = a.Corners.Min(p => p.Y); var by = b.Corners.Min(p => p.Y);
                return ay != by ? ay.CompareTo(by) : a.Corners.Min(p => p.X).CompareTo(b.Corners.Min(p => p.X));
            });
            if (options.MaximumCodes > 0 && result.Codes.Count >= options.MaximumCodes)
            {
                result.LimitReached = true;
                if (result.Codes.Count > options.MaximumCodes) result.Codes.RemoveRange(options.MaximumCodes, result.Codes.Count - options.MaximumCodes);
            }
        }
        catch (TimeoutException) { result.Codes.Clear(); result.TimedOut = true; result.LimitReached = false; }
        finally { result.ElapsedMilliseconds = watch.Elapsed.TotalMilliseconds; }
        return result;
    }

    private static bool Accept(MatrixCode code, MatrixCodeOptions options)
    {
        var rows = code.ModuleRows; var columns = code.ModuleColumns;
        if (code.CodeType == "DataMatrix")
        {
            if (options.CodeShape == "Square" && rows != columns || options.CodeShape == "Rectangle" && rows == columns) return false;
            if (rows == columns && options.DmSquareModules != "IndexDmAuto" && options.DmSquareModules != "Index" + rows) return false;
            if (rows != columns && options.DmRectangleModules != "IndexDmRectAuto" && options.DmRectangleModules != $"Index{rows}_{columns}") return false;
        }
        else if (options.QrModules != "IndexQrAuto" && options.QrModules != "Index" + rows) return false;
        if (options.MinimumModuleWidth > 0)
        {
            if (rows <= 0 || columns <= 0) return false;
            double Length(int a, int b) => Math.Sqrt(Math.Pow(code.Corners[a].X - code.Corners[b].X, 2) + Math.Pow(code.Corners[a].Y - code.Corners[b].Y, 2));
            var minimum = Math.Min((Length(0, 1) + Length(2, 3)) / (2 * columns), (Length(1, 2) + Length(3, 0)) / (2 * rows));
            if (minimum < options.MinimumModuleWidth) return false;
        }
        return true;
    }

    private static bool Inside(MatrixCode code, Mat mask)
    {
        var bounds = Cv2.BoundingRect(code.Corners) & new Rect(0, 0, mask.Width, mask.Height);
        if (bounds.Width == 0 || bounds.Height == 0) return false;
        using var shape = new Mat(bounds.Size, MatType.CV_8UC1, Scalar.Black);
        Cv2.FillConvexPoly(shape, code.Corners.Select(p => new Point(p.X - bounds.X, p.Y - bounds.Y)), Scalar.White);
        var area = Cv2.CountNonZero(shape); if (area == 0) return false;
        using var region = new Mat(mask, bounds); using var overlap = new Mat(); Cv2.BitwiseAnd(shape, region, overlap);
        return Cv2.CountNonZero(overlap) >= area * .995;
    }

    private static bool SameLocation(MatrixCode a, MatrixCode b)
    {
        var first = Cv2.BoundingRect(a.Corners); var second = Cv2.BoundingRect(b.Corners);
        var overlap = first & second;
        return overlap.Width * (double)overlap.Height > .5 * Math.Min(first.Width * (double)first.Height, second.Width * (double)second.Height);
    }

    private static void Erase(Mat image, MatrixCode code, Rect roi, int scale)
        => Cv2.FillConvexPoly(image, code.Corners.Select(p => new Point((p.X - roi.X) * scale, (p.Y - roi.Y) * scale)), Scalar.White);
}

public sealed class MatrixCodeReadOp : IOperator
{
    public string Driver => "Basic 2D Code Reader";
    public bool Run(OpContext ctx, ParamBag p)
    {
        DetectionOverlay2D.Begin(ctx, Driver);
        Publish(ctx, new MatrixCodeResult());
        ctx.Vars.Set("Result", false); ctx.Vars.Set("Warning", false); ctx.Vars.Set("Message", "");
        var input = ctx.Images.Get(p.Int("IM In"));
        if (input == null || input.Empty()) throw new ArgumentException("二维码识别：输入图像为空，请先加载图像。");
        ValidateCompatibility(p);
        using var mask = ImageRegions2D.Mask(ctx, p, input.Size());
        var options = new MatrixCodeOptions
        {
            DecodeType = p.Str("Decode Type", "Auto"), Polarity = p.Str("Code Polar", "Dark On Light"),
            MaximumCodes = p.Int("Code Number"), TimeoutMilliseconds = p.Int("Time Out", 2000),
            ErodeIterations = p.Int("Erode Num"), KernelSize = int.TryParse(p.Str("Kernel Size", "D3").TrimStart('D'), out var size) ? size : 3,
            CodeShape = p.Str("Code Shape", "Square"), MinimumModuleWidth = p.Dbl("Min Unit Width", -1),
            DmSquareModules = p.Str("DM Square Module Index", "IndexDmAuto"), DmRectangleModules = p.Str("DM Rect Module Index", "IndexDmRectAuto"),
            QrModules = p.Str("QR Module Index", "IndexQrAuto")
        };
        var result = MatrixCodeEngine.Read(input, options, mask, ctx.CancellationToken);
        Publish(ctx, result);
        var success = !result.TimedOut && result.Codes.Count > 0;
        if (!success)
        {
            var message = result.TimedOut ? "二维码识别超时，请缩小读取区域或增加超时时间。" : "区域内未解出 QRCode / DataMatrix 内容，请检查码制、清晰度、极性和区域范围。";
            ctx.Vars.Set("Message", message); ctx.Log(message, true); return false;
        }
        DetectionOverlay2D.MaskOutline(ctx, p, Driver, mask, "Detection Area");
        for (var index = 0; index < result.Codes.Count; index++)
        {
            var code = result.Codes[index];
            if (p.Bool("Result Area")) DetectionOverlay2D.Add(ctx, p, Driver, "polygon2d", new()
            { ["Points"] = code.Corners.Select(q => (object)DetectionOverlay2D.XY(q.X, q.Y)).ToArray() }, "Result Area Attribute");
            var top = code.Corners.OrderBy(q => q.Y).ThenBy(q => q.X).First();
            var label = code.Content.Replace('\r', ' ').Replace('\n', ' ');
            if (label.Length > 80) label = label[..80] + "…";
            DetectionOverlay2D.Text(ctx, p, Driver, "Text Display", new Point2d(top.X, Math.Max(0, top.Y - 18)), $"{index + 1}: {label}");
        }
        ctx.Vars.Set("Result", true); ctx.Vars.Set("Warning", false); ctx.Vars.Set("Message", ""); return true;
    }

    private static void Publish(OpContext ctx, MatrixCodeResult result)
    {
        ctx.Vars.Set("Code Count", result.Codes.Count);
        ctx.Vars.Set("Content", string.Join(Environment.NewLine, result.Codes.Select(code => code.Content)));
        ctx.Vars.Set("Codes", result.Codes.Select(code => (object)new Dictionary<string, object>
        {
            ["Content"] = code.Content, ["Code Type"] = code.CodeType, ["Raw Bytes Base64"] = Convert.ToBase64String(code.Bytes),
            ["Symbology Identifier"] = code.SymbologyIdentifier, ["Angle"] = code.Angle, ["Mirrored"] = code.Mirrored, ["Inverted"] = code.Inverted,
            ["Module Rows"] = code.ModuleRows, ["Module Columns"] = code.ModuleColumns,
            ["Corners"] = code.Corners.Select(q => (object)DetectionOverlay2D.XY(q.X, q.Y)).ToArray()
        }).ToList());
        ctx.Vars.Set("Timed Out", result.TimedOut); ctx.Vars.Set("Limit Reached", result.LimitReached);
        ctx.Vars.Set("Elapsed Time", result.ElapsedMilliseconds);
    }

    private static void ValidateCompatibility(ParamBag p)
    {
        if (p.Str("Print Quality Type", "Empty") != "Empty") throw new NotSupportedException("当前提供内容解码，不提供打印质量分级；请将打印质量类型设为“无”。");
        if (p.Str("Dot Matrix Type", "Normal Image") != "Normal Image") throw new NotSupportedException("当前支持普通图像解码，点阵专用模式尚未接入；请选择普通图像。");
    }
}

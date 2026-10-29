using System.Runtime.CompilerServices;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

/// <summary>Presentation geometry only. Never replaces the trained contours or gradient features.</summary>
public static class ContourTemplateDisplay
{
    private sealed record Drawing(IReadOnlyList<IReadOnlyList<ContourTemplatePoint>> Contours);
    private static readonly ConditionalWeakTable<ContourTemplateModel, Drawing> Cache = new();

    public static IReadOnlyList<IReadOnlyList<ContourTemplatePoint>> GetContours(ContourTemplateModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Version != 1) return model.Contours;
        return Cache.GetValue(model, Build).Contours;
    }

    private static Drawing Build(ContourTemplateModel model)
    {
        using var image = ContourTemplateEngine.DecodeTrainingImage(model);
        using var mask = ContourTemplateEngine.DecodeMask(model);
        using var inside = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.Erode(mask, inside, kernel, borderType: BorderTypes.Constant, borderValue: Scalar.Black);
        using var edges = new Mat(image.Size(), MatType.CV_8UC1, Scalar.Black);
        foreach (var contour in model.Contours.Where(contour => contour.Count > 1))
            Cv2.Polylines(edges, [contour.Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y))).ToArray()], false, Scalar.White);
        using var inverse = new Mat(); using var distance = new Mat();
        Cv2.BitwiseNot(edges, inverse);
        Cv2.DistanceTransform(inverse, distance, DistanceTypes.L2, DistanceTransformMasks.Mask5);
        inside.GetArray(out byte[] allowed); distance.GetArray(out float[] distances);
        double tolerance = Math.Clamp(Math.Min(model.Width, model.Height) * .006, .75, 3);
        bool Valid(Point2d point)
        {
            int x = (int)Math.Round(point.X), y = (int)Math.Round(point.Y);
            return x >= 0 && y >= 0 && x < model.Width && y < model.Height && allowed[y * model.Width + x] != 0
                && distances[y * model.Width + x] <= tolerance + 3;
        }

        // A uniform image border identifies background, allowing a silhouette with genuine holes
        // while suppressing engraving, machining marks, and other texture inside the object.
        using var smooth = new Mat(); using var foreground = new Mat();
        Cv2.GaussianBlur(image, smooth, new Size(5, 5), 1.2);
        double threshold = Cv2.Threshold(smooth, foreground, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        var border = new List<byte>();
        int width = image.Width, height = image.Height;
        for (int x = 0; x < width; x++) { border.Add(smooth.At<byte>(0, x)); border.Add(smooth.At<byte>(height - 1, x)); }
        for (int y = 1; y < height - 1; y++) { border.Add(smooth.At<byte>(y, 0)); border.Add(smooth.At<byte>(y, width - 1)); }
        double bright = border.Count(value => value > threshold) / (double)Math.Max(1, border.Count);
        var result = new List<IReadOnlyList<ContourTemplatePoint>>();
        if (bright > .8 || bright < .2)
        {
            if (bright > .8) Cv2.BitwiseNot(foreground, foreground);
            Cv2.FindContours(foreground, out Point[][] silhouettes, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
            double largest = silhouettes.Select(contour => Math.Abs(Cv2.ContourArea(contour))).DefaultIfEmpty().Max();
            foreach (var contour in silhouettes.Where(contour => contour.Length >= 12 && Math.Abs(Cv2.ContourArea(contour)) >= Math.Max(10, largest * .012)))
            {
                var active = contour.Where(point => point.X >= 0 && point.Y >= 0 && point.X < model.Width && point.Y < model.Height && allowed[point.Y * model.Width + point.X] != 0).ToArray();
                if (active.Length < 8 || active.Count(point => Valid(new(point.X, point.Y))) < active.Length * .65) continue;
                var fitted = Fit(contour, tolerance);
                AddClipped(result, fitted, Valid);
            }
        }
        if (result.Count == 0)
        {
            // Low-contrast or cluttered backgrounds: retain the longest supported trained paths.
            int longest = model.Contours.Select(contour => contour.Count).DefaultIfEmpty().Max();
            foreach (var contour in model.Contours.Where(contour => contour.Count >= Math.Max(12, longest * .2)))
            {
                var points = contour.Select(point => new Point((int)Math.Round(point.X), (int)Math.Round(point.Y))).ToArray();
                bool closed = points[0].DistanceTo(points[^1]) <= 2;
                var fitted = Cv2.ApproxPolyDP(points, tolerance, closed).Select(point => new Point2d(point.X, point.Y)).ToList();
                if (closed && fitted.Count > 2) fitted.Add(fitted[0]);
                AddClipped(result, fitted, Valid);
            }
        }
        return new Drawing(result);
    }

    private static List<Point2d> Fit(Point[] contour, double tolerance)
    {
        if (contour.Length >= 20)
        {
            var ellipse = Cv2.FitEllipse(contour);
            double a = ellipse.Size.Width / 2, b = ellipse.Size.Height / 2, angle = ellipse.Angle * Math.PI / 180;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            if (a > 3 && b > 3)
            {
                double error = contour.Average(point =>
                {
                    double dx = point.X - ellipse.Center.X, dy = point.Y - ellipse.Center.Y;
                    double x = (dx * cosine + dy * sine) / a, y = (-dx * sine + dy * cosine) / b;
                    return Math.Abs(Math.Sqrt(x * x + y * y) - 1);
                });
                if (error < .018)
                    return Enumerable.Range(0, 97).Select(index =>
                    {
                        double phase = index * 2 * Math.PI / 96;
                        double x = a * Math.Cos(phase), y = b * Math.Sin(phase);
                        return new Point2d(ellipse.Center.X + x * cosine - y * sine, ellipse.Center.Y + x * sine + y * cosine);
                    }).ToList();
            }
        }
        var fitted = Cv2.ApproxPolyDP(contour, tolerance, true).Select(point => new Point2d(point.X, point.Y)).ToList();
        if (fitted.Count > 2) fitted.Add(fitted[0]);
        return fitted;
    }

    private static void AddClipped(List<IReadOnlyList<ContourTemplatePoint>> result, IReadOnlyList<Point2d> fitted, Func<Point2d, bool> valid)
    {
        var samples = new List<Point2d>();
        for (int index = 1; index < fitted.Count; index++)
        {
            var a = fitted[index - 1]; var b = fitted[index];
            int steps = Math.Max(1, (int)Math.Ceiling(a.DistanceTo(b)));
            for (int step = 0; step < steps; step++) samples.Add(a + (b - a) * (step / (double)steps));
        }
        if (samples.Count < 2) return;
        samples.Add(fitted[^1]);
        bool closed = fitted[0].DistanceTo(fitted[^1]) < .01;
        var visible = samples.Select(valid).ToArray();
        if (visible.All(value => value)) { result.Add(fitted.Select(point => new ContourTemplatePoint { X = point.X, Y = point.Y }).ToArray()); return; }
        int start = closed ? Array.FindIndex(visible, value => !value) + 1 : 0;
        var run = new List<ContourTemplatePoint>();
        for (int offset = 0; offset <= samples.Count; offset++)
        {
            int index = (start + offset) % samples.Count;
            if (offset < samples.Count && visible[index]) run.Add(new() { X = samples[index].X, Y = samples[index].Y });
            else
            {
                if (run.Count >= 6) result.Add(run);
                run = new();
            }
        }
    }
}

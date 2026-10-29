using System.Diagnostics;
using OpenCvSharp;

namespace AiVisionStudio.Algorithm.Impl;

public static partial class ContourTemplateEngine
{
    private sealed class MatchBudget
    {
        internal readonly Stopwatch Watch = Stopwatch.StartNew();
        internal readonly CancellationToken Cancellation;
        private readonly int _milliseconds;
        internal MatchBudget(int milliseconds, CancellationToken cancellation) { _milliseconds = milliseconds; Cancellation = cancellation; }
        internal void Check()
        {
            Cancellation.ThrowIfCancellationRequested();
            if (_milliseconds > 0 && Watch.ElapsedMilliseconds >= _milliseconds) throw new TimeoutException();
        }
    }

    private sealed class EdgeScene
    {
        internal int Width, Height, Factor;
        internal float[] Distances;
        internal int[] Labels;
        internal double[] Nx, Ny;
        internal byte[] Mask;
        internal ContourTemplateFeature[][] Bins;
        internal bool HasEdges;
        internal ContourTemplateModel Model;

        internal static EdgeScene Build(Mat image, Mat searchMask, int factor, ContourTemplateModel model, MatchBudget budget)
        {
            budget.Check();
            using var gray = new Mat();
            if (factor == 1) image.CopyTo(gray);
            else Cv2.Resize(image, gray, new Size(Math.Max(1, image.Width / factor), Math.Max(1, image.Height / factor)), 0, 0, InterpolationFlags.Area);
            using var smooth = new Mat(); Cv2.GaussianBlur(gray, smooth, new Size(3, 3), .8);
            using var gx = new Mat(); using var gy = new Mat(); using var edges = new Mat();
            Cv2.Sobel(smooth, gx, MatType.CV_32F, 1, 0); Cv2.Sobel(smooth, gy, MatType.CV_32F, 0, 1);
            Cv2.Canny(smooth, edges, model.EdgeLow, model.EdgeHigh);
            using var mask = new Mat();
            if (searchMask != null)
            {
                Cv2.Resize(searchMask, mask, gray.Size(), 0, 0, InterpolationFlags.Nearest); Cv2.BitwiseAnd(edges, mask, edges);
            }
            gx.GetArray(out float[] dx); gy.GetArray(out float[] dy); edges.GetArray(out byte[] edgePixels);
            var scene = new EdgeScene { Width = gray.Width, Height = gray.Height, Factor = factor, Model = model };
            if (searchMask != null) mask.GetArray(out scene.Mask);
            var points = new List<ContourTemplateFeature>();
            for (var i = 0; i < edgePixels.Length; i++)
            {
                if ((i & 65535) == 0) budget.Check();
                if (edgePixels[i] == 0) continue;
                var magnitude = Math.Sqrt(dx[i] * dx[i] + dy[i] * dy[i]);
                if (magnitude < Math.Max(1, model.MinContrast)) { edgePixels[i] = 0; continue; }
                points.Add(new() { X = i % scene.Width, Y = i / scene.Width, Nx = dx[i] / magnitude, Ny = dy[i] / magnitude });
            }
            if (points.Count == 0) { scene.Bins = Enumerable.Range(0, 16).Select(_ => Array.Empty<ContourTemplateFeature>()).ToArray(); return scene; }
            // Pixels removed by the contrast check must also be absent from the distance transform.
            using var cleanEdges = new Mat(scene.Height, scene.Width, MatType.CV_8UC1); cleanEdges.SetArray(edgePixels);
            using var inverted = new Mat(); using var distance = new Mat(); using var labels = new Mat();
            Cv2.BitwiseNot(cleanEdges, inverted);
            Cv2.DistanceTransformWithLabels(inverted, distance, labels, DistanceTypes.L2, DistanceTransformMasks.Mask5, DistanceTransformLabelTypes.Pixel);
            budget.Check(); distance.GetArray(out scene.Distances); labels.GetArray(out scene.Labels);
            var maximumLabel = scene.Labels.Max(); scene.Nx = new double[maximumLabel + 1]; scene.Ny = new double[maximumLabel + 1];
            foreach (var point in points)
            {
                var label = scene.Labels[(int)point.Y * scene.Width + (int)point.X]; scene.Nx[label] = point.Nx; scene.Ny[label] = point.Ny;
            }
            var sampled = Sample(points, 8000);
            scene.Bins = Enumerable.Range(0, 16).Select(bin => sampled.Where(point => DirectionBin(point.Nx, point.Ny) == bin).ToArray()).ToArray();
            scene.HasEdges = true; return scene;
        }
    }

    private sealed class Candidate
    {
        internal double X, Y, Rotation, Scale, Score;
        internal Candidate Copy() => (Candidate)MemberwiseClone();
    }

    /// <summary>Oriented chamfer matching with gradient voting, a coarse-to-fine pose search, and geometric refinement.</summary>
    private static ContourMatchResult MatchShape(Mat image, ContourTemplateModel model, ContourMatchOptions options, Mat searchMask = null, CancellationToken cancellation = default)
    {
        ValidateImage(image); ValidateModel(model); ValidateShapeOptions(options);
        // Copy before applying the trained sampling limits; callers may reuse their options instance.
        options = new ContourMatchOptions
        {
            AngleMin = options.AngleMin, AngleMax = options.AngleMax, ScaleMin = options.ScaleMin, ScaleMax = options.ScaleMax,
            AngleStep = Math.Max(options.AngleStep, model.TrainingAngleStep), ScaleStep = Math.Max(options.ScaleStep, model.TrainingScaleStep),
            NumLevels = options.NumLevels, Greediness = options.Greediness, MinScore = options.MinScore, MaxOverlap = options.MaxOverlap,
            MaxMatches = options.MaxMatches, Polarity = options.Polarity, SubPixel = options.SubPixel, TimeoutMilliseconds = options.TimeoutMilliseconds
        };
        if (searchMask != null && (searchMask.IsDisposed || searchMask.Empty() || searchMask.Type() != MatType.CV_8UC1 || searchMask.Size() != image.Size()))
            throw new ArgumentException("搜索区域遮罩必须是与输入图像等大的 8 位单通道图像。");
        if (searchMask != null && Cv2.CountNonZero(searchMask) < 25) throw new ArgumentException("搜索区域为空或过小，请重新绘制搜索区域。");
        var result = new ContourMatchResult(); var budget = new MatchBudget(options.TimeoutMilliseconds, cancellation);
        try
        {
            budget.Check(); using var gray = Img.ToGray8(image);
            var angleMin = options.AngleMin; var angleMax = options.AngleMax;
            var scaleMin = Math.Max(options.ScaleMin, model.ScaleMin); var scaleMax = Math.Min(options.ScaleMax, model.ScaleMax);
            if (!SearchAngles(angleMin, angleMax, options.AngleStep, model).Any() || scaleMin > scaleMax) throw new ArgumentException("匹配角度或缩放范围与模板训练范围没有交集，请调整范围或重新训练模板。");
            var minimumExtent = Math.Min(model.Width, model.Height) * scaleMin;
            var startLevel = Math.Min(Math.Min(options.NumLevels, model.PyramidLevels) - 1, Math.Max(0, (int)Math.Floor(Math.Log2(Math.Max(1, minimumExtent / 10)))));
            startLevel = Math.Min(startLevel, Math.Max(0, (int)Math.Floor(Math.Log2(Math.Min(image.Width, image.Height) / 16d))));
            var factor = 1 << startLevel;
            var scene = EdgeScene.Build(gray, searchMask, factor, model, budget);
            if (!scene.HasEdges) return result;
            var candidates = Vote(scene, model, options, angleMin, angleMax, scaleMin, scaleMax, budget);
            var beam = Math.Clamp((options.MaxMatches == 0 ? 32 : options.MaxMatches) * 12 + (int)((1 - options.Greediness) * 80), 32, 240);
            candidates = Trim(candidates, beam, Math.Max(2, minimumExtent / factor * .15), options.AngleStep * .8, options.ScaleStep * .8);
            var coarseSteps = 0;
            for (var level = startLevel - 1; level >= 0 && candidates.Count > 0; level--)
            {
                budget.Check(); factor = 1 << level;
                scene = EdgeScene.Build(gray, searchMask, factor, model, budget);
                if (!scene.HasEdges) return result;
                coarseSteps++;
                var angleStep = options.AngleStep / Math.Pow(2, coarseSteps);
                var scaleStep = options.ScaleStep / Math.Pow(2, coarseSteps);
                var features = RelativeFeatures(model, factor, 256 + (startLevel - level) * 100);
                foreach (var candidate in candidates)
                {
                    budget.Check(); candidate.X *= 2; candidate.Y *= 2;
                    Refine(candidate, scene, features, options, angleMin, angleMax, scaleMin, scaleMax, 1, angleStep, scaleStep, 3, budget);
                }
                candidates = Trim(candidates, beam, Math.Max(2, minimumExtent / factor * .10), Math.Max(.2, angleStep), Math.Max(.002, scaleStep));
            }
            var finalFeatures = RelativeFeatures(model, 1, model.Features.Count);
            var finalistFeatures = RelativeFeatures(model, 1, Math.Min(model.Features.Count, 800));
            foreach (var candidate in candidates.OrderByDescending(c => c.Score))
            {
                budget.Check();
                var angularStep = options.AngleStep / Math.Pow(2, Math.Max(1, startLevel));
                var scalingStep = options.ScaleStep / Math.Pow(2, Math.Max(1, startLevel));
                Refine(candidate, scene, finalistFeatures, options, angleMin, angleMax, scaleMin, scaleMax, 1, angularStep, scalingStep, 3, budget);
                if (options.SubPixel)
                {
                    Refine(candidate, scene, finalistFeatures, options, angleMin, angleMax, scaleMin, scaleMax, .5, angularStep * .4, scalingStep * .4, 2, budget);
                    Refine(candidate, scene, finalistFeatures, options, angleMin, angleMax, scaleMin, scaleMax, .25, angularStep * .15, scalingStep * .15, 2, budget);
                }
                candidate.Score = Score(scene, finalFeatures, candidate, options.Polarity, options.MinScore, options.Greediness);
            }
            foreach (var candidate in candidates.Where(c => c.Score >= options.MinScore).OrderByDescending(c => c.Score))
            {
                budget.Check();
                var match = new ContourMatch { X = candidate.X, Y = candidate.Y, Rotation = NormalizeAngle(candidate.Rotation), Angle = NormalizeAngle(model.OriginAngle + candidate.Rotation), Scale = candidate.Scale, Score = Math.Clamp(candidate.Score, 0, 1) };
                // Keep HeBing's physical-target deduplication for rotationally symmetric shapes,
                // including templates whose origin is outside the target center.
                if (result.Matches.Any(existing => Overlap(model, existing, match) > Math.Min(options.MaxOverlap, .8) + 1e-8 || Math.Pow(existing.X - match.X, 2) + Math.Pow(existing.Y - match.Y, 2) < 2.25)) continue;
                result.Matches.Add(match); if (result.Matches.Count >= (options.MaxMatches == 0 ? 1000 : options.MaxMatches)) break;
            }
        }
        catch (TimeoutException) { result.Matches.Clear(); result.TimedOut = true; }
        finally { result.ElapsedMilliseconds = budget.Watch.Elapsed.TotalMilliseconds; }
        return result;
    }

    private static List<Candidate> Vote(EdgeScene scene, ContourTemplateModel model, ContourMatchOptions options, double amin, double amax, double smin, double smax, MatchBudget budget)
    {
        var features = RelativeFeatures(model, scene.Factor, 160);
        var anchors = Sample(features, 24 + (int)((1 - options.Greediness) * 24));
        var votes = new int[scene.Width * scene.Height]; var candidates = new List<Candidate>();
        var ignorePolarity = options.Polarity != "Use Polarity";
        var peaksPerPose = Math.Clamp((options.MaxMatches == 0 ? 8 : options.MaxMatches) * 2 + 4, 6, 32);
        var separation = Math.Max(3, Math.Min(model.Width, model.Height) * smin / scene.Factor * .15);
        var counter = 0;
        foreach (var scale in Values(smin, smax, options.ScaleStep))
        foreach (var angle in SearchAngles(amin, amax, options.AngleStep, model))
        {
            budget.Check(); Array.Clear(votes);
            var radians = angle * Math.PI / 180; var cos = Math.Cos(radians); var sin = Math.Sin(radians);
            foreach (var anchor in anchors)
            {
                budget.Check();
                var ox = scale * (cos * anchor.X - sin * anchor.Y); var oy = scale * (sin * anchor.X + cos * anchor.Y);
                var nx = cos * anchor.Nx - sin * anchor.Ny; var ny = sin * anchor.Nx + cos * anchor.Ny; var bin = DirectionBin(nx, ny);
                foreach (var direction in ignorePolarity ? new[] { -1, 0, 1, 7, 8, 9 } : new[] { -1, 0, 1 })
                foreach (var edge in scene.Bins[(bin + direction + 16) % 16])
                {
                    var x = (int)Math.Round(edge.X - ox); var y = (int)Math.Round(edge.Y - oy);
                    if ((uint)x >= (uint)scene.Width || (uint)y >= (uint)scene.Height) continue;
                    if (scene.Mask != null && scene.Mask[y * scene.Width + x] == 0) continue;
                    votes[y * scene.Width + x]++;
                }
            }
            foreach (var peak in Peaks(votes, scene.Width, scene.Height, peaksPerPose, separation))
            {
                var candidate = new Candidate { X = peak.X, Y = peak.Y, Rotation = angle, Scale = scale };
                Refine(candidate, scene, features, options, amin, amax, smin, smax, 1, 0, 0, 1, budget);
                if (candidate.Score >= Math.Max(.10, options.MinScore * .45)) candidates.Add(candidate);
            }
            if ((++counter & 15) == 0 && candidates.Count > 2000) candidates = Trim(candidates, 800, separation, options.AngleStep * .8, options.ScaleStep * .8);
        }
        return candidates;
    }

    private static ContourTemplateFeature[] RelativeFeatures(ContourTemplateModel model, int factor, int maximum)
        => Sample(model.Features, maximum).Select(feature => new ContourTemplateFeature { X = (feature.X + model.OffsetX - model.OriginX) / factor, Y = (feature.Y + model.OffsetY - model.OriginY) / factor, Nx = feature.Nx, Ny = feature.Ny }).ToArray();

    private static IEnumerable<Point> Peaks(int[] votes, int width, int height, int maximum, double separation)
    {
        var queue = new PriorityQueue<(int Index, int Votes), int>();
        for (var y = 1; y < height - 1; y++) for (var x = 1; x < width - 1; x++)
        {
            var index = y * width + x; var value = votes[index]; if (value < 2) continue;
            if (value < votes[index - 1] || value < votes[index + 1] || value < votes[index - width] || value < votes[index + width]) continue;
            queue.Enqueue((index, value), value); if (queue.Count > maximum * 32) queue.Dequeue();
        }
        var picked = new List<Point>(); var radius = separation * separation;
        foreach (var entry in queue.UnorderedItems.Select(q => q.Element).OrderByDescending(q => q.Votes))
        {
            var point = new Point(entry.Index % width, entry.Index / width);
            if (picked.Any(q => Math.Pow(q.X - point.X, 2) + Math.Pow(q.Y - point.Y, 2) < radius)) continue;
            picked.Add(point); if (picked.Count >= maximum) break;
        }
        return picked;
    }

    private static void Refine(Candidate candidate, EdgeScene scene, ContourTemplateFeature[] features, ContourMatchOptions options,
        double amin, double amax, double smin, double smax, double translationStep, double angleStep, double scaleStep, int rounds, MatchBudget budget)
    {
        candidate.Score = Score(scene, features, candidate, options.Polarity, .05, options.Greediness);
        for (var round = 0; round < rounds; round++)
        {
            budget.Check(); var before = candidate.Score;
            var seed = candidate.Copy();
            for (var y = -1; y <= 1; y++) for (var x = -1; x <= 1; x++)
            {
                if (x == 0 && y == 0) continue;
                var probe = seed.Copy(); probe.X += x * translationStep; probe.Y += y * translationStep; Improve(probe);
            }
            if (angleStep > 0 && amax > amin)
            {
                seed = candidate.Copy();
                foreach (var step in new[] { -angleStep, angleStep }) { var probe = seed.Copy(); probe.Rotation = Math.Clamp(probe.Rotation + step, amin, amax); Improve(probe); }
            }
            if (scaleStep > 0 && smax > smin)
            {
                seed = candidate.Copy();
                foreach (var step in new[] { -scaleStep, scaleStep }) { var probe = seed.Copy(); probe.Scale = Math.Clamp(probe.Scale + step, smin, smax); Improve(probe); }
            }
            if (candidate.Score <= before + 1e-8) break;
        }
        void Improve(Candidate probe)
        {
            probe.Score = Score(scene, features, probe, options.Polarity, Math.Max(.05, candidate.Score * .7), options.Greediness);
            if (probe.Score <= candidate.Score) return;
            candidate.X = probe.X; candidate.Y = probe.Y; candidate.Rotation = probe.Rotation; candidate.Scale = probe.Scale; candidate.Score = probe.Score;
        }
    }

    private static double Score(EdgeScene scene, ContourTemplateFeature[] features, Candidate pose, string polarity, double minimum, double greediness)
    {
        if (!AngleAllowed(pose.Rotation, scene.Model)) return 0;
        var radians = pose.Rotation * Math.PI / 180; var cos = Math.Cos(radians); var sin = Math.Sin(radians);
        var positive = 0d; var negative = 0d; var local = polarity == "Ignore Local Polarity"; var global = polarity == "Ignore Global Polarity";
        var checkInterval = Math.Max(8, (int)(64 - greediness * 48));
        for (var i = 0; i < features.Length; i++)
        {
            var feature = features[i]; var x = pose.X + pose.Scale * (cos * feature.X - sin * feature.Y); var y = pose.Y + pose.Scale * (sin * feature.X + cos * feature.Y);
            if (x < 0 || y < 0 || x >= scene.Width - 1 || y >= scene.Height - 1) return 0;
            var left = (int)x; var top = (int)y; var index = top * scene.Width + left;
            if (scene.Mask != null && scene.Mask[index] == 0) return 0;
            var nx = cos * feature.Nx - sin * feature.Ny; var ny = sin * feature.Nx + cos * feature.Ny;
            var tx = x - left; var ty = y - top;
            Add(index, (1 - tx) * (1 - ty)); Add(index + 1, tx * (1 - ty)); Add(index + scene.Width, (1 - tx) * ty); Add(index + scene.Width + 1, tx * ty);
            if (i > 0 && i % checkInterval == 0)
            {
                var current = global ? Math.Max(positive, negative) : positive;
                if ((current + features.Length - i - 1) / features.Length < minimum) return 0;
            }
            void Add(int offset, double weight)
            {
                if (weight < 1e-10) return;
                var distance = scene.Distances[offset]; if (distance >= 5) return;
                var label = scene.Labels[offset]; var dot = nx * scene.Nx[label] + ny * scene.Ny[label];
                var closeness = DistanceWeights[Math.Clamp((int)(distance * 32), 0, DistanceWeights.Length - 1)] * weight;
                if (local) positive += Math.Abs(dot) * Math.Abs(dot) * closeness;
                else if (dot > 0) positive += dot * dot * closeness;
                else if (global) negative += dot * dot * closeness;
            }
        }
        return (global ? Math.Max(positive, negative) : positive) / Math.Max(1, features.Length);
    }

    private static readonly double[] DistanceWeights = Enumerable.Range(0, 161).Select(i => Math.Exp(-Math.Pow(i / 32d, 2) / (2 * 1.3 * 1.3))).ToArray();
    private static int DirectionBin(double nx, double ny) => ((int)Math.Round(Math.Atan2(ny, nx) * 8 / Math.PI) + 16) % 16;
    private static bool AngleAllowed(double angle, ContourTemplateModel model)
        => Enumerable.Range(-2, 5).Any(turn => angle + 360 * turn >= model.AngleMin - 1e-8 && angle + 360 * turn <= model.AngleMax + 1e-8);
    private static IEnumerable<double> SearchAngles(double min, double max, double step, ContourTemplateModel model)
    {
        var seen = new HashSet<double>();
        for (var turn = -2; turn <= 2; turn++)
        {
            var left = Math.Max(min, model.AngleMin + 360 * turn); var right = Math.Min(max, model.AngleMax + 360 * turn);
            if (left > right) continue;
            foreach (var angle in Values(left, right, step)) if (seen.Add(Math.Round(angle, 8))) yield return angle;
        }
    }
    private static IEnumerable<double> Values(double min, double max, double step)
    {
        var count = (int)Math.Floor((max - min) / step + 1e-8); for (var i = 0; i <= count; i++) yield return min + step * i;
        if (max - (min + count * step) > step * .1) yield return max;
    }
    private static List<Candidate> Trim(List<Candidate> source, int maximum, double distance, double angle, double scale)
    {
        var picked = new List<Candidate>();
        foreach (var candidate in source.OrderByDescending(c => c.Score))
        {
            if (picked.Any(p => Math.Pow(p.X - candidate.X, 2) + Math.Pow(p.Y - candidate.Y, 2) < distance * distance && Math.Abs(NormalizeAngle(p.Rotation - candidate.Rotation)) < angle && Math.Abs(p.Scale - candidate.Scale) < scale)) continue;
            picked.Add(candidate); if (picked.Count >= maximum) break;
        }
        return picked;
    }
    private static double Overlap(ContourTemplateModel model, ContourMatch first, ContourMatch second)
    {
        RotatedRect Rect(ContourMatch match)
        {
            var center = TransformPoint(model, match, (model.Width - 1) / 2d, (model.Height - 1) / 2d);
            return new(new Point2f((float)center.X, (float)center.Y), new Size2f((float)(model.Width * match.Scale), (float)(model.Height * match.Scale)), (float)match.Rotation);
        }
        var a = Rect(first); var b = Rect(second);
        Cv2.RotatedRectangleIntersection(a, b, out Point2f[] polygon);
        var area = polygon.Length < 3 ? 0 : Math.Abs(Cv2.ContourArea(polygon));
        return area / Math.Max(1, Math.Min(a.Size.Width * a.Size.Height, b.Size.Width * b.Size.Height));
    }
    private static void ValidateShapeOptions(ContourMatchOptions options)
    {
        if (options == null) throw new ArgumentNullException(nameof(options));
        ValidateRanges(options.AngleMin, options.AngleMax, options.ScaleMin, options.ScaleMax);
        if (!Finite(options.AngleStep, options.ScaleStep, options.MinScore, options.Greediness, options.MaxOverlap) || options.AngleStep <= 0 || options.AngleStep > 90 || options.ScaleStep <= 0 || options.ScaleStep > 1)
            throw new ArgumentException("角度步长需在 (0, 90] 度，缩放步长需在 (0, 1] 之间。");
        if (options.NumLevels < 1 || options.NumLevels > 10 || options.MinScore < 0 || options.MinScore > 1 || options.Greediness < 0 || options.Greediness > 1 || options.MaxOverlap < 0 || options.MaxOverlap > 1 || options.MaxMatches < 0 || options.MaxMatches > 1000 || options.TimeoutMilliseconds < 0)
            throw new ArgumentException("匹配参数无效：分数/贪心度/重叠率需在 0～1，层数 1～10，匹配数 0～1000，超时不得为负数。");
        if (options.Polarity is not ("Use Polarity" or "Ignore Global Polarity" or "Ignore Local Polarity")) throw new ArgumentException("未知匹配极性，请选择使用极性、忽略全局极性或忽略局部极性。");
        if ((options.AngleMax - options.AngleMin) / options.AngleStep * Math.Max(1, (options.ScaleMax - options.ScaleMin) / options.ScaleStep) > 100000)
            throw new ArgumentException("角度和缩放搜索组合过多，请增大步长或缩小搜索范围。");
    }
}

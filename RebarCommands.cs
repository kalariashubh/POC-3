// CODE FOR PNG AND JSON (ADVANCED SIGNATURE GENERATION)

using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.RegularExpressions;
using System.Text.Json;

using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

[assembly: CommandClass(typeof(RebarShapePlugin.RebarCommand))]

namespace RebarShapePlugin
{
    public class RebarCommand
    {
        private static readonly string PreviewDirectory =
            @"D:\Buniyad Byte\POC 3\AutoCAD Plugin\Previews\";

        [CommandMethod("CHECKREBAR")]
        public void CheckRebar()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor ed = doc.Editor;
            Database db = doc.Database;

            PromptSelectionResult psr = ed.GetSelection();

            if (psr.Status != PromptStatus.OK)
            {
                ed.WriteMessage("\nNothing selected.");
                return;
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                SelectionSet ss = psr.Value;

                Polyline poly = null;
                Polyline2d poly2d = null;
                Polyline3d poly3d = null;
                List<Line> lines = new List<Line>();
                List<Circle> circles = new List<Circle>();
                List<Arc> arcs = new List<Arc>();

                foreach (SelectedObject so in ss)
                {
                    if (so == null) continue;

                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;

                    if (ent is Polyline)
                        poly = ent as Polyline;

                    if (ent is Polyline2d)
                        poly2d = ent as Polyline2d;

                    if (ent is Polyline3d)
                        poly3d = ent as Polyline3d;

                    if (ent is Line)
                        lines.Add(ent as Line);

                    if (ent is Circle)
                        circles.Add(ent as Circle);

                    if (ent is Arc)
                        arcs.Add(ent as Arc);
                }

                // Convert every collected entity into a unified segment list
                List<(Point3d Start, Point3d End)> segments =
                    new List<(Point3d, Point3d)>();

                // For arc sampled points we store them keyed by their
                // (start, end) anchor so StitchSegments can order them correctly
                // We use a separate list of point chains for arcs; each arc
                // becomes a chain of Point3d that gets inserted in order during
                // stitching via the arc segment map below
                Dictionary<string, List<Point3d>> arcChains =
                    new Dictionary<string, List<Point3d>>();

                bool hasCircle = false;
                List<Point2d> circlePoints = new List<Point2d>();

                // --- Polyline (2D lightweight) ---
                if (poly != null)
                {
                    List<Point3d> pts = new List<Point3d>();
                    for (int i = 0; i < poly.NumberOfVertices; i++)
                    {
                        Point2d p = poly.GetPoint2dAt(i);
                        pts.Add(new Point3d(p.X, p.Y, 0));
                    }
                    for (int i = 0; i < pts.Count - 1; i++)
                        segments.Add((pts[i], pts[i + 1]));
                    if (poly.Closed && pts.Count > 1)
                        segments.Add((pts.Last(), pts[0]));
                }

                // --- Polyline2d ---
                if (poly2d != null)
                {
                    List<Point3d> pts = new List<Point3d>();
                    foreach (ObjectId vertexId in poly2d)
                    {
                        Vertex2d vertex =
                            tr.GetObject(vertexId, OpenMode.ForRead) as Vertex2d;
                        if (vertex != null)
                            pts.Add(new Point3d(vertex.Position.X, vertex.Position.Y, 0));
                    }
                    for (int i = 0; i < pts.Count - 1; i++)
                        segments.Add((pts[i], pts[i + 1]));
                    if (poly2d.Closed && pts.Count > 1)
                        segments.Add((pts.Last(), pts[0]));
                }

                // --- Polyline3d ---
                if (poly3d != null)
                {
                    List<Point3d> pts = new List<Point3d>();
                    foreach (ObjectId vertexId in poly3d)
                    {
                        PolylineVertex3d vertex =
                            tr.GetObject(vertexId, OpenMode.ForRead) as PolylineVertex3d;
                        if (vertex != null)
                            pts.Add(vertex.Position);
                    }
                    for (int i = 0; i < pts.Count - 1; i++)
                        segments.Add((pts[i], pts[i + 1]));
                    if (poly3d.Closed && pts.Count > 1)
                        segments.Add((pts.Last(), pts[0]));
                }

                // --- Lines ---
                foreach (var line in lines)
                    segments.Add((line.StartPoint, line.EndPoint));

                // --- Arcs ---
                foreach (var arc in arcs)
                {
                    List<Point3d> chain = SampleArc3d(arc);

                    Point3d arcStart = chain.First();
                    Point3d arcEnd = chain.Last();

                    string key = ArcKey(arcStart, arcEnd);
                    arcChains[key] = chain;

                    segments.Add((arcStart, arcEnd));
                }

                // --- Circle (special: no segments, handled as sampled closed loop) ---
                if (circles.Count > 0)
                {
                    hasCircle = true;
                    foreach (var circle in circles)
                        circlePoints.AddRange(SampleCircle(circle));
                }

                // ----------------------------------------------------------------
                // Determine final points and isClosed
                // ----------------------------------------------------------------
                List<Point2d> points = new List<Point2d>();
                bool isClosed = false;

                if (segments.Count == 0 && hasCircle)
                {
                    points = circlePoints;
                    isClosed = true;
                }
                else if (segments.Count > 0)
                {
                    points = StitchSegments(segments, arcChains);
                    isClosed = false;

                    if (hasCircle)
                        points.AddRange(circlePoints);
                }
                else
                {
                    ed.WriteMessage("\nNo valid geometry selected.");
                    return;
                }

                if (points.Count < 2)
                {
                    ed.WriteMessage("\nNot enough points.");
                    return;
                }

                if (!Directory.Exists(PreviewDirectory))
                    Directory.CreateDirectory(PreviewDirectory);

                int nextId = GetNextShapeId();

                string imageName = $"shape_{nextId}.png";
                string imagePath = Path.Combine(PreviewDirectory, imageName);

                SavePreviewImage(points, isClosed, imagePath);

                string jsonName = $"shape_{nextId}.json";
                string jsonPath = Path.Combine(PreviewDirectory, jsonName);

                SaveSignature(points, isClosed, jsonPath);

                ed.WriteMessage($"\nPreview saved as {imageName}");
                ed.WriteMessage($"\nSignature saved as {jsonName}");

                tr.Commit();
            }
        }

        // ----------------------------------------------------------------
        // Sample an Arc into a chain of Point3d (forward CCW direction)
        // ----------------------------------------------------------------
        private List<Point3d> SampleArc3d(Arc arc, int segmentsPerFullCircle = 72)
        {
            List<Point3d> pts = new List<Point3d>();

            double startAngle = arc.StartAngle;
            double endAngle = arc.EndAngle;

            double sweepAngle = endAngle - startAngle;
            if (sweepAngle <= 0)
                sweepAngle += 2 * Math.PI;

            int segments = (int)Math.Ceiling(segmentsPerFullCircle * sweepAngle / (2 * Math.PI));
            if (segments < 3) segments = 3;

            for (int i = 0; i <= segments; i++)
            {
                double angle = startAngle + sweepAngle * i / segments;
                double x = arc.Center.X + arc.Radius * Math.Cos(angle);
                double y = arc.Center.Y + arc.Radius * Math.Sin(angle);
                pts.Add(new Point3d(x, y, 0));
            }

            return pts;
        }

        // Key that identifies an arc chain by its two anchor endpoints.
        // We store both directions (start→end and end→start) so we can
        // look up the chain regardless of which way stitching traverses it.
        private string ArcKey(Point3d a, Point3d b)
        {
            return $"{Math.Round(a.X, 3)}_{Math.Round(a.Y, 3)}" +
                   $"|{Math.Round(b.X, 3)}_{Math.Round(b.Y, 3)}";
        }

        // ----------------------------------------------------------------
        // Unified stitching — arc chains are expanded inline
        // ----------------------------------------------------------------
        private List<Point2d> StitchSegments(
            List<(Point3d Start, Point3d End)> segments,
            Dictionary<string, List<Point3d>> arcChains = null)
        {
            if (arcChains == null)
                arcChains = new Dictionary<string, List<Point3d>>();

            Dictionary<string, int> endpointCount = new Dictionary<string, int>();

            foreach (var seg in segments)
            {
                string s = Key(seg.Start);
                string e = Key(seg.End);

                if (!endpointCount.ContainsKey(s)) endpointCount[s] = 0;
                if (!endpointCount.ContainsKey(e)) endpointCount[e] = 0;

                endpointCount[s]++;
                endpointCount[e]++;
            }

            Point3d startPoint = segments[0].Start;

            foreach (var seg in segments)
            {
                if (endpointCount[Key(seg.Start)] == 1)
                {
                    startPoint = seg.Start;
                    break;
                }

                if (endpointCount[Key(seg.End)] == 1)
                {
                    startPoint = seg.End;
                    break;
                }
            }

            List<Point2d> result = new List<Point2d>();
            Point3d current = startPoint;
            List<(Point3d Start, Point3d End)> remaining =
                new List<(Point3d Start, Point3d End)>(segments);

            result.Add(new Point2d(current.X, current.Y));

            while (remaining.Count > 0)
            {
                var next = remaining.FirstOrDefault(seg =>
                    IsSamePoint(seg.Start, current) ||
                    IsSamePoint(seg.End, current));

                if (next == default) break;

                bool forward = IsSamePoint(next.Start, current);

                Point3d segStart = forward ? next.Start : next.End;
                Point3d segEnd = forward ? next.End : next.Start;

                // Check if this segment is actually an arc chain
                string keyFwd = ArcKey(segStart, segEnd);
                string keyRev = ArcKey(segEnd, segStart);

                if (arcChains.ContainsKey(keyFwd))
                {
                    // Expand arc chain forward (skip first point, already in result)
                    List<Point3d> chain = arcChains[keyFwd];
                    for (int i = 1; i < chain.Count; i++)
                        result.Add(new Point2d(chain[i].X, chain[i].Y));
                }
                else if (arcChains.ContainsKey(keyRev))
                {
                    // Expand arc chain reversed (skip first point)
                    List<Point3d> chain = arcChains[keyRev];
                    for (int i = chain.Count - 2; i >= 0; i--)
                        result.Add(new Point2d(chain[i].X, chain[i].Y));
                }
                else
                {
                    // Normal straight segment — just add the endpoint
                    result.Add(new Point2d(segEnd.X, segEnd.Y));
                }

                current = segEnd;
                remaining.Remove(next);
            }

            return result;
        }

        private List<Point2d> SampleCircle(Circle circle, int segments = 36)
        {
            List<Point2d> pts = new List<Point2d>();

            for (int i = 0; i < segments; i++)
            {
                double angle = 2 * Math.PI * i / segments;

                double x = circle.Center.X + circle.Radius * Math.Cos(angle);
                double y = circle.Center.Y + circle.Radius * Math.Sin(angle);

                pts.Add(new Point2d(x, y));
            }

            return pts;
        }

        private void SaveSignature(List<Point2d> points, bool isClosed, string jsonPath)
        {
            List<Vector2d> vectors = new List<Vector2d>();

            for (int i = 0; i < points.Count - 1; i++)
                vectors.Add(points[i + 1] - points[i]);

            if (isClosed)
                vectors.Add(points[0] - points.Last());

            int segmentCount = vectors.Count;

            List<double> signedAngles = new List<double>();

            for (int i = 0; i < vectors.Count - 1; i++)
            {
                Vector2d v1 = vectors[i].GetNormal();
                Vector2d v2 = vectors[i + 1].GetNormal();

                double angle = v1.GetAngleTo(v2) * 180 / Math.PI;

                double cross = v1.X * v2.Y - v1.Y * v2.X;

                double signedAngle = cross > 0 ? angle : -angle;

                signedAngles.Add(Math.Round(signedAngle, 2));
            }

            Vector2d first = vectors.First().GetNormal();
            Vector2d last = vectors.Last().GetNormal();

            double dot = first.DotProduct(last);

            bool firstLastParallel = Math.Abs(Math.Abs(dot) - 1) < 0.01;

            string topology = isClosed ? "closed_chain" : "open_chain";

            List<string> directions = new List<string>();

            foreach (var v in vectors)
            {
                double dx = v.X;
                double dy = v.Y;

                if (Math.Abs(dx) > Math.Abs(dy))
                {
                    directions.Add(dx > 0 ? "right" : "left");
                }
                else
                {
                    directions.Add(dy > 0 ? "up" : "down");
                }
            }

            List<string> reverseDirections = new List<string>();

            for (int i = directions.Count - 1; i >= 0; i--)
            {
                string dir = directions[i];

                if (dir == "up") reverseDirections.Add("down");
                else if (dir == "down") reverseDirections.Add("up");
                else if (dir == "left") reverseDirections.Add("right");
                else if (dir == "right") reverseDirections.Add("left");
            }

            var jsonObject = new
            {
                signature = new
                {
                    topology = topology,
                    segment_count = segmentCount,
                    signed_angles = signedAngles,
                    first_last_parallel = firstLastParallel,
                    segment_directions = directions,
                    reverse_directions = reverseDirections
                }
            };

            string json = JsonSerializer.Serialize(jsonObject,
                new JsonSerializerOptions { WriteIndented = true });

            File.WriteAllText(jsonPath, json);
        }

        private int GetNextShapeId()
        {
            var files = Directory.GetFiles(PreviewDirectory, "shape_*.png");

            int maxId = 0;

            foreach (var file in files)
            {
                var name = Path.GetFileNameWithoutExtension(file);

                var match = Regex.Match(name, @"shape_(\d+)");

                if (match.Success)
                {
                    int id = int.Parse(match.Groups[1].Value);

                    if (id > maxId)
                        maxId = id;
                }
            }
            return maxId + 1;
        }

        private List<Point2d> MergeLinesIntoPoints(List<Line> lines)
        {
            List<Point2d> points = new List<Point2d>();

            Dictionary<string, int> pointCount = new Dictionary<string, int>();

            foreach (var line in lines)
            {
                string s = Key(line.StartPoint);
                string e = Key(line.EndPoint);

                if (!pointCount.ContainsKey(s)) pointCount[s] = 0;
                if (!pointCount.ContainsKey(e)) pointCount[e] = 0;

                pointCount[s]++;
                pointCount[e]++;
            }

            Point3d startPoint = lines[0].StartPoint;

            foreach (var line in lines)
            {
                if (pointCount[Key(line.StartPoint)] == 1)
                {
                    startPoint = line.StartPoint;
                    break;
                }

                if (pointCount[Key(line.EndPoint)] == 1)
                {
                    startPoint = line.EndPoint;
                    break;
                }
            }

            Point3d current = startPoint;

            points.Add(new Point2d(current.X, current.Y));

            List<Line> remaining = new List<Line>(lines);

            while (remaining.Count > 0)
            {
                Line next = remaining.FirstOrDefault(l =>
                    IsSamePoint(l.StartPoint, current) ||
                    IsSamePoint(l.EndPoint, current));

                if (next == null)
                    break;

                if (IsSamePoint(next.StartPoint, current))
                    current = next.EndPoint;
                else
                    current = next.StartPoint;

                points.Add(new Point2d(current.X, current.Y));

                remaining.Remove(next);
            }

            return points;
        }

        private bool IsSamePoint(Point3d p1, Point3d p2)
        {
            return Math.Abs(p1.X - p2.X) < 0.001 &&
                   Math.Abs(p1.Y - p2.Y) < 0.001;
        }

        private string Key(Point3d p)
        {
            return $"{Math.Round(p.X, 3)}_{Math.Round(p.Y, 3)}";
        }

        private void SavePreviewImage(List<Point2d> points, bool isClosed, string fullPath)
        {
            int canvasSize = 400;

            Bitmap bmp = new Bitmap(canvasSize, canvasSize);
            Graphics g = Graphics.FromImage(bmp);

            g.Clear(System.Drawing.Color.White);
            Pen pen = new Pen(System.Drawing.Color.Black, 2);

            double minX = points.Min(p => p.X);
            double maxX = points.Max(p => p.X);
            double minY = points.Min(p => p.Y);
            double maxY = points.Max(p => p.Y);

            double width = maxX - minX;
            double height = maxY - minY;

            if (width == 0) width = 1;
            if (height == 0) height = 1;

            double maxDim = Math.Max(width, height);
            double scale = (canvasSize - 40) / maxDim;

            double scaledWidth = width * scale;
            double scaledHeight = height * scale;

            double offsetX = (canvasSize - scaledWidth) / 2;
            double offsetY = (canvasSize - scaledHeight) / 2;

            List<PointF> scaledPoints = new List<PointF>();

            foreach (var pt in points)
            {
                float x = (float)((pt.X - minX) * scale + offsetX);
                float y = (float)((maxY - pt.Y) * scale + offsetY);

                scaledPoints.Add(new PointF(x, y));
            }

            if (isClosed)
                g.DrawPolygon(pen, scaledPoints.ToArray());
            else
                g.DrawLines(pen, scaledPoints.ToArray());

            bmp.Save(fullPath, ImageFormat.Png);

            g.Dispose();
            bmp.Dispose();
        }
    }
}

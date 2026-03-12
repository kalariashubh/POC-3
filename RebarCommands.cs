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
                List<Line> lines = new List<Line>();
                List<Circle> circles = new List<Circle>(); // ADDED

                foreach (SelectedObject so in ss)
                {
                    if (so == null) continue;

                    Entity ent = tr.GetObject(so.ObjectId, OpenMode.ForRead) as Entity;

                    if (ent is Polyline)
                        poly = ent as Polyline;

                    if (ent is Line)
                        lines.Add(ent as Line);

                    if (ent is Circle) // ADDED
                        circles.Add(ent as Circle);
                }

                List<Point2d> points = new List<Point2d>();
                bool isClosed = false;

                if (poly != null)
                {
                    for (int i = 0; i < poly.NumberOfVertices; i++)
                        points.Add(poly.GetPoint2dAt(i));

                    isClosed = poly.Closed;
                }
                else if (lines.Count > 0)
                {
                    points = MergeLinesIntoPoints(lines);
                }
                else if (circles.Count > 0) // ADDED
                {
                    foreach (var circle in circles)
                    {
                        points.AddRange(SampleCircle(circle));
                    }
                    isClosed = true;
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

                SaveSignature(points, isClosed, imageName, jsonPath);

                ed.WriteMessage($"\nPreview saved as {imageName}");
                ed.WriteMessage($"\nSignature saved as {jsonName}");

                tr.Commit();
            }
        }

        // ADDED FUNCTION
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

        private void SaveSignature(List<Point2d> points, bool isClosed, string imageName, string jsonPath)
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
                },
                preview_image = imageName
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

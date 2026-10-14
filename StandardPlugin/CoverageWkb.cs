using System;
using System.Buffers.Binary;

namespace AgGateway.ADAPT.StandardPlugin
{
    internal readonly struct CoverageBounds
    {
        internal CoverageBounds(double minX, double minY, double maxX, double maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        internal double MinX { get; }
        internal double MinY { get; }
        internal double MaxX { get; }
        internal double MaxY { get; }
    }

    /// <summary>
    /// Writes a little-endian ISO WKB polygon without NetTopologySuite.
    /// The single, closed ring is leading-left, leading-right, back-right, and back-left;
    /// X is longitude and Y is latitude.
    /// </summary>
    internal enum CoverageQuadStatus
    {
        Valid,
        Degenerate,
        SelfIntersection
    }

    internal static class CoverageWkb
    {
        internal const int PolygonWkbSize = 93;

        internal static byte[] WritePolygon(
            double leftLon,
            double leftLat,
            double rightLon,
            double rightLat,
            double backRightLon,
            double backRightLat,
            double backLeftLon,
            double backLeftLat)
        {
            byte[] wkb = new byte[PolygonWkbSize];
            int offset = 0;
            wkb[offset++] = 1; // little endian
            BinaryPrimitives.WriteInt32LittleEndian(wkb.AsSpan(offset), 3);
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(wkb.AsSpan(offset), 1);
            offset += 4;
            BinaryPrimitives.WriteInt32LittleEndian(wkb.AsSpan(offset), 5);
            offset += 4;

            offset = WritePoint(wkb, offset, leftLon, leftLat);
            offset = WritePoint(wkb, offset, rightLon, rightLat);
            offset = WritePoint(wkb, offset, backRightLon, backRightLat);
            offset = WritePoint(wkb, offset, backLeftLon, backLeftLat);
            WritePoint(wkb, offset, leftLon, leftLat);
            return wkb;
        }

        internal static bool IsValidQuad(
            LocalGeodesy geodesy,
            double leftLon,
            double leftLat,
            double rightLon,
            double rightLat,
            double backRightLon,
            double backRightLat,
            double backLeftLon,
            double backLeftLat)
        {
            return EvaluateQuad(
                geodesy,
                leftLon,
                leftLat,
                rightLon,
                rightLat,
                backRightLon,
                backRightLat,
                backLeftLon,
                backLeftLat) == CoverageQuadStatus.Valid;
        }

        internal static CoverageQuadStatus EvaluateQuad(
            LocalGeodesy geodesy,
            double leftLon,
            double leftLat,
            double rightLon,
            double rightLat,
            double backRightLon,
            double backRightLat,
            double backLeftLon,
            double backLeftLat)
        {
            if (!IsFinite(leftLon, leftLat, rightLon, rightLat, backRightLon, backRightLat, backLeftLon, backLeftLat))
            {
                return CoverageQuadStatus.Degenerate;
            }

            geodesy.ToLocal(leftLon, leftLat, out double leftEast, out double leftNorth);
            geodesy.ToLocal(rightLon, rightLat, out double rightEast, out double rightNorth);
            geodesy.ToLocal(backRightLon, backRightLat, out double backRightEast, out double backRightNorth);
            geodesy.ToLocal(backLeftLon, backLeftLat, out double backLeftEast, out double backLeftNorth);

            // The shoelace formula gives the local polygon area.
            double area = Math.Abs(
                (leftEast * (rightNorth - backLeftNorth)) +
                (rightEast * (backRightNorth - leftNorth)) +
                (backRightEast * (backLeftNorth - rightNorth)) +
                (backLeftEast * (leftNorth - backRightNorth))) / 2d;
            if (area < 1e-12)
            {
                return CoverageQuadStatus.Degenerate;
            }

            var left = new LocalPoint(leftEast, leftNorth);
            var right = new LocalPoint(rightEast, rightNorth);
            var backRight = new LocalPoint(backRightEast, backRightNorth);
            var backLeft = new LocalPoint(backLeftEast, backLeftNorth);

            // A tight turn copies one inside corner onto the previous leading edge.
            // That exact repeated vertex serializes as a triangle, which cannot bow-tie.
            // A corner that is only nearby still has two coordinates and can cross.
            if (IsPinnedTriangle(
                leftLon,
                leftLat,
                rightLon,
                rightLat,
                backRightLon,
                backRightLat,
                backLeftLon,
                backLeftLat))
            {
                return CoverageQuadStatus.Valid;
            }

            // A self-intersecting bow-tie is not a valid GeoParquet polygon.
            if (LocalGeodesy.SegmentsIntersect(left, right, backRight, backLeft) ||
                LocalGeodesy.SegmentsIntersect(right, backRight, backLeft, left))
            {
                return CoverageQuadStatus.SelfIntersection;
            }

            return CoverageQuadStatus.Valid;
        }

        private static bool IsPinnedTriangle(
            double leftLon,
            double leftLat,
            double rightLon,
            double rightLat,
            double backRightLon,
            double backRightLat,
            double backLeftLon,
            double backLeftLat)
        {
            bool leftRight = SameVertex(leftLon, leftLat, rightLon, rightLat);
            bool rightBackRight = SameVertex(rightLon, rightLat, backRightLon, backRightLat);
            bool backRightBackLeft = SameVertex(backRightLon, backRightLat, backLeftLon, backLeftLat);
            bool backLeftLeft = SameVertex(backLeftLon, backLeftLat, leftLon, leftLat);
            int repeatedAdjacentCorners = (leftRight ? 1 : 0) + (rightBackRight ? 1 : 0) + (backRightBackLeft ? 1 : 0) + (backLeftLeft ? 1 : 0);
            if (repeatedAdjacentCorners != 1)
            {
                return false;
            }

            // Opposite corners meeting is a collapse or a bow-tie, not a pinned triangle.
            return !SameVertex(leftLon, leftLat, backRightLon, backRightLat) &&
                   !SameVertex(rightLon, rightLat, backLeftLon, backLeftLat);
        }

        private static bool SameVertex(double firstLon, double firstLat, double secondLon, double secondLat)
        {
            return firstLon == secondLon && firstLat == secondLat;
        }

        internal static CoverageBounds ComputeBounds(
            double leftLon,
            double leftLat,
            double rightLon,
            double rightLat,
            double backRightLon,
            double backRightLat,
            double backLeftLon,
            double backLeftLat)
        {
            double minX = leftLon;
            double maxX = leftLon;
            double minY = leftLat;
            double maxY = leftLat;

            Include(leftLon, leftLat, ref minX, ref minY, ref maxX, ref maxY);
            Include(rightLon, rightLat, ref minX, ref minY, ref maxX, ref maxY);
            Include(backRightLon, backRightLat, ref minX, ref minY, ref maxX, ref maxY);
            Include(backLeftLon, backLeftLat, ref minX, ref minY, ref maxX, ref maxY);

            return new CoverageBounds(minX, minY, maxX, maxY);
        }

        private static int WritePoint(byte[] buffer, int offset, double x, double y)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(offset), x);
            offset += 8;
            BinaryPrimitives.WriteDoubleLittleEndian(buffer.AsSpan(offset), y);
            return offset + 8;
        }

        private static bool IsFinite(
            double leftLon,
            double leftLat,
            double rightLon,
            double rightLat,
            double backRightLon,
            double backRightLat,
            double backLeftLon,
            double backLeftLat)
        {
            return double.IsFinite(leftLon) &&
                   double.IsFinite(leftLat) &&
                   double.IsFinite(rightLon) &&
                   double.IsFinite(rightLat) &&
                   double.IsFinite(backRightLon) &&
                   double.IsFinite(backRightLat) &&
                   double.IsFinite(backLeftLon) &&
                   double.IsFinite(backLeftLat);
        }

        private static void Include(double x, double y, ref double minX, ref double minY, ref double maxX, ref double maxY)
        {
            if (x < minX)
            {
                minX = x;
            }

            if (x > maxX)
            {
                maxX = x;
            }

            if (y < minY)
            {
                minY = y;
            }

            if (y > maxY)
            {
                maxY = y;
            }
        }
    }
}

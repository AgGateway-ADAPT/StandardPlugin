using System;
using AgGateway.ADAPT.StandardPlugin;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NUnit.Framework;

namespace StandardPluginTests
{
    public class CoverageWkbTests
    {
        [Test]
        public void WritePolygonMatchesNetTopologySuiteBinary()
        {
            double leftLon = -87.60d;
            double leftLat = 41.88d;
            double rightLon = -87.599d;
            double rightLat = 41.88d;
            double backRightLon = -87.599d;
            double backRightLat = 41.879d;
            double backLeftLon = -87.60d;
            double backLeftLat = 41.879d;

            var ring = new LinearRing(new[]
            {
                new Coordinate(leftLon, leftLat),
                new Coordinate(rightLon, rightLat),
                new Coordinate(backRightLon, backRightLat),
                new Coordinate(backLeftLon, backLeftLat),
                new Coordinate(leftLon, leftLat)
            });
            byte[] expected = new Polygon(ring).AsBinary();

            byte[] actual = CoverageWkb.WritePolygon(
                leftLon,
                leftLat,
                rightLon,
                rightLat,
                backRightLon,
                backRightLat,
                backLeftLon,
                backLeftLat);

            Assert.That(actual, Has.Length.EqualTo(CoverageWkb.PolygonWkbSize));
            Assert.That(actual, Is.EqualTo(expected));
        }

        [Test]
        public void CoveragePolygonWriterProducesValidStraightPassWkb()
        {
            const double baseLon = -87.60d;
            const double baseLat = 41.88d;
            const double metersPerDegreeLat = 111320d;
            const double widthM = 12d;
            const double heading = 0d;

            var geodesy = new LocalGeodesy(baseLon, baseLat);
            LeadingEdge leadingEdge = default;

            Assert.That(CoveragePolygonWriter.TryWrite(
                geodesy,
                new LocalPoint(0d, 0d),
                widthM,
                ref leadingEdge,
                heading,
                reportedDistance: 1d,
                calculatedDistance: null,
                out byte[] firstWkb,
                out _), Is.True);
            Assert.That(firstWkb, Has.Length.EqualTo(CoverageWkb.PolygonWkbSize));

            double nextLat = baseLat + (0.5d / metersPerDegreeLat);
            geodesy = new LocalGeodesy(baseLon, nextLat);
            Assert.That(CoveragePolygonWriter.TryWrite(
                geodesy,
                new LocalPoint(0d, 0d),
                widthM,
                ref leadingEdge,
                heading,
                reportedDistance: null,
                calculatedDistance: 0.5d,
                out byte[] secondWkb,
                out _), Is.True);
            Assert.That(secondWkb, Has.Length.EqualTo(CoverageWkb.PolygonWkbSize));
            Assert.That(secondWkb, Is.Not.EqualTo(firstWkb));
        }

        [Test]
        public void CoveragePolygonWriterRejectsBowtieQuad()
        {
            var geodesy = new LocalGeodesy(-87.60d, 41.88d);
            Assert.That(CoverageWkb.IsValidQuad(
                geodesy,
                -87.60d,
                41.88d,
                -87.599d,
                41.88d,
                -87.60d,
                41.879d,
                -87.599d,
                41.879d), Is.False);
        }

        [Test]
        public void CoveragePolygonWriterProducesWkbNearEquator()
        {
            var section = WorkRecordTestData.CreateSectionDefinition(widthM: 3d, lateralOffsetM: 0d);
            var records = WorkRecordTestData.CreateStraightPassRecords(3, WorkRecordTestData.CreateAppRateWorkingData(), baseLat: 0.2d);
            Assert.That(CoverageRecordContext.TryCreate(
                records[1],
                records[0],
                out CoverageRecordContext context), Is.True);

            double bearing = context.ResolveBearing(records[1], headingDefinition: null);
            double? reportedDistance = context.ResolveReportedDistance(records[1], distanceDefinition: null);
            Assert.That(section.TryGetCoverageWkb(
                context,
                bearing,
                reportedDistance,
                context.ResolveCalculatedDistance(reportedDistance),
                out byte[] wkb,
                out _), Is.True);
            Assert.That(wkb, Has.Length.EqualTo(CoverageWkb.PolygonWkbSize));
        }

        [Test]
        public void OffsetQuadCornersMatchHaversineWithinOneCentimeter()
        {
            const double lon = -87.60d;
            const double lat = 41.88d;
            const double bearing = 15d;
            const double inlineOffset = 2d;
            const double lateralOffset = 6d;
            const double width = 12d;
            const double distance = 1d;

            var geodesy = new LocalGeodesy(lon, lat);
            var leadingCenter = geodesy.DestinationLocal(new LocalPoint(0d, 0d), inlineOffset, bearing);
            leadingCenter = geodesy.DestinationLocal(leadingCenter, lateralOffset, bearing + 90d);
            LeadingEdge leadingEdge = default;
            Assert.That(CoveragePolygonWriter.TryWrite(
                geodesy,
                leadingCenter,
                width,
                ref leadingEdge,
                bearing,
                distance,
                calculatedDistance: null,
                out byte[] wkb,
                out _), Is.True);

            var start = new Point(lon, lat);
            var inlinePoint = GeometryExporter.HaversineDestination(start, inlineOffset, bearing);
            var offsetPoint = GeometryExporter.HaversineDestination(inlinePoint, lateralOffset, bearing + 90d);
            var left = GeometryExporter.HaversineDestination(offsetPoint, width / 2d, bearing - 90d);
            var right = GeometryExporter.HaversineDestination(offsetPoint, width / 2d, bearing + 90d);
            var backRight = GeometryExporter.HaversineDestination(right, distance, bearing - 180d);
            var backLeft = GeometryExporter.HaversineDestination(backRight, width, bearing - 90d);

            AssertCornerWithinOneCentimeter(wkb, 0, left);
            AssertCornerWithinOneCentimeter(wkb, 1, right);
            AssertCornerWithinOneCentimeter(wkb, 2, backRight);
            AssertCornerWithinOneCentimeter(wkb, 3, backLeft);
        }

        [TestCase(350d, 10d)]
        [TestCase(10d, 350d)]
        [TestCase(170d, -170d)]
        [TestCase(-170d, 170d)]
        public void TightTurnWritesAValidPinnedTriangle(double priorHeading, double heading)
        {
            var geodesy = new LocalGeodesy(-87.60d, 41.88d);
            var center = new LocalPoint(0d, 0d);
            LeadingEdge leadingEdge = default;
            Assert.That(CoveragePolygonWriter.TryWrite(
                geodesy,
                center,
                10d,
                ref leadingEdge,
                priorHeading,
                reportedDistance: 1d,
                calculatedDistance: null,
                out _,
                out _), Is.True);

            Assert.That(CoveragePolygonWriter.TryWrite(
                geodesy,
                center,
                10d,
                ref leadingEdge,
                heading,
                reportedDistance: 1d,
                calculatedDistance: null,
                out byte[] wkb,
                out _), Is.True);

            var geometry = new WKBReader().Read(wkb);
            Assert.That(wkb, Is.Not.Empty);
            Assert.That(geometry.Area, Is.GreaterThan(0d));
            Assert.That(geometry.IsValid, Is.True);
        }

        [TestCase(10e-6d)]
        [TestCase(50e-6d)]
        public void NearDuplicatePinnedCornerIsStillABowTie(double separationM)
        {
            var geodesy = new LocalGeodesy(-87.60d, 41.88d);
            var center = new LocalPoint(0d, 0d);
            var prior = new LeadingEdge(geodesy, center, 10d, default, 10d);
            var next = new LeadingEdge(geodesy, center, 10d, prior, 350d);
            Assert.That(next.LeftLon, Is.EqualTo(prior.LeftLon));
            Assert.That(next.LeftLat, Is.EqualTo(prior.LeftLat));

            var pin = geodesy.ToLocalPoint(prior.LeftLon, prior.LeftLat);
            var backRight = geodesy.ToLocalPoint(prior.RightLon, prior.RightLat);
            var swungRight = geodesy.ToLocalPoint(next.RightLon, next.RightLat);
            double edgeEast = backRight.EastM - pin.EastM;
            double edgeNorth = backRight.NorthM - pin.NorthM;
            double edgeLength = Math.Sqrt((edgeEast * edgeEast) + (edgeNorth * edgeNorth));
            double normalEast = -edgeNorth / edgeLength;
            double normalNorth = edgeEast / edgeLength;
            double side = ((swungRight.EastM - pin.EastM) * normalEast) + ((swungRight.NorthM - pin.NorthM) * normalNorth);
            if (side > 0d)
            {
                normalEast = -normalEast;
                normalNorth = -normalNorth;
            }

            var shifted = new LocalPoint(pin.EastM + (normalEast * separationM), pin.NorthM + (normalNorth * separationM));
            geodesy.ToGeographic(shifted.EastM, shifted.NorthM, out double shiftedLon, out double shiftedLat);
            Assert.That(GeometryExporter.HaversineDistance(shiftedLon, shiftedLat, prior.LeftLon, prior.LeftLat), Is.EqualTo(separationM).Within(1e-6d));

            Assert.That(CoverageWkb.EvaluateQuad(
                geodesy,
                shiftedLon,
                shiftedLat,
                next.RightLon,
                next.RightLat,
                prior.RightLon,
                prior.RightLat,
                prior.LeftLon,
                prior.LeftLat), Is.EqualTo(CoverageQuadStatus.SelfIntersection));

            byte[] wkb = CoverageWkb.WritePolygon(
                shiftedLon,
                shiftedLat,
                next.RightLon,
                next.RightLat,
                prior.RightLon,
                prior.RightLat,
                prior.LeftLon,
                prior.LeftLat);
            Assert.That(new WKBReader().Read(wkb).IsValid, Is.False);
        }

        [TestCase(350d, 10d, false)]
        [TestCase(10d, 350d, true)]
        [TestCase(170d, -170d, false)]
        [TestCase(-170d, 170d, true)]
        public void CrossingHeadingPinsTheInsideCorner(double priorHeading, double heading, bool pinLeft)
        {
            var geodesy = new LocalGeodesy(-87.60d, 41.88d);
            var center = new LocalPoint(0d, 0d);
            var prior = new LeadingEdge(geodesy, center, 10d, default, priorHeading);
            var next = new LeadingEdge(geodesy, center, 10d, prior, heading);

            if (pinLeft)
            {
                Assert.That(next.LeftLon, Is.EqualTo(prior.LeftLon));
                Assert.That(next.LeftLat, Is.EqualTo(prior.LeftLat));
                Assert.That(next.RightLat, Is.Not.EqualTo(prior.RightLat));
            }
            else
            {
                Assert.That(next.RightLon, Is.EqualTo(prior.RightLon));
                Assert.That(next.RightLat, Is.EqualTo(prior.RightLat));
                Assert.That(next.LeftLat, Is.Not.EqualTo(prior.LeftLat));
            }
        }

        private static void AssertCornerWithinOneCentimeter(byte[] wkb, int cornerIndex, Point expected)
        {
            int offset = 13 + (cornerIndex * 16);
            double lon = BitConverter.ToDouble(wkb, offset);
            double lat = BitConverter.ToDouble(wkb, offset + 8);
            double errorM = GeometryExporter.HaversineDistance(lon, lat, expected.X, expected.Y);
            Assert.That(errorM, Is.LessThan(0.01d), $"corner={cornerIndex}");
        }
    }
}

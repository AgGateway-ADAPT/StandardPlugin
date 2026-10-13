using System;

namespace AgGateway.ADAPT.StandardPlugin
{
    /// <summary>
    /// Builds one coverage quad in the current GPS fix's local east/north grid and returns WKB.
    /// The prior leading edge becomes the next quad's back edge. The first quad uses a clamped
    /// travel distance to avoid a collapsed ring or a long polygon from a bad initial fix.
    /// </summary>
    internal static class CoveragePolygonWriter
    {
        internal static bool TryWrite(
            LocalGeodesy geodesy,
            LocalPoint leadingCenter,
            double width,
            ref LeadingEdge latestLeadingEdge,
            double heading,
            double? reportedDistance,
            double? calculatedDistance,
            out byte[] wkb,
            out CoverageBounds bounds)
        {
            wkb = null;
            bounds = default;

            LeadingEdge priorLeadingEdge = latestLeadingEdge;
            LeadingEdge candidateLeadingEdge = new LeadingEdge(geodesy, leadingCenter, width, in priorLeadingEdge, heading);

            LocalPoint backRightLocal;
            LocalPoint backLeftLocal;
            if (priorLeadingEdge.HasValue)
            {
                backRightLocal = geodesy.ToLocalPoint(priorLeadingEdge.RightLon, priorLeadingEdge.RightLat);
                backLeftLocal = geodesy.ToLocalPoint(priorLeadingEdge.LeftLon, priorLeadingEdge.LeftLat);
            }
            else
            {
                double distance = 1d;
                if (reportedDistance > 0d)
                {
                    distance = reportedDistance.Value;
                }
                else if (calculatedDistance > 0d)
                {
                    distance = calculatedDistance.Value;
                }

                distance = Math.Clamp(distance, 0.1d, 4d);
                var rightLocal = geodesy.ToLocalPoint(candidateLeadingEdge.RightLon, candidateLeadingEdge.RightLat);
                backRightLocal = geodesy.DestinationLocal(rightLocal, distance, Extensions.HeadingBack(heading));
                backLeftLocal = geodesy.DestinationLocal(backRightLocal, width, Extensions.HeadingLeft(heading));
            }

            geodesy.ToGeographic(backRightLocal.EastM, backRightLocal.NorthM, out double backRightLon, out double backRightLat);
            geodesy.ToGeographic(backLeftLocal.EastM, backLeftLocal.NorthM, out double backLeftLon, out double backLeftLat);

            CoverageQuadStatus status = CoverageWkb.EvaluateQuad(
                geodesy,
                candidateLeadingEdge.LeftLon,
                candidateLeadingEdge.LeftLat,
                candidateLeadingEdge.RightLon,
                candidateLeadingEdge.RightLat,
                backRightLon,
                backRightLat,
                backLeftLon,
                backLeftLat);
            if (status == CoverageQuadStatus.SelfIntersection)
            {
                latestLeadingEdge = default;
                return false;
            }

            if (status != CoverageQuadStatus.Valid)
            {
                // A stop or a collapsed ring keeps the previous leading edge so the next
                // movement continues from it instead of starting a new short polygon.
                latestLeadingEdge = priorLeadingEdge;
                return false;
            }

            latestLeadingEdge = candidateLeadingEdge;
            wkb = CoverageWkb.WritePolygon(
                candidateLeadingEdge.LeftLon,
                candidateLeadingEdge.LeftLat,
                candidateLeadingEdge.RightLon,
                candidateLeadingEdge.RightLat,
                backRightLon,
                backRightLat,
                backLeftLon,
                backLeftLat);
            bounds = CoverageWkb.ComputeBounds(
                candidateLeadingEdge.LeftLon,
                candidateLeadingEdge.LeftLat,
                candidateLeadingEdge.RightLon,
                candidateLeadingEdge.RightLat,
                backRightLon,
                backRightLat,
                backLeftLon,
                backLeftLat);
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Equipment;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Representations;

namespace AgGateway.ADAPT.StandardPlugin
{
    internal class SectionDefinition
    {
        public SectionDefinition(DeviceElementUse deviceElementUse, DeviceElementConfiguration deviceElementConfiguration, DeviceElement deviceElement, OperationData operationData, Catalog catalog, List<TypeMapping> typeMappings)
        {
            DeviceElement = deviceElement;
            
            WorkstateDefinition = deviceElementUse.GetWorkingDatas().OfType<EnumeratedWorkingData>().FirstOrDefault(x => x.Representation.Code == "dtRecordingStatus");
            FactoredDefinitionsBySourceCodeByProduct = new Dictionary<string, Dictionary<string, FactoredWorkingData>>();

            WidthM = deviceElementConfiguration.WidthM();
            Offset = deviceElementConfiguration.AsOffset();

            //Add only the variables we can map to a standard type
            var numericWorkingDatas = deviceElementUse.GetWorkingDatas().OfType<NumericWorkingData>().Where(nwd => typeMappings.Any(m => m.Source == nwd.Representation.Code));
            var definitions = numericWorkingDatas.Select(nwd => new FactoredWorkingData(nwd, WidthM, WidthM, typeMappings.First(m => m.Source == nwd.Representation.Code)));

             //Track any variable product
            ProductIndexWorkingData = deviceElementUse.GetWorkingDatas().FirstOrDefault(wd => wd.Representation.Code == "vrProductIndex") as NumericWorkingData;
            
            if (ProductIndexWorkingData == null)
            {
                FactoredDefinitionsBySourceCodeByProduct.Add(string.Empty, definitions.ToDictionary(d => d.WorkingData.Representation.Code));
            }
            else 
            {
                FactoredDefinitionsBySourceCodeByProduct.Add(string.Empty, new Dictionary<string, FactoredWorkingData>());
                foreach (var productId in operationData.ProductIds)
                {
                    FactoredDefinitionsBySourceCodeByProduct.Add(productId.ToString(), new Dictionary<string, FactoredWorkingData>());
                }
                foreach (var definition in definitions)
                {
                    if (typeMappings.First(m => m.Source == definition.WorkingData.Representation.Code).IsMultiProductCapable)
                    {
                        foreach (var id in operationData.ProductIds)
                        {
                            FactoredDefinitionsBySourceCodeByProduct[id.ToString()].Add(definition.WorkingData.Representation.Code, definition);
                        }
                    }  
                    else
                    {
                        FactoredDefinitionsBySourceCodeByProduct[string.Empty].Add(definition.WorkingData.Representation.Code, definition);
                    }
                }
            }
        }
        public Offset Offset { get; set; }
        public DeviceElement DeviceElement { get; set; }
        public EnumeratedWorkingData WorkstateDefinition { get; set; }
        public Dictionary<string, Dictionary<string, FactoredWorkingData>> FactoredDefinitionsBySourceCodeByProduct { get; set; }

        public double WidthM { get; set; }

        public NumericWorkingData ProductIndexWorkingData { get; set; }

        public string GetDefinitionKey()
        {
            StringBuilder builder = new StringBuilder();
            builder.Append(DeviceElement?.Description ?? string.Empty);
            builder.Append(Offset.X?.ToString() ?? string.Empty);
            builder.Append(Offset.Y?.ToString() ?? string.Empty);
            builder.Append(WidthM.ToString());
            foreach(string productId in FactoredDefinitionsBySourceCodeByProduct.Keys)
            {
                foreach(var factoredDefinition in FactoredDefinitionsBySourceCodeByProduct[productId].Values)
                {
                    builder.Append(factoredDefinition.WorkingData.Representation.Code);
                }
            }

            return builder.ToString().AsMD5Hash();
        }

        public void AddAncestorWorkingDatas(DeviceElementUse ancestorUse, DeviceElementConfiguration ancestorConfig, OperationData operationData, List<TypeMapping> typeMappings)
        {
            var productIndex = ancestorUse.GetWorkingDatas().FirstOrDefault(wd => wd.Representation.Code == "vrProductIndex") as NumericWorkingData;
            if (productIndex != null && ProductIndexWorkingData == null)
            {   
                ProductIndexWorkingData = productIndex;
                foreach (var productId in operationData.ProductIds)
                {
                    if (!FactoredDefinitionsBySourceCodeByProduct.ContainsKey(productId.ToString()))
                    {
                        FactoredDefinitionsBySourceCodeByProduct.Add(productId.ToString(), new Dictionary<string, FactoredWorkingData>());
                    }
                }
                List<FactoredWorkingData> definitionMoved = new List<FactoredWorkingData>();
                foreach (var definition in FactoredDefinitionsBySourceCodeByProduct[string.Empty].Values.Where(d => typeMappings.First(m => m.Source == d.WorkingData.Representation.Code).IsMultiProductCapable))
                {
                    definitionMoved.Add(definition);
                    foreach (var productId in operationData.ProductIds)
                    {
                        FactoredDefinitionsBySourceCodeByProduct[productId.ToString()].Add(definition.WorkingData.Representation.Code, definition);
                    }
                }
                foreach (var definition in definitionMoved)
                {
                    FactoredDefinitionsBySourceCodeByProduct[string.Empty].Remove(definition.WorkingData.Representation.Code);
                }
            }

            foreach (var workingData in ancestorUse.GetWorkingDatas())
            {
                if (WorkstateDefinition == null &&
                    workingData.Representation.Code == "dtRecordingStatus" &&
                    workingData is EnumeratedWorkingData ewd)
                {
                    WorkstateDefinition = ewd;
                }
                else if (workingData is NumericWorkingData nwd &&
                        typeMappings.Any(m => m.Source == nwd.Representation.Code))
                {
                    if (typeMappings.First(m => m.Source == nwd.Representation.Code).IsMultiProductCapable)
                    {
                        foreach (var productId in FactoredDefinitionsBySourceCodeByProduct.Keys)
                        {
                            if (!FactoredDefinitionsBySourceCodeByProduct[productId].ContainsKey(nwd.Representation.Code))
                            {
                                FactoredDefinitionsBySourceCodeByProduct[productId].Add(nwd.Representation.Code, new FactoredWorkingData(nwd, ancestorConfig.WidthM(), WidthM, typeMappings.First(m => m.Source == nwd.Representation.Code)));
                            }
                        }
                    }
                    else
                    {
                        if (!FactoredDefinitionsBySourceCodeByProduct[string.Empty].ContainsKey(nwd.Representation.Code))
                        {
                            FactoredDefinitionsBySourceCodeByProduct[string.Empty].Add(nwd.Representation.Code, new FactoredWorkingData(nwd, ancestorConfig.WidthM(), WidthM, typeMappings.First(m => m.Source == nwd.Representation.Code)));
                        }
                    }
                }
            }
        }

        public bool IsEngaged(SpatialRecord record)
        {
            if (WorkstateDefinition != null)
            {
                EnumeratedValue engagedValue = record.GetMeterValue(WorkstateDefinition) as EnumeratedValue;
                return engagedValue != null && (engagedValue.Value.Value == "dtiRecordingStatusOn" || engagedValue.Value.Value == "On");
            }
            return true;
        }

        private LeadingEdge _latestLeadingEdge;
        public bool TryGetCoverageWkb(
            in CoverageRecordContext context,
            double bearing,
            double? reportedDistance,
            double? calculatedDistance,
            out byte[] wkb,
            out CoverageBounds bounds)
        {
            wkb = null;
            bounds = default;

            double inlineOffset = Offset.X ?? 0d;
            double lateralOffset = Offset.Y ?? 0d;

            // The grid origin is the GPS fix. ADAPT X offset is inline (along the heading) and
            // Y offset is lateral. Lateral is applied at heading + 90, which is to the right
            // because headings are clockwise from north.
            var leadingCenter = new LocalPoint(0d, 0d);
            if (inlineOffset != 0d || lateralOffset != 0d)
            {
                leadingCenter = context.Geodesy.DestinationLocal(leadingCenter, inlineOffset, bearing % 360d);
                leadingCenter = context.Geodesy.DestinationLocal(leadingCenter, lateralOffset, bearing + 90d);
            }

            return CoveragePolygonWriter.TryWrite(
                context.Geodesy,
                leadingCenter,
                WidthM,
                ref _latestLeadingEdge,
                bearing,
                reportedDistance,
                calculatedDistance,
                out wkb,
                out bounds);
        }

        public void ClearLeadingEdge()
        {
            _latestLeadingEdge = default;
        }

        internal bool TryGetLeadingHeading(out double heading)
        {
            heading = _latestLeadingEdge.Heading;
            return _latestLeadingEdge.HasValue;
        }

    }

    internal struct LeadingEdge
    {
        internal bool HasValue;
        internal double LeftLon;
        internal double LeftLat;
        internal double CenterLon;
        internal double CenterLat;
        internal double RightLon;
        internal double RightLat;
        internal double Heading;

        internal LeadingEdge(LocalGeodesy geodesy, LocalPoint leadingCenter, double width, in LeadingEdge priorLeadingEdge, double heading)
        {
            HasValue = true;
            Heading = heading;
            // Half the section width to either side. Headings grow clockwise, so left is heading - 90 and right is heading + 90.
            double wh = width / 2d;
            var leftLocal = geodesy.DestinationLocal(leadingCenter, wh, Extensions.HeadingLeft(heading));
            var rightLocal = geodesy.DestinationLocal(leadingCenter, wh, Extensions.HeadingRight(heading));
            geodesy.ToGeographic(leadingCenter.EastM, leadingCenter.NorthM, out CenterLon, out CenterLat);
            geodesy.ToGeographic(leftLocal.EastM, leftLocal.NorthM, out LeftLon, out LeftLat);
            geodesy.ToGeographic(rightLocal.EastM, rightLocal.NorthM, out RightLon, out RightLat);

            if (priorLeadingEdge.HasValue)
            {
                var priorLeftLocal = geodesy.ToLocalPoint(priorLeadingEdge.LeftLon, priorLeadingEdge.LeftLat);
                var priorRightLocal = geodesy.ToLocalPoint(priorLeadingEdge.RightLon, priorLeadingEdge.RightLat);
                // The new leading edge crossed the previous one, which happens when the implement
                // turns tighter than the section is wide. Pin the inside corner to the previous
                // inside corner so the quad does not bow-tie. Heading delta is wrapped into -180..180
                // first: 350 to 10 is +20, a right turn, and 10 to 350 is -20, a left turn.
                if (LocalGeodesy.SegmentsIntersect(leftLocal, rightLocal, priorLeftLocal, priorRightLocal))
                {
                    bool turningLeft = WrapSignedDegrees(heading - priorLeadingEdge.Heading) < 0d;
                    if (turningLeft)
                    {
                        LeftLon = priorLeadingEdge.LeftLon;
                        LeftLat = priorLeadingEdge.LeftLat;
                    }
                    else
                    {
                        RightLon = priorLeadingEdge.RightLon;
                        RightLat = priorLeadingEdge.RightLat;
                    }
                }
            }
        }

        private static double WrapSignedDegrees(double delta)
        {
            delta %= 360d;
            if (delta > 180d)
            {
                delta -= 360d;
            }
            else if (delta < -180d)
            {
                delta += 360d;
            }

            return delta;
        }
    }

    internal class Offset
    {
        public Offset(double? x, double? y, double? z)
        {
            X = x;
            Y = y;
            Z = z;
        }
        public double? X { get; set; }
        public double? Y { get; set; }
        public double? Z { get; set; }

        public void Add(Offset other)
        {
            if (!X.HasValue) X = 0;
            X += other.X ?? 0;
            if (!Y.HasValue) Y = 0;
            Y += other.Y ?? 0;
            if (!Z.HasValue) Z = 0;
            Z += other.Z ?? 0;
        }
    }

    /// <summary>
    /// In cases where an implement reports a single total value, e.g., at the implement level and then models multiple sections for on/off values only
    /// The total must be factored down for the width of the section relative to the total width of the implement
    /// For rates, percentages, etc.  No factor is needed.
    /// </summary>
    internal class FactoredWorkingData
    {
        public FactoredWorkingData(NumericWorkingData workingData, double dataWidthM, double sectionWidthM, TypeMapping mapping)
        {
            WorkingData = workingData;
            
            if (mapping.ShouldFactor && 
                dataWidthM > 0d &&
                Math.Abs(dataWidthM - sectionWidthM) > .001) //Floating point values are sometimes not exact
            {
                Factor = sectionWidthM / dataWidthM;
            }
            else
            {
                Factor = 1d;
            }
        }
        public double Factor { get; set; }
        public NumericWorkingData WorkingData { get; set; }
    }
}
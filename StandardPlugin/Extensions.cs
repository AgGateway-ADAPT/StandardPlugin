using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AgGateway.ADAPT.ApplicationDataModel.Equipment;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using RepresentationUnitSystem = AgGateway.ADAPT.Representation.UnitSystem;
using NetTopologySuite.Geometries;
using System.Security.Cryptography;
using System.Text;
using AgGateway.ADAPT.ApplicationDataModel.Common;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;

namespace AgGateway.ADAPT.StandardPlugin
{
    internal static class Extensions
    {
        // Convert has no instance state, so one converter is shared. The affine cache calls it twice per unit pair.
        private static readonly RepresentationUnitSystem.UnitOfMeasureConverter SharedUnitConverter = new RepresentationUnitSystem.UnitOfMeasureConverter();
        // MD5.Create allocates, and a shared hasher is not safe across the operation threads.
        [ThreadStatic]
        private static MD5 _md5Hasher;

        private class UnitCodeRemap
        {
            public string SourceUnitCode;
            public string NewSourceUnitCode;
            public string TargetUnitCode;
        }
        private readonly static Dictionary<string, UnitCodeRemap> UnitOfMeasureMapping = new Dictionary<string, UnitCodeRemap>
        {
            // vrSeedRateSeeds... are mapped to AppliedCountPerArea... variables that use a different UoM domain.
            //'Seeds' is effectively the same as 'Count', so these unit conversions will suceed and be correct
            { "vrSeedRateSeedsTarget", new UnitCodeRemap { TargetUnitCode = "seeds1ha-1" } },
            { "vrSeedRateSeedsActual", new UnitCodeRemap { TargetUnitCode = "seeds1ha-1" } },
            { "vrSeedRateSeedsSetPoint", new UnitCodeRemap { TargetUnitCode = "seeds1ha-1" } },
            { "vrTotalSeedQuantityAppliedSeed", new UnitCodeRemap { TargetUnitCode = "seeds" } },
            { "vrSeedsProductivity", new UnitCodeRemap { TargetUnitCode = "seed1sec-1" } },
            // Some plugins by mistake assign "lb" to DownForce sensors instead of "lbf".
            // Here we fix this by replacing "lb" with "lbf"
            { "vrDownForceMargin", new UnitCodeRemap { SourceUnitCode = "lb", NewSourceUnitCode = "lbf" } },
            { "vrDownForceApplied", new UnitCodeRemap { SourceUnitCode = "lb", NewSourceUnitCode = "lbf" } },
        };

        public static bool IsNullOrEmpty<T>(this IEnumerable<T> collection)
        {
            return collection == null || !collection.Any();
        }

        public static bool IsNullOrEmpty<T>(this List<T> collection)
        {
            return collection == null || collection.Count == 0;
        }

        public static bool EqualsIgnoreCase(this string source, string other)
        {
            return string.Equals(source, other, StringComparison.OrdinalIgnoreCase);
        }

        public static List<string> FilterEmptyValues(params string[] values)
        {
            return values.Where(x => !string.IsNullOrEmpty(x)).ToList();
        }

        public static IEnumerable<IEnumerable<T>> Batch<T>(this IEnumerable<T> items, int maxItems)
        {
            return items.Select((item, inx) => new { item, inx })
                        .GroupBy(x => x.inx / maxItems)
                        .Select(g => g.Select(x => x.item));
        }

        // Dimension lookup is expensive in the framework and invariant per unit pair; called per spatial value.
        private static readonly ConcurrentDictionary<(string Source, string Target), bool> CanConvertCache = new ConcurrentDictionary<(string, string), bool>();

        // Composite conversion repeats that dimension walk and allocates a unit on every call.
        // The result is an affine scale and offset, so it can be resolved once per unit pair.
        private static readonly ConcurrentDictionary<(string Source, string Target), ValueConverter> ValueConverterCache = new ConcurrentDictionary<(string, string), ValueConverter>();

        public static bool CanConvert(string srcUnitCode, string targetUnitCode)
        {
            return CanConvertCache.GetOrAdd((srcUnitCode, targetUnitCode), key => ComputeCanConvert(key.Source, key.Target));
        }

        private static bool ComputeCanConvert(string srcUnitCode, string targetUnitCode)
        {
            if (RepresentationUnitSystem.InternalUnitSystemManager.Instance.UnitOfMeasures[targetUnitCode] != null &&
                RepresentationUnitSystem.InternalUnitSystemManager.Instance.UnitOfMeasures[srcUnitCode] != null)
            {
                var targetUOM = RepresentationUnitSystem.UnitSystemManager.GetUnitOfMeasure(targetUnitCode);
                var sourceUOM = RepresentationUnitSystem.UnitSystemManager.GetUnitOfMeasure(srcUnitCode);
                return sourceUOM.Dimension == targetUOM.Dimension;
            }
            return false;
        }

        public static bool CanConvertInto(this UnitOfMeasure srcUOM, string targetUnitCode)
        {
            return CanConvert(srcUOM.Code, targetUnitCode);
        }

        public static bool CanConvertInto(this NumericWorkingData nwd, string targetUnitCode)
        {
            if (!nwd.UnitOfMeasure.CanConvertInto(targetUnitCode))
            {
                if (UnitOfMeasureMapping.TryGetValue(nwd.Representation.Code, out var unitCodeRemap))
                {
                    var srcUnitCode = nwd.UnitOfMeasure.Code;
                    if (!string.IsNullOrEmpty(unitCodeRemap.NewSourceUnitCode) && unitCodeRemap.SourceUnitCode == srcUnitCode)
                    {
                        srcUnitCode = unitCodeRemap.NewSourceUnitCode;
                    }
                    if (!string.IsNullOrEmpty(unitCodeRemap.TargetUnitCode))
                    {
                        targetUnitCode = unitCodeRemap.TargetUnitCode;
                    }
                    return CanConvert(srcUnitCode, targetUnitCode);
                }
                else
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool TryGetRemappedUnitCodes(string representationCode, string srcUnitCode, string targetUnitCode, out string remappedSource, out string remappedTarget)
        {
            remappedSource = srcUnitCode;
            remappedTarget = targetUnitCode;
            if (UnitOfMeasureMapping.TryGetValue(representationCode, out var unitCodeRemap))
            {
                if (!string.IsNullOrEmpty(unitCodeRemap.NewSourceUnitCode) && unitCodeRemap.SourceUnitCode == srcUnitCode)
                {
                    remappedSource = unitCodeRemap.NewSourceUnitCode;
                }

                if (!string.IsNullOrEmpty(unitCodeRemap.TargetUnitCode))
                {
                    remappedTarget = unitCodeRemap.TargetUnitCode;
                }

                return true;
            }

            return false;
        }

        public static double? AsConvertedDouble(this NumericRepresentationValue value, string targetUnitCode)
        {
            if (value == null)
            {
                return null;
            }
            else if (value.Value.UnitOfMeasure == null)
            {
                return value.Value.Value; //Return the unconverted value
            }

            var srcUnitCode = value.Value.UnitOfMeasure.Code;
            if (!value.Value.UnitOfMeasure.CanConvertInto(targetUnitCode))
            {
                if (TryGetRemappedUnitCodes(value.Representation.Code, srcUnitCode, targetUnitCode, out var remappedSource, out var remappedTarget))
                {
                    srcUnitCode = remappedSource;
                    targetUnitCode = remappedTarget;
                }
                else
                {
                    return null; //Cannot convert
                }
            }


            return value.Value.Value.ConvertValue(srcUnitCode, targetUnitCode);
        }

        public static double ConvertValue(this double n, string srcUnitCode, string dstUnitCode)
        {
            return GetValueConverter(srcUnitCode, dstUnitCode)(n);
        }

        internal delegate double ValueConverter(double value);

        internal static ValueConverter BuildValueConverter(string representationCode, string srcUnitCode, string targetUnitCode)
        {
            // Remap only when the reported unit cannot convert to the standard unit.
            // Seed rates reported as count per area already convert to count1ha-1; forcing seeds1ha-1 throws.
            if (!CanConvert(srcUnitCode, targetUnitCode) &&
                TryGetRemappedUnitCodes(representationCode, srcUnitCode, targetUnitCode, out var remappedSource, out var remappedTarget))
            {
                srcUnitCode = remappedSource;
                targetUnitCode = remappedTarget;
            }

            return GetValueConverter(srcUnitCode, targetUnitCode);
        }

        private static ValueConverter GetValueConverter(string srcUnitCode, string dstUnitCode)
        {
            if (srcUnitCode == dstUnitCode)
            {
                return value => value;
            }

            return ValueConverterCache.GetOrAdd((srcUnitCode, dstUnitCode), key => CreateValueConverter(key.Source, key.Target));
        }

        private static ValueConverter CreateValueConverter(string srcUnitCode, string dstUnitCode)
        {
            RepresentationUnitSystem.UnitOfMeasure sourceUOM = RepresentationUnitSystem.InternalUnitSystemManager.Instance.UnitOfMeasures[srcUnitCode];
            RepresentationUnitSystem.UnitOfMeasure targetUOM = RepresentationUnitSystem.InternalUnitSystemManager.Instance.UnitOfMeasures[dstUnitCode];
            if (sourceUOM == null || targetUOM == null)
            {
                return value => value;
            }

            // ADAPT units are a scale and an offset, so two conversions define every later value:
            // converted = value * (Convert(1) - Convert(0)) + Convert(0). That matches Convert,
            // including temperature offsets.
            double convertedZero = SharedUnitConverter.Convert(sourceUOM, targetUOM, 0d);
            double convertedOne = SharedUnitConverter.Convert(sourceUOM, targetUOM, 1d);
            double scale = convertedOne - convertedZero;
            return value => (value * scale) + convertedZero;
        }

        public static double WidthM(this DeviceElementConfiguration deviceElementConfiguration)
        {
            if (deviceElementConfiguration is SectionConfiguration sectionConfiguration)
            {
                return sectionConfiguration.SectionWidth?.AsConvertedDouble("m") ?? 0d;
            }
            else if (deviceElementConfiguration is ImplementConfiguration implementConfiguration)
            {
                return implementConfiguration.PhysicalWidth.AsConvertedDouble("m") ?? implementConfiguration.Width?.AsConvertedDouble("m") ?? 0d;
            }
            return 0d;
        }

        public static Offset AsOffset(this List<NumericRepresentationValue> offsets)
        {
            return new Offset(offsets.FirstOrDefault(x => x.Representation.Code == "vrInlineOffset")?.AsConvertedDouble("m") ?? 0d,
                offsets.FirstOrDefault(x => x.Representation.Code == "vrLateralOffset")?.AsConvertedDouble("m") ?? 0d,
                offsets.FirstOrDefault(x => x.Representation.Code == "vrVerticalOffset")?.AsConvertedDouble("m") ?? 0d);
        }

        public static Offset AsOffset(this ReferencePoint referencePoint)
        {
            return new Offset(referencePoint.XOffset?.AsConvertedDouble("m") ?? 0d,
                referencePoint.YOffset?.AsConvertedDouble("m") ?? 0d,
                referencePoint.ZOffset?.AsConvertedDouble("m") ?? 0d);
        }

        public static Offset AsOffsetInlineReversed(this ReferencePoint referencePoint)
        {
            return new Offset(-referencePoint.XOffset?.AsConvertedDouble("m") ?? 0d,
                referencePoint.YOffset?.AsConvertedDouble("m") ?? 0d,
                referencePoint.ZOffset?.AsConvertedDouble("m") ?? 0d);
        }

        public static Offset AsOffset(this DeviceElementConfiguration configuration)
        {
            if (configuration is SectionConfiguration sectionConfiguration)
            {
                return new Offset(sectionConfiguration.InlineOffset.AsConvertedDouble("m"),
                    sectionConfiguration.LateralOffset.AsConvertedDouble("m"),
                    0d);
            }
            else if (configuration is ImplementConfiguration implementConfiguration)
            {
                if (implementConfiguration.Offsets.Any())
                {
                    return implementConfiguration.Offsets.AsOffset();
                }
            }
            else if (configuration is MachineConfiguration machineConfiguration)
            {
                return new Offset(-machineConfiguration.GpsReceiverXOffset.AsConvertedDouble("m") ?? 0d, //The GPS recevier inline offset is in the opposing direction
                    machineConfiguration.GpsReceiverYOffset.AsConvertedDouble("m") ?? 0d,
                    machineConfiguration.GpsReceiverZOffset.AsConvertedDouble("m") ?? 0d);
            }
            return new Offset(0d, 0d, 0d);
        }

        public static Point Destination(this Point point, double distanceM, double headingDeg)
        {
            return GeometryExporter.Destination(point, distanceM, headingDeg);
        }

        public static Coordinate AsCoordinate(this Point point)
        {
            return new Coordinate(point.X, point.Y);
        }

        public static double HeadingBack(this double heading)
        {
            return heading - 180;
        }

        public static double HeadingLeft(this double heading)
        {
            return heading - 90;
        }

        public static double HeadingRight(this double heading)
        {
            return heading + 90;
        }

        public static string AsMD5Hash(this string input)
        {
            var bytes = ASCIIEncoding.ASCII.GetBytes(input);
            _md5Hasher ??= MD5.Create();
            var hashBytes = _md5Hasher.ComputeHash(bytes);
            int i;
            StringBuilder sOutput = new StringBuilder(hashBytes.Length);
            for (i = 0; i < hashBytes.Length; i++)
            {
                sOutput.Append(hashBytes[i].ToString("X2"));
            }
            return sOutput.ToString();
        }

        public static string AsName(this string suppliedName, string objectType, string id)
        {
            if (suppliedName.IsNullOrEmpty())
            {
                return string.Concat(objectType, id);
            }
            return suppliedName;
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Common;
using AgGateway.ADAPT.ApplicationDataModel.Documents;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.StandardPlugin;
using NetTopologySuite.Geometries;
using NUnit.Framework;
using ParquetSharp;

namespace StandardPluginTests
{
    public class GeometryExporterTests
    {
        [TestCase(0.1d, 41.88d)]
        [TestCase(4d, 41.88d)]
        [TestCase(15d, 41.88d)]
        [TestCase(30d, 41.88d)]
        [TestCase(15d, 0d)]
        [TestCase(15d, 60d)]
        public void LocalDestinationMatchesHaversineWithinOneCentimeter(double distanceM, double latitudeDeg)
        {
            var start = new Point(-87.60d, latitudeDeg);
            for (double heading = 0d; heading < 360d; heading += 45d)
            {
                var localResult = GeometryExporter.Destination(start, distanceM, heading);
                var haversineResult = GeometryExporter.HaversineDestination(start, distanceM, heading);
                double errorM = GeometryExporter.HaversineDistance(localResult, haversineResult);
                Assert.That(errorM, Is.LessThan(0.01d), $"heading={heading}");
            }
        }

        [Test]
        public void LocalDistanceAndBearingMatchHaversineWithinOneCentimeter()
        {
            var start = new Point(-87.60d, 41.88d);
            var end = GeometryExporter.HaversineDestination(start, 12d, 15d);

            double localDistance = GeometryExporter.Distance(start, end);
            double haversineDistance = GeometryExporter.HaversineDistance(start, end);
            Assert.That(Math.Abs(localDistance - haversineDistance), Is.LessThan(0.01d));

            double localBearing = GeometryExporter.Bearing(start, end);
            double haversineBearing = GeometryExporter.HaversineBearing(start, end);
            Assert.That(Math.Abs(localBearing - haversineBearing), Is.LessThan(0.01d));
        }
    }

    public class UnitConversionTests
    {
        [Test]
        public void CachedConverterPreservesScaleAndOffset()
        {
            Assert.That(0d.ConvertValue("C", "F"), Is.EqualTo(32d).Within(1e-9));
            Assert.That(100d.ConvertValue("C", "F"), Is.EqualTo(212d).Within(1e-6));
            Assert.That(1d.ConvertValue("m", "ft"), Is.EqualTo(1d / 0.3048d).Within(1e-9));
            Assert.That(2d.ConvertValue("m", "ft"), Is.EqualTo(2d / 0.3048d).Within(1e-9));
            Assert.That(5d.ConvertValue("m", "m"), Is.EqualTo(5d));
            Assert.That(1d.ConvertValue("seeds1ac-1", "seeds1ha-1"), Is.EqualTo(1d / 0.40468564224d).Within(1e-9));
            Assert.That(3d.ConvertValue("seeds1ac-1", "seeds1ha-1"), Is.EqualTo(3d / 0.40468564224d).Within(1e-9));
        }

        [Test]
        public void UnknownUnitDoesNotPassTheValueThrough()
        {
            Assert.That(Extensions.BuildValueConverter("vrHeading", "not-a-unit", "m"), Is.Null);

            var column = new ADAPTDataColumn(
                new NumericWorkingData
                {
                    Representation = new NumericRepresentation { Code = "vrHeading" },
                    UnitOfMeasure = new UnitOfMeasure { Code = "not-a-unit" }
                },
                "Heading",
                "m");
            column.InitializeConverter();
            var meterValue = new NumericRepresentationValue
            {
                Representation = new NumericRepresentation { Code = "vrHeading" },
                Value = new NumericValue(new UnitOfMeasure { Code = "not-a-unit" }, 5d)
            };
            Assert.That(column.ConvertMeterValue(meterValue), Is.Null);
        }

        [Test]
        public void RemappedRepresentationWithAnUnknownUnitReturnsNull()
        {
            var downForce = new NumericRepresentationValue
            {
                Representation = new NumericRepresentation { Code = "vrDownForceApplied" },
                Value = new NumericValue(new UnitOfMeasure { Code = "not-a-unit" }, 5d)
            };
            Assert.That(downForce.AsConvertedDouble("N"), Is.Null);
            Assert.That(Extensions.BuildValueConverter("vrDownForceApplied", "not-a-unit", "N"), Is.Null);

            var seedRate = new NumericRepresentationValue
            {
                Representation = new NumericRepresentation { Code = "vrSeedRateSeedsActual" },
                Value = new NumericValue(new UnitOfMeasure { Code = "not-a-unit" }, 5d)
            };
            Assert.That(seedRate.AsConvertedDouble("count1ha-1"), Is.Null);
            Assert.That(5d.ConvertValue("not-a-unit", "count1ha-1"), Is.Null);
        }

        [Test]
        public void RepeatedConversionReusesTheCachedConverter()
        {
            Assert.That(1d.ConvertValue("m", "ft"), Is.Not.Null);
            Assert.That(1d.ConvertValue("not-a-unit", "m"), Is.Null);
            for (int i = 0; i < 1000; i++)
            {
                _ = 2d.ConvertValue("m", "ft");
                _ = 2d.ConvertValue("not-a-unit", "m");
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1000; i++)
            {
                _ = 2d.ConvertValue("m", "ft");
                _ = 2d.ConvertValue("not-a-unit", "m");
            }

            // A warm lookup used to allocate on every call. A one-time runtime cost can remain; a per-call cost cannot.
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(allocated, Is.LessThan(64), $"allocated={allocated}");
        }

        [Test]
        public void SeedRateCountPerAreaConvertsWithoutForcingSeeds()
        {
            var converter = Extensions.BuildValueConverter("vrSeedRateSeedsActual", "count1[m2]-1", "count1ha-1");
            Assert.That(converter(1d), Is.EqualTo(10000d).Within(1e-6));
        }

        [Test]
        public void SeedRateInSeedsRemapsOntoSeedsPerHectare()
        {
            var perSquareMeter = Extensions.BuildValueConverter("vrSeedRateSeedsActual", "seeds1[m2]-1", "count1ha-1");
            Assert.That(perSquareMeter(1d), Is.EqualTo(10000d).Within(1e-6));

            var perAcre = Extensions.BuildValueConverter("vrSeedRateSeedsActual", "seeds1ac-1", "count1ha-1");
            Assert.That(perAcre(1d), Is.EqualTo(1d / 0.40468564224d).Within(1e-9));
        }

        [Test]
        public void MeterValueInADifferentUnitUsesThatUnit()
        {
            var column = new ADAPTDataColumn(
                new NumericWorkingData
                {
                    Representation = new NumericRepresentation { Code = "vrHeading" },
                    UnitOfMeasure = new UnitOfMeasure { Code = "ft" }
                },
                "Heading",
                "m");
            column.InitializeConverter();

            var meterValue = new NumericRepresentationValue
            {
                Representation = new NumericRepresentation { Code = "vrHeading" },
                Value = new NumericValue(new UnitOfMeasure { Code = "m" }, 2d)
            };

            Assert.That(column.ConvertMeterValue(meterValue), Is.EqualTo(2d).Within(1e-9));
        }
    }

    public class WorkRecordTests
    {
        private static string CreateCleanFolder(string name)
        {
            var folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, name);
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }

            Directory.CreateDirectory(folder);
            return folder;
        }

        [Test]
        public void StraightPassExportProducesDeterministicParquet()
        {
            var adm = WorkRecordTestData.CreateStraightPassApplicationDataModel();
            var properties = new Properties();
            properties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            properties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            properties.SetProperty("ParallelThreadCount", "1");

            var firstFolder = CreateCleanFolder("workrecord-first");
            var secondFolder = CreateCleanFolder("workrecord-second");

            var plugin = new Plugin();
            plugin.Export(adm, firstFolder, properties);
            plugin.Export(adm, secondFolder, properties);

            var parquetFiles = Directory.GetFiles(firstFolder, "*.parquet");
            Assert.That(parquetFiles, Has.Length.EqualTo(1));
            Assert.That(ReadGeometryTypes(parquetFiles[0]), Is.EqualTo(new[] { "Polygon" }));
            Assert.That(Directory.GetFiles(secondFolder, "*.parquet"), Has.Length.EqualTo(1));

            var adaptJson = File.ReadAllText(Path.Combine(firstFolder, "adapt.json"));
            Assert.That(adaptJson, Does.Contain("spatialRecordsFile"));
            Assert.That(adaptJson, Does.Contain("AppliedMassPerAreaActual"));

            var firstBytes = File.ReadAllBytes(parquetFiles[0]);
            var secondBytes = File.ReadAllBytes(Directory.GetFiles(secondFolder, "*.parquet")[0]);
            Assert.That(secondBytes, Is.EqualTo(firstBytes));

            using (var reader = new ParquetFileReader(parquetFiles[0]))
            {
                Assert.That(reader.FileMetaData.NumRows, Is.EqualTo(25));
                Assert.That(reader.FileMetaData.NumRowGroups, Is.GreaterThan(0));
            }
        }

        [Test]
        public void MultiSectionExportProducesExpectedRowCount()
        {
            const int sectionCount = 8;
            const int recordCount = 10;
            var adm = WorkRecordTestData.CreateMultiSectionApplicationDataModel(sectionCount, recordCount);
            var properties = new Properties();
            properties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            properties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            properties.SetProperty("ParallelThreadCount", "1");

            var exportFolder = CreateCleanFolder("workrecord-multisection");
            var plugin = new Plugin();
            plugin.Export(adm, exportFolder, properties);

            var parquetFiles = Directory.GetFiles(exportFolder, "*.parquet");
            Assert.That(parquetFiles, Has.Length.EqualTo(1));

            using (var reader = new ParquetFileReader(parquetFiles[0]))
            {
                Assert.That(reader.FileMetaData.NumRows, Is.EqualTo(sectionCount * recordCount));
            }
        }

        [Test]
        public void StraightPassExportMatchesAcrossParallelismSettings()
        {
            var adm = WorkRecordTestData.CreateStraightPassApplicationDataModel();
            var serialFolder = CreateCleanFolder("workrecord-serial");
            var parallelFolder = CreateCleanFolder("workrecord-parallel");

            var serialProperties = new Properties();
            serialProperties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            serialProperties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            serialProperties.SetProperty("ParallelThreadCount", "1");

            var parallelProperties = new Properties();
            parallelProperties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            parallelProperties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            parallelProperties.SetProperty("Multithread", "true");
            parallelProperties.SetProperty("ParallelThreadCount", Environment.ProcessorCount.ToString());

            var plugin = new Plugin();
            plugin.Export(adm, serialFolder, serialProperties);
            plugin.Export(adm, parallelFolder, parallelProperties);

            var serialParquet = File.ReadAllBytes(Directory.GetFiles(serialFolder, "*.parquet").Single());
            var parallelParquet = File.ReadAllBytes(Directory.GetFiles(parallelFolder, "*.parquet").Single());
            Assert.That(parallelParquet, Is.EqualTo(serialParquet));
        }

        [Test]
        public void ParallelExportOfDistinctOperationsMatchesSerialAndReleasesAfterReadersFinish()
        {
            var adm = WorkRecordTestData.CreateUnmergedOperationsApplicationDataModel(2, recordCount: 4);
            var loggedData = ((List<LoggedData>)adm.Documents.LoggedData).First();
            var probe = new ReaderProbe();
            foreach (var operation in (List<OperationData>)loggedData.OperationData)
            {
                var records = operation.GetSpatialRecords().ToList();
                operation.GetSpatialRecords = () => probe.Enumerate(records);
            }

            loggedData.ReleaseSpatialData = () => probe.OnRelease();

            var serialFolder = CreateCleanFolder("workrecord-parallel-proof-serial");
            var parallelFolder = CreateCleanFolder("workrecord-parallel-proof-parallel");
            var serialProperties = new Properties();
            serialProperties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            serialProperties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            serialProperties.SetProperty("Multithread", "false");

            var parallelProperties = new Properties();
            parallelProperties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            parallelProperties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            parallelProperties.SetProperty("Multithread", "true");
            parallelProperties.SetProperty("ParallelThreadCount", "2");

            var plugin = new Plugin();
            plugin.Export(adm, serialFolder, serialProperties);
            Assert.That(probe.MaxActive, Is.EqualTo(1));
            Assert.That(probe.ReleaseCount, Is.EqualTo(1));
            Assert.That(probe.ReleasedWhileActive, Is.False);

            probe.Reset();
            plugin.Export(adm, parallelFolder, parallelProperties);
            Assert.That(probe.MaxActive, Is.GreaterThan(1));
            Assert.That(probe.ReleaseCount, Is.EqualTo(1));
            Assert.That(probe.ReleasedWhileActive, Is.False);

            var serialOperations = ReadWorkRecordOperations(serialFolder);
            var parallelOperations = ReadWorkRecordOperations(parallelFolder);
            Assert.That(parallelOperations.Select(operation => operation.ReferenceId), Is.EqualTo(serialOperations.Select(operation => operation.ReferenceId)));
            Assert.That(parallelOperations.Select(operation => operation.SpatialFile), Is.EqualTo(serialOperations.Select(operation => operation.SpatialFile)));
            Assert.That(serialOperations.Select(operation => operation.SpatialFile), Is.Unique);
            foreach (var operation in serialOperations)
            {
                byte[] serialBytes = File.ReadAllBytes(Path.Combine(serialFolder, operation.SpatialFile));
                byte[] parallelBytes = File.ReadAllBytes(Path.Combine(parallelFolder, operation.SpatialFile));
                Assert.That(parallelBytes, Is.EqualTo(serialBytes));
                using (var reader = new ParquetFileReader(new MemoryStream(serialBytes)))
                {
                    Assert.That(reader.FileMetaData.NumRows, Is.EqualTo(4));
                }
            }
        }

        private sealed class ReaderProbe
        {
            private readonly object _gate = new object();

            internal int MaxActive { get; private set; }
            internal int ReleaseCount { get; private set; }
            internal bool ReleasedWhileActive { get; private set; }

            internal void Reset()
            {
                lock (_gate)
                {
                    MaxActive = 0;
                    ReleaseCount = 0;
                    ReleasedWhileActive = false;
                }
            }

            internal void OnRelease()
            {
                lock (_gate)
                {
                    if (_active != 0)
                    {
                        ReleasedWhileActive = true;
                    }

                    ReleaseCount++;
                }
            }

            private int _active;

            internal IEnumerable<SpatialRecord> Enumerate(List<SpatialRecord> records)
            {
                for (int index = 0; index < records.Count; index++)
                {
                    if (index == 1)
                    {
                        lock (_gate)
                        {
                            _active++;
                            if (_active > MaxActive)
                            {
                                MaxActive = _active;
                            }
                        }

                        Thread.Sleep(300);
                        lock (_gate)
                        {
                            _active--;
                        }
                    }

                    yield return records[index];
                }
            }
        }

        [Test]
        public void SectionsUseTheirOwnHeadingAndDistance()
        {
            var adm = WorkRecordTestData.CreateSectionMotionApplicationDataModel(
                sectionHeadings: new double?[] { 0d, 90d },
                sectionDistances: new double?[] { 1d, 3d });
            var geometries = ExportFirstOperationGeometries(adm, "workrecord-section-motion");
            Assert.That(geometries.Count, Is.GreaterThanOrEqualTo(2));
            AssertPolygonOrientation(geometries[0], expectedBearingDeg: 0d, expectedLengthM: 1d);
            AssertPolygonOrientation(geometries[1], expectedBearingDeg: 90d, expectedLengthM: 3d);
        }

        [Test]
        public void SectionWithoutHeadingOrDistanceDoesNotCopyTheOtherSection()
        {
            var adm = WorkRecordTestData.CreateSectionMotionApplicationDataModel(
                sectionHeadings: new double?[] { 90d, null },
                sectionDistances: new double?[] { 3d, null });
            var geometries = ExportFirstOperationGeometries(adm, "workrecord-section-missing");
            Assert.That(geometries.Count, Is.GreaterThanOrEqualTo(2));
            AssertPolygonOrientation(geometries[0], expectedBearingDeg: 90d, expectedLengthM: 3d);
            AssertPolygonOrientation(geometries[1], expectedBearingDeg: 0d, expectedLengthM: 1d);
        }

        [Test]
        public void SectionsInheritHeadingFromTheImplement()
        {
            var adm = WorkRecordTestData.CreateSectionMotionApplicationDataModel(
                sectionHeadings: new double?[] { null, null },
                sectionDistances: new double?[] { null, null },
                inheritedHeading: 90d);
            var geometries = ExportFirstOperationGeometries(adm, "workrecord-section-inherited");
            Assert.That(geometries.Count, Is.GreaterThanOrEqualTo(2));
            AssertPolygonOrientation(geometries[0], expectedBearingDeg: 90d, expectedLengthM: 1d);
            AssertPolygonOrientation(geometries[1], expectedBearingDeg: 90d, expectedLengthM: 1d);
        }

        private static List<byte[]> ExportFirstOperationGeometries(ApplicationDataModel adm, string folderName)
        {
            var properties = new Properties();
            properties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            properties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            var folder = CreateCleanFolder(folderName);
            new Plugin().Export(adm, folder, properties);
            string parquetPath = Directory.GetFiles(folder, "*.parquet").Single();
            var geometries = new List<byte[]>();
            using (var reader = new ParquetFileReader(parquetPath))
            {
                int geometryColumn = checked((int)reader.FileMetaData.NumColumns - 1);
                for (int groupIndex = 0; groupIndex < reader.FileMetaData.NumRowGroups; groupIndex++)
                {
                    using (var rowGroup = reader.RowGroup(groupIndex))
                    {
                        int groupRowCount = checked((int)rowGroup.MetaData.NumRows);
                        using (var column = rowGroup.Column(geometryColumn))
                        {
                            geometries.AddRange(column.LogicalReader<byte[]>().ReadAll(groupRowCount));
                        }
                    }
                }
            }

            return geometries;
        }

        private static void AssertPolygonOrientation(byte[] wkb, double expectedBearingDeg, double expectedLengthM)
        {
            double leftLon = BitConverter.ToDouble(wkb, 13);
            double leftLat = BitConverter.ToDouble(wkb, 21);
            double rightLon = BitConverter.ToDouble(wkb, 29);
            double rightLat = BitConverter.ToDouble(wkb, 37);
            double backRightLon = BitConverter.ToDouble(wkb, 45);
            double backRightLat = BitConverter.ToDouble(wkb, 53);
            double backLeftLon = BitConverter.ToDouble(wkb, 61);
            double backLeftLat = BitConverter.ToDouble(wkb, 69);
            double leadingLon = (leftLon + rightLon) / 2d;
            double leadingLat = (leftLat + rightLat) / 2d;
            double backLon = (backRightLon + backLeftLon) / 2d;
            double backLat = (backRightLat + backLeftLat) / 2d;
            double bearing = GeometryExporter.HaversineBearing(backLon, backLat, leadingLon, leadingLat);
            double bearingDelta = bearing - expectedBearingDeg;
            while (bearingDelta > 180d)
            {
                bearingDelta -= 360d;
            }

            while (bearingDelta < -180d)
            {
                bearingDelta += 360d;
            }

            Assert.That(Math.Abs(bearingDelta), Is.LessThan(2d), $"bearing={bearing}");
            double length = GeometryExporter.HaversineDistance(backLon, backLat, leadingLon, leadingLat);
            Assert.That(length, Is.EqualTo(expectedLengthM).Within(0.05d), $"length={length}");
        }

        [Test]
        public void ParallelExportReservesAUniqueFilePerOperationWhenOutputAlreadyExists()
        {
            const int operationCount = 8;
            const string priorFileName = "Fertilizing_Test Product.parquet";
            var adm = WorkRecordTestData.CreateUnmergedOperationsApplicationDataModel(operationCount);
            var serialFolder = CreateCleanFolder("workrecord-reserve-serial");
            var parallelFolder = CreateCleanFolder("workrecord-reserve-parallel");
            File.WriteAllText(Path.Combine(serialFolder, priorFileName), "keep");
            File.WriteAllText(Path.Combine(parallelFolder, priorFileName), "keep");

            var serialProperties = new Properties();
            serialProperties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            serialProperties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());

            var parallelProperties = new Properties();
            parallelProperties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            parallelProperties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            parallelProperties.SetProperty("Multithread", "true");
            parallelProperties.SetProperty("ParallelThreadCount", Math.Max(operationCount, Environment.ProcessorCount).ToString());

            var plugin = new Plugin();
            plugin.Export(adm, serialFolder, serialProperties);
            plugin.Export(adm, parallelFolder, parallelProperties);

            Assert.That(File.ReadAllText(Path.Combine(serialFolder, priorFileName)), Is.EqualTo("keep"));
            Assert.That(File.ReadAllText(Path.Combine(parallelFolder, priorFileName)), Is.EqualTo("keep"));
            Assert.That(Directory.GetFiles(serialFolder, "*.parquet"), Has.Length.EqualTo(operationCount + 1));
            Assert.That(Directory.GetFiles(parallelFolder, "*.parquet"), Has.Length.EqualTo(operationCount + 1));

            var serialOperations = ReadWorkRecordOperations(serialFolder);
            var parallelOperations = ReadWorkRecordOperations(parallelFolder);
            Assert.That(serialOperations, Has.Count.EqualTo(operationCount));
            Assert.That(serialOperations.Select(operation => operation.ReferenceId), Is.Unique);
            Assert.That(serialOperations.Select(operation => operation.SpatialFile), Is.Unique);
            Assert.That(serialOperations.Select(operation => operation.SpatialFile), Does.Not.Contain(priorFileName));
            Assert.That(parallelOperations.Select(operation => operation.ReferenceId), Is.EqualTo(serialOperations.Select(operation => operation.ReferenceId)));
            Assert.That(parallelOperations.Select(operation => operation.SpatialFile), Is.EqualTo(serialOperations.Select(operation => operation.SpatialFile)));

            var rates = new List<double>();
            foreach (var operation in serialOperations)
            {
                byte[] serialBytes = File.ReadAllBytes(Path.Combine(serialFolder, operation.SpatialFile));
                byte[] parallelBytes = File.ReadAllBytes(Path.Combine(parallelFolder, operation.SpatialFile));
                Assert.That(parallelBytes, Is.EqualTo(serialBytes));
                rates.Add(ReadFirstAppliedRate(serialBytes));
            }

            Assert.That(rates, Is.Unique);
        }

        [Test]
        public void RejectedZeroFixDoesNotBecomeThePreviousPosition()
        {
            var adm = WorkRecordTestData.CreateStraightPassApplicationDataModel(recordCount: 3);
            var loggedData = ((List<LoggedData>)adm.Documents.LoggedData).First();
            var operation = ((List<OperationData>)loggedData.OperationData).First();
            var records = operation.GetSpatialRecords().ToList();
            records[1].Geometry = new AgGateway.ADAPT.ApplicationDataModel.Shapes.Point { X = 0d, Y = 0d };

            var geometries = ExportFirstOperationGeometries(adm, "workrecord-rejected-fix");
            Assert.That(geometries.Count, Is.EqualTo(2));
            AssertPolygonOrientation(geometries[1], expectedBearingDeg: 0d, expectedLengthM: 1d);
        }

        [Test]
        public void SummaryOnlyOperationKeepsItsIdAndOmitsTheSpatialFile()
        {
            var adm = WorkRecordTestData.CreateStraightPassApplicationDataModel(recordCount: 2);
            var loggedData = ((List<LoggedData>)adm.Documents.LoggedData).First();
            var operation = ((List<OperationData>)loggedData.OperationData).First();
            foreach (var record in operation.GetSpatialRecords())
            {
                record.Geometry = new AgGateway.ADAPT.ApplicationDataModel.Shapes.Point { X = 0d, Y = 0d };
            }

            int productId = operation.ProductIds.First();
            adm.Documents.Summaries = new List<Summary>
            {
                new Summary
                {
                    LoggedDataIds = new List<int> { loggedData.Id.ReferenceId },
                    OperationSummaries = new List<OperationSummary>
                    {
                        new OperationSummary
                        {
                            OperationType = operation.OperationType,
                            ProductId = productId,
                            Data = new List<StampedMeteredValues>
                            {
                                new StampedMeteredValues
                                {
                                    Values = new List<MeteredValue>()
                                }
                            }
                        }
                    }
                }
            };

            var folder = CreateCleanFolder("workrecord-summary-only");
            var properties = new Properties();
            properties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            properties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            new Plugin().Export(adm, folder, properties);

            Assert.That(Directory.GetFiles(folder, "*.parquet"), Is.Empty);
            var json = File.ReadAllText(Path.Combine(folder, "adapt.json")).TrimStart('\uFEFF');
            using (var document = JsonDocument.Parse(json))
            {
                var exported = document.RootElement.GetProperty("documents").GetProperty("workRecords")[0].GetProperty("operations")[0];
                Assert.That(exported.GetProperty("id").GetProperty("referenceId").GetString(), Is.Not.Null.And.Not.Empty);
                Assert.That(exported.TryGetProperty("spatialRecordsFile", out _), Is.False);
            }
        }

        [Test]
        public void PlantingAndFertilizingWithin36HoursShareAWorkRecord()
        {
            var start = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.SowingAndPlanting, start, start.AddHours(4), "Plant"),
                (OperationTypeEnum.Fertilizing, start.AddHours(1), start.AddHours(5), "Fertilize"));

            var records = ExportWorkRecordOperationTypes(model, "workrecord-plant-fert");

            Assert.That(records, Has.Count.EqualTo(1));
            Assert.That(records[0], Is.EquivalentTo(new[]
            {
                "APPLICATION_SOWING_AND_PLANTING_SEEDS",
                "APPLICATION_FERTILIZING"
            }));
        }

        [Test]
        public void HarvestAndTillageWithin36HoursStaySeparate()
        {
            var start = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.Harvesting, start, start.AddHours(4), "Harvest"),
                (OperationTypeEnum.Tillage, start.AddHours(2), start.AddHours(6), "Till"));

            var records = ExportWorkRecordOperationTypes(model, "workrecord-harvest-till");

            Assert.That(records, Has.Count.EqualTo(2));
            Assert.That(records.SelectMany(record => record), Is.EquivalentTo(new[]
            {
                "HARVEST",
                "FIELD_PREPARATION_TILLAGE"
            }));
        }

        [Test]
        public void ApplicationSplitsWhenTheGapReaches36Hours()
        {
            var start = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
            var end = start.AddHours(4);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.Fertilizing, start, end, "First"),
                (OperationTypeEnum.Fertilizing, end.AddHours(36), end.AddHours(40), "Second"));

            var records = ExportWorkRecordOperationTypes(model, "workrecord-gap-36");

            Assert.That(records, Has.Count.EqualTo(2));
            Assert.That(records.Select(record => record.Single()), Is.All.EqualTo("APPLICATION_FERTILIZING"));
        }

        [Test]
        public void HarvestChainsAcrossDaysInside36Hours()
        {
            var first = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
            var second = new DateTime(2024, 6, 2, 8, 0, 0, DateTimeKind.Utc);
            var third = new DateTime(2024, 6, 3, 8, 0, 0, DateTimeKind.Utc);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.Harvesting, first, first.AddHours(10), "Day1"),
                (OperationTypeEnum.Harvesting, second, second.AddHours(10), "Day2"),
                (OperationTypeEnum.Harvesting, third, third.AddHours(4), "Day3"));

            var folder = CreateCleanFolder("workrecord-harvest-chain");
            ExportModel(model, folder);
            var records = ReadWorkRecordOperationTypes(folder);
            var name = ReadWorkRecordNames(folder).Single();

            Assert.That(records, Has.Count.EqualTo(1));
            Assert.That(name, Does.Contain("Day1").And.Contain("Day2").And.Contain("Day3"));
        }

        [Test]
        public void MixedLoggedDataSplitsHarvestFromPlanting()
        {
            var start = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.Harvesting, start, start.AddHours(4), "Mixed"),
                (OperationTypeEnum.SowingAndPlanting, start, start.AddHours(4), "PlantOnSameData"));
            var loggedDatas = (List<LoggedData>)model.Documents.LoggedData;
            var operations = loggedDatas.SelectMany(loggedData => loggedData.OperationData).ToList();
            loggedDatas[0].OperationData = operations;
            loggedDatas.RemoveAt(1);

            var records = ExportWorkRecordOperationTypes(model, "workrecord-mixed-logged-data");

            Assert.That(records, Has.Count.EqualTo(2));
            Assert.That(records.SelectMany(record => record), Is.EquivalentTo(new[]
            {
                "HARVEST",
                "APPLICATION_SOWING_AND_PLANTING_SEEDS"
            }));
        }

        [Test]
        public void DifferentCropZonesStayOnSeparateWorkRecords()
        {
            var start = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.SowingAndPlanting, start, start.AddHours(4), "Soy"),
                (OperationTypeEnum.Fertilizing, start, start.AddHours(4), "Corn"));
            var loggedDatas = (List<LoggedData>)model.Documents.LoggedData;
            WorkRecordTestData.AssignCropZone(model, loggedDatas[0], cropId: 11);
            WorkRecordTestData.AssignCropZone(model, loggedDatas[1], cropId: 22);

            var records = ExportWorkRecordOperationTypes(model, "workrecord-crop-zones");

            Assert.That(records, Has.Count.EqualTo(2));
        }

        [Test]
        public void DifferentProductCropsStayOnSeparateWorkRecords()
        {
            var start = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.SowingAndPlanting, start, start.AddHours(4), "SoySeed"),
                (OperationTypeEnum.SowingAndPlanting, start, start.AddHours(4), "CornSeed"));
            var loggedDatas = (List<LoggedData>)model.Documents.LoggedData;
            WorkRecordTestData.AssignVarietyCrop(model, loggedDatas[0], cropId: 11);
            WorkRecordTestData.AssignVarietyCrop(model, loggedDatas[1], cropId: 22);

            var records = ExportWorkRecordOperationTypes(model, "workrecord-product-crops");

            Assert.That(records, Has.Count.EqualTo(2));
        }

        [Test]
        public void SpatialFirstPointsWithin36HoursShareAWorkRecord()
        {
            var first = new DateTime(2015, 5, 2, 21, 23, 10, DateTimeKind.Utc);
            var second = new DateTime(2015, 5, 2, 22, 21, 41, DateTimeKind.Utc);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.SowingAndPlanting, first, first.AddHours(1), "FirstVariety"),
                (OperationTypeEnum.SowingAndPlanting, second, second.AddHours(2), "SecondVariety"));
            foreach (var loggedData in (List<LoggedData>)model.Documents.LoggedData)
            {
                loggedData.TimeScopes.Clear();
            }

            var records = ExportWorkRecordOperationTypes(model, "workrecord-spatial-near");

            Assert.That(records, Has.Count.EqualTo(1));
        }

        [Test]
        public void SpatialFirstPointsBeyond36HoursStaySeparate()
        {
            var first = new DateTime(2015, 5, 2, 21, 0, 0, DateTimeKind.Utc);
            var second = first.AddHours(40);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.SowingAndPlanting, first, first.AddHours(1), "Early"),
                (OperationTypeEnum.SowingAndPlanting, second, second.AddHours(1), "Late"));
            foreach (var loggedData in (List<LoggedData>)model.Documents.LoggedData)
            {
                loggedData.TimeScopes.Clear();
            }

            var records = ExportWorkRecordOperationTypes(model, "workrecord-spatial-far");

            Assert.That(records, Has.Count.EqualTo(2));
        }

        [Test]
        public void FertilizerWithoutACropJoinsOneNearbyPlanting()
        {
            var start = new DateTime(2024, 6, 1, 8, 0, 0, DateTimeKind.Utc);
            var model = WorkRecordTestData.CreateTimedField(
                (OperationTypeEnum.SowingAndPlanting, start, start.AddHours(4), "SoyPlant"),
                (OperationTypeEnum.Fertilizing, start.AddHours(1), start.AddHours(5), "Fertilizer"));
            WorkRecordTestData.AssignVarietyCrop(model, ((List<LoggedData>)model.Documents.LoggedData)[0], cropId: 11);

            var records = ExportWorkRecordOperationTypes(model, "workrecord-fert-joins-plant");

            Assert.That(records, Has.Count.EqualTo(1));
            Assert.That(records[0], Is.EquivalentTo(new[]
            {
                "APPLICATION_SOWING_AND_PLANTING_SEEDS",
                "APPLICATION_FERTILIZING"
            }));
        }

        private static List<List<string>> ExportWorkRecordOperationTypes(ApplicationDataModel adm, string folderName)
        {
            var folder = CreateCleanFolder(folderName);
            ExportModel(adm, folder);
            return ReadWorkRecordOperationTypes(folder);
        }

        private static void ExportModel(ApplicationDataModel adm, string folder)
        {
            var properties = new Properties();
            properties.SetProperty("SourceGeometryPosition", SourceGeometryPosition.ImplementReferencePoint.ToString());
            properties.SetProperty("SourceDeviceDefinition", SourceDeviceDefinition.Machine0Implement1Section2.ToString());
            properties.SetProperty("ParallelThreadCount", "1");
            new Plugin().Export(adm, folder, properties);
        }

        private static List<List<string>> ReadWorkRecordOperationTypes(string folder)
        {
            using (var document = JsonDocument.Parse(ReadAdaptJson(folder)))
            {
                var records = document.RootElement.GetProperty("documents").GetProperty("workRecords");
                var results = new List<List<string>>();
                foreach (var record in records.EnumerateArray())
                {
                    var types = new List<string>();
                    foreach (var operation in record.GetProperty("operations").EnumerateArray())
                    {
                        types.Add(operation.GetProperty("operationTypeCode").GetString());
                    }

                    results.Add(types);
                }

                return results;
            }
        }

        private static List<string> ReadWorkRecordNames(string folder)
        {
            using (var document = JsonDocument.Parse(ReadAdaptJson(folder)))
            {
                return document.RootElement.GetProperty("documents").GetProperty("workRecords")
                    .EnumerateArray()
                    .Select(record => record.GetProperty("name").GetString())
                    .ToList();
            }
        }

        private static string ReadAdaptJson(string folder)
        {
            return File.ReadAllText(Path.Combine(folder, "adapt.json")).TrimStart('\uFEFF');
        }

        private static List<(string ReferenceId, string SpatialFile)> ReadWorkRecordOperations(string folder)
        {
            var json = File.ReadAllText(Path.Combine(folder, "adapt.json")).TrimStart('\uFEFF');
            using (var document = JsonDocument.Parse(json))
            {
                var operations = document.RootElement.GetProperty("documents").GetProperty("workRecords")[0].GetProperty("operations");
                var results = new List<(string ReferenceId, string SpatialFile)>();
                foreach (var operation in operations.EnumerateArray())
                {
                    results.Add((
                        operation.GetProperty("id").GetProperty("referenceId").GetString(),
                        operation.GetProperty("spatialRecordsFile").GetString()));
                }

                return results;
            }
        }

        private static string[] ReadGeometryTypes(string parquetPath)
        {
            using (var reader = new ParquetFileReader(parquetPath))
            {
                using (var document = JsonDocument.Parse(reader.FileMetaData.KeyValueMetadata["geo"]))
                {
                    return document.RootElement
                        .GetProperty("columns")
                        .GetProperty("geometry")
                        .GetProperty("geometry_types")
                        .EnumerateArray()
                        .Select(item => item.GetString())
                        .ToArray();
                }
            }
        }

        private static double ReadFirstAppliedRate(byte[] parquetBytes)
        {
            string path = Path.Combine(Path.GetTempPath(), "adapt-rate-" + Guid.NewGuid().ToString("N") + ".parquet");
            File.WriteAllBytes(path, parquetBytes);
            try
            {
                using (var reader = new ParquetFileReader(path))
                using (var rowGroup = reader.RowGroup(0))
                using (var column = rowGroup.Column(1))
                {
                    return column.LogicalReader<double?>().ReadAll(1)[0].Value;
                }
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    public class SeasonExportTests
    {
        [Test]
        public void SeasonWithoutANameUsesTheYear()
        {
            var exporters = new CommonExporters(new AgGateway.ADAPT.Standard.Root { Catalog = new AgGateway.ADAPT.Standard.Catalog() });
            var season = exporters.GetOrAddSeason(new TimeScope
            {
                DateContext = DateContextEnum.CropSeason,
                Description = "Spring planting",
                TimeStamp1 = new DateTime(2022, 9, 1, 0, 0, 0, DateTimeKind.Utc),
                TimeStamp2 = new DateTime(2023, 11, 1, 0, 0, 0, DateTimeKind.Utc)
            });

            Assert.That(season.Name, Is.EqualTo("2022"));
            Assert.That(season.Description, Is.EqualTo("Spring planting"));
        }

        [Test]
        public void SeasonWithoutADescriptionUsesTheYearForNameAndDescription()
        {
            var exporters = new CommonExporters(new AgGateway.ADAPT.Standard.Root { Catalog = new AgGateway.ADAPT.Standard.Catalog() });
            var season = exporters.GetOrAddSeason(new TimeScope
            {
                DateContext = DateContextEnum.CropSeason,
                TimeStamp2 = new DateTime(2015, 5, 2, 0, 0, 0, DateTimeKind.Utc)
            });

            Assert.That(season.Name, Is.EqualTo("2015"));
            Assert.That(season.Description, Is.EqualTo("2015"));
        }
    }
}

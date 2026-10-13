using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Common;
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
}

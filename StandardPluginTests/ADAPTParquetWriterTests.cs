using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using AgGateway.ADAPT.StandardPlugin;
using NUnit.Framework;
using ParquetSharp;

namespace StandardPluginTests
{
    public class ADAPTParquetWriterTests
    {
        [Test]
        public void BufferedRowsStayWithinOneRowGroup()
        {
            const int rowGroupSize = 4;
            const int rowCount = 10;
            string path = Path.Combine(Path.GetTempPath(), "adapt-parquet-row-groups-" + Guid.NewGuid().ToString("N") + ".parquet");
            var columnData = CreateColumnData();
            var timestamps = new string[rowCount];
            var appliedRates = new double?[rowCount];
            var elevations = new double?[rowCount];
            var geometries = new byte[rowCount][];

            try
            {
                using (var writer = new ADAPTParquetWriter(columnData, includeTimestamps: true, openFile: () => path, rowGroupSize: rowGroupSize))
                {
                    for (int rowIndex = 0; rowIndex < rowCount; rowIndex++)
                    {
                        var timestamp = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(rowIndex);
                        timestamps[rowIndex] = timestamp.ToString("O", CultureInfo.InvariantCulture);
                        appliedRates[rowIndex] = rowIndex + 0.25d;
                        elevations[rowIndex] = rowIndex + 10d;
                        geometries[rowIndex] = new[] { (byte)rowIndex };
                        writer.AddRow(timestamp, new[] { appliedRates[rowIndex], elevations[rowIndex] }, geometries[rowIndex], rowIndex, 0d, rowIndex + 1d, 100d);

                        Assert.That(writer.BufferedRowCount, Is.LessThanOrEqualTo(rowGroupSize));
                        if (rowIndex == 3)
                        {
                            Assert.That(writer.BufferedRowCount, Is.EqualTo(0));
                            Assert.That(writer.WrittenRowCount, Is.EqualTo(4));
                        }

                        if (rowIndex == 7)
                        {
                            Assert.That(writer.BufferedRowCount, Is.EqualTo(0));
                            Assert.That(writer.WrittenRowCount, Is.EqualTo(8));
                        }
                    }

                    writer.Complete();
                    Assert.That(writer.BufferedRowCount, Is.EqualTo(0));
                    Assert.That(writer.WrittenRowCount, Is.EqualTo(rowCount));
                    Assert.That(writer.NativeWriterDisposed, Is.True);
                }

                Assert.That(columnData.RowCount, Is.EqualTo(rowCount));
                AssertWrittenFile(path, timestamps, appliedRates, elevations, geometries);
            }
            finally
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void CompleteWithNoRowsDoesNotCreateAFile()
        {
            string path = Path.Combine(Path.GetTempPath(), "adapt-parquet-empty-" + Guid.NewGuid().ToString("N") + ".parquet");
            bool opened = false;
            using (var writer = new ADAPTParquetWriter(CreateColumnData(), includeTimestamps: true, openFile: () =>
            {
                opened = true;
                return path;
            }, rowGroupSize: 4))
            {
                writer.Complete();
                Assert.That(writer.BufferedRowCount, Is.EqualTo(0));
                Assert.That(writer.WrittenRowCount, Is.EqualTo(0));
            }

            Assert.That(opened, Is.False);
            Assert.That(File.Exists(path), Is.False);
        }

        [Test]
        public void CompleteIsIdempotentAndReleasesTheNativeWriter()
        {
            string path = Path.Combine(Path.GetTempPath(), "adapt-parquet-complete-" + Guid.NewGuid().ToString("N") + ".parquet");
            var writer = new ADAPTParquetWriter(CreateColumnData(), includeTimestamps: true, openFile: () => path, rowGroupSize: 4);
            try
            {
                writer.AddRow(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), new double?[] { 1d, 2d }, new byte[] { 1 }, 0d, 0d, 1d, 1d);
                writer.Complete();
                writer.Complete();
                writer.Dispose();
                writer.Dispose();

                Assert.That(writer.NativeWriterDisposed, Is.True);
                Assert.That(File.Exists(path), Is.True);
                using (var reader = new ParquetFileReader(path))
                {
                    Assert.That(reader.FileMetaData.NumRows, Is.EqualTo(1));
                }
            }
            finally
            {
                writer.Dispose();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        [Test]
        public void DisposeWithoutCompleteDeletesAFlushedFile()
        {
            const int rowGroupSize = 2;
            string path = Path.Combine(Path.GetTempPath(), "adapt-parquet-abort-" + Guid.NewGuid().ToString("N") + ".parquet");
            var writer = new ADAPTParquetWriter(CreateColumnData(), includeTimestamps: true, openFile: () => path, rowGroupSize: rowGroupSize);
            try
            {
                for (int rowIndex = 0; rowIndex < rowGroupSize; rowIndex++)
                {
                    writer.AddRow(
                        new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(rowIndex),
                        new[] { (double?)rowIndex, rowIndex + 0.5d },
                        new[] { (byte)rowIndex },
                        rowIndex,
                        0d,
                        rowIndex + 1d,
                        1d);
                }

                Assert.That(File.Exists(path), Is.True);
                writer.AddRow(new DateTime(2020, 1, 1, 0, 0, 2, DateTimeKind.Utc), new double?[] { 9d, 8d }, new byte[] { 9 }, 2d, 0d, 3d, 1d);
                writer.Dispose();
                writer.Dispose();

                Assert.That(writer.NativeWriterDisposed, Is.True);
                Assert.That(writer.BufferedRowCount, Is.EqualTo(1));
                Assert.That(File.Exists(path), Is.False);
            }
            finally
            {
                writer.Dispose();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
        }

        private static void AssertWrittenFile(string path, string[] timestamps, double?[] appliedRates, double?[] elevations, byte[][] geometries)
        {
            using (var reader = new ParquetFileReader(path))
            using (var metadata = reader.FileMetaData)
            {
                Assert.That(metadata.NumRowGroups, Is.EqualTo(3));
                Assert.That(metadata.NumRows, Is.EqualTo(timestamps.Length));

                var readTimestamps = new List<string>();
                var readAppliedRates = new List<double?>();
                var readElevations = new List<double?>();
                var readGeometries = new List<byte[]>();
                var rowGroupSizes = new List<long>();
                for (int groupIndex = 0; groupIndex < metadata.NumRowGroups; groupIndex++)
                {
                    using (var rowGroup = reader.RowGroup(groupIndex))
                    {
                        int groupRowCount = checked((int)rowGroup.MetaData.NumRows);
                        rowGroupSizes.Add(groupRowCount);
                        using (var timestampColumn = rowGroup.Column(0))
                        {
                            readTimestamps.AddRange(timestampColumn.LogicalReader<string>().ReadAll(groupRowCount));
                        }

                        using (var appliedRateColumn = rowGroup.Column(1))
                        {
                            readAppliedRates.AddRange(appliedRateColumn.LogicalReader<double?>().ReadAll(groupRowCount));
                        }

                        using (var elevationColumn = rowGroup.Column(2))
                        {
                            readElevations.AddRange(elevationColumn.LogicalReader<double?>().ReadAll(groupRowCount));
                        }

                        using (var geometryColumn = rowGroup.Column(3))
                        {
                            readGeometries.AddRange(geometryColumn.LogicalReader<byte[]>().ReadAll(groupRowCount));
                        }
                    }
                }

                Assert.That(rowGroupSizes, Is.EqualTo(new[] { 4L, 4L, 2L }));
                Assert.That(readTimestamps, Is.EqualTo(timestamps));
                Assert.That(readAppliedRates, Is.EqualTo(appliedRates));
                Assert.That(readElevations, Is.EqualTo(elevations));
                for (int rowIndex = 0; rowIndex < geometries.Length; rowIndex++)
                {
                    Assert.That(readGeometries[rowIndex], Is.EqualTo(geometries[rowIndex]));
                }

                double[] bbox = ReadGeometryBbox(metadata.KeyValueMetadata["geo"]);
                Assert.That(bbox, Is.EqualTo(new[] { 0d, 0d, 10d, 100d }));
            }
        }

        private static double[] ReadGeometryBbox(string geoJson)
        {
            using (var document = JsonDocument.Parse(geoJson))
            {
                var bbox = document.RootElement.GetProperty("columns").GetProperty("geometry").GetProperty("bbox");
                var values = new double[bbox.GetArrayLength()];
                int index = 0;
                foreach (var item in bbox.EnumerateArray())
                {
                    values[index++] = item.GetDouble();
                }

                return values;
            }
        }

        private static ADAPTParquetColumnData CreateColumnData()
        {
            var columnData = new ADAPTParquetColumnData();
            columnData.Columns.Add(new ADAPTDataColumn("AppliedRate"));
            columnData.Columns.Add(new ADAPTDataColumn("Elevation"));
            return columnData;
        }
    }
}

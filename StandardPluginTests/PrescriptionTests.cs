using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Common;
using AgGateway.ADAPT.ApplicationDataModel.Documents;
using AgGateway.ADAPT.ApplicationDataModel.Prescriptions;
using AgGateway.ADAPT.ApplicationDataModel.Products;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.ApplicationDataModel.Shapes;
using NUnit.Framework;

namespace StandardPluginTests
{
    public class PrescriptionTests
    {
        private ApplicationDataModel _adm;

        [SetUp]
        public void Setup()
        {
        }

        [Test]
        public void ManualRx()
        {
            SetupProducts();
            var field = new AgGateway.ADAPT.ApplicationDataModel.Logistics.Field() { Description = "Test Field" };
            _adm.Catalog.Fields = new List<AgGateway.ADAPT.ApplicationDataModel.Logistics.Field>()
            {
                field
            };

            ProductUse use = new ProductUse()
            {
                Rate = new NumericRepresentationValue() { Representation = new NumericRepresentation() { Code = "vrAppRateMassActual" }, Value = new NumericValue(new UnitOfMeasure() { Code = "lb1ac-1" }, 15d) },
                ProductId = _adm.Catalog.Products.First().Id.ReferenceId
            };
            ManualPrescription rx = new ManualPrescription();
            rx.ProductUses.Add(use);
            _adm.Catalog.Prescriptions = new List<ManualPrescription>() { rx };


            var plugin = new AgGateway.ADAPT.StandardPlugin.Plugin();
            var testFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "manual");
            plugin.Export(_adm, testFolder);

            var expected = File.ReadAllText("expected/manual/adapt.json");
            var current = File.ReadAllText(Path.Combine(testFolder, "adapt.json"));
            Assert.That(NormalizeReferenceIds(current), Is.EqualTo(NormalizeReferenceIds(expected)));

        }

        [Test]
        public void MultiProductVectorPrescription()
        {
            SetupProducts();

            var field = new AgGateway.ADAPT.ApplicationDataModel.Logistics.Field() { Description = "Test Field" };
            _adm.Catalog.Fields = new List<AgGateway.ADAPT.ApplicationDataModel.Logistics.Field>()
            {
                field
            };


            RxProductLookup p1 = new RxProductLookup()
            {
                ProductId = _adm.Catalog.Products.First().Id.ReferenceId,
                Representation = new NumericRepresentation() { Code = "vrSeedRateSeedsActual" },
                UnitOfMeasure = new UnitOfMeasure() { Code = "count1ac-1" }
            };
            RxProductLookup p4 = new RxProductLookup()
            {
                ProductId = _adm.Catalog.Products.Last().Id.ReferenceId,
                Representation = new NumericRepresentation() { Code = "vrAppRateVolumeActual" },
                UnitOfMeasure = new UnitOfMeasure() { Code = "gal1ac-1" }
            };
            VectorPrescription rx = new VectorPrescription() { FieldId = field.Id.ReferenceId, RxProductLookups = new List<RxProductLookup>() { p1, p4 } };

            Polygon one = new Polygon()
            {
                ExteriorRing = new LinearRing()
                {
                    Points = new List<Point>()
                    {
                        new Point { X = -87.60, Y = 41.88  },
                        new Point { X = -87.60, Y = 41.881  },
                        new Point { X = -87.599, Y = 41.881  },
                        new Point { X = -87.599, Y = 41.88  },
                        new Point { X = -87.60, Y = 41.88  },
                    }
                }
            };

            Polygon two = new Polygon()
            {
                ExteriorRing = new LinearRing()
                {
                    Points = new List<Point>()
                    {
                        new Point { X = -87.60, Y = 41.879  },
                        new Point { X = -87.60, Y = 41.88  },
                        new Point { X = -87.599, Y = 41.88  },
                        new Point { X = -87.599, Y = 41.879  },
                        new Point { X = -87.60, Y = 41.879  },
                    }
                }
            };

            rx.RxShapeLookups.Add(new RxShapeLookup()
            {
                Shape = new MultiPolygon() { Polygons = new List<Polygon>() { one } },
                Rates = new List<RxRate>()
                {
                    new RxRate(){ Rate = 33000, RxProductLookupId = p1.Id.ReferenceId},
                    new RxRate(){ Rate = 100, RxProductLookupId = p4.Id.ReferenceId},
                }
            });
            rx.RxShapeLookups.Add(new RxShapeLookup()
            {
                Shape = new MultiPolygon() { Polygons = new List<Polygon>() { two } },
                Rates = new List<RxRate>()
                {
                    new RxRate(){ Rate = 35500, RxProductLookupId = p1.Id.ReferenceId},
                    new RxRate(){ Rate = 75, RxProductLookupId = p4.Id.ReferenceId},
                }
            });

            WorkItemOperation op = new WorkItemOperation()
            {
                OperationType = OperationTypeEnum.SowingAndPlanting,
                PrescriptionId = rx.Id.ReferenceId
            };
            _adm.Documents.WorkItemOperations = new List<WorkItemOperation>() { op };


            WorkItem item = new WorkItem()
            {
                WorkItemOperationIds = new List<int>() { op.Id.ReferenceId },
                FieldId = field.Id.ReferenceId
            };
            _adm.Documents.WorkItems = new List<WorkItem>() { item };
            _adm.Catalog.Prescriptions = new List<VectorPrescription>() { rx };



            var plugin = new AgGateway.ADAPT.StandardPlugin.Plugin();
            var testFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "vector");
            plugin.Export(_adm, testFolder);

            var expected = File.ReadAllText("expected/vector/adapt.json");
            var current = File.ReadAllText(Path.Combine(testFolder, "adapt.json"));
            Assert.That(NormalizeReferenceIds(current), Is.EqualTo(NormalizeReferenceIds(expected)));
            AssertSpatialFileExists(testFolder, current);

        }

        [Test]
        public void MultiProductRasterPrescription()
        {
            SetupProducts();

            var rxPrescription = SetupRasterPrescription();
            for (int h = 0; h < rxPrescription.RowCount; h++)
            {
                for (int w = 0; w < rxPrescription.ColumnCount; w++)
                {
                    var rates = rxPrescription.RxProductLookups.Select((x, i) => new RxRate
                    {
                        Rate = h * rxPrescription.ColumnCount + w + i * 100,
                        RxProductLookupId = x.Id.ReferenceId
                    }).ToList();
                    rxPrescription.Rates.Add(new RxCellLookup { RxRates = rates });
                }
            }
            _adm.Catalog.Prescriptions = new List<Prescription> { rxPrescription };

            var plugin = new AgGateway.ADAPT.StandardPlugin.Plugin();
            var testFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "raster");
            plugin.Export(_adm, testFolder);

            var expected = File.ReadAllText("expected/raster/adapt.json");
            var current = File.ReadAllText(Path.Combine(testFolder, "adapt.json"));
            Assert.That(NormalizeReferenceIds(current), Is.EqualTo(NormalizeReferenceIds(expected)));

            AssertSpatialFileExists(testFolder, current);
        }

        [Test]
        public void ShortRasterRateListDoesNotThrow()
        {
            SetupProducts();
            var rxPrescription = SetupRasterPrescription();
            const int cellCount = 23;
            for (int cellIndex = 0; cellIndex < cellCount; cellIndex++)
            {
                var rates = rxPrescription.RxProductLookups.Select((lookup, productIndex) => new RxRate
                {
                    Rate = cellIndex + productIndex,
                    RxProductLookupId = lookup.Id.ReferenceId
                }).ToList();
                rxPrescription.Rates.Add(new RxCellLookup { RxRates = rates });
            }

            _adm.Catalog.Prescriptions = new List<Prescription> { rxPrescription };
            var testFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "raster-short");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            new AgGateway.ADAPT.StandardPlugin.Plugin().Export(_adm, testFolder);

            var tiffFiles = Directory.GetFiles(testFolder, "*.tiff");
            Assert.That(tiffFiles, Has.Length.EqualTo(1));
            Assert.That(new FileInfo(tiffFiles[0]).Length, Is.GreaterThan(0));
        }

        [Test]
        public void RepeatedVectorPrescriptionIsExportedOnce()
        {
            SetupProducts();
            var field = new AgGateway.ADAPT.ApplicationDataModel.Logistics.Field { Description = "Shared Field" };
            _adm.Catalog.Fields = new List<AgGateway.ADAPT.ApplicationDataModel.Logistics.Field> { field };

            var shared = CreateVectorPrescription(field.Id.ReferenceId, "SharedRx", 10d);
            var other = CreateVectorPrescription(field.Id.ReferenceId, "OtherRx", 20d);
            var sharedOperation = new WorkItemOperation
            {
                Description = "SharedOne",
                OperationType = OperationTypeEnum.SowingAndPlanting,
                PrescriptionId = shared.Id.ReferenceId
            };
            var sharedAgain = new WorkItemOperation
            {
                Description = "SharedTwo",
                OperationType = OperationTypeEnum.SowingAndPlanting,
                PrescriptionId = shared.Id.ReferenceId
            };
            var otherOperation = new WorkItemOperation
            {
                Description = "Other",
                OperationType = OperationTypeEnum.SowingAndPlanting,
                PrescriptionId = other.Id.ReferenceId
            };
            _adm.Documents.WorkItemOperations = new List<WorkItemOperation> { sharedOperation, sharedAgain, otherOperation };
            _adm.Documents.WorkItems = new List<WorkItem>
            {
                new WorkItem { WorkItemOperationIds = new List<int> { sharedOperation.Id.ReferenceId }, FieldId = field.Id.ReferenceId },
                new WorkItem { WorkItemOperationIds = new List<int> { sharedAgain.Id.ReferenceId }, FieldId = field.Id.ReferenceId },
                new WorkItem { WorkItemOperationIds = new List<int> { otherOperation.Id.ReferenceId }, FieldId = field.Id.ReferenceId }
            };
            _adm.Catalog.Prescriptions = new List<Prescription> { shared, other };

            var properties = new AgGateway.ADAPT.ApplicationDataModel.ADM.Properties();
            properties.SetProperty("Multithread", "true");
            properties.SetProperty("ParallelThreadCount", "4");
            var testFolder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "vector-shared");
            if (Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            new AgGateway.ADAPT.StandardPlugin.Plugin().Export(_adm, testFolder, properties);

            var parquetFiles = Directory.GetFiles(testFolder, "*.parquet");
            Assert.That(parquetFiles, Has.Length.EqualTo(2));
            foreach (string parquetFile in parquetFiles)
            {
                using (var reader = new ParquetSharp.ParquetFileReader(parquetFile))
                {
                    Assert.That(reader.FileMetaData.NumRows, Is.EqualTo(1));
                    using (var document = JsonDocument.Parse(reader.FileMetaData.KeyValueMetadata["geo"]))
                    {
                        var geometryTypes = document.RootElement
                            .GetProperty("columns")
                            .GetProperty("geometry")
                            .GetProperty("geometry_types")
                            .EnumerateArray()
                            .Select(item => item.GetString())
                            .ToArray();
                        Assert.That(geometryTypes, Is.EqualTo(new[] { "MultiPolygon" }));
                    }
                }
            }

            var json = File.ReadAllText(Path.Combine(testFolder, "adapt.json")).TrimStart('\uFEFF');
            using (var document = JsonDocument.Parse(json))
            {
                var spatialFiles = document.RootElement
                    .GetProperty("documents")
                    .GetProperty("workOrders")[0]
                    .GetProperty("operations")
                    .EnumerateArray()
                    .Select(operation => operation.GetProperty("spatialRecordsFile").GetString())
                    .ToList();
                Assert.That(spatialFiles, Has.Count.EqualTo(3));
                Assert.That(spatialFiles[0], Is.EqualTo(spatialFiles[1]));
                Assert.That(spatialFiles[2], Is.Not.EqualTo(spatialFiles[0]));
                Assert.That(spatialFiles.Distinct().Count(), Is.EqualTo(2));
            }
        }

        [Test]
        public void NormalizeReferenceIdsKeepsNonIdentifierData()
        {
            const string firstNumbering = "{\"products\":[{\"id\":{\"referenceId\":\"-1\"}}],\"operation\":{\"id\":{\"referenceId\":\"-2\"},\"productId\":\"-1\",\"valueText\":\"-1\",\"name\":\"Row -1\",\"description\":\"north\",\"geometry\":[-87.6,41.88],\"spatialRecordsFile\":\"RasterRx-2.tiff\"}}";
            const string renumbered = "{\"products\":[{\"id\":{\"referenceId\":\"-8\"}}],\"operation\":{\"id\":{\"referenceId\":\"-9\"},\"productId\":\"-8\",\"valueText\":\"-1\",\"name\":\"Row -1\",\"description\":\"north\",\"geometry\":[-87.6,41.88],\"spatialRecordsFile\":\"RasterRx-9.tiff\"}}";
            Assert.That(NormalizeReferenceIds(firstNumbering), Is.EqualTo(NormalizeReferenceIds(renumbered)));

            const string changedValue = "{\"products\":[{\"id\":{\"referenceId\":\"-1\"}}],\"operation\":{\"id\":{\"referenceId\":\"-2\"},\"productId\":\"-1\",\"valueText\":\"-2\",\"name\":\"Row -1\",\"description\":\"north\",\"geometry\":[-87.6,41.88],\"spatialRecordsFile\":\"RasterRx-2.tiff\"}}";
            const string changedCoordinate = "{\"products\":[{\"id\":{\"referenceId\":\"-1\"}}],\"operation\":{\"id\":{\"referenceId\":\"-2\"},\"productId\":\"-1\",\"valueText\":\"-1\",\"name\":\"Row -1\",\"description\":\"north\",\"geometry\":[-87.7,41.88],\"spatialRecordsFile\":\"RasterRx-2.tiff\"}}";
            const string changedName = "{\"products\":[{\"id\":{\"referenceId\":\"-1\"}}],\"operation\":{\"id\":{\"referenceId\":\"-2\"},\"productId\":\"-1\",\"valueText\":\"-1\",\"name\":\"Row -2\",\"description\":\"north\",\"geometry\":[-87.6,41.88],\"spatialRecordsFile\":\"RasterRx-2.tiff\"}}";
            const string changedDescription = "{\"products\":[{\"id\":{\"referenceId\":\"-1\"}}],\"operation\":{\"id\":{\"referenceId\":\"-2\"},\"productId\":\"-1\",\"valueText\":\"-1\",\"name\":\"Row -1\",\"description\":\"south\",\"geometry\":[-87.6,41.88],\"spatialRecordsFile\":\"RasterRx-2.tiff\"}}";
            const string changedRelationship = "{\"products\":[{\"id\":{\"referenceId\":\"-1\"}}],\"operation\":{\"id\":{\"referenceId\":\"-2\"},\"productId\":\"-2\",\"valueText\":\"-1\",\"name\":\"Row -1\",\"description\":\"north\",\"geometry\":[-87.6,41.88],\"spatialRecordsFile\":\"RasterRx-2.tiff\"}}";
            Assert.That(NormalizeReferenceIds(changedValue), Is.Not.EqualTo(NormalizeReferenceIds(firstNumbering)));
            Assert.That(NormalizeReferenceIds(changedCoordinate), Is.Not.EqualTo(NormalizeReferenceIds(firstNumbering)));
            Assert.That(NormalizeReferenceIds(changedName), Is.Not.EqualTo(NormalizeReferenceIds(firstNumbering)));
            Assert.That(NormalizeReferenceIds(changedDescription), Is.Not.EqualTo(NormalizeReferenceIds(firstNumbering)));
            Assert.That(NormalizeReferenceIds(changedRelationship), Is.Not.EqualTo(NormalizeReferenceIds(firstNumbering)));
        }

        // Reference ids come from a process-wide counter. Remap only identifier fields, in order
        // of first appearance, so values, names, and coordinates stay comparable.
        private static readonly HashSet<string> IdentifierProperties = new HashSet<string>
        {
            "referenceId",
            "productId",
            "fieldId",
            "cropZoneId",
            "seasonId",
            "variableId"
        };

        private static readonly HashSet<string> IdentifierArrayProperties = new HashSet<string>
        {
            "productIds",
            "referenceLayerIds"
        };

        private static string NormalizeReferenceIds(string json)
        {
            var root = JToken.Parse(json);
            var orderedIds = new List<string>();
            var seen = new HashSet<string>();
            CollectReferenceIds(root, orderedIds, seen);
            var map = new Dictionary<string, string>(orderedIds.Count);
            var filenameTokens = new List<KeyValuePair<string, string>>(orderedIds.Count);
            for (int index = 0; index < orderedIds.Count; index++)
            {
                string placeholder = string.Concat("id", (index + 1).ToString(CultureInfo.InvariantCulture));
                map.Add(orderedIds[index], placeholder);
                filenameTokens.Add(new KeyValuePair<string, string>(orderedIds[index], placeholder));
            }

            filenameTokens.Sort((left, right) => right.Key.Length.CompareTo(left.Key.Length));
            RemapIdentifiers(root, map, filenameTokens);
            return root.ToString(Formatting.None);
        }

        private static void CollectReferenceIds(JToken token, List<string> orderedIds, HashSet<string> seen)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties())
                {
                    if (property.Name == "referenceId" &&
                        property.Value.Type == JTokenType.String &&
                        seen.Add(property.Value.Value<string>()))
                    {
                        orderedIds.Add(property.Value.Value<string>());
                    }
                    else if (property.Name == "spatialRecordsFile" && property.Value.Type == JTokenType.String)
                    {
                        CollectFilenameIds(property.Value.Value<string>(), orderedIds, seen);
                    }

                    CollectReferenceIds(property.Value, orderedIds, seen);
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    CollectReferenceIds(item, orderedIds, seen);
                }
            }
        }

        private static void CollectFilenameIds(string value, List<string> orderedIds, HashSet<string> seen)
        {
            for (int index = 0; index < value.Length; index++)
            {
                if (value[index] != '-' || (index > 0 && char.IsDigit(value[index - 1])))
                {
                    continue;
                }

                int end = index + 1;
                while (end < value.Length && char.IsDigit(value[end]))
                {
                    end++;
                }

                if (end == index + 1)
                {
                    continue;
                }

                string token = value.Substring(index, end - index);
                if (seen.Add(token))
                {
                    orderedIds.Add(token);
                }

                index = end - 1;
            }
        }

        private static void RemapIdentifiers(JToken token, Dictionary<string, string> map, List<KeyValuePair<string, string>> filenameTokens)
        {
            if (token is JObject obj)
            {
                foreach (var property in obj.Properties().ToList())
                {
                    if (property.Value.Type == JTokenType.String)
                    {
                        string text = property.Value.Value<string>();
                        if (property.Name == "spatialRecordsFile")
                        {
                            property.Value = ReplaceIdTokens(text, filenameTokens);
                        }
                        else if (IdentifierProperties.Contains(property.Name) && map.TryGetValue(text, out string placeholder))
                        {
                            property.Value = placeholder;
                        }
                    }
                    else if (property.Value is JArray array && IdentifierArrayProperties.Contains(property.Name))
                    {
                        for (int index = 0; index < array.Count; index++)
                        {
                            if (array[index].Type == JTokenType.String &&
                                map.TryGetValue(array[index].Value<string>(), out string placeholder))
                            {
                                array[index] = placeholder;
                            }
                        }
                    }
                    else
                    {
                        RemapIdentifiers(property.Value, map, filenameTokens);
                    }
                }
            }
            else if (token is JArray array)
            {
                foreach (var item in array)
                {
                    RemapIdentifiers(item, map, filenameTokens);
                }
            }
        }

        private static string ReplaceIdTokens(string value, List<KeyValuePair<string, string>> filenameTokens)
        {
            var normalized = new StringBuilder(value.Length);
            for (int index = 0; index < value.Length;)
            {
                bool matched = false;
                foreach (var token in filenameTokens)
                {
                    if (index + token.Key.Length > value.Length ||
                        string.CompareOrdinal(value, index, token.Key, 0, token.Key.Length) != 0)
                    {
                        continue;
                    }

                    int right = index + token.Key.Length;
                    bool leftBounded = index == 0 || !char.IsDigit(value[index - 1]);
                    bool rightBounded = right >= value.Length || !char.IsDigit(value[right]);
                    if (!leftBounded || !rightBounded)
                    {
                        continue;
                    }

                    normalized.Append(token.Value);
                    index = right;
                    matched = true;
                    break;
                }

                if (!matched)
                {
                    normalized.Append(value[index]);
                    index++;
                }
            }

            return normalized.ToString();
        }

        private static void AssertSpatialFileExists(string outputDirectory, string json)
        {
            var spatialFile = JObject.Parse(json).SelectTokens("$..spatialRecordsFile").First().Value<string>();
            Assert.That(File.Exists(Path.Combine(outputDirectory, spatialFile)), Is.True);
        }

        private VectorPrescription CreateVectorPrescription(int fieldId, string description, double rate)
        {
            var lookup = new RxProductLookup
            {
                ProductId = _adm.Catalog.Products.First().Id.ReferenceId,
                Representation = new NumericRepresentation { Code = "vrAppRateMassActual" },
                UnitOfMeasure = new UnitOfMeasure { Code = "kg1ha-1" }
            };
            var prescription = new VectorPrescription
            {
                Description = description,
                FieldId = fieldId,
                OperationType = OperationTypeEnum.SowingAndPlanting,
                RxProductLookups = new List<RxProductLookup> { lookup }
            };
            prescription.RxShapeLookups.Add(new RxShapeLookup
            {
                Shape = new MultiPolygon
                {
                    Polygons = new List<Polygon>
                    {
                        new Polygon
                        {
                            ExteriorRing = new LinearRing
                            {
                                Points = new List<Point>
                                {
                                    new Point { X = -87.60, Y = 41.88 },
                                    new Point { X = -87.60, Y = 41.881 },
                                    new Point { X = -87.599, Y = 41.881 },
                                    new Point { X = -87.599, Y = 41.88 },
                                    new Point { X = -87.60, Y = 41.88 }
                                }
                            }
                        }
                    }
                },
                Rates = new List<RxRate>
                {
                    new RxRate { Rate = rate, RxProductLookupId = lookup.Id.ReferenceId }
                }
            });
            return prescription;
        }

        private RasterGridPrescription SetupRasterPrescription()
        {
            return new RasterGridPrescription
            {
                CellHeight = new NumericRepresentationValue { Value = new NumericValue(new UnitOfMeasure(), 0.001) },
                CellWidth = new NumericRepresentationValue { Value = new NumericValue(new UnitOfMeasure(), 0.002) },
                ColumnCount = 10,
                RowCount = 5,
                OperationType = OperationTypeEnum.SowingAndPlanting,
                Origin = new Point { X = -87.60, Y = 41.88 },
                RxProductLookups = _adm.Catalog.Products.Select(x => new RxProductLookup { ProductId = x.Id.ReferenceId, UnitOfMeasure = new UnitOfMeasure { Code = "kg1ha-1" }, Representation = new NumericRepresentation() { Code = "vrAppRateMassActual" } }).ToList(),
                ProductIds = _adm.Catalog.Products.Select(x => x.Id.ReferenceId).ToList(),
                Rates = new List<RxCellLookup>(),
                Description = "RasterRx"
            };
        }

        private void SetupProducts()
        {
            _adm = new ApplicationDataModel
            {
                Catalog = new Catalog
                {
                    Products = new List<Product>
                    {
                        new GenericProduct { Description = "Product 1", Category = CategoryEnum.Fertilizer, ProductType = ProductTypeEnum.Fertilizer, Form = ProductFormEnum.Solid, Status = ProductStatusEnum.Active },
                        new GenericProduct { Description = "Product 2", Category = CategoryEnum.Fungicide, ProductType = ProductTypeEnum.Chemical, Form = ProductFormEnum.Liquid, Status = ProductStatusEnum.Active },
                        new GenericProduct { Description = "Product 3", Category = CategoryEnum.Herbicide, ProductType = ProductTypeEnum.Chemical, Form = ProductFormEnum.Liquid, Status = ProductStatusEnum.Active },
                        new GenericProduct { Description = "Product 4", Category = CategoryEnum.Insecticide, ProductType = ProductTypeEnum.Chemical, Form = ProductFormEnum.Liquid, Status = ProductStatusEnum.Active }
                    },
                },
                Documents = new Documents(),
            };

        }
    }
}
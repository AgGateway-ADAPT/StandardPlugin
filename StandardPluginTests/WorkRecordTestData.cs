using System;
using System.Collections.Generic;
using System.Linq;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Common;
using AgGateway.ADAPT.ApplicationDataModel.Equipment;
using AgGateway.ADAPT.ApplicationDataModel.LoggedData;
using AgGateway.ADAPT.ApplicationDataModel.Logistics;
using AgGateway.ADAPT.ApplicationDataModel.Products;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.ApplicationDataModel.Shapes;
using AgGateway.ADAPT.StandardPlugin;

namespace StandardPluginTests
{
    internal static class WorkRecordTestData
    {
        internal static ApplicationDataModel CreateStraightPassApplicationDataModel(int recordCount = 25)
        {
            return CreateMultiSectionApplicationDataModel(sectionCount: 1, recordCount: recordCount);
        }

        internal static ApplicationDataModel CreateUnmergedOperationsApplicationDataModel(int operationCount, int recordCount = 4)
        {
            var model = CreateMultiSectionApplicationDataModel(sectionCount: 1, recordCount: recordCount);
            var loggedData = ((List<LoggedData>)model.Documents.LoggedData)[0];
            var template = ((List<OperationData>)loggedData.OperationData)[0];
            var appRateWorkingData = template.GetDeviceElementUses(2).First().GetWorkingDatas().OfType<NumericWorkingData>().First();
            var operations = new List<OperationData>(operationCount);
            var origin = new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
            for (int operationIndex = 0; operationIndex < operationCount; operationIndex++)
            {
                var records = CreateStraightPassRecords(
                    recordCount,
                    appRateWorkingData,
                    start: origin.AddHours(operationIndex * 48d),
                    rate: 100d + operationIndex);
                operations.Add(new OperationData
                {
                    Description = "UnmergedPass_" + operationIndex.ToString(),
                    OperationType = template.OperationType,
                    ProductIds = template.ProductIds.ToList(),
                    MaxDepth = template.MaxDepth,
                    GetSpatialRecords = () => records,
                    GetDeviceElementUses = template.GetDeviceElementUses
                });
            }

            loggedData.OperationData = operations;
            return model;
        }

        internal static ApplicationDataModel CreateMultiSectionApplicationDataModel(int sectionCount, int recordCount = 25)
        {
            var product = new GenericProduct
            {
                Description = "Test Product",
                Category = CategoryEnum.Fertilizer,
                ProductType = ProductTypeEnum.Fertilizer,
                Form = ProductFormEnum.Solid,
                Status = ProductStatusEnum.Active
            };

            var field = new Field { Description = "Coverage Field" };
            var appRateWorkingData = CreateAppRateWorkingData();

            var implementConfiguration = new ImplementConfiguration
            {
                PhysicalWidth = new NumericRepresentationValue
                {
                    Representation = new NumericRepresentation { Code = "vrImplementWidth" },
                    Value = new NumericValue(new UnitOfMeasure { Code = "m" }, 12d * sectionCount)
                }
            };

            var sectionConfigurations = new List<SectionConfiguration>(sectionCount);
            var sectionUses = new List<DeviceElementUse>(sectionCount);
            double sectionWidthM = 12d;
            double totalWidthM = sectionWidthM * sectionCount;
            for (int sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
            {
                double lateralOffsetM = ((sectionIndex + 0.5d) * sectionWidthM) - (totalWidthM / 2d);
                var sectionConfiguration = new SectionConfiguration
                {
                    DeviceElementId = 100 + sectionIndex,
                    SectionWidth = new NumericRepresentationValue
                    {
                        Representation = new NumericRepresentation { Code = "vrSectionWidth" },
                        Value = new NumericValue(new UnitOfMeasure { Code = "m" }, sectionWidthM)
                    },
                    InlineOffset = new NumericRepresentationValue
                    {
                        Representation = new NumericRepresentation { Code = "vrInlineOffset" },
                        Value = new NumericValue(new UnitOfMeasure { Code = "m" }, 0d)
                    },
                    LateralOffset = new NumericRepresentationValue
                    {
                        Representation = new NumericRepresentation { Code = "vrLateralOffset" },
                        Value = new NumericValue(new UnitOfMeasure { Code = "m" }, lateralOffsetM)
                    }
                };
                sectionConfigurations.Add(sectionConfiguration);
                sectionUses.Add(new DeviceElementUse
                {
                    Depth = 2,
                    DeviceConfigurationId = sectionConfiguration.Id.ReferenceId,
                    GetWorkingDatas = () => new List<WorkingData> { appRateWorkingData }
                });
            }

            var vehicleUse = new DeviceElementUse
            {
                Depth = 0,
                DeviceConfigurationId = implementConfiguration.Id.ReferenceId,
                GetWorkingDatas = () => new List<WorkingData>()
            };

            var implementUse = new DeviceElementUse
            {
                Depth = 1,
                DeviceConfigurationId = implementConfiguration.Id.ReferenceId,
                GetWorkingDatas = () => new List<WorkingData>()
            };

            var spatialRecords = CreateStraightPassRecords(recordCount, appRateWorkingData);
            var operationData = new OperationData
            {
                Description = sectionCount == 1 ? "StraightPass" : $"MultiSectionPass_{sectionCount}",
                OperationType = OperationTypeEnum.Fertilizing,
                ProductIds = new List<int> { product.Id.ReferenceId },
                MaxDepth = 2,
                GetSpatialRecords = () => spatialRecords,
                GetDeviceElementUses = depth =>
                {
                    switch (depth)
                    {
                        case 0:
                            return new[] { vehicleUse };
                        case 1:
                            return new[] { implementUse };
                        case 2:
                            return sectionUses.ToArray();
                        default:
                            return Array.Empty<DeviceElementUse>();
                    }
                }
            };

            var loggedData = new LoggedData
            {
                Description = "StraightPassLoggedData",
                FieldId = field.Id.ReferenceId,
                OperationData = new List<OperationData> { operationData }
            };

            var deviceElementConfigurations = new List<DeviceElementConfiguration> { implementConfiguration };
            deviceElementConfigurations.AddRange(sectionConfigurations);

            return new ApplicationDataModel
            {
                Catalog = new Catalog
                {
                    Fields = new List<Field> { field },
                    Products = new List<Product> { product },
                    DeviceElementConfigurations = deviceElementConfigurations
                },
                Documents = new Documents
                {
                    LoggedData = new List<LoggedData> { loggedData }
                }
            };
        }

        internal static ApplicationDataModel CreateSectionMotionApplicationDataModel(double?[] sectionHeadings, double?[] sectionDistances, double? inheritedHeading = null)
        {
            int sectionCount = sectionHeadings.Length;
            var model = CreateMultiSectionApplicationDataModel(sectionCount, recordCount: 2);
            var loggedData = ((List<LoggedData>)model.Documents.LoggedData)[0];
            var template = ((List<OperationData>)loggedData.OperationData)[0];
            var appRateWorkingData = template.GetDeviceElementUses(2).First().GetWorkingDatas().OfType<NumericWorkingData>().First();
            var headingDatas = new NumericWorkingData[sectionCount];
            var distanceDatas = new NumericWorkingData[sectionCount];
            var sectionUses = new List<DeviceElementUse>(sectionCount);
            for (int sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
            {
                int index = sectionIndex;
                if (sectionHeadings[index].HasValue)
                {
                    headingDatas[index] = CreateNumericWorkingData("vrHeading", "arcdeg");
                }

                if (sectionDistances[index].HasValue)
                {
                    distanceDatas[index] = CreateNumericWorkingData("vrDistanceTraveled", "m");
                }

                var configurationId = template.GetDeviceElementUses(2).ElementAt(index).DeviceConfigurationId;
                sectionUses.Add(new DeviceElementUse
                {
                    Depth = 2,
                    DeviceConfigurationId = configurationId,
                    GetWorkingDatas = () =>
                    {
                        var workingDatas = new List<WorkingData> { appRateWorkingData };
                        if (headingDatas[index] != null)
                        {
                            workingDatas.Add(headingDatas[index]);
                        }

                        if (distanceDatas[index] != null)
                        {
                            workingDatas.Add(distanceDatas[index]);
                        }

                        return workingDatas;
                    }
                });
            }

            NumericWorkingData inheritedHeadingData = null;
            if (inheritedHeading.HasValue)
            {
                inheritedHeadingData = CreateNumericWorkingData("vrHeading", "arcdeg");
            }

            var implementUse = template.GetDeviceElementUses(1).First();
            var vehicleUse = template.GetDeviceElementUses(0).First();
            var records = CreateStraightPassRecords(2, appRateWorkingData);
            for (int recordIndex = 0; recordIndex < records.Count; recordIndex++)
            {
                for (int sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
                {
                    if (headingDatas[sectionIndex] != null)
                    {
                        records[recordIndex].SetMeterValue(headingDatas[sectionIndex], new NumericRepresentationValue
                        {
                            Representation = new NumericRepresentation { Code = "vrHeading" },
                            Value = new NumericValue(headingDatas[sectionIndex].UnitOfMeasure, sectionHeadings[sectionIndex].Value)
                        });
                    }

                    if (distanceDatas[sectionIndex] != null)
                    {
                        records[recordIndex].SetMeterValue(distanceDatas[sectionIndex], new NumericRepresentationValue
                        {
                            Representation = new NumericRepresentation { Code = "vrDistanceTraveled" },
                            Value = new NumericValue(distanceDatas[sectionIndex].UnitOfMeasure, sectionDistances[sectionIndex].Value)
                        });
                    }
                }

                if (inheritedHeadingData != null)
                {
                    records[recordIndex].SetMeterValue(inheritedHeadingData, new NumericRepresentationValue
                    {
                        Representation = new NumericRepresentation { Code = "vrHeading" },
                        Value = new NumericValue(inheritedHeadingData.UnitOfMeasure, inheritedHeading.Value)
                    });
                }
            }

            loggedData.OperationData = new List<OperationData>
            {
                new OperationData
                {
                    Description = "SectionMotion",
                    OperationType = template.OperationType,
                    ProductIds = template.ProductIds.ToList(),
                    MaxDepth = template.MaxDepth,
                    GetSpatialRecords = () => records,
                    GetDeviceElementUses = depth =>
                    {
                        switch (depth)
                        {
                            case 0:
                                return new[] { vehicleUse };
                            case 1:
                                return new[]
                                {
                                    new DeviceElementUse
                                    {
                                        Depth = 1,
                                        DeviceConfigurationId = implementUse.DeviceConfigurationId,
                                        GetWorkingDatas = () => inheritedHeadingData == null
                                            ? new List<WorkingData>()
                                            : new List<WorkingData> { inheritedHeadingData }
                                    }
                                };
                            case 2:
                                return sectionUses.ToArray();
                            default:
                                return Array.Empty<DeviceElementUse>();
                        }
                    }
                }
            };
            return model;
        }

        internal static NumericWorkingData CreateNumericWorkingData(string representationCode, string unitCode)
        {
            return new NumericWorkingData
            {
                Representation = new NumericRepresentation { Code = representationCode },
                UnitOfMeasure = new UnitOfMeasure { Code = unitCode }
            };
        }

        internal static NumericWorkingData CreateAppRateWorkingData()
        {
            return new NumericWorkingData
            {
                Representation = new NumericRepresentation { Code = "vrAppRateMassActual" },
                UnitOfMeasure = new UnitOfMeasure { Code = "lb1ac-1" }
            };
        }

        internal static SectionDefinition CreateSectionDefinition(double widthM, double lateralOffsetM)
        {
            var sectionConfiguration = new SectionConfiguration
            {
                DeviceElementId = 3,
                SectionWidth = new NumericRepresentationValue
                {
                    Representation = new NumericRepresentation { Code = "vrSectionWidth" },
                    Value = new NumericValue(new UnitOfMeasure { Code = "m" }, widthM)
                },
                InlineOffset = new NumericRepresentationValue
                {
                    Representation = new NumericRepresentation { Code = "vrInlineOffset" },
                    Value = new NumericValue(new UnitOfMeasure { Code = "m" }, 0d)
                },
                LateralOffset = new NumericRepresentationValue
                {
                    Representation = new NumericRepresentation { Code = "vrLateralOffset" },
                    Value = new NumericValue(new UnitOfMeasure { Code = "m" }, lateralOffsetM)
                }
            };

            var sectionUse = new DeviceElementUse
            {
                Depth = 2,
                DeviceConfigurationId = sectionConfiguration.Id.ReferenceId,
                GetWorkingDatas = () => new List<WorkingData> { CreateAppRateWorkingData() }
            };

            return new SectionDefinition(
                sectionUse,
                sectionConfiguration,
                null,
                new OperationData { ProductIds = new List<int>() },
                new Catalog(),
                new List<TypeMapping>());
        }

        internal static List<SpatialRecord> CreateStraightPassRecords(int recordCount, NumericWorkingData appRateWorkingData, double baseLat = 41.88d, DateTime? start = null, double? rate = null)
        {
            var records = new List<SpatialRecord>(recordCount);
            var startTime = start ?? new DateTime(2024, 6, 1, 10, 0, 0, DateTimeKind.Utc);
            const double baseLon = -87.60d;
            const double metersPerDegreeLat = 111320d;

            for (int i = 0; i < recordCount; i++)
            {
                var record = new SpatialRecord
                {
                    Timestamp = startTime.AddSeconds(i),
                    Geometry = new Point
                    {
                        X = baseLon,
                        Y = baseLat + ((i * 0.5d) / metersPerDegreeLat)
                    }
                };
                record.SetMeterValue(appRateWorkingData, new NumericRepresentationValue
                {
                    Representation = new NumericRepresentation { Code = "vrAppRateMassActual" },
                    Value = new NumericValue(appRateWorkingData.UnitOfMeasure, rate ?? (50d + i))
                });
                records.Add(record);
            }

            return records;
        }
    }
}

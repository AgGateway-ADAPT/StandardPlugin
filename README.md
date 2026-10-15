# Overview
The ADAPT Standard Plugin is a transitional tool to assist the industry in migrating from the ADAPT Framework to the ADAPT Standard.  It converts the in-memory ADAPT Framework `ApplicationDataModel` into serialized data that conforms to the latest version of the  ADAPT Standard.

The ADAPT Framework is a .Net plugin toolkit that allows data producers to provide software to data consumers to read proprietary files. 

The ADAPT Standard is a replacement that encourages data producers to keep proprietary files in-house and provide consumers with a readable format that requires no software dependencies.

# Acknowledgements
AgGateway acknowledges generous support from [NIFA](https://www.nifa.usda.gov/) (USDA's National Institute of Food and Agriculture) and [NAPDC](https://www.agdatacoop.org/) (National Agricultural Producers Data Cooperative).

# Usage
See the [StandardPluginHarness](https://github.com/ADAPT/StandardPlugin/tree/develop/StandardPluginHarness) for a simple example.

## Required Input Properties
The ADAPT Framework was largely undocumented.  As such, different plugin implementers used data elements in different ways.  To convert data, plugin users will need to specify properties based on the source plugin so that data can be interpreted correctly.  Below are known settings for various plugins.  Data originating in the ADAPT Framework ADM plugin will need to account for the original source of the data. 

### Source Geometry Position
Some plugins report SpatialRecord.Geometry as the point of the GNSS Receiver, and others report it as the Implement Reference Point.   To correctly map the data, this plugin needs to factor this definition to determine which offsets to apply. 

_GNSS Receiver_
- ISOv4Plugin
- Precision Planting

_Implement Reference Point_
- Climate
- CNH
- John Deere

### Source Device Definition
Plugins take different approaches on how to use the DeviceElementUse.Depth concept in the Framework.

_DeviceElementHierarchy_
- CNH
- ISOv4Plugin
- John Deere
- Precision Planting

_Machine0Implement1Section2_
- Climate

### Multithread
Spatial export can process operations concurrently. Some source plugins mutate shared state while `GetSpatialRecords()` is read, and concurrent export then fails. Set `Multithread` to `false` for those plugins and `true` only after that plugin has exported cleanly with more than one operation. When `Multithread` is omitted or `false`, operations are exported one at a time and each field's spatial data is released before the next field starts. When `true`, `ParallelThreadCount` sets how many operations run at once.

## Optional Export Properties
These are not required to interpret a source plugin. Omitted values keep the previous export behavior.

### ParallelThreadCount
Used only when `Multithread` is `true`. A positive integer sets how many operations or prescriptions export at once. When omitted, the count is the number of processors. `0` or a negative value exports one at a time through the concurrent path. This property is ignored when `Multithread` is `false`.

### CompactJson
`false` or omitted writes indented `adapt.json`. `true` writes the same document without indentation.

# Data Mappings
ADAPT Standard Version 2.0 released in June 2025.   The plugin converts to ADAPT Standard Version 2.0 and intends only to convert to the latest version of the specification at any time. 

The following tables summarize plugin implementation where the ADAPT Framework and ADAPT Standard diverge.

## Common Properties
| ADAPT Framework | ADAPT Standard |
|------------------|-----------------|
| `*.Description` | `*.Name` The ADAPT Framework used `Name` and `Description` interchangeably.  The ADAPT Standard treats `name` as a short name and `description` as an extended description.|
| `ContextItem.Code` | `ContextItem.definitionCode` |
| `ContextItem.Value` | `ContextItem.valueText` |
| `ContextItem.NestedItems` | `ContextItem.contextItems` |
| `TimeScope.TimeStamp1` | `TimeScope.start` |
| `TimeScope.TimeStamp2` | `TimeScope.end` |
| `TimeScope.DateContext.CropSeason` | `Season` |
| `TimeScope.DateContext.TimingEvent` | Not in ADAPT Standard |
| `TimeScope.Location1` and `Location2` | Not in ADAPT Standard |
| Any types not mappable to https://github.com/ADAPT/Standard/blob/main/adapt-data-type-definitions.json | `CustomDataTypeDefinition` |

## Entity Metadata
| ADAPT Framework | ADAPT Standard |
|------------------|-----------------|
| `Company` and `Person` | `Party` |
| `PersonRole` | `PartyRole` |
| `Field.GrowerId` | `Field.farmId` -> `Farm.growerId` The ADAPT Standard does not allow Fields to map to Growers without Farm objects. |
| `Farm.ContactInfo` | `Farm.partyId` -> `Party.contactInfo` |
| `Grower.ContactInfo` | `Grower.partyId` -> `Party.contactInfo` |
| `ContactInfo` except `ContactInfo.Contacts` | `ContactInfo.addressContactMethod` |
| `ContactInfo.Contacts` | `ContactInfo.telecommunicationContactMethods` |

## Devices and Equipment
Most of the Device/Equipment model is omitted from the ADAPT Standard.  The ADAPT Standard does not model data from the equipment's point-of-view. 

| ADAPT Framework | ADAPT Standard |
|------------------|-----------------|
| `DeviceModel.ManufacturerId` | `DeviceModel.brandId` -> `Brand.manufacturerId` |
| `DeviceModel.SeriesId` | `DeviceModel.devicesSeries` |
| `DeviceElement` | `Device` |
| `DeviceElement.BrandId`, `ManufacturerId`, `SeriesId` | Mapped via `Device.deviceModelId` |
| `Connector`, `HitchPoint`, `EquipmentConfiguration`, `DeviceConfiguration` and its child types | Implement geometry information is intentionally omitted in favor of derived coverage polygons.  |
| `DeviceElementUse` | Hierarchial equipment information is necessarily flattened on the `Operation` to the field's point-of-view. |

## Geometry
| ADAPT Framework | ADAPT Standard |
|------------------|-----------------|
| `Shapes` types | WKT for unattributed geometries.   GeoParquet for attributed geometries |
| `FieldBoundary.SpatialData` | `FieldBoundary.boundary.geometry` |
| `CropZone.BoundingRegion` | `CropZone.boundary.geometry` |
| `DrivenHeadland` | `Headland` |
| `ConstantOffsetHeadland` | Omitted.  Headlands in ADAPT Standard require Geometries. |

## Products
| ADAPT Framework | ADAPT Standard |
|------------------|-----------------|
| `Product.Status, Form, Type` | `Product.productStatusCode, productFormCode, productTypeCode` |
| `CropNutritionProduct.IsManure` | `Product.productTypeCode` = `FERTILIZER_ORGANIC` |
| `CropProtectionProduct.Biological` | `Product.contextItems` `definitionCode`= `HasBiological` |
| `CropProtectionProduct.Organophosphate` | `Product.contextItems` `definitionCode`= `HasOrganophosphate` |
| `CropProtectionProduct.Carbamate` | `Product.contextItems` `definitionCode`= `HasCarbamate` |
| `CropVarietyProduct` | `Product.productTypeCode` = `SEED` or `PLANT` |
| `CropVarietyProduct.CropId` | `Product.cropId` |
| `CropVarietyProduct.Traits` | `Product.traits` |
| `CropVarietyProduct.GeneticallyEnhanced` | `Product.contextItems` `definitionCode`= `CropVarietyIsGeneticallyEnhanced` |
| `HarvestedCommmodityProduct` | `Product.productTypeCode` = `HARVESTED_PRODUCT` |
| `MixProduct` | `Product` with non-empty `ProductComponents` | 
| `ProductComponent.IngredientID` | `ProductComponent.productId` or `ProductComponent.ingredientId` |
| `ProductComponent.Quantity` | `ProductComponent.amount` |
| `ProductComponent.IsProduct` | `ProductComponent.productId` populated |
| `Ingredient` | `ProductComponent.ingredientId` |
| `ActiveIngredient` | `IngredientId.isActiveIngredient` = `true` |
| `InertIngredient` | `IngredientId.isActiveIngredient` = `false` |
| `CropNutritionIngredient.IngredientCode` | `IngredientId.ingredientCode` |
| `Trait.CropIds` | `Trait` is a child of a specific `Product.productTypeCode` = `SEED` or `PLANT` with a required `CropId`. |

## Work Orders
| ADAPT Framework | ADAPT Standard |
|------------------|-----------------|
| `Prescription`, `WorkItem` and `WorkItemOperation` | `WorkOrder.operation` |
| `Prescription.OperationType` | `WorkOrder.operation.operationTypeCode` |
| `Prescription.RxProductLookups` | `WorkOrder.operation.variables` specifying a `ProductId` |
| `RasterGridPrescription` | `WorkOrder.operation` with GeoTIFF `.tiff` file export |
| `RasterPrescription.Rates` (`RxCellLookup`) | Individual GeoTIFF pixels |
| `VectorPrescription` | `WorkOrder.operation` with GeoParquet `.parquet` file export |
| `VectorPrescription.RxShapeLookups` | Individual GeoParquet features |
| `ManualPrescription.ProductUses` | `WorkOrder.operation.summaryValues` |


## Work Records

| ADAPT Framework | ADAPT Standard |
|------------------|-----------------|
| `LoggedData` | `WorkRecords` grouped by field id and like `Operations` (see next) |
| `OperationData`| `WorkRecord.operations` grouped by like types, products and start times occurring within 36 hours |
| `OperationData.OperationType` | `Operation.operationTypeCode` |
| `OperationData.GetSpatialRecords()` | GeoParquet file identified by `Operation.spatialRecordsFile`  |
| `OperationData.GetDeviceElementUses()` and `OperationData.maxDepth` |  Hierarchial equipment information is necessarily "flattened" into data from the field's point-of-view |
| `DeviceElementUse.GetWorkingDatas()` | `Operation.variables` |
| `WorkingData.Representation.Code` | `Variable.definitionCode` |
| `WorkingData.Representation.Code` = `vrProductIndex` | `Variable.productId` |
| `SpatialRecord` | Individual GeoParquet feature |
| `SpatialRecord.GetMeterValue(WorkingData)` | Individual GeoParquet feature attribute value mapped via `Variable.geoParquetColumnName` | 
| `Load` | `Operation.harvestLoadIdentifier` + `Operation.summaryValues` |

## Omitted Mappings
With decade of production usage behind us, we can look at some of the ADAPT Framework model as well-intentioned theory that never saw usage.   Some of this model has not yet or will not be added to the ADAPT Standard.   

### Not part of ADAPT Standard
- _Observations_: Observations and Measurements is a likely future extension to the ADAPT Standard
- _Irrigation_: Not known to be adopted.
- _Containers and Packaged Products_: Products in ADAPT are models and not instances.  Supply chain and traceability are not part of the ADAPT Standard at this time.

### No/Limited Use in ADAPT Framework 
- _Reference Layers_:  Included in the ADAPT Standard but not known to be used in the Framework 
- _Plans & Recommendations_: While these document types persist in the ADAPT Standard, no known source ADAPT Framework plugin populates these types. 
- _Sensor Latencies & Calibration Factors_: The ADAPT Standard requires that any known calibration/sensor latencies be applied in the data and not reported separately for the data consumer to handle.  Many equipment OEM plugins take this approach by default and any calibration data is included in the Framework for information only.  Any Framework data that does not handle calibration / flow delay will be technically out of spec for the ADAPT Standard as output by this plugin.  To the limited extent such data can be identified and adjusted, this is a known issue.
- _Pivot Guidance Patterns Defined as Three Points on a Circle_: Pivot guidance patterns are not often seen, and 3-point definitions are rarer.   For the plugin to perform such a conversion we would need to add code to reproject coordinates, and this sits open as a known issue.



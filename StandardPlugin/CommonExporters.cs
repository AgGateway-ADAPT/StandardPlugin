using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using AgGateway.ADAPT.ApplicationDataModel.ADM;
using AgGateway.ADAPT.ApplicationDataModel.Common;
using AgGateway.ADAPT.ApplicationDataModel.Logistics;
using AgGateway.ADAPT.ApplicationDataModel.Representations;
using AgGateway.ADAPT.Standard;
using Newtonsoft.Json;

namespace AgGateway.ADAPT.StandardPlugin
{
    internal class CommonExporters
    {
        private static readonly object ConfigSyncRoot = new object();
        private static List<TypeMapping> _cachedTypeMappings;
        private static DataTypeDefinitions.DataTypeDefinitions _cachedStandardDataTypes;
        // Seasons, errors, and custom data types are appended from parallel operations.
        private readonly object _seasonSyncRoot = new object();
        private readonly object _errorsSyncRoot = new object();
        private readonly object _customDataTypeSyncRoot = new object();
        private readonly Standard.Catalog _catalog;

        public List<TypeMapping> TypeMappings { get; private set; }

        public AgGateway.ADAPT.DataTypeDefinitions.DataTypeDefinitions StandardDataTypes { get; private set; }

        public CommonExporters(Standard.Root root)
        {
            _catalog = root.Catalog;
            Errors = new List<IError>();

            // The mapping files do not change during a process. Cache them so each export does not re-read and re-parse.
            lock (ConfigSyncRoot)
            {
                if (_cachedTypeMappings == null || _cachedStandardDataTypes == null)
                {
                    var mappingFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "framework-to-standard-type-mappings.json");
                    string mappingData = File.ReadAllText(mappingFile);
                    _cachedTypeMappings = JsonConvert.DeserializeObject<List<TypeMapping>>(mappingData);

                    var standardTypesFile = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "adapt-data-type-definitions.json");
                    string typesJson = File.ReadAllText(standardTypesFile);
                    _cachedStandardDataTypes = JsonConvert.DeserializeObject<DataTypeDefinitions.DataTypeDefinitions>(typesJson);
                }
            }

            TypeMappings = _cachedTypeMappings;
            StandardDataTypes = _cachedStandardDataTypes;
        }

        public List<IError> Errors { get; }

        internal void AddError(IError error)
        {
            lock (_errorsSyncRoot)
            {
                Errors.Add(error);
            }
        }

        public Id ExportID(CompoundIdentifier srcId)
        {
            var id = new Id { ReferenceId = srcId.ReferenceId.ToString(CultureInfo.InvariantCulture) };

            var ids = new List<UniqueIdElement>();
            foreach (var srcUniqueId in srcId.UniqueIds)
            {
                if (srcUniqueId.Source == "http://dictionary.isobus.net/isobus/")
                {
                    //Suppress the "DET-1" etc. ids that the ISO Plugin creates
                    //These are instance and not persistent ids.
                    continue;
                }
                var uniqueId = new UniqueIdElement
                {
                    IdText = srcUniqueId.Id,
                    IdSource = srcUniqueId.Source,
                };
                switch (srcUniqueId.IdType)
                {
                    case IdTypeEnum.UUID: uniqueId.IdTypeCode = "UUID"; break;
                    case IdTypeEnum.LongInt: uniqueId.IdTypeCode = "LONGINT"; break;
                    case IdTypeEnum.String: uniqueId.IdTypeCode = "STRING"; break;
                    case IdTypeEnum.URI: uniqueId.IdTypeCode = "URI"; break;
                }
                switch (srcUniqueId.SourceType)
                {
                    case IdSourceTypeEnum.URI: uniqueId.IdSourceTypeCode = "URI"; break;
                    case IdSourceTypeEnum.GLN: uniqueId.IdSourceTypeCode = "GLN"; break;
                }
                ids.Add(uniqueId);
            }
            if (ids.Any())
            {
                id.UniqueIds = ids;
            }
            return id;
        }

        public List<TimeScopeElement> ExportTimeScopes(List<TimeScope> srcTimeScopes, out List<string> seasonIds)
        {
            seasonIds = new List<string>();
            if (srcTimeScopes.IsNullOrEmpty())
            {
                return null;
            }
            var validTimeScopes = srcTimeScopes.Where(x => x != null).ToList();
            List<TimeScopeElement> output = new List<TimeScopeElement>();
            output.AddRange(HandleStartEndTimeScopes(validTimeScopes, DateContextEnum.ActualStart, DateContextEnum.ActualEnd));
            output.AddRange(HandleStartEndTimeScopes(validTimeScopes, DateContextEnum.ProposedStart, DateContextEnum.ProposedEnd));
            output.AddRange(HandleStartEndTimeScopes(validTimeScopes, DateContextEnum.RequestedStart, DateContextEnum.RequestedEnd));

            foreach (var frameworkTimeScope in validTimeScopes)
            {
                if (frameworkTimeScope.DateContext == DateContextEnum.TimingEvent)
                {
                    AddError(new Error
                    {
                        Description = "Discarding TimingEvent TimeScope",
                        Id = frameworkTimeScope.Id.ReferenceId.ToString(CultureInfo.InvariantCulture),
                    });
                    continue; //Not in the ADAPT Standard
                }

                if (frameworkTimeScope.DateContext == DateContextEnum.CropSeason)
                {
                    var season = GetOrAddSeason(frameworkTimeScope);
                    seasonIds = seasonIds ?? new List<string>();
                    seasonIds.Add(season.Id.ReferenceId);
                    continue; //Not a timescope in the ADAPT Standard
                }

                var timeScope = ExportTimeScope(frameworkTimeScope);
                output.Add(timeScope);

            }

            if (!output.Any())
            {
                output = null;
            }
            return output;
        }

        internal SeasonElement GetOrAddSeason(TimeScope timeScope)
        {
            var start = timeScope.TimeStamp1?.ToString("O", CultureInfo.InvariantCulture);
            var end = timeScope.TimeStamp2?.ToString("O", CultureInfo.InvariantCulture);
            var description = !timeScope.Description.IsNullOrEmpty() ? timeScope.Description : timeScope.TimeStamp1?.ToString("yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

            lock (_seasonSyncRoot)
            {
                if (_catalog.Seasons == null)
                {
                    _catalog.Seasons = new List<SeasonElement>();
                }
                var season = _catalog.Seasons.FirstOrDefault(x => x.Start == start && x.End == end && x.Description == description);
                if (season == null)
                {
                    season = new SeasonElement
                    {
                        Id = ExportID(timeScope.Id),
                        Start = start,
                        End = end,
                        Description = description
                    };
                    _catalog.Seasons.Add(season);
                }
                return season;
            }
        }

        private TimeScopeElement ExportTimeScope(TimeScope frameworkTimeScope)
        {
            return new TimeScopeElement
            {
                DateContextCode = ExportDateContext(frameworkTimeScope.DateContext),
                Duration = frameworkTimeScope.Duration?.TotalSeconds,
                Start = frameworkTimeScope.TimeStamp1?.ToString("O", CultureInfo.InvariantCulture),
                End = frameworkTimeScope.TimeStamp2?.ToString("O", CultureInfo.InvariantCulture),
            };
        }

        private IEnumerable<TimeScopeElement> HandleStartEndTimeScopes(List<TimeScope> srcTimeScopes, DateContextEnum startContext, DateContextEnum endContext)
        {
            List<TimeScopeElement> output = new List<TimeScopeElement>();
            var startEndTimeScopes = srcTimeScopes.Where(x => x != null && (x.DateContext == startContext || x.DateContext == endContext)).ToList();

            TimeScope startTimeScope = null;
            TimeScopeElement startTimeScopeElement = null;
            foreach (var srcTimeScope in startEndTimeScopes.OrderBy(x => x.DateContext))
            {
                srcTimeScopes.Remove(srcTimeScope);

                if (srcTimeScope.DateContext == startContext)
                {
                    startTimeScope = srcTimeScope;
                    startTimeScopeElement = ExportTimeScope(srcTimeScope);

                    output.Add(startTimeScopeElement);
                }
                else
                {
                    startTimeScopeElement.End = srcTimeScope.TimeStamp1?.ToString("O", CultureInfo.InvariantCulture);
                }
            }

            return output;
        }

        public List<ContextItemElement> ExportContextItems(List<ContextItem> srcContextItems, int level = 1)
        {
            if (srcContextItems.IsNullOrEmpty())
            {
                return null;
            }
            if (level > 2)
            {
                AddError(new Error
                {
                    Description = "Discarding nested context items",
                });
                return null;
            }

            var output = new List<ContextItemElement>();
            foreach (var frameworkContextItem in srcContextItems)
            {
                var contextItem = new ContextItemElement
                {
                    ValueText = frameworkContextItem.Value,
                    ContextItems = ExportContextItems(frameworkContextItem.NestedItems, level + 1)
                };

                if (frameworkContextItem.Code.EqualsIgnoreCase("US-EPA-N"))
                {
                    contextItem.DefinitionCode = "US-EPA-RegistrationNumber";
                }
                else if (TypeMappings.Any(x => x.Source.EqualsIgnoreCase(frameworkContextItem.Code)))
                {
                    contextItem.DefinitionCode = TypeMappings.First(x => x.Source.EqualsIgnoreCase(frameworkContextItem.Code)).Target;
                }
                else
                {
                    contextItem.DefinitionCode = frameworkContextItem.Code;
                    TryAddCustomDataType(frameworkContextItem.Code, frameworkContextItem.ValueUOM, "TEXT", "INVALID");
                }

                output.Add(contextItem);
            }
            return output;
        }

        public string ExportRole(EnumeratedValue role)
        {
            if (role?.Representation?.Code == null)
            {
                return "UNKNOWN";
            }
            if (role.Representation.Code != "dtPersonRole")
            {
                AddError(new Error
                {
                    Description = $"Found unsupported Role - {role.Representation.Code}",
                });
                return "UNKNOWN";
            }

            switch (role.Value.Value)
            {
                case "dtiPersonRoleAuthorizer":
                    return "AUTHORIZER";
                case "dtiPersonRoleCropAdvisor":
                    return "CROP_ADVISOR";
                case "dtiPersonRoleCustomer":
                    return "CUSTOMER";
                case "dtiPersonRoleCustomServiceProvider":
                    return "CUSTOM_SERVICE_PROVIDER";
                case "dtiPersonRoleDataServicesProvider":
                    return "DATA_SERVICES_PROVIDER";
                case "dtiPersonRoleEndUser":
                    return "END_USER";
                case "dtiPersonRoleFarmManager":
                    return "FARM_MANAGER";
                case "dtiPersonRoleFinancier":
                    return "FINANCIER";
                case "dtiPersonRoleFixedAssetSupplier":
                    return "STATIONARY_ASSET_SUPPLIER";
                case "dtiPersonRoleGovernmentAgency":
                    return "GOVERNMENT_AGENCY";
                case "dtiPersonRoleGrower":
                    return "GROWER";
                case "dtiPersonRoleInputSupplier":
                    return "INPUT_SUPPLIER";
                case "dtiPersonRoleInsuranceAgent":
                    return "INSURANCE_AGENT";
                case "dtiPersonRoleIrrigationManager":
                    return "IRRIGATION_MANAGER";
                case "dtiPersonRoleLaborer":    
                    return "LABORER";  
                case "dtiPersonRoleMarketAdvisor":
                    return "MARKET_ADVISOR";    
                case "dtiPersonRoleMarketProvider":
                    return "MARKET_PROVIDER";
                case "dtiPersonRoleMobileAssetSupplier":
                    return "MOBILE_ASSET_SUPPLIER";
                case "dtiPersonRoleOperator":
                    return "OPERATOR";
                case "dtiPersonRoleOwner":
                    return "OWNER";
                case "dtiPersonRoleTransporter":
                    return "TRANSPORTER";   
                default:
                    return "UNKNOWN";
            }
        }

        internal List<GuidanceAllocationElement> ExportGuidanceAllocations(List<int> srcGuidanceAllocationIds, ApplicationDataModel.ADM.ApplicationDataModel model)
        {
            if (srcGuidanceAllocationIds.IsNullOrEmpty())
            {
                return null;
            }

            List<GuidanceAllocationElement> output = new List<GuidanceAllocationElement>();
            foreach (var frameworkGuidanceAllocationId in srcGuidanceAllocationIds)
            {
                var frmeworkGuidanceAllocation = model.Documents.GuidanceAllocations.FirstOrDefault(x => x.Id.ReferenceId == frameworkGuidanceAllocationId);
                if (frmeworkGuidanceAllocation == null)
                {
                    continue;
                }

                var guidanceAllocation = new GuidanceAllocationElement
                {
                    Id = ExportID(frmeworkGuidanceAllocation.Id),
                    GuidanceGroupId = frmeworkGuidanceAllocation.GuidanceGroupId.ToString(CultureInfo.InvariantCulture),
                    GuidanceShift = ExportGuidanceShift(frmeworkGuidanceAllocation.GuidanceShift, model.Catalog),
                    TimeScopes = ExportTimeScopes(frmeworkGuidanceAllocation.TimeScopes, out _),
                    Name = "GuidanceAllocation"
                };
            }

            return output;
        }

        public List<PartyRoleElement> ExportPersonRoles(List<PersonRole> srcPersonRoles)
        {
            if (srcPersonRoles.IsNullOrEmpty())
            {
                return null;
            }

            List<PartyRoleElement> output = new List<PartyRoleElement>();
            foreach (var frmeworkPersonRole in srcPersonRoles)
            {
                var partyRole = new PartyRoleElement
                {
                    PartyId = frmeworkPersonRole.PersonId.ToString(CultureInfo.InvariantCulture),
                    RoleCode = ExportRole(frmeworkPersonRole.Role),
                    TimeScopes = ExportTimeScopes(frmeworkPersonRole.TimeScopes, out _),
                };
                output.Add(partyRole);
            }
            return output;
        }

        public T ExportAsNumericValue<T>(NumericRepresentationValue srcRepresentationValue)
            where T : class
        {
            if (srcRepresentationValue == null)
            {
                return default(T);
            }

            var numericValue = srcRepresentationValue.Value.Value;
            var unitOfMeasureCode = srcRepresentationValue.Value.UnitOfMeasure?.Code ?? "unitless";

            var output = Activator.CreateInstance(typeof(T));

            switch (output)
            {
                case Density density:
                    density.NumericValue = numericValue;
                    density.UnitOfMeasureCode = unitOfMeasureCode;
                    break;

                case SwathWidth swathWidth:
                    swathWidth.NumericValue = numericValue;
                    swathWidth.UnitOfMeasureCode = unitOfMeasureCode;
                    break;

                case Radius radius:
                    radius.NumericValue = numericValue;
                    radius.UnitOfMeasureCode = unitOfMeasureCode;
                    break;

                case ArableArea arableArea:
                    arableArea.NumericValue = numericValue;
                    arableArea.UnitOfMeasureCode = unitOfMeasureCode;
                    break;

                case ReferenceWeight referenceWeight:
                    referenceWeight.NumericValue = numericValue;
                    referenceWeight.UnitOfMeasureCode = unitOfMeasureCode;
                    break;

                case StandardPayableMoisture standardPayableMoisture:
                    standardPayableMoisture.NumericValue = numericValue;
                    standardPayableMoisture.UnitOfMeasureCode = unitOfMeasureCode;
                    break;

                case EastShift eastShift:
                    eastShift.NumericValue = numericValue;
                    eastShift.UnitOfMeasureCode = unitOfMeasureCode;
                    break;

                case NorthShift northShift:
                    northShift.NumericValue = numericValue;
                    northShift.UnitOfMeasureCode = unitOfMeasureCode;
                    break;

                case PropagationOffset propagationOffset:
                    propagationOffset.NumericValue = numericValue;
                    propagationOffset.UnitOfMeasureCode = unitOfMeasureCode;
                    break;

                default:
                    throw new ApplicationException($"Unsupported numeric value - {typeof(T).Name}");
            }

            return (T)output;
        }

        public string ExportOperationType(OperationTypeEnum operationType)
        {
            switch (operationType)
            {
                case OperationTypeEnum.Baling:
                    return "HARVEST";
                case OperationTypeEnum.CropProtection:
                    return "APPLICATION_CROP_PROTECTION";
                case OperationTypeEnum.DataCollection:
                    return "VEHICLE_DATA_COLLECTION_GENERAL";
                case OperationTypeEnum.Fertilizing:
                    return "APPLICATION_FERTILIZING";
                case OperationTypeEnum.ForageHarvesting:
                    return "HARVEST";
                case OperationTypeEnum.Harvesting:
                    return "HARVEST";
                case OperationTypeEnum.Irrigation:
                    return "APPLICATION_IRRIGATION";
                case OperationTypeEnum.Mowing:
                    return "HARVEST_PRE_HARVEST";
                case OperationTypeEnum.SowingAndPlanting:
                    return "APPLICATION_SOWING_AND_PLANTING_SEEDS";
                case OperationTypeEnum.Swathing:
                    return "HARVEST_PRE_HARVEST";
                case OperationTypeEnum.Tillage:
                    return "FIELD_PREPARATION_TILLAGE";
                case OperationTypeEnum.Transport:
                    return "VEHICLE_DATA_COLLECTION_GENERAL";
                case OperationTypeEnum.Unknown:
                    return "UNKNOWN";
                case OperationTypeEnum.Wrapping:
                    return "HARVEST_POST_HARVEST";
            }
            return null;
        }

        private static string ExportDateContext(DateContextEnum dateContext)
        {
            switch (dateContext)
            {
                case DateContextEnum.ActualEnd:
                case DateContextEnum.ActualShipping:
                case DateContextEnum.ActualStart:
                    return "ACTUAL";
                case DateContextEnum.Approval:
                    return "APPROVAL";
                case DateContextEnum.Calibration:
                    return "CALIBRATION";
                case DateContextEnum.Creation:
                    return "CREATION";
                case DateContextEnum.Expiration:
                    return "EXPIRATION";
                case DateContextEnum.Installation:
                    return "INSTALLATION";
                case DateContextEnum.Load:
                    return "LOAD";
                case DateContextEnum.Maintenance:
                    return "MAINTENANCE";
                case DateContextEnum.Modification:
                    return "MODIFICATION";
                case DateContextEnum.PhenomenonTime:
                    return "PHENOMENON_TIME";
                case DateContextEnum.ProposedEnd:
                case DateContextEnum.ProposedStart:
                    return "PROPOSED";
                case DateContextEnum.RequestedEnd:
                case DateContextEnum.RequestedStart:
                    return "REQUESTED";
                case DateContextEnum.RequestedShipping:
                    return "REQUESTED_SHIPPING";
                case DateContextEnum.ResultTime:
                    return "RESULT_TIME";
                case DateContextEnum.Resume:
                    return "RESUME";
                case DateContextEnum.Suspend:
                    return "SUSPEND";
                case DateContextEnum.Unload:
                    return "UNLOAD";
                case DateContextEnum.Unspecified:
                    return "UNSPECIFIED";
                case DateContextEnum.ValidityRange:
                    return "VALIDITY";
                default:
                    return null;
            }
        }

        private void TryAddCustomDataType(string code, string valueUOM, string baseTypeCode, string statusCode)
        {
            lock (_customDataTypeSyncRoot)
            {
                var existingCustomDataType = _catalog.CustomDataTypeDefinitions.FirstOrDefault(x => x.DefinitionCode == code);
                if (existingCustomDataType != null)
                {
                    return;
                }

                _catalog.CustomDataTypeDefinitions.Add(new CustomDataTypeDefinitionElement
                {
                    DefinitionCode = code,
                    Name = $"{code} ({valueUOM})",
                    DataDefinitionBaseTypeCode = baseTypeCode,
                    DataDefinitionStatusCode = statusCode,
                });
            }
        }

        private Standard.GuidanceShift ExportGuidanceShift(ApplicationDataModel.Guidance.GuidanceShift guidanceShift, ApplicationDataModel.ADM.Catalog catalog)
        {
            if (guidanceShift == null)
            {
                return null;
            }

            return new Standard.GuidanceShift
            {
                GuidanceGroupId = guidanceShift.GuidanceGroupId.ToString(CultureInfo.InvariantCulture),
                EastShift = ExportAsNumericValue<EastShift>(guidanceShift.EastShift),
                GuidancePatternId = guidanceShift.GuidancePatterId.ToString(CultureInfo.InvariantCulture),
                NorthShift = ExportAsNumericValue<NorthShift>(guidanceShift.NorthShift),
                PropagationOffset = ExportAsNumericValue<PropagationOffset>(guidanceShift.PropagationOffset),
                TimeScopes = guidanceShift.TimeScopeIds.IsNullOrEmpty() 
                    ? null 
                    : ExportTimeScopes(catalog.TimeScopes.Where(x => guidanceShift.TimeScopeIds.Contains(x.Id.ReferenceId)).ToList(), out _)
            };
        }

    }
}

// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core.WindowsInstaller.Bind
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using WixToolset.Core.Bind;
    using WixToolset.Data;
    using WixToolset.Data.Bind;
    using WixToolset.Data.Tuples;
    using WixToolset.Data.WindowsInstaller;
    using WixToolset.Extensibility;
    using WixToolset.Extensibility.Services;

    /// <summary>
    /// Binds a databse.
    /// </summary>
    internal class BindDatabaseCommand
    {
        // As outlined in RFC 4122, this is our namespace for generating name-based (version 3) UUIDs.
        internal static readonly Guid WixComponentGuidNamespace = new Guid("{3064E5C6-FB63-4FE9-AC49-E446A792EFA5}");

        public BindDatabaseCommand(IBindContext context, IEnumerable<IWindowsInstallerBackendExtension> backendExtension, Validator validator)
        {
            this.TableDefinitions = WindowsInstallerStandardInternal.GetTableDefinitions();

            this.CabbingThreadCount = context.CabbingThreadCount;
            this.CabCachePath = context.CabCachePath;
            this.Codepage = context.Codepage;
            this.DefaultCompressionLevel = context.DefaultCompressionLevel;
            this.DelayedFields = context.DelayedFields;
            this.ExpectedEmbeddedFiles = context.ExpectedEmbeddedFiles;
            this.FileSystemExtensions = context.FileSystemExtensions;
            this.Intermediate = context.IntermediateRepresentation;
            this.Messaging = context.Messaging;
            this.OutputPath = context.OutputPath;
            this.IntermediateFolder = context.IntermediateFolder;
            this.Validator = validator;

            this.BackendExtensions = backendExtension;
        }

        private int Codepage { get; }

        private int CabbingThreadCount { get; }

        private string CabCachePath { get; }

        private CompressionLevel? DefaultCompressionLevel { get; }

        public IEnumerable<IDelayedField> DelayedFields { get; }

        public IEnumerable<IExpectedExtractFile> ExpectedEmbeddedFiles { get; }

        public IEnumerable<IFileSystemExtension> FileSystemExtensions { get; }

        public bool DeltaBinaryPatch { get; set; }

        private IEnumerable<IWindowsInstallerBackendExtension> BackendExtensions { get; }

        private Intermediate Intermediate { get; }

        private IMessaging Messaging { get; }

        private string OutputPath { get; }

        private bool SuppressAddingValidationRows { get; }

        private bool SuppressLayout { get; }

        private TableDefinitionCollection TableDefinitions { get; }

        private string IntermediateFolder { get; }

        private Validator Validator { get; }


        public IEnumerable<FileTransfer> FileTransfers { get; private set; }

        public IEnumerable<string> ContentFilePaths { get; private set; }

        public Pdb Pdb { get; private set; }

        public void Execute()
        {
            var section = this.Intermediate.Sections.Single();

            var fileTransfers = new List<FileTransfer>();

            var containsMergeModules = false;
            var suppressedTableNames = new HashSet<string>();

            // If there are any fields to resolve later, create the cache to populate during bind.
            var variableCache = this.DelayedFields.Any() ? new Dictionary<string, string>(StringComparer.InvariantCultureIgnoreCase) : null;

            // Process the summary information table before the other tables.
            bool compressed;
            bool longNames;
            int installerVersion;
            string modularizationGuid;
            {
                var command = new BindSummaryInfoCommand(section);
                command.Execute();

                compressed = command.Compressed;
                longNames = command.LongNames;
                installerVersion = command.InstallerVersion;
                modularizationGuid = command.ModularizationGuid;
            }

            // Add binder variables for all properties.
            if (SectionType.Product == section.Type || variableCache != null)
            {
                foreach (var propertyRow in section.Tuples.OfType<PropertyTuple>())
                {
                    // Set the ProductCode if it is to be generated.
                    if ("ProductCode".Equals(propertyRow.Property, StringComparison.Ordinal) && "*".Equals(propertyRow.Value, StringComparison.Ordinal))
                    {
                        propertyRow.Value = Common.GenerateGuid();

#if TODO_FIX_INSTANCE_TRANSFORM // Is this still necessary?

                        // Update the target ProductCode in any instance transforms.
                        foreach (SubStorage subStorage in this.Output.SubStorages)
                        {
                            Output subStorageOutput = subStorage.Data;
                            if (OutputType.Transform != subStorageOutput.Type)
                            {
                                continue;
                            }

                            Table instanceSummaryInformationTable = subStorageOutput.Tables["_SummaryInformation"];
                            foreach (Row row in instanceSummaryInformationTable.Rows)
                            {
                                if ((int)SummaryInformation.Transform.ProductCodes == row.FieldAsInteger(0))
                                {
                                    row[1] = row.FieldAsString(1).Replace("*", propertyRow.Value);
                                    break;
                                }
                            }
                        }
#endif
                    }

                    // Add the property name and value to the variableCache.
                    if (variableCache != null)
                    {
                        var key = String.Concat("property.", propertyRow.Property);
                        variableCache[key] = propertyRow.Value;
                    }
                }
            }

            // Sequence all the actions.
            {
                var command = new SequenceActionsCommand(section);
                command.Messaging = this.Messaging;
                command.Execute();
            }

            {
                var command = new CreateSpecialPropertiesCommand(section);
                command.Execute();
            }

#if TODO_FINISH_PATCH
            ////if (OutputType.Patch == this.Output.Type)
            ////{
            ////    foreach (SubStorage substorage in this.Output.SubStorages)
            ////    {
            ////        Output transform = substorage.Data;

            ////        ResolveFieldsCommand command = new ResolveFieldsCommand();
            ////        command.Tables = transform.Tables;
            ////        command.FilesWithEmbeddedFiles = filesWithEmbeddedFiles;
            ////        command.FileManagerCore = this.FileManagerCore;
            ////        command.FileManagers = this.FileManagers;
            ////        command.SupportDelayedResolution = false;
            ////        command.TempFilesLocation = this.TempFilesLocation;
            ////        command.WixVariableResolver = this.WixVariableResolver;
            ////        command.Execute();

            ////        this.MergeUnrealTables(transform.Tables);
            ////    }
            ////}
#endif

            if (this.Messaging.EncounteredError)
            {
                return;
            }

            this.Messaging.Write(VerboseMessages.UpdatingFileInformation());

            // This must occur after all variables and source paths have been resolved.
            List<FileFacade> fileFacades;
            {
                var command = new GetFileFacadesCommand(section);
                command.Execute();

                fileFacades = command.FileFacades;
            }

            // Extract files that come from binary .wixlibs and WixExtensions (this does not extract files from merge modules).
            {
                var command = new ExtractEmbeddedFilesCommand(this.ExpectedEmbeddedFiles);
                command.Execute();
            }

            // Gather information about files that do not come from merge modules.
            {
                var command = new UpdateFileFacadesCommand(this.Messaging, section);
                command.FileFacades = fileFacades;
                command.UpdateFileFacades = fileFacades.Where(f => !f.FromModule);
                command.OverwriteHash = true;
                command.TableDefinitions = this.TableDefinitions;
                command.VariableCache = variableCache;
                command.Execute();
            }

            // Now that the variable cache is populated, resolve any delayed fields.
            if (this.DelayedFields.Any())
            {
                var command = new ResolveDelayedFieldsCommand(this.Messaging, this.DelayedFields, variableCache);
                command.Execute();
            }

            // Set generated component guids.
            {
                var command = new CalculateComponentGuids(this.Messaging, section);
                command.Execute();
            }

            // Retrieve file information from merge modules.
            if (SectionType.Product == section.Type)
            {
                var wixMergeTuples = section.Tuples.OfType<WixMergeTuple>().ToList();

                if (wixMergeTuples.Any())
                {
                    containsMergeModules = true;

                    var command = new ExtractMergeModuleFilesCommand(this.Messaging, section, wixMergeTuples);
                    command.FileFacades = fileFacades;
                    command.OutputInstallerVersion = installerVersion;
                    command.SuppressLayout = this.SuppressLayout;
                    command.IntermediateFolder = this.IntermediateFolder;
                    command.Execute();

                    fileFacades.AddRange(command.MergeModulesFileFacades);
                }
            }
#if TODO_FINISH_PATCH
            else if (OutputType.Patch == this.Output.Type)
            {
                // Merge transform data into the output object.
                IEnumerable<FileFacade> filesFromTransform = this.CopyFromTransformData(this.Output);

                fileFacades.AddRange(filesFromTransform);
            }
#endif

            // stop processing if an error previously occurred
            if (this.Messaging.EncounteredError)
            {
                return;
            }

            // Assign files to media.
            Dictionary<int, MediaTuple> assignedMediaRows;
            Dictionary<MediaTuple, IEnumerable<FileFacade>> filesByCabinetMedia;
            IEnumerable<FileFacade> uncompressedFiles;
            {
                var command = new AssignMediaCommand(section, this.Messaging);
                command.FileFacades = fileFacades;
                command.FilesCompressed = compressed;
                command.Execute();

                assignedMediaRows = command.MediaRows;
                filesByCabinetMedia = command.FileFacadesByCabinetMedia;
                uncompressedFiles = command.UncompressedFileFacades;
            }

            // stop processing if an error previously occurred
            if (this.Messaging.EncounteredError)
            {
                return;
            }

            // Time to create the output object. Try to put as much above here as possible, updating the IR is better.
            Output output;
            {
                var command = new CreateOutputFromIRCommand(section, this.TableDefinitions, this.BackendExtensions);
                command.Execute();

                output = command.Output;
            }

            // Update file sequence.
            {
                var command = new UpdateMediaSequencesCommand(output, fileFacades, assignedMediaRows);
                command.Execute();
            }

            // Modularize identifiers.
            if (OutputType.Module == output.Type)
            {
                var command = new ModularizeCommand(output, modularizationGuid, section.Tuples.OfType<WixSuppressModularizationTuple>());
                command.Execute();
            }
            else // we can create instance transforms since Component Guids are set.
            {
#if TODO_FIX_INSTANCE_TRANSFORM
                this.CreateInstanceTransforms(this.Output);
#endif
            }

#if TODO_FINISH_UPDATE
            // Extended binder extensions can be called now that fields are resolved.
            {
                Table updatedFiles = this.Output.EnsureTable(this.TableDefinitions["WixBindUpdatedFiles"]);

                foreach (IBinderExtension extension in this.Extensions)
                {
                    extension.AfterResolvedFields(this.Output);
                }

                List<FileFacade> updatedFileFacades = new List<FileFacade>();

                foreach (Row updatedFile in updatedFiles.Rows)
                {
                    string updatedId = updatedFile.FieldAsString(0);

                    FileFacade updatedFacade = fileFacades.First(f => f.File.File.Equals(updatedId));

                    updatedFileFacades.Add(updatedFacade);
                }

                if (updatedFileFacades.Any())
                {
                    UpdateFileFacadesCommand command = new UpdateFileFacadesCommand();
                    command.FileFacades = fileFacades;
                    command.UpdateFileFacades = updatedFileFacades;
                    command.ModularizationGuid = modularizationGuid;
                    command.Output = this.Output;
                    command.OverwriteHash = true;
                    command.TableDefinitions = this.TableDefinitions;
                    command.VariableCache = variableCache;
                    command.Execute();
                }
            }
#endif

            // Stop processing if an error previously occurred.
            if (this.Messaging.EncounteredError)
            {
                return;
            }

            // Ensure the intermediate folder is created since delta patches will be
            // created there.
            Directory.CreateDirectory(this.IntermediateFolder);

            if (SectionType.Patch == section.Type && this.DeltaBinaryPatch)
            {
                var command = new CreateDeltaPatchesCommand(fileFacades, this.IntermediateFolder, section.Tuples.OfType<WixPatchIdTuple>().FirstOrDefault());
                command.Execute();
            }

            // create cabinet files and process uncompressed files
            var layoutDirectory = Path.GetDirectoryName(this.OutputPath);
            if (!this.SuppressLayout || OutputType.Module == output.Type)
            {
                this.Messaging.Write(VerboseMessages.CreatingCabinetFiles());

                var command = new CreateCabinetsCommand();
                command.CabbingThreadCount = this.CabbingThreadCount;
                command.CabCachePath = this.CabCachePath;
                command.DefaultCompressionLevel = this.DefaultCompressionLevel;
                command.Output = output;
                command.Messaging = this.Messaging;
                command.BackendExtensions = this.BackendExtensions;
                command.LayoutDirectory = layoutDirectory;
                command.Compressed = compressed;
                command.FileRowsByCabinet = filesByCabinetMedia;
                command.ResolveMedia = this.ResolveMedia;
                command.TableDefinitions = this.TableDefinitions;
                command.TempFilesLocation = this.IntermediateFolder;
                command.WixMediaTuples = section.Tuples.OfType<WixMediaTuple>();
                command.Execute();

                fileTransfers.AddRange(command.FileTransfers);
            }

#if TODO_FINISH_PATCH
            if (OutputType.Patch == this.Output.Type)
            {
                // copy output data back into the transforms
                this.CopyToTransformData(this.Output);
            }
#endif

            this.ValidateComponentGuids(output);

            // stop processing if an error previously occurred
            if (this.Messaging.EncounteredError)
            {
                return;
            }

            // Generate database file.
            this.Messaging.Write(VerboseMessages.GeneratingDatabase());
            string tempDatabaseFile = Path.Combine(this.IntermediateFolder, Path.GetFileName(this.OutputPath));
            this.GenerateDatabase(output, tempDatabaseFile, false, false);

            if (FileTransfer.TryCreate(tempDatabaseFile, this.OutputPath, true, output.Type.ToString(), null, out var transfer)) // note where this database needs to move in the future
            {
                transfer.Built = true;
                fileTransfers.Add(transfer);
            }

            // Stop processing if an error previously occurred.
            if (this.Messaging.EncounteredError)
            {
                return;
            }

            // Merge modules.
            if (containsMergeModules)
            {
                this.Messaging.Write(VerboseMessages.MergingModules());

                // Add back possibly suppressed sequence tables since all sequence tables must be present
                // for the merge process to work. We'll drop the suppressed sequence tables again as
                // necessary.
                foreach (SequenceTable sequence in Enum.GetValues(typeof(SequenceTable)))
                {
                    var sequenceTableName = sequence.ToString();
                    var sequenceTable = output.Tables[sequenceTableName];

                    if (null == sequenceTable)
                    {
                        sequenceTable = output.EnsureTable(this.TableDefinitions[sequenceTableName]);
                    }

                    if (0 == sequenceTable.Rows.Count)
                    {
                        suppressedTableNames.Add(sequenceTableName);
                    }
                }

                var command = new MergeModulesCommand();
                command.FileFacades = fileFacades;
                command.Output = output;
                command.OutputPath = tempDatabaseFile;
                command.SuppressedTableNames = suppressedTableNames;
                command.Execute();
            }

            if (this.Messaging.EncounteredError)
            {
                return;
            }

#if TODO_FINISH_VALIDATION
            // Validate the output if there is an MSI validator.
            if (null != this.Validator)
            {
                Stopwatch stopwatch = Stopwatch.StartNew();

                // set the output file for source line information
                this.Validator.Output = this.Output;

                Messaging.Instance.Write(WixVerboses.ValidatingDatabase());

                this.Validator.Validate(tempDatabaseFile);

                stopwatch.Stop();
                Messaging.Instance.Write(WixVerboses.ValidatedDatabase(stopwatch.ElapsedMilliseconds));

                // Stop processing if an error occurred.
                if (Messaging.Instance.EncounteredError)
                {
                    return;
                }
            }
#endif

            // Process uncompressed files.
            if (!this.Messaging.EncounteredError && !this.SuppressLayout && uncompressedFiles.Any())
            {
                var command = new ProcessUncompressedFilesCommand(section);
                command.Compressed = compressed;
                command.FileFacades = uncompressedFiles;
                command.LayoutDirectory = layoutDirectory;
                command.LongNamesInImage = longNames;
                command.ResolveMedia = this.ResolveMedia;
                command.DatabasePath = tempDatabaseFile;
                command.Execute();

                fileTransfers.AddRange(command.FileTransfers);
            }

            this.FileTransfers = fileTransfers;
            this.ContentFilePaths = fileFacades.Select(r => r.WixFile.Source.Path).ToList();
            this.Pdb = new Pdb { Output = output };

            // TODO: Eventually this gets removed
            var intermediate = new Intermediate(this.Intermediate.Id, new[] { section }, this.Intermediate.Localizations.ToDictionary(l => l.Culture, StringComparer.OrdinalIgnoreCase), this.Intermediate.EmbedFilePaths);
            intermediate.Save(Path.ChangeExtension(this.OutputPath, "wir"));
        }

#if TODO_FINISH_PATCH
        /// <summary>
        /// Copy file data between transform substorages and the patch output object
        /// </summary>
        /// <param name="output">The output to bind.</param>
        /// <param name="allFileRows">True if copying from transform to patch, false the other way.</param>
        private IEnumerable<FileFacade> CopyFromTransformData(Output output)
        {
            var command = new CopyTransformDataCommand();
            command.CopyOutFileRows = true;
            command.Output = output;
            command.TableDefinitions = this.TableDefinitions;
            command.Execute();

            return command.FileFacades;
        }

        /// <summary>
        /// Copy file data between transform substorages and the patch output object
        /// </summary>
        /// <param name="output">The output to bind.</param>
        /// <param name="allFileRows">True if copying from transform to patch, false the other way.</param>
        private void CopyToTransformData(Output output)
        {
            var command = new CopyTransformDataCommand();
            command.CopyOutFileRows = false;
            command.Output = output;
            command.TableDefinitions = this.TableDefinitions;
            command.Execute();
        }
#endif


#if TODO_FIX_INSTANCE_TRANSFORM
        /// <summary>
        /// Creates instance transform substorages in the output.
        /// </summary>
        /// <param name="output">Output containing instance transform definitions.</param>
        private void CreateInstanceTransforms(Output output)
        {
            // Create and add substorages for instance transforms.
            Table wixInstanceTransformsTable = output.Tables["WixInstanceTransforms"];
            if (null != wixInstanceTransformsTable && 0 <= wixInstanceTransformsTable.Rows.Count)
            {
                string targetProductCode = null;
                string targetUpgradeCode = null;
                string targetProductVersion = null;

                Table targetSummaryInformationTable = output.Tables["_SummaryInformation"];
                Table targetPropertyTable = output.Tables["Property"];

                // Get the data from target database
                foreach (Row propertyRow in targetPropertyTable.Rows)
                {
                    if ("ProductCode" == (string)propertyRow[0])
                    {
                        targetProductCode = (string)propertyRow[1];
                    }
                    else if ("ProductVersion" == (string)propertyRow[0])
                    {
                        targetProductVersion = (string)propertyRow[1];
                    }
                    else if ("UpgradeCode" == (string)propertyRow[0])
                    {
                        targetUpgradeCode = (string)propertyRow[1];
                    }
                }

                // Index the Instance Component Rows.
                Dictionary<string, ComponentRow> instanceComponentGuids = new Dictionary<string, ComponentRow>();
                Table targetInstanceComponentTable = output.Tables["WixInstanceComponent"];
                if (null != targetInstanceComponentTable && 0 < targetInstanceComponentTable.Rows.Count)
                {
                    foreach (Row row in targetInstanceComponentTable.Rows)
                    {
                        // Build up all the instances, we'll get the Components rows from the real Component table.
                        instanceComponentGuids.Add((string)row[0], null);
                    }

                    Table targetComponentTable = output.Tables["Component"];
                    foreach (ComponentRow componentRow in targetComponentTable.Rows)
                    {
                        string component = (string)componentRow[0];
                        if (instanceComponentGuids.ContainsKey(component))
                        {
                            instanceComponentGuids[component] = componentRow;
                        }
                    }
                }

                // Generate the instance transforms
                foreach (Row instanceRow in wixInstanceTransformsTable.Rows)
                {
                    string instanceId = (string)instanceRow[0];

                    Output instanceTransform = new Output(instanceRow.SourceLineNumbers);
                    instanceTransform.Type = OutputType.Transform;
                    instanceTransform.Codepage = output.Codepage;

                    Table instanceSummaryInformationTable = instanceTransform.EnsureTable(this.TableDefinitions["_SummaryInformation"]);
                    string targetPlatformAndLanguage = null;

                    foreach (Row summaryInformationRow in targetSummaryInformationTable.Rows)
                    {
                        if (7 == (int)summaryInformationRow[0]) // PID_TEMPLATE
                        {
                            targetPlatformAndLanguage = (string)summaryInformationRow[1];
                        }

                        // Copy the row's data to the transform.
                        Row copyOfSummaryRow = instanceSummaryInformationTable.CreateRow(null);
                        copyOfSummaryRow[0] = summaryInformationRow[0];
                        copyOfSummaryRow[1] = summaryInformationRow[1];
                    }

                    // Modify the appropriate properties.
                    Table propertyTable = instanceTransform.EnsureTable(this.TableDefinitions["Property"]);

                    // Change the ProductCode property
                    string productCode = (string)instanceRow[2];
                    if ("*" == productCode)
                    {
                        productCode = Common.GenerateGuid();
                    }

                    Row productCodeRow = propertyTable.CreateRow(instanceRow.SourceLineNumbers);
                    productCodeRow.Operation = RowOperation.Modify;
                    productCodeRow.Fields[1].Modified = true;
                    productCodeRow[0] = "ProductCode";
                    productCodeRow[1] = productCode;

                    // Change the instance property
                    Row instanceIdRow = propertyTable.CreateRow(instanceRow.SourceLineNumbers);
                    instanceIdRow.Operation = RowOperation.Modify;
                    instanceIdRow.Fields[1].Modified = true;
                    instanceIdRow[0] = (string)instanceRow[1];
                    instanceIdRow[1] = instanceId;

                    if (null != instanceRow[3])
                    {
                        // Change the ProductName property
                        Row productNameRow = propertyTable.CreateRow(instanceRow.SourceLineNumbers);
                        productNameRow.Operation = RowOperation.Modify;
                        productNameRow.Fields[1].Modified = true;
                        productNameRow[0] = "ProductName";
                        productNameRow[1] = (string)instanceRow[3];
                    }

                    if (null != instanceRow[4])
                    {
                        // Change the UpgradeCode property
                        Row upgradeCodeRow = propertyTable.CreateRow(instanceRow.SourceLineNumbers);
                        upgradeCodeRow.Operation = RowOperation.Modify;
                        upgradeCodeRow.Fields[1].Modified = true;
                        upgradeCodeRow[0] = "UpgradeCode";
                        upgradeCodeRow[1] = instanceRow[4];

                        // Change the Upgrade table
                        Table targetUpgradeTable = output.Tables["Upgrade"];
                        if (null != targetUpgradeTable && 0 <= targetUpgradeTable.Rows.Count)
                        {
                            string upgradeId = (string)instanceRow[4];
                            Table upgradeTable = instanceTransform.EnsureTable(this.TableDefinitions["Upgrade"]);
                            foreach (Row row in targetUpgradeTable.Rows)
                            {
                                // In case they are upgrading other codes to this new product, leave the ones that don't match the
                                // Product.UpgradeCode intact.
                                if (targetUpgradeCode == (string)row[0])
                                {
                                    Row upgradeRow = upgradeTable.CreateRow(null);
                                    upgradeRow.Operation = RowOperation.Add;
                                    upgradeRow.Fields[0].Modified = true;
                                    // I was hoping to be able to RowOperation.Modify, but that didn't appear to function.
                                    // upgradeRow.Fields[0].PreviousData = (string)row[0];

                                    // Inserting a new Upgrade record with the updated UpgradeCode
                                    upgradeRow[0] = upgradeId;
                                    upgradeRow[1] = row[1];
                                    upgradeRow[2] = row[2];
                                    upgradeRow[3] = row[3];
                                    upgradeRow[4] = row[4];
                                    upgradeRow[5] = row[5];
                                    upgradeRow[6] = row[6];

                                    // Delete the old row
                                    Row upgradeRemoveRow = upgradeTable.CreateRow(null);
                                    upgradeRemoveRow.Operation = RowOperation.Delete;
                                    upgradeRemoveRow[0] = row[0];
                                    upgradeRemoveRow[1] = row[1];
                                    upgradeRemoveRow[2] = row[2];
                                    upgradeRemoveRow[3] = row[3];
                                    upgradeRemoveRow[4] = row[4];
                                    upgradeRemoveRow[5] = row[5];
                                    upgradeRemoveRow[6] = row[6];
                                }
                            }
                        }
                    }

                    // If there are instance Components generate new GUIDs for them.
                    if (0 < instanceComponentGuids.Count)
                    {
                        Table componentTable = instanceTransform.EnsureTable(this.TableDefinitions["Component"]);
                        foreach (ComponentRow targetComponentRow in instanceComponentGuids.Values)
                        {
                            string guid = targetComponentRow.Guid;
                            if (!String.IsNullOrEmpty(guid))
                            {
                                Row instanceComponentRow = componentTable.CreateRow(targetComponentRow.SourceLineNumbers);
                                instanceComponentRow.Operation = RowOperation.Modify;
                                instanceComponentRow.Fields[1].Modified = true;
                                instanceComponentRow[0] = targetComponentRow[0];
                                instanceComponentRow[1] = Uuid.NewUuid(BindDatabaseCommand.WixComponentGuidNamespace, String.Concat(guid, instanceId)).ToString("B").ToUpper(CultureInfo.InvariantCulture);
                                instanceComponentRow[2] = targetComponentRow[2];
                                instanceComponentRow[3] = targetComponentRow[3];
                                instanceComponentRow[4] = targetComponentRow[4];
                                instanceComponentRow[5] = targetComponentRow[5];
                            }
                        }
                    }

                    // Update the summary information
                    Hashtable summaryRows = new Hashtable(instanceSummaryInformationTable.Rows.Count);
                    foreach (Row row in instanceSummaryInformationTable.Rows)
                    {
                        summaryRows[row[0]] = row;

                        if ((int)SummaryInformation.Transform.UpdatedPlatformAndLanguage == (int)row[0])
                        {
                            row[1] = targetPlatformAndLanguage;
                        }
                        else if ((int)SummaryInformation.Transform.ProductCodes == (int)row[0])
                        {
                            row[1] = String.Concat(targetProductCode, targetProductVersion, ';', productCode, targetProductVersion, ';', targetUpgradeCode);
                        }
                        else if ((int)SummaryInformation.Transform.ValidationFlags == (int)row[0])
                        {
                            row[1] = 0;
                        }
                        else if ((int)SummaryInformation.Transform.Security == (int)row[0])
                        {
                            row[1] = "4";
                        }
                    }

                    if (!summaryRows.Contains((int)SummaryInformation.Transform.UpdatedPlatformAndLanguage))
                    {
                        Row summaryRow = instanceSummaryInformationTable.CreateRow(null);
                        summaryRow[0] = (int)SummaryInformation.Transform.UpdatedPlatformAndLanguage;
                        summaryRow[1] = targetPlatformAndLanguage;
                    }
                    else if (!summaryRows.Contains((int)SummaryInformation.Transform.ValidationFlags))
                    {
                        Row summaryRow = instanceSummaryInformationTable.CreateRow(null);
                        summaryRow[0] = (int)SummaryInformation.Transform.ValidationFlags;
                        summaryRow[1] = "0";
                    }
                    else if (!summaryRows.Contains((int)SummaryInformation.Transform.Security))
                    {
                        Row summaryRow = instanceSummaryInformationTable.CreateRow(null);
                        summaryRow[0] = (int)SummaryInformation.Transform.Security;
                        summaryRow[1] = "4";
                    }

                    output.SubStorages.Add(new SubStorage(instanceId, instanceTransform));
                }
            }
        }
#endif

        /// <summary>
        /// Validate that there are no duplicate GUIDs in the output.
        /// </summary>
        /// <remarks>
        /// Duplicate GUIDs without conditions are an error condition; with conditions, it's a
        /// warning, as the conditions might be mutually exclusive.
        /// </remarks>
        private void ValidateComponentGuids(Output output)
        {
            Table componentTable = output.Tables["Component"];
            if (null != componentTable)
            {
                Dictionary<string, bool> componentGuidConditions = new Dictionary<string, bool>(componentTable.Rows.Count);

                foreach (Data.WindowsInstaller.Rows.ComponentRow row in componentTable.Rows)
                {
                    // we don't care about unmanaged components and if there's a * GUID remaining,
                    // there's already an error that prevented it from being replaced with a real GUID.
                    if (!String.IsNullOrEmpty(row.Guid) && "*" != row.Guid)
                    {
                        bool thisComponentHasCondition = !String.IsNullOrEmpty(row.Condition);
                        bool allComponentsHaveConditions = thisComponentHasCondition;

                        if (componentGuidConditions.ContainsKey(row.Guid))
                        {
                            allComponentsHaveConditions = componentGuidConditions[row.Guid] && thisComponentHasCondition;

                            if (allComponentsHaveConditions)
                            {
                                this.Messaging.Write(WarningMessages.DuplicateComponentGuidsMustHaveMutuallyExclusiveConditions(row.SourceLineNumbers, row.Component, row.Guid));
                            }
                            else
                            {
                                this.Messaging.Write(ErrorMessages.DuplicateComponentGuids(row.SourceLineNumbers, row.Component, row.Guid));
                            }
                        }

                        componentGuidConditions[row.Guid] = allComponentsHaveConditions;
                    }
                }
            }
        }

        /// <summary>
        /// Update Control and BBControl text by reading from files when necessary.
        /// </summary>
        /// <param name="output">Internal representation of the msi database to operate upon.</param>
        private void UpdateControlText(Output output)
        {
            var command = new UpdateControlTextCommand();
            command.Messaging = this.Messaging;
            command.BBControlTable = output.Tables["BBControl"];
            command.WixBBControlTable = output.Tables["WixBBControl"];
            command.ControlTable = output.Tables["Control"];
            command.WixControlTable = output.Tables["WixControl"];
            command.Execute();
        }

        private string ResolveMedia(MediaTuple mediaRow, string mediaLayoutDirectory, string layoutDirectory)
        {
            string layout = null;

            foreach (var extension in this.BackendExtensions)
            {
                layout = extension.ResolveMedia(mediaRow, mediaLayoutDirectory, layoutDirectory);
                if (!String.IsNullOrEmpty(layout))
                {
                    break;
                }
            }

            // If no binder file manager resolved the layout, do the default behavior.
            if (String.IsNullOrEmpty(layout))
            {
                if (String.IsNullOrEmpty(mediaLayoutDirectory))
                {
                    layout = layoutDirectory;
                }
                else if (Path.IsPathRooted(mediaLayoutDirectory))
                {
                    layout = mediaLayoutDirectory;
                }
                else
                {
                    layout = Path.Combine(layoutDirectory, mediaLayoutDirectory);
                }
            }

            return layout;
        }

        /// <summary>
        /// Creates the MSI/MSM/PCP database.
        /// </summary>
        /// <param name="output">Output to create database for.</param>
        /// <param name="databaseFile">The database file to create.</param>
        /// <param name="keepAddedColumns">Whether to keep columns added in a transform.</param>
        /// <param name="useSubdirectory">Whether to use a subdirectory based on the <paramref name="databaseFile"/> file name for intermediate files.</param>
        private void GenerateDatabase(Output output, string databaseFile, bool keepAddedColumns, bool useSubdirectory)
        {
            var command = new GenerateDatabaseCommand();
            command.Extensions = this.FileSystemExtensions;
            command.Output = output;
            command.OutputPath = databaseFile;
            command.KeepAddedColumns = keepAddedColumns;
            command.UseSubDirectory = useSubdirectory;
            command.SuppressAddingValidationRows = this.SuppressAddingValidationRows;
            command.TableDefinitions = this.TableDefinitions;
            command.TempFilesLocation = this.IntermediateFolder;
            command.Codepage = this.Codepage;
            command.Execute();
        }
    }
}

﻿// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core
{
    using System;
    using System.Collections.Generic;
    using WixToolset.Data;
    using WixToolset.Extensibility;
    using WixToolset.Extensibility.Services;

    public class BindContext : IBindContext
    {
        internal BindContext(IServiceProvider serviceProvider)
        {
            this.ServiceProvider = serviceProvider;
        }

        public IServiceProvider ServiceProvider { get; }

        public Messaging Messaging { get; set; }

        public IEnumerable<BindPath> BindPaths { get; set; }

        public int CabbingThreadCount { get; set; }

        public string CabCachePath { get; set; }

        public int Codepage { get; set; }

        public CompressionLevel DefaultCompressionLevel { get; set; }

        public IEnumerable<IDelayedField> DelayedFields { get; set; }

        public IEnumerable<IExpectedExtractFile> ExpectedEmbeddedFiles { get; set; }

        public IExtensionManager ExtensionManager { get; set; }

        public IEnumerable<IBinderExtension> Extensions { get; set; }

        public IEnumerable<string> Ices { get; set; }

        public string IntermediateFolder { get; set; }

        public Output IntermediateRepresentation { get; set; }

        public string OutputPath { get; set; }

        public string OutputPdbPath { get; set; }

        public bool SuppressAclReset { get; set; }

        public IEnumerable<string> SuppressIces { get; set; }

        public bool SuppressValidation { get; set; }

        public IBindVariableResolver WixVariableResolver { get; set; }

        public string ContentsFile { get; set; }

        public string OutputsFile { get; set; }

        public string BuiltOutputsFile { get; set; }

        public string WixprojectFile { get; set; }
    }
}
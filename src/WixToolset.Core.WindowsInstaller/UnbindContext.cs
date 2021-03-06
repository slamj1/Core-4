﻿// Copyright (c) .NET Foundation and contributors. All rights reserved. Licensed under the Microsoft Reciprocal License. See LICENSE.TXT file in the project root for full license information.

namespace WixToolset.Core
{
    using System;
    using WixToolset.Extensibility;
    using WixToolset.Extensibility.Services;

    internal class UnbindContext : IUnbindContext
    {
        public IServiceProvider ServiceProvider { get; }

        public IMessaging Messaging { get; set; }

        public string ExportBasePath { get; set; }

        public string InputFilePath { get; set; }

        public string IntermediateFolder { get; set; }

        public bool IsAdminImage { get; set; }

        public bool SuppressExtractCabinets { get; set; }

        public bool SuppressDemodularization { get; set; }
    }
}

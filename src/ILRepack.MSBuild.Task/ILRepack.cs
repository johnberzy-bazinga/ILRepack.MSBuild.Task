﻿/**
 * Copyright (c) 2004, Evain Jb (jb@evain.net)
 * Modified 2007 Marcus Griep (neoeinstein+boo@gmail.com)
 * Modified 2013 Peter Sunde <peter.sunde@gmail.com>
 * All rights reserved.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *
 *     - Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     - Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     - Neither the name of Evain Jb nor the names of its contributors may
 *       be used to endorse or promote products derived from this software
 *       without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS"
 * AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE
 * IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
 * ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE
 * LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR
 * CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF
 * SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN
 * CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE)
 * ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE
 * POSSIBILITY OF SUCH DAMAGE.
 *
 *****************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using ILRepacking;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Build.Utilities;

[assembly: InternalsVisibleTo("ILRepack.MSBuild.Task.Tests")]

namespace ILRepack.MSBuild.Task
{


    [SuppressMessage("ReSharper", "UnusedMember.Global")]
    [SuppressMessage("ReSharper", "InconsistentNaming")]
    public class ILRepack : Microsoft.Build.Utilities.Task, IDisposable
    {
        string _logFile;
        string _outputAssembly;
#if NETFULLFRAMEWORK
        string _keyFile;
#endif
        ITaskItem[] _inputAssemblies = new ITaskItem[0];
        ILRepacking.ILRepack.Kind _outputType;
        string _excludeFileTmpPath;

#if NETFULLFRAMEWORK
        /// <summary>
        /// Specifies a keyfile to sign the output assembly
        /// </summary>
        public virtual string KeyFile
        {
            get => _keyFile;
            set => _keyFile = BuildPath(ConvertEmptyToNull(value));
        }
#endif

        internal IBuildEngine FakeBuildEngine
        {
            get => BuildEngine;
            set => BuildEngine = value;
        }

        /// <summary>
        /// Specifies a logfile to output log information.
        /// </summary>
        public virtual string LogFile
        {
            get => _logFile;
            set => _logFile = value;
        }

        /// <summary>
        /// Merges types with identical names into one
        /// </summary>
        public virtual bool Union { get; set; }

        /// <summary>
        /// Enable/disable symbol file generation
        /// </summary>
        public virtual bool DebugInfo { get; set; } = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

        /// <summary>
        /// Read assembly attributes from the given assembly file
        /// </summary>
        public virtual string AttributeAssembly { get; set; }

        /// <summary>
        /// Copy assembly attributes (by default only the
        /// primary assembly attributes are copied)
        /// </summary>
        public virtual bool CopyAttributes { get; set; }

        /// <summary>
        /// Allows multiple attributes (if type allows)
        /// </summary>
        public virtual bool AllowMultiple { get; set; }

        /// <summary>
        /// Target assembly kind (Exe|Dll|Library|WinExe|SameAsPrimaryAssembly)
        /// </summary>
        [Required]
        public virtual string OutputType
        {
            get => _outputType.ToString();
            set
            {
                var targetKind = value?.ToLowerInvariant();
                switch (targetKind)
                {
                    default:
                        _outputType = ILRepacking.ILRepack.Kind.SameAsPrimaryAssembly;
                        break;
                    case "dll":
                    case "library":
                        _outputType = ILRepacking.ILRepack.Kind.Dll;
                        break;
                    case "exe":
                        _outputType = ILRepacking.ILRepack.Kind.Exe;
                        break;
                    case "winexe":
                        _outputType = ILRepacking.ILRepack.Kind.WinExe;
                        break;
                }
            }
        }

#if NETFULLFRAMEWORK
        /// <summary>
        /// Target platform (v1, v1.1, v2, v4 supported)
        /// </summary>
        public virtual string TargetPlatformVersion { get; set; }
#endif

        /// <summary>
        /// Merge assembly xml documentation
        /// </summary>
        public bool XmlDocumentation { get; set; }

        /// <summary>
        /// List of paths to use as "include directories" when
        /// attempting to merge assemblies
        /// </summary>
        [Required]
        public virtual string WorkingDirectory { get; set; }

        /// <summary>
        /// Set all types but the ones from
        /// the first assembly 'internal'
        /// </summary>
        public virtual bool Internalize { get; set; } = true;

        /// <summary>
        /// List of assemblies that should not be interalized.
        /// </summary>
        public virtual ITaskItem[] InternalizeExcludeAssemblies { get; set; }

        /// <summary>
        /// Output name for merged assembly
        /// </summary>
        [Required]
        public virtual string OutputAssembly
        {
            get => _outputAssembly;
            set => _outputAssembly = value;
        }

        /// <summary>
        /// List of assemblies that will be merged
        /// </summary>
        public virtual ITaskItem[] InputAssemblies
        {
            get => _inputAssemblies;
            set => _inputAssemblies = value;
        }

        /// <summary>
        /// Set the keyfile, but don't sign the assembly
        /// </summary>
        public virtual bool DelaySign { get; set; }

        /// <summary>
        /// Allows to duplicate resources in output assembly
        /// (by default they're ignored)
        /// </summary>
        public virtual bool AllowDuplicateResources { get; set; }

        /// <summary>
        /// Allows assemblies with Zero PeKind (but obviously only IL will get merged)
        /// </summary>
        public virtual bool ZeroPeKind { get; set; }

        /// <summary>
        /// Use as many CPUs as possible to merge the assemblies
        /// </summary>
        public virtual bool Parallel { get; set; } = true;

        /// <summary>
        /// Name of primary assembly when used in conjunction
        /// with Internalize.
        /// </summary>
        [Required]
        public virtual string MainAssembly { get; set; }

        /// <summary>
        /// Additional debug information during merge that
        /// will be outputted to LogFile
        /// </summary>
        public virtual bool Verbose { get; set; }

        /// <summary>
        /// Allows (and resolves) file wildcards (e.g. `*`.dll)
        /// in input assemblies
        /// </summary>
        public virtual bool WilcardInputAssemblies { get; set; }

        public override bool Execute()
        {
            if (DebugInfo && !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Log.LogError($"{nameof(ILRepacking.ILRepack)}: PDB (debug info) writer is only available on Windows.");
                return false;
            }

            WorkingDirectory = WorkingDirectory == null ? null : Path.GetFullPath(WorkingDirectory);
            if (WorkingDirectory == null || !Directory.Exists(WorkingDirectory))
            {
                Log.LogError($"{nameof(ILRepack)}: The working directory you specified does not exist: {WorkingDirectory}.");
                return false;
            }

            MainAssembly = MainAssembly == null ? null : Path.IsPathRooted(MainAssembly) ? MainAssembly : Path.Combine(WorkingDirectory, MainAssembly);
            if (MainAssembly == null || !File.Exists(MainAssembly))
            {
                Log.LogError($"{nameof(ILRepack)}: The main assembly you specified does not exist: {MainAssembly}.");
                return false;
            }

            OutputAssembly = OutputAssembly == null ? null : Path.IsPathRooted(OutputAssembly) ? OutputAssembly : Path.Combine(WorkingDirectory, Path.GetFileName(OutputAssembly));
            if (OutputAssembly == null)
            {
                Log.LogError($"{nameof(ILRepack)}: Please specify a output assembly.");
                return false;
            }

            if (WilcardInputAssemblies)
            {
                if (InputAssemblies == null || !InputAssemblies.Any())
                {
                    Log.LogError($"{nameof(ILRepack)}: Please specify wildcards using {nameof(InputAssemblies)} property. E.g. *.dll");
                    return false;
                }
            }
            else
            {
                for (var index = 0; index < InputAssemblies.Length; index++)
                {
                    var inputAssembly = InputAssemblies[index]?.ItemSpec;
                    var inputAssemblyWorkingDirectory =  Path.IsPathRooted(inputAssembly) ? inputAssembly : Path.Combine(WorkingDirectory, inputAssembly ?? string.Empty);
                    if (!File.Exists(inputAssemblyWorkingDirectory))
                    {
                        Log.LogError($"{nameof(ILRepack)}: Unable to resolve input assembly at index {index}: {inputAssemblyWorkingDirectory}.");
                        return false;
                    }

                    InputAssemblies[index] = new TaskItem(inputAssemblyWorkingDirectory);
                }
            }

            if (InternalizeExcludeAssemblies != null && InternalizeExcludeAssemblies.Any())
            {
                for (var index = 0; index < InternalizeExcludeAssemblies.Length; index++)
                {
                    var excludeAssembly = InternalizeExcludeAssemblies[index]?.ItemSpec;
                    var excludeAssemblyWorkingDirectory = Path.IsPathRooted(excludeAssembly) ? excludeAssembly : Path.Combine(WorkingDirectory, excludeAssembly ?? string.Empty);
                    if (!File.Exists(excludeAssemblyWorkingDirectory))
                    {
                        Log.LogError($"{nameof(ILRepack)}: Unable to resolve exclude assembly at index {index}: {excludeAssemblyWorkingDirectory}.");
                        return false;
                    }

                    InternalizeExcludeAssemblies[index] = new TaskItem(excludeAssemblyWorkingDirectory);
                }

                _excludeFileTmpPath = Path.GetTempFileName();

                File.WriteAllLines(_excludeFileTmpPath, 
                    InternalizeExcludeAssemblies.Select(x =>
                    {
                        // Use forward slashes or ILRepack dies :(
                        return x.ItemSpec.Replace('\\', '/');
                    }).ToArray(), new UTF8Encoding(false) /* no byte order marker */);
            }

            try
            {
                var repackOptions = new RepackOptions
                {
                    #if NETFULLFRAMEWORK
                    KeyFile = _keyFile,
                    #endif
                    LogFile = _logFile,
                    Log = !string.IsNullOrEmpty(_logFile),
                    LogVerbose = Verbose,
                    UnionMerge = Union,
                    DebugInfo = DebugInfo,
                    CopyAttributes = CopyAttributes,
                    AttributeFile = AttributeAssembly,
                    AllowMultipleAssemblyLevelAttributes = AllowMultiple,
                    TargetKind = _outputType,
                    #if NETFULLFRAMEWORK
                    TargetPlatformVersion = TargetPlatformVersion,
                    #endif
                    XmlDocumentation = XmlDocumentation,
                    Internalize = Internalize,
                    DelaySign = DelaySign,
                    AllowDuplicateResources = AllowDuplicateResources,
                    AllowZeroPeKind = ZeroPeKind,
                    Parallel = Parallel,
                    OutputFile = _outputAssembly,
                    AllowWildCards = WilcardInputAssemblies,
                    InputAssemblies = InputAssemblies.Select(x => x.ItemSpec.ToString()).Distinct().ToArray(),
                    SearchDirectories = new List<string> {WorkingDirectory},
                    ExcludeFile =  _excludeFileTmpPath
                };

                Log.LogMessage(MessageImportance.High, $"{nameof(ILRepack)}: Output type: {OutputType}.");
                Log.LogMessage(MessageImportance.High, $"{nameof(ILRepack)}: Internalize: {(Internalize ? "yes" : "no")}.");
                Log.LogMessage(MessageImportance.High, $"{nameof(ILRepack)}: Working directory: {WorkingDirectory}.");
                Log.LogMessage(MessageImportance.High, $"{nameof(ILRepack)}: Main assembly: {Path.GetFileName(MainAssembly)}.");
                Log.LogMessage(MessageImportance.High, $"{nameof(ILRepack)}: Output assembly: {Path.GetFileName(OutputAssembly)}.");

                Log.LogMessage(MessageImportance.High,
                    WilcardInputAssemblies
                        ? $"{nameof(ILRepack)}: Input assemblies (wildcards): {string.Join(",", InputAssemblies.Select(x => x.ItemSpec))}."
                        : $"{nameof(ILRepack)}: Input assemblies ({InputAssemblies.Length}): {string.Join(",", InputAssemblies.Select(x => Path.GetFileName(x.ItemSpec)))}.");

                if (InternalizeExcludeAssemblies != null && InternalizeExcludeAssemblies.Length > 0)
                {
                    Log.LogMessage(MessageImportance.High, 
                        $"{nameof(ILRepack)}: Internalize exclude assemblies ({InternalizeExcludeAssemblies.Length}): " +
                        $"{string.Join(",", InternalizeExcludeAssemblies.Select(x => Path.GetFileName(x.ItemSpec)))}.");
                }

                var ilMerger = new ILRepacking.ILRepack(repackOptions, new ILRepackLogger(this)
                {
                    ShouldLogVerbose = Verbose
                });

                ilMerger.Repack();
            }
            catch (Exception e)
            {
                Log.LogErrorFromException(e, true);
                return false;
            }

            return true;
        }

        public void Dispose()
        {
            if (_excludeFileTmpPath != null && File.Exists(_excludeFileTmpPath))
            {
                File.Delete(_excludeFileTmpPath);
            }
        }

    }
}
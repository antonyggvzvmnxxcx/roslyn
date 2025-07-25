﻿' Licensed to the .NET Foundation under one or more agreements.
' The .NET Foundation licenses this file to you under the MIT license.
' See the LICENSE file in the project root for more information.

Imports System.IO
Imports System.Reflection.PortableExecutable
Imports Microsoft.CodeAnalysis.Scripting
Imports Microsoft.CodeAnalysis.Scripting.Hosting

Namespace Microsoft.CodeAnalysis.VisualBasic.Scripting.Hosting

    Friend NotInheritable Class VisualBasicInteractiveCompiler
        Inherits VisualBasicCompiler

        Private ReadOnly _createFromFileFunc As Func(Of String, PEStreamOptions, MetadataReferenceProperties, MetadataImageReference)

        Friend Sub New(responseFile As String, buildPaths As BuildPaths, args As String(), analyzerLoader As IAnalyzerAssemblyLoader, Optional createFromFileFunc As Func(Of String, PEStreamOptions, MetadataReferenceProperties, MetadataImageReference) = Nothing)
            MyBase.New(VisualBasicCommandLineParser.Script, responseFile, args, buildPaths, Nothing, analyzerLoader)
            If createFromFileFunc Is Nothing Then
                createFromFileFunc = AddressOf Script.CreateFromFile
            End If
            _createFromFileFunc = createFromFileFunc
        End Sub

        Friend Overrides Function GetCommandLineMetadataReferenceResolver(loggerOpt As TouchedFileLogger) As MetadataReferenceResolver
            Return CommandLineRunner.GetMetadataReferenceResolver(Arguments, loggerOpt, _createFromFileFunc)
        End Function

        Friend Overrides ReadOnly Property Type As Type
            Get
                Return GetType(VisualBasicInteractiveCompiler)
            End Get
        End Property

        Public Overrides Sub PrintLogo(consoleOutput As TextWriter)
            consoleOutput.WriteLine(VBScriptingResources.LogoLine1, GetCompilerVersion())
            consoleOutput.WriteLine(VBScriptingResources.LogoLine2)
            consoleOutput.WriteLine()
        End Sub

        Public Overrides Sub PrintHelp(consoleOutput As TextWriter)
            consoleOutput.Write(VBScriptingResources.InteractiveHelp)
        End Sub
    End Class

End Namespace

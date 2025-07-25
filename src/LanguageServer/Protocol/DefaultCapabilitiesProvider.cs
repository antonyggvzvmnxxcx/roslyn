﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Completion;
using Microsoft.CodeAnalysis.Completion.Providers;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageServer.Handler.Completion;
using Microsoft.CodeAnalysis.LanguageServer.Handler.SemanticTokens;
using Microsoft.CodeAnalysis.SignatureHelp;
using Roslyn.LanguageServer.Protocol;

namespace Microsoft.CodeAnalysis.LanguageServer;

[Export(typeof(ExperimentalCapabilitiesProvider)), Shared]
internal sealed class ExperimentalCapabilitiesProvider : ICapabilitiesProvider
{
    private readonly ImmutableArray<Lazy<CompletionProvider, CompletionProviderMetadata>> _completionProviders;
    private readonly ImmutableArray<Lazy<ISignatureHelpProvider, OrderableLanguageMetadata>> _signatureHelpProviders;

    [ImportingConstructor]
    [Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
    public ExperimentalCapabilitiesProvider(
        [ImportMany] IEnumerable<Lazy<CompletionProvider, CompletionProviderMetadata>> completionProviders,
        [ImportMany] IEnumerable<Lazy<ISignatureHelpProvider, OrderableLanguageMetadata>> signatureHelpProviders)
    {
        _completionProviders = [.. completionProviders.Where(lz => lz.Metadata.Language is LanguageNames.CSharp or LanguageNames.VisualBasic)];
        _signatureHelpProviders = [.. signatureHelpProviders.Where(lz => lz.Metadata.Language is LanguageNames.CSharp or LanguageNames.VisualBasic)];
    }

    public void Initialize()
    {
        // Force completion providers to resolve in initialize, because it means MEF parts will be loaded.
        // We need to do this before GetCapabilities is called as that is on the UI thread, and loading MEF parts
        // could cause assembly loads, which we want to do off the UI thread.
        foreach (var completionProvider in _completionProviders)
        {
            _ = completionProvider.Value;
        }
        foreach (var signatureHelpProvider in _signatureHelpProviders)
        {
            _ = signatureHelpProvider.Value;
        }
    }

    public ServerCapabilities GetCapabilities(ClientCapabilities clientCapabilities)
    {
        var supportsVsExtensions = clientCapabilities.HasVisualStudioLspCapability();
        var capabilities = supportsVsExtensions ? GetVSServerCapabilities() : new VSInternalServerCapabilities();

        var commitCharacters = CompletionResultFactory.DefaultCommitCharactersArray;
        var triggerCharacters = _completionProviders.SelectMany(
            lz => CommonCompletionUtilities.GetTriggerCharacters(lz.Value)).Distinct().Select(c => c.ToString()).ToArray();

        capabilities.DefinitionProvider = true;
        capabilities.TypeDefinitionProvider = true;
        capabilities.DocumentHighlightProvider = true;
        capabilities.RenameProvider = new RenameOptions
        {
            PrepareProvider = true,
        };
        capabilities.ImplementationProvider = true;
        capabilities.CodeActionProvider = new CodeActionOptions { CodeActionKinds = [CodeActionKind.QuickFix, CodeActionKind.Refactor], ResolveProvider = true };
        capabilities.CompletionProvider = new Roslyn.LanguageServer.Protocol.CompletionOptions
        {
            ResolveProvider = true,
            AllCommitCharacters = commitCharacters,
            TriggerCharacters = triggerCharacters,
        };

        var signatureHelpTriggerCharacters = _signatureHelpProviders.SelectMany(
            lz => lz.Value.TriggerCharacters).Distinct().Select(c => c.ToString()).ToArray();
        var signatureHelpRetriggerCharacters = _signatureHelpProviders.SelectMany(
            lz => lz.Value.RetriggerCharacters).Distinct().Select(c => c.ToString()).ToArray();

        capabilities.SignatureHelpProvider = new SignatureHelpOptions { TriggerCharacters = signatureHelpTriggerCharacters, RetriggerCharacters = signatureHelpRetriggerCharacters };
        capabilities.DocumentSymbolProvider = true;
        capabilities.WorkspaceSymbolProvider = true;
        capabilities.DocumentFormattingProvider = true;
        capabilities.DocumentRangeFormattingProvider = true;
        capabilities.DocumentOnTypeFormattingProvider = new DocumentOnTypeFormattingOptions { FirstTriggerCharacter = "}", MoreTriggerCharacter = [";", "\n"] };
        capabilities.ReferencesProvider = new ReferenceOptions
        {
            WorkDoneProgress = true,
        };

        capabilities.FoldingRangeProvider = true;
        capabilities.ExecuteCommandProvider = new ExecuteCommandOptions() { Commands = [] };
        capabilities.TextDocumentSync = new TextDocumentSyncOptions
        {
            Change = TextDocumentSyncKind.Incremental,
            OpenClose = true
        };

        capabilities.HoverProvider = true;

        // Using only range handling has shown to be more performant than using a combination of full/edits/range
        // handling, especially for larger files. With range handling, we only need to compute tokens for whatever
        // is in view, while with full/edits handling we need to compute tokens for the entire file and then
        // potentially run a diff between the old and new tokens. Therefore, we only enable full handling if
        // the client does not support ranges.
        var rangeCapabilities = clientCapabilities.TextDocument?.SemanticTokens?.Requests?.Range;
        var supportsSemanticTokensRange = rangeCapabilities?.Value is not (false or null);
        capabilities.SemanticTokensOptions = new SemanticTokensOptions
        {
            Full = !supportsSemanticTokensRange,
            Range = true,
            Legend = new SemanticTokensLegend
            {
                TokenTypes = [.. SemanticTokensSchema.GetSchema(clientCapabilities.HasVisualStudioLspCapability()).AllTokenTypes],
                TokenModifiers = SemanticTokensSchema.TokenModifiers
            }
        };

        capabilities.CodeLensProvider = new CodeLensOptions
        {
            ResolveProvider = true,
            // TODO - Code lens should support streaming
            // See https://devdiv.visualstudio.com/DevDiv/_workitems/edit/1730465
            WorkDoneProgress = false,
        };

        capabilities.InlayHintOptions = new InlayHintOptions
        {
            ResolveProvider = true,
            WorkDoneProgress = false,
        };

        // Using VS server capabilities because we have our own custom client.
        capabilities.OnAutoInsertProvider = new VSInternalDocumentOnAutoInsertOptions { TriggerCharacters = ["'", "/", "\n"] };

        var diagnosticDynamicRegistationCapabilities = clientCapabilities.TextDocument?.Diagnostic?.DynamicRegistration;
        if (diagnosticDynamicRegistationCapabilities is false)
        {
            capabilities.DiagnosticOptions = new DiagnosticOptions()
            {
                InterFileDependencies = true
            };
        }

        return capabilities;
    }

    private static VSInternalServerCapabilities GetVSServerCapabilities()
        => new()
        {
            ProjectContextProvider = true,
            BreakableRangeProvider = true,

            // Diagnostic requests are only supported from PullDiagnosticsInProcLanguageClient.
            SupportsDiagnosticRequests = false,
        };
}

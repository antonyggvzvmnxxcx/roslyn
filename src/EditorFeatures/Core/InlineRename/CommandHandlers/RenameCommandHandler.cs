﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.ComponentModel.Composition;
using System.Linq;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Notification;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Telemetry;
using Microsoft.VisualStudio.Commanding;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace Microsoft.CodeAnalysis.Editor.Implementation.InlineRename;

[Export(typeof(ICommandHandler))]
[ContentType(ContentTypeNames.RoslynContentType)]
[ContentType(ContentTypeNames.XamlContentType)]
[Name(PredefinedCommandHandlerNames.Rename)]
// Line commit and rename are both executed on Save. Ensure any rename session is committed
// before line commit runs to ensure changes from both are correctly applied.
[Order(Before = PredefinedCommandHandlerNames.Commit)]
// Commit rename before invoking command-based refactorings
[Order(Before = PredefinedCommandHandlerNames.ChangeSignature)]
[Order(Before = PredefinedCommandHandlerNames.ExtractInterface)]
[Order(Before = PredefinedCommandHandlerNames.EncapsulateField)]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed partial class RenameCommandHandler(
    IThreadingContext threadingContext,
    InlineRenameService renameService,
    IAsynchronousOperationListenerProvider asynchronousOperationListenerProvider)
    : AbstractRenameCommandHandler(threadingContext, renameService, asynchronousOperationListenerProvider.GetListener(FeatureAttribute.Rename))
{
    protected override void SetFocusToTextView(ITextView textView)
    {
        (textView as IWpfTextView)?.VisualElement.Focus();
    }

    protected override void SetFocusToAdornment(ITextView textView)
    {
        if (GetAdornment(textView) is { } adornment)
        {
            adornment.Focus();
        }
    }

    private static InlineRenameAdornment? GetAdornment(ITextView textView)
    {
        // If our adornment layer somehow didn't get composed, GetAdornmentLayer will throw.
        // Don't crash if that happens.
        try
        {
            var adornment = ((IWpfTextView)textView).GetAdornmentLayer("RoslynRenameDashboard");
            return adornment.Elements.Any()
                ? adornment.Elements[0].Adornment as InlineRenameAdornment
                : null;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    protected override void CommitAndSetFocus(InlineRenameSession activeSession, ITextView textView, IUIThreadOperationContext operationContext)
    {
        try
        {
            base.CommitAndSetFocus(activeSession, textView, operationContext);
        }
        catch (NotSupportedException ex)
        {
            // Session.Commit can throw if it can't commit
            // rename operation.
            // handle that case gracefully
            var notificationService = activeSession.Workspace.Services.GetService<INotificationService>();
            notificationService?.SendNotification(ex.Message, title: EditorFeaturesResources.Rename, severity: NotificationSeverity.Error);
        }
        catch (Exception ex) when (FatalError.ReportAndCatch(ex, ErrorSeverity.Critical))
        {
            // Show a nice error to the user via an info bar
            var errorReportingService = activeSession.Workspace.Services.GetService<IErrorReportingService>();
            if (errorReportingService is null)
            {
                return;
            }

            errorReportingService.ShowGlobalErrorInfo(
                message: string.Format(EditorFeaturesWpfResources.Error_performing_rename_0, ex.Message),
                TelemetryFeatureName.InlineRename,
                ex,
                new InfoBarUI(
                    WorkspacesResources.Show_Stack_Trace,
                    InfoBarUI.UIKind.HyperLink,
                    () => errorReportingService.ShowDetailedErrorInfo(ex), closeAfterAction: true));
        }
    }
}

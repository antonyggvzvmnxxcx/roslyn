// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Components;
using Microsoft.AspNetCore.Razor.Language.Extensions;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using Microsoft.AspNetCore.Razor.PooledObjects;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Razor.Formatting;
using Microsoft.CodeAnalysis.Razor.Protocol;
using Microsoft.CodeAnalysis.Razor.Workspaces;
using Microsoft.CodeAnalysis.Text;
using RoslynSyntaxNode = Microsoft.CodeAnalysis.SyntaxNode;

namespace Microsoft.CodeAnalysis.Razor.DocumentMapping;

internal partial class RazorEditService
{
    private static void AddMemberChanges(ref PooledArrayBuilder<RazorTextChange> edits, RazorCodeDocument codeDocument, ImmutableArray<CSharpMember> addedMembers, RazorFormattingOptions options)
    {
        if (addedMembers.Length == 0)
        {
            return;
        }

        var tree = codeDocument.GetRequiredTagHelperRewrittenSyntaxTree();
        var firstDirective = tree.EnumerateDirectives<RazorDirectiveSyntax>(static dir => dir.IsCodeDirective() || dir.IsFunctionsDirective()).FirstOrDefault();

        var csharpCodeBlock = firstDirective?.DirectiveBody.CSharpCode;
        if (csharpCodeBlock is null ||
            !csharpCodeBlock.Children.TryGetOpenBraceNode(out var openBrace) ||
            !csharpCodeBlock.Children.TryGetCloseBraceNode(out var closeBrace))
        {
            AddMembersInNewCodeBlock(ref edits, codeDocument, addedMembers, options);
            return;
        }

        var source = codeDocument.Source;
        var sourceText = source.Text;
        var openBraceLine = openBrace.GetSourceLocation(source).LineIndex;
        var closeBraceLocation = closeBrace.GetSourceLocation(source);
        var closeBraceLine = closeBraceLocation.LineIndex;

        var insertAbsoluteIndex = closeBraceLocation.AbsoluteIndex;
        var insertLineIndex = closeBraceLine;

        if (openBraceLine != closeBraceLine && closeBraceLocation.AbsoluteIndex > 0)
        {
            var previousLineAbsoluteIndex = closeBraceLocation.AbsoluteIndex - closeBraceLocation.CharacterIndex - 1;
            var previousLinePosition = sourceText.GetLinePosition(previousLineAbsoluteIndex);
            var previousLine = sourceText.Lines[previousLinePosition.Line];

            if (IsLineEmpty(previousLine))
            {
                insertAbsoluteIndex = previousLine.End;
                insertLineIndex = previousLine.LineNumber;
            }
        }

        using var _ = StringBuilderPool.GetPooledObject(out var builder);
        AddMembersInExistingCodeBlock(builder, sourceText, addedMembers, options, openBraceLine, closeBraceLine, insertLineIndex);

        edits.Add(new RazorTextChange()
        {
            Span = new RazorTextSpan
            {
                Start = insertAbsoluteIndex,
                Length = 0
            },
            NewText = builder.ToString()
        });
    }

    private static void AddMembersInNewCodeBlock(ref PooledArrayBuilder<RazorTextChange> edits, RazorCodeDocument codeDocument, ImmutableArray<CSharpMember> members, RazorFormattingOptions options)
    {
        var sourceText = codeDocument.Source.Text;
        var lastLine = sourceText.Lines[^1];

        using var _ = StringBuilderPool.GetPooledObject(out var builder);

        if (!IsLineEmpty(lastLine))
        {
            builder.AppendLine();
        }

        builder.Append('@');
        builder.Append(codeDocument.FileKind == RazorFileKind.Legacy
            ? FunctionsDirective.Directive.Directive
            : ComponentCodeDirective.Directive.Directive);
        if (options.CodeBlockBraceOnNextLine)
        {
            builder.AppendLine();
        }
        else
        {
            builder.Append(' ');
        }

        builder.Append('{');
        builder.AppendLine();
        AppendMembersText(builder, members, options);
        builder.AppendLine();
        builder.Append('}');

        edits.Add(new RazorTextChange()
        {
            Span = new RazorTextSpan
            {
                Start = lastLine.End,
                Length = 0
            },
            NewText = builder.ToString()
        });
    }

    private static void AddMembersInExistingCodeBlock(StringBuilder builder, SourceText sourceText, ImmutableArray<CSharpMember> addedMembers, RazorFormattingOptions options, int openBraceLineIndex, int closeBraceLineIndex, int insertLineIndex)
    {
        var lineAboveInsertionIsNotEmpty = insertLineIndex > 0 &&
            insertLineIndex - 1 != openBraceLineIndex &&
            !IsLineEmpty(sourceText.Lines[insertLineIndex - 1]);
        if (openBraceLineIndex == closeBraceLineIndex || lineAboveInsertionIsNotEmpty)
        {
            builder.AppendLine();
        }

        AppendMembersText(builder, addedMembers, options);

        if (openBraceLineIndex == closeBraceLineIndex || insertLineIndex == closeBraceLineIndex)
        {
            builder.AppendLine();
        }
    }

    private static void AppendMembersText(StringBuilder builder, ImmutableArray<CSharpMember> members, RazorFormattingOptions options)
    {
        var first = true;
        foreach (var member in members)
        {
            if (!first)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            first = false;

            AppendIndentedMember(builder, member, options);
        }
    }

    private static void AppendIndentedMember(StringBuilder builder, CSharpMember member, RazorFormattingOptions options)
    {
        // Roslyn will have indented the member by an appropriate amount for the generated file, but we need it to be placed nicely in the Razor
        // file, so we add each line one at a time, adjusting the indentation as we go.
        int? initialIndentation = null;
        var sourceText = member.Text;

        var endLine = member.GetEndLineNumber();
        for (var i = member.GetStartLineNumber(); i <= endLine; i++)
        {
            var line = sourceText.Lines[i];
            var currentIndentation = line.GetIndentationSize(options.TabSize);

            if (initialIndentation is null)
            {
                // The indentation of the first line is used as the baseline
                initialIndentation = currentIndentation;
            }
            else
            {
                builder.AppendLine();
            }

            if (line.GetFirstNonWhitespaceOffset() is int offset)
            {
                // New indentation is the Roslyn indentation, minus the baseline indentation, plus our desired indentation, which is just one
                // level, to nest inside the @code block.
                var newIndentation = options.TabSize + currentIndentation - initialIndentation.GetValueOrDefault();
                builder.Append(FormattingUtilities.GetIndentationString(Math.Max(0, newIndentation), options.InsertSpaces, options.TabSize));
                builder.Append(sourceText.ToString(TextSpan.FromBounds(line.Start + offset, line.End)));
            }
        }
    }

    private static ImmutableArray<CSharpMember> FindMembers(RoslynSyntaxNode syntaxRoot, SourceText sourceText)
    {
        if (!syntaxRoot.TryGetClassDeclaration(out var classDecl))
        {
            return [];
        }

        using var members = new PooledArrayBuilder<CSharpMember>();
        foreach (var member in classDecl.Members)
        {
            if (TryCreateMember(member, sourceText) is { } csharpMember)
            {
                members.Add(csharpMember);
            }
        }

        return members.ToImmutableAndClear();
    }

    private static CSharpMember? TryCreateMember(MemberDeclarationSyntax member, SourceText sourceText)
        => member switch
        {
            MethodDeclarationSyntax method => new(method, GetComparisonSpan(method), sourceText),
            PropertyDeclarationSyntax property => new(property, GetComparisonSpan(property), sourceText),
            FieldDeclarationSyntax field => new(field, GetComparisonSpan(field), sourceText),
            _ => null,
        };

    private static TextSpan GetComparisonSpan(MethodDeclarationSyntax method)
    {
        // Since we only want to know about additions, we need to ignore any body changes, so we end our comparison span
        // before the body, or expression body, starts. This prevents changes inside method bodies that are entirely unmapped
        // causing us to add that method. Since an existing unmapped method can only be present if the Razor compiler emitted
        // it, we never want those in the Razor file.
        // Strictly speaking this is comparing more than necessary - since a C# method can't be overloaded by return type for
        // example, having that as part of the comparison is redundant. Same for visibility modifiers, which would seem to show
        // a bug in this logic: If Roslyn changes a method from public to private via a code action, that would appear to this
        // logic as an addition. In reality though, such a change would have to be in a mappable region to be a valid code action,
        // so the edits will have been processed already, and not seen by this code. For a method to go from public to private
        // in an unmappable region means Roslyn is changing one of the Razor compiler generated methods, which the user can
        // never see or interact with.
        // If the user has an incomplete method, then we are safe to just use the end of the method node.
        if (((SyntaxNode?)method.Body ?? method.ExpressionBody)?.SpanStart is not { } spanEnd)
        {
            spanEnd = method.Span.End;
        }

        return TextSpan.FromBounds(method.SpanStart, spanEnd);
    }

    private static TextSpan GetComparisonSpan(PropertyDeclarationSyntax property)
    {
        // Properties can't be overloaded, so the name alone is enough to tell whether a generated property
        // already exists. Keeping the comparison this narrow avoids treating accessor, initializer, or
        // modifier changes as additions.
        return property.Identifier.Span;
    }

    private static TextSpan GetComparisonSpan(FieldDeclarationSyntax field)
    {
        // Fields can't be overloaded either, so the declared variable name(s) are enough to identify an
        // existing generated field. Comparing only that span avoids treating modifier or initializer changes
        // as additions.
        var variables = field.Declaration.Variables;
        if (variables.Count == 0)
        {
            return field.Declaration.Span;
        }

        return TextSpan.FromBounds(variables[0].Identifier.SpanStart, variables[^1].Identifier.Span.End);
    }

    private static bool IsLineEmpty(TextLine textLine)
        => textLine.Start == textLine.End;

}

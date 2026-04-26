// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Microsoft.CodeAnalysis.Razor.DocumentMapping;

internal partial class RazorEditService
{
    private sealed class CSharpMember(MemberDeclarationSyntax member, TextSpan comparisonSpan, SourceText text) : IEquatable<CSharpMember>
    {
        private readonly MemberDeclarationSyntax _member = member;
        private readonly TextSpan _comparisonSpan = comparisonSpan;
        public SourceText Text { get; } = text;

        public bool Equals(CSharpMember? other)
        {
            if (other is null || _member.RawKind != other._member.RawKind)
            {
                return false;
            }

            return Text.NonWhitespaceContentEquals(other.Text, _comparisonSpan.Start, _comparisonSpan.End, other._comparisonSpan.Start, other._comparisonSpan.End);
        }

        public override bool Equals(object? obj)
            => Equals(obj as CSharpMember);

        public override int GetHashCode()
        {
            // Given the gymnastics we are doing to construct a modified generated document, we want to always fallback to the Equals check
            // as that is the only actual trustworthy comparison we can do. Constructing a string from the source text without whitespace just
            // to get the hashcode seems like overkill for the amount of members we expect to be added/removed in a typical code action.
            return 0;
        }

        // We don't want trivia, because it will include generated artifacts like #line directives, so using Span instead of FullSpan in the two
        // methods below is deliberate
        public int GetStartLineNumber()
            => Text.Lines.GetLineFromPosition(_member.SpanStart).LineNumber;

        public int GetEndLineNumber()
            => Text.Lines.GetLineFromPosition(Math.Max(_member.SpanStart, _member.Span.End - 1)).LineNumber;
    }
}

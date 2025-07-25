﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Collections;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Simplification;
using Microsoft.CodeAnalysis.CSharp.Symbols;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.CSharp.Utilities;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Rename.ConflictEngine;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Simplification;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.CSharp.Rename;

[ExportLanguageService(typeof(IRenameRewriterLanguageService), LanguageNames.CSharp), Shared]
[method: ImportingConstructor]
[method: Obsolete(MefConstruction.ImportingConstructorMessage, error: true)]
internal sealed class CSharpRenameConflictLanguageService() : AbstractRenameRewriterLanguageService
{
    #region "Annotation"

    public override SyntaxNode AnnotateAndRename(RenameRewriterParameters parameters)
    {
        var renameAnnotationRewriter = new RenameRewriter(parameters);
        return renameAnnotationRewriter.Visit(parameters.SyntaxRoot)!;
    }

    private sealed class RenameRewriter : CSharpSyntaxRewriter
    {
        private readonly DocumentId _documentId;
        private readonly RenameAnnotation _renameRenamableSymbolDeclaration;
        private readonly Solution _solution;
        private readonly string _replacementText;
        private readonly string _originalText;
        private readonly ImmutableArray<string> _possibleNameConflicts;
        private readonly ImmutableDictionary<TextSpan, RenameLocation> _renameLocations;
        private readonly ImmutableHashSet<TextSpan> _conflictLocations;
        private readonly SemanticModel _semanticModel;
        private readonly CancellationToken _cancellationToken;

        private readonly ISymbol _renamedSymbol;
        private readonly IAliasSymbol? _aliasSymbol;
        private readonly Location? _renamableDeclarationLocation;

        private readonly RenamedSpansTracker _renameSpansTracker;
        private readonly bool _isVerbatim;
        private readonly bool _replacementTextValid;
        private readonly ISimplificationService _simplificationService;
        private readonly ISemanticFactsService _semanticFactsService;
        private readonly HashSet<SyntaxToken> _annotatedIdentifierTokens = [];
        private readonly HashSet<InvocationExpressionSyntax> _invocationExpressionsNeedingConflictChecks = [];

        private readonly AnnotationTable<RenameAnnotation> _renameAnnotations;

        /// <summary>
        /// Flag indicating if we should perform a rename inside string literals.
        /// </summary>
        private readonly bool _isRenamingInStrings;

        /// <summary>
        /// Flag indicating if we should perform a rename inside comment trivia.
        /// </summary>
        private readonly bool _isRenamingInComments;

        /// <summary>
        /// A map from spans of tokens needing rename within strings or comments to an optional
        /// set of specific sub-spans within the token span that
        /// have <see cref="_originalText"/> matches and should be renamed.
        /// If this sorted set is null, it indicates that sub-spans to rename within the token span
        /// are not available, and a regex match should be performed to rename
        /// all <see cref="_originalText"/> matches within the span.
        /// </summary>
        private readonly ImmutableDictionary<TextSpan, ImmutableSortedSet<TextSpan>?> _stringAndCommentTextSpans;

        public bool AnnotateForComplexification
        {
            get
            {
                return _skipRenameForComplexification > 0 && !_isProcessingComplexifiedSpans;
            }
        }

        private int _skipRenameForComplexification;
        private bool _isProcessingComplexifiedSpans;
        private List<(TextSpan oldSpan, TextSpan newSpan)>? _modifiedSubSpans;
        private SemanticModel? _speculativeModel;
        private int _isProcessingTrivia;

        private void AddModifiedSpan(TextSpan oldSpan, TextSpan newSpan)
        {
            newSpan = new TextSpan(oldSpan.Start, newSpan.Length);

            if (!_isProcessingComplexifiedSpans)
            {
                _renameSpansTracker.AddModifiedSpan(_documentId, oldSpan, newSpan);
            }
            else
            {
                RoslynDebug.Assert(_modifiedSubSpans != null);
                _modifiedSubSpans.Add((oldSpan, newSpan));
            }
        }

        public RenameRewriter(RenameRewriterParameters parameters)
            : base(visitIntoStructuredTrivia: true)
        {
            _documentId = parameters.Document.Id;
            _renameRenamableSymbolDeclaration = parameters.RenamedSymbolDeclarationAnnotation;
            _solution = parameters.OriginalSolution;
            _replacementText = parameters.ReplacementText;
            _originalText = parameters.OriginalText;
            _possibleNameConflicts = parameters.PossibleNameConflicts;
            _renameLocations = parameters.RenameLocations;
            _conflictLocations = parameters.ConflictLocationSpans;
            _cancellationToken = parameters.CancellationToken;
            _semanticModel = parameters.SemanticModel;
            _renamedSymbol = parameters.RenameSymbol;
            _replacementTextValid = parameters.ReplacementTextValid;
            _renameSpansTracker = parameters.RenameSpansTracker;
            _isRenamingInStrings = parameters.IsRenamingInStrings;
            _isRenamingInComments = parameters.IsRenamingInComments;
            _stringAndCommentTextSpans = parameters.StringAndCommentTextSpans;
            _renameAnnotations = parameters.RenameAnnotations;

            _aliasSymbol = _renamedSymbol as IAliasSymbol;
            _renamableDeclarationLocation = _renamedSymbol.Locations.FirstOrDefault(loc => loc.IsInSource && loc.SourceTree == _semanticModel.SyntaxTree);
            _isVerbatim = _replacementText.StartsWith("@", StringComparison.Ordinal);

            _simplificationService = parameters.Document.Project.Services.GetRequiredService<ISimplificationService>();
            _semanticFactsService = parameters.Document.Project.Services.GetRequiredService<ISemanticFactsService>();
        }

        public override SyntaxNode? Visit(SyntaxNode? node)
        {
            if (node == null)
            {
                return node;
            }

            var isInConflictLambdaBody = false;
            var lambdas = node.GetAncestorsOrThis(n => n is SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax);
            foreach (var lambda in lambdas)
            {
                if (_conflictLocations.Any(cf => cf.Contains(lambda.Span)))
                {
                    isInConflictLambdaBody = true;
                    break;
                }
            }

            var shouldComplexifyNode = ShouldComplexifyNode(node, isInConflictLambdaBody);

            SyntaxNode result;

            // in case the current node was identified as being a complexification target of
            // a previous node, we'll handle it accordingly.
            if (shouldComplexifyNode)
            {
                _skipRenameForComplexification++;
                result = base.Visit(node)!;
                _skipRenameForComplexification--;
                result = Complexify(node, result);
            }
            else
            {
                result = base.Visit(node)!;
            }

            return result;
        }

        private bool ShouldComplexifyNode(SyntaxNode node, bool isInConflictLambdaBody)
        {
            return !isInConflictLambdaBody &&
                   _skipRenameForComplexification == 0 &&
                   !_isProcessingComplexifiedSpans &&
                   _conflictLocations.Contains(node.Span) &&
                   (node is AttributeSyntax ||
                    node is AttributeArgumentSyntax ||
                    node is ConstructorInitializerSyntax ||
                    node is ExpressionSyntax ||
                    node is FieldDeclarationSyntax ||
                    node is StatementSyntax ||
                    node is CrefSyntax ||
                    node is XmlNameAttributeSyntax ||
                    node is TypeConstraintSyntax ||
                    node is BaseTypeSyntax);
        }

        public override SyntaxToken VisitToken(SyntaxToken token)
        {
            var shouldCheckTrivia = _stringAndCommentTextSpans.ContainsKey(token.Span);
            _isProcessingTrivia += shouldCheckTrivia ? 1 : 0;
            var newToken = base.VisitToken(token);
            _isProcessingTrivia -= shouldCheckTrivia ? 1 : 0;

            // Handle Alias annotations
            newToken = UpdateAliasAnnotation(newToken);

            // Rename matches in strings and comments
            newToken = RenameWithinToken(token, newToken);

            // We don't want to annotate XmlName with RenameActionAnnotation
            if (newToken.Parent.IsKind(SyntaxKind.XmlName))
            {
                return newToken;
            }

            var isRenameLocation = IsRenameLocation(token);

            // if this is a reference location, or the identifier token's name could possibly
            // be a conflict, we need to process this token
            var isOldText = token.ValueText == _originalText;
            var tokenNeedsConflictCheck =
                isRenameLocation ||
                token.ValueText == _replacementText ||
                isOldText ||
                _possibleNameConflicts.Contains(token.ValueText) ||
                IsPossiblyDestructorConflict(token) ||
                IsPropertyAccessorNameConflict(token);

            if (tokenNeedsConflictCheck)
            {
                newToken = RenameAndAnnotate(token, newToken, isRenameLocation, isOldText);
                if (!_isProcessingComplexifiedSpans)
                {
                    _invocationExpressionsNeedingConflictChecks.AddRange(token.GetAncestors<InvocationExpressionSyntax>());
                }
            }

            return newToken;
        }

        private bool IsPropertyAccessorNameConflict(SyntaxToken token)
            => IsGetPropertyAccessorNameConflict(token)
            || IsSetPropertyAccessorNameConflict(token)
            || IsInitPropertyAccessorNameConflict(token);

        private bool IsGetPropertyAccessorNameConflict(SyntaxToken token)
            => token.IsKind(SyntaxKind.GetKeyword)
            && IsNameConflictWithProperty("get", token.Parent as AccessorDeclarationSyntax);

        private bool IsSetPropertyAccessorNameConflict(SyntaxToken token)
            => token.IsKind(SyntaxKind.SetKeyword)
            && IsNameConflictWithProperty("set", token.Parent as AccessorDeclarationSyntax);

        private bool IsInitPropertyAccessorNameConflict(SyntaxToken token)
            => token.IsKind(SyntaxKind.InitKeyword)
            // using "set" here is intentional. The compiler generates set_PropName for both set and init accessors.
            && IsNameConflictWithProperty("set", token.Parent as AccessorDeclarationSyntax);

        private bool IsNameConflictWithProperty(string prefix, AccessorDeclarationSyntax? accessor)
            => accessor?.Parent?.Parent is PropertyDeclarationSyntax property   // 3 null checks in one: accessor -> accessor list -> property declaration
            && _replacementText.Equals(prefix + "_" + property.Identifier.Text, StringComparison.Ordinal);

        private bool IsPossiblyDestructorConflict(SyntaxToken token)
        {
            return _replacementText == "Finalize" &&
                token.IsKind(SyntaxKind.IdentifierToken) &&
                token.Parent.IsKind(SyntaxKind.DestructorDeclaration);
        }

        private SyntaxNode Complexify(SyntaxNode originalNode, SyntaxNode newNode)
        {
            _isProcessingComplexifiedSpans = true;
            _modifiedSubSpans = [];

            var annotation = new SyntaxAnnotation();
            newNode = newNode.WithAdditionalAnnotations(annotation);
            var speculativeTree = originalNode.SyntaxTree.GetRoot(_cancellationToken).ReplaceNode(originalNode, newNode);
            newNode = speculativeTree.GetAnnotatedNodes<SyntaxNode>(annotation).First();

            _speculativeModel = GetSemanticModelForNode(newNode, _semanticModel);
            RoslynDebug.Assert(_speculativeModel != null, "expanding a syntax node which cannot be speculated?");

            var oldSpan = originalNode.Span;
            var expandParameter = !originalNode.GetAncestorsOrThis(n => n is SimpleLambdaExpressionSyntax or ParenthesizedLambdaExpressionSyntax).Any();

            newNode = _simplificationService.Expand(newNode,
                                                                _speculativeModel,
                                                                annotationForReplacedAliasIdentifier: null,
                                                                expandInsideNode: null,
                                                                expandParameter: expandParameter,
                                                                cancellationToken: _cancellationToken);
            speculativeTree = originalNode.SyntaxTree.GetRoot(_cancellationToken).ReplaceNode(originalNode, newNode);
            newNode = speculativeTree.GetAnnotatedNodes<SyntaxNode>(annotation).First();

            _speculativeModel = GetSemanticModelForNode(newNode, _semanticModel);

            newNode = base.Visit(newNode)!;
            var newSpan = newNode.Span;

            newNode = newNode.WithoutAnnotations(annotation);
            newNode = _renameAnnotations.WithAdditionalAnnotations(newNode, new RenameNodeSimplificationAnnotation() { OriginalTextSpan = oldSpan });

            _renameSpansTracker.AddComplexifiedSpan(_documentId, oldSpan, new TextSpan(oldSpan.Start, newSpan.Length), _modifiedSubSpans);
            _modifiedSubSpans = null;

            _isProcessingComplexifiedSpans = false;
            _speculativeModel = null;
            return newNode;
        }

        private SyntaxToken RenameAndAnnotate(SyntaxToken token, SyntaxToken newToken, bool isRenameLocation, bool isOldText)
        {
            try
            {
                if (_isProcessingComplexifiedSpans)
                {
                    // Rename Token
                    if (isRenameLocation)
                    {
                        var annotation = _renameAnnotations.GetAnnotations(token).OfType<RenameActionAnnotation>().FirstOrDefault();
                        if (annotation != null)
                        {
                            newToken = RenameToken(token, newToken, annotation.Prefix, annotation.Suffix);
                            AddModifiedSpan(annotation.OriginalSpan, newToken.Span);
                        }
                        else
                        {
                            newToken = RenameToken(token, newToken, prefix: null, suffix: null);
                        }
                    }

                    return newToken;
                }

                var symbols = RenameUtilities.GetSymbolsTouchingPosition(token.Span.Start, _semanticModel, _solution.Services, _cancellationToken);

                string? suffix = null;
                var prefix = isRenameLocation && _renameLocations[token.Span].IsRenamableAccessor
                    ? newToken.ValueText[..(newToken.ValueText.IndexOf('_') + 1)]
                    : null;

                if (symbols.Length == 1)
                {
                    var symbol = symbols[0];

                    if (symbol.IsConstructor())
                    {
                        symbol = symbol.ContainingSymbol;
                    }

                    // We cannot make this containing method async since it's being used in a rewriter. FindSourceDefinitionAsync will only yield in cross-language cases
                    // when the compilation is not already available, so this is expected to not really cause any significant blocking.
                    var sourceDefinition = SymbolFinder.FindSourceDefinitionAsync(symbol, _solution, _cancellationToken).WaitAndGetResult_CanCallOnBackground(_cancellationToken);
                    symbol = sourceDefinition ?? symbol;

                    if (symbol is INamedTypeSymbol namedTypeSymbol)
                    {
                        if (namedTypeSymbol.IsImplicitlyDeclared &&
                            namedTypeSymbol.IsDelegateType() &&
                            namedTypeSymbol.AssociatedSymbol != null)
                        {
                            suffix = "EventHandler";
                        }
                    }

                    // This is a conflicting namespace declaration token. Even if the rename results in conflict with this namespace
                    // conflict is not shown for the namespace so we are tracking this token
                    if (!isRenameLocation && symbol is INamespaceSymbol && token.GetPreviousToken().IsKind(SyntaxKind.NamespaceKeyword))
                    {
                        return newToken;
                    }
                }

                // Rename Token
                if (isRenameLocation && !this.AnnotateForComplexification)
                {
                    var oldSpan = token.Span;
                    newToken = RenameToken(token, newToken, prefix, suffix);

                    AddModifiedSpan(oldSpan, newToken.Span);
                }

                var renameDeclarationLocations = ConflictResolver.CreateDeclarationLocationAnnotations(
                    _solution, symbols, _cancellationToken);

                var isNamespaceDeclarationReference = false;
                if (isRenameLocation && token.GetPreviousToken().IsKind(SyntaxKind.NamespaceKeyword))
                {
                    isNamespaceDeclarationReference = true;
                }

                var isMemberGroupReference = _semanticFactsService.IsInsideNameOfExpression(_semanticModel, token.Parent, _cancellationToken);

                var renameAnnotation = new RenameActionAnnotation(
                    token.Span,
                    isRenameLocation,
                    prefix,
                    suffix,
                    renameDeclarationLocations: renameDeclarationLocations,
                    isOriginalTextLocation: isOldText,
                    isNamespaceDeclarationReference: isNamespaceDeclarationReference,
                    isInvocationExpression: false,
                    isMemberGroupReference: isMemberGroupReference);

                newToken = _renameAnnotations.WithAdditionalAnnotations(newToken, renameAnnotation, new RenameTokenSimplificationAnnotation() { OriginalTextSpan = token.Span });

                _annotatedIdentifierTokens.Add(token);
                if (_renameRenamableSymbolDeclaration != null && _renamableDeclarationLocation == token.GetLocation())
                {
                    newToken = _renameAnnotations.WithAdditionalAnnotations(newToken, _renameRenamableSymbolDeclaration);
                }

                return newToken;
            }
            catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e))
            {
                throw ExceptionUtilities.Unreachable();
            }
        }

        private RenameActionAnnotation? GetAnnotationForInvocationExpression(InvocationExpressionSyntax invocationExpression)
        {
            var identifierToken = default(SyntaxToken);
            var expressionOfInvocation = invocationExpression.Expression;

            while (expressionOfInvocation != null)
            {
                switch (expressionOfInvocation.Kind())
                {
                    case SyntaxKind.IdentifierName:
                    case SyntaxKind.GenericName:
                        identifierToken = ((SimpleNameSyntax)expressionOfInvocation).Identifier;
                        break;

                    case SyntaxKind.SimpleMemberAccessExpression:
                        identifierToken = ((MemberAccessExpressionSyntax)expressionOfInvocation).Name.Identifier;
                        break;

                    case SyntaxKind.QualifiedName:
                        identifierToken = ((QualifiedNameSyntax)expressionOfInvocation).Right.Identifier;
                        break;

                    case SyntaxKind.AliasQualifiedName:
                        identifierToken = ((AliasQualifiedNameSyntax)expressionOfInvocation).Name.Identifier;
                        break;

                    case SyntaxKind.ParenthesizedExpression:
                        expressionOfInvocation = ((ParenthesizedExpressionSyntax)expressionOfInvocation).Expression;
                        continue;
                }

                break;
            }

            if (identifierToken != default && !_annotatedIdentifierTokens.Contains(identifierToken))
            {
                var symbolInfo = _semanticModel.GetSymbolInfo(invocationExpression, _cancellationToken);
                IEnumerable<ISymbol> symbols;
                if (symbolInfo.Symbol == null)
                {
                    return null;
                }
                else
                {
                    symbols = [symbolInfo.Symbol];
                }

                var renameDeclarationLocations = ConflictResolver.CreateDeclarationLocationAnnotations(
                    _solution, symbols, _cancellationToken);

                var renameAnnotation = new RenameActionAnnotation(
                    identifierToken.Span,
                    isRenameLocation: false,
                    prefix: null,
                    suffix: null,
                    renameDeclarationLocations: renameDeclarationLocations,
                    isOriginalTextLocation: false,
                    isNamespaceDeclarationReference: false,
                    isInvocationExpression: true,
                    isMemberGroupReference: false);

                return renameAnnotation;
            }

            return null;
        }

        public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
        {
            var result = base.VisitInvocationExpression(node);
            RoslynDebug.AssertNotNull(result);

            if (_invocationExpressionsNeedingConflictChecks.Contains(node))
            {
                var renameAnnotation = GetAnnotationForInvocationExpression(node);
                if (renameAnnotation != null)
                {
                    result = _renameAnnotations.WithAdditionalAnnotations(result, renameAnnotation);
                }
            }

            return result;
        }

        private bool IsRenameLocation(SyntaxToken token)
        {
            if (!_isProcessingComplexifiedSpans)
            {
                return _renameLocations.ContainsKey(token.Span);
            }
            else
            {
                RoslynDebug.Assert(_speculativeModel != null);

                if (token.HasAnnotations(AliasAnnotation.Kind))
                {
                    return false;
                }

                if (token.HasAnnotations(RenameAnnotation.Kind))
                {
                    return _renameAnnotations.GetAnnotations(token).OfType<RenameActionAnnotation>().First().IsRenameLocation;
                }

                if (token.Parent is SimpleNameSyntax &&
                    !token.IsKind(SyntaxKind.GlobalKeyword) &&
                    token.Parent.Parent is (kind: SyntaxKind.AliasQualifiedName or SyntaxKind.QualifiedCref or SyntaxKind.QualifiedName))
                {
                    var symbol = _speculativeModel.GetSymbolInfo(token.Parent, _cancellationToken).Symbol;

                    if (symbol != null && _renamedSymbol.Kind != SymbolKind.Local && _renamedSymbol.Kind != SymbolKind.RangeVariable &&
                        (Equals(symbol, _renamedSymbol) || SymbolKey.GetComparer(ignoreCase: true, ignoreAssemblyKeys: false).Equals(symbol.GetSymbolKey(), _renamedSymbol.GetSymbolKey())))
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private SyntaxToken UpdateAliasAnnotation(SyntaxToken newToken)
        {
            if (_aliasSymbol != null && !this.AnnotateForComplexification && newToken.HasAnnotations(AliasAnnotation.Kind))
            {
                newToken = RenameUtilities.UpdateAliasAnnotation(newToken, _aliasSymbol, _replacementText);
            }

            return newToken;
        }

        private SyntaxToken RenameToken(SyntaxToken oldToken, SyntaxToken newToken, string? prefix, string? suffix)
        {
            var parent = oldToken.Parent!;
            var currentNewIdentifier = _isVerbatim ? _replacementText[1..] : _replacementText;
            var oldIdentifier = newToken.ValueText;
            var isAttributeName = SyntaxFacts.IsAttributeName(parent);

            if (isAttributeName)
            {
                if (oldIdentifier != _renamedSymbol.Name)
                {
                    if (currentNewIdentifier.TryGetWithoutAttributeSuffix(out var withoutSuffix))
                    {
                        currentNewIdentifier = withoutSuffix;
                    }
                }
            }
            else
            {
                if (!string.IsNullOrEmpty(prefix))
                {
                    currentNewIdentifier = prefix + currentNewIdentifier;
                }

                if (!string.IsNullOrEmpty(suffix))
                {
                    currentNewIdentifier += suffix;
                }
            }

            // determine the canonical identifier name (unescaped, no unicode escaping, ...)
            var valueText = currentNewIdentifier;
            var kind = SyntaxFacts.GetKeywordKind(currentNewIdentifier);
            if (kind != SyntaxKind.None)
            {
                valueText = SyntaxFacts.GetText(kind);
            }
            else
            {
                var parsedIdentifier = SyntaxFactory.ParseName(currentNewIdentifier);
                if (parsedIdentifier is IdentifierNameSyntax identifierName)
                {
                    valueText = identifierName.Identifier.ValueText;
                }
            }

            // TODO: we can't use escaped unicode characters in xml doc comments, so we need to pass the valuetext as text as well.
            // <param name="\u... is invalid.

            // if it's an attribute name we don't mess with the escaping because it might change overload resolution
            newToken = _isVerbatim || (isAttributeName && oldToken.IsVerbatimIdentifier())
                ? newToken.CopyAnnotationsTo(SyntaxFactory.VerbatimIdentifier(newToken.LeadingTrivia, currentNewIdentifier, valueText, newToken.TrailingTrivia))
                : newToken.CopyAnnotationsTo(SyntaxFactory.Identifier(newToken.LeadingTrivia, SyntaxKind.IdentifierToken, currentNewIdentifier, valueText, newToken.TrailingTrivia));

            if (_replacementTextValid)
            {
                if (newToken.IsVerbatimIdentifier())
                {
                    // a reference location should always be tried to be unescaped, whether it was escaped before rename 
                    // or the replacement itself is escaped.
                    newToken = newToken.WithAdditionalAnnotations(Simplifier.Annotation);
                }
                else
                {
                    newToken = CSharpSimplificationHelpers.TryEscapeIdentifierToken(newToken, parent);
                }
            }

            return newToken;
        }

        private SyntaxToken RenameInStringLiteral(SyntaxToken oldToken, SyntaxToken newToken, ImmutableSortedSet<TextSpan>? subSpansToReplace, Func<SyntaxTriviaList, string, string, SyntaxTriviaList, SyntaxToken> createNewStringLiteral)
        {
            var originalString = newToken.ToString();
            var replacedString = RenameUtilities.ReplaceMatchingSubStrings(originalString, _originalText, _replacementText, subSpansToReplace);
            if (replacedString != originalString)
            {
                var oldSpan = oldToken.Span;
                newToken = createNewStringLiteral(newToken.LeadingTrivia, replacedString, replacedString, newToken.TrailingTrivia);
                AddModifiedSpan(oldSpan, newToken.Span);
                return newToken.CopyAnnotationsTo(_renameAnnotations.WithAdditionalAnnotations(newToken, new RenameTokenSimplificationAnnotation() { OriginalTextSpan = oldSpan }));
            }

            return newToken;
        }

        private SyntaxToken RenameInTrivia(SyntaxToken token, IEnumerable<SyntaxTrivia> leadingOrTrailingTriviaList)
        {
            return token.ReplaceTrivia(leadingOrTrailingTriviaList, (oldTrivia, newTrivia) =>
            {
                if (newTrivia.IsSingleLineComment() || newTrivia.IsMultiLineComment())
                {
                    return RenameInCommentTrivia(newTrivia);
                }

                return newTrivia;
            });
        }

        private SyntaxTrivia RenameInCommentTrivia(SyntaxTrivia trivia)
        {
            var originalString = trivia.ToString();
            var replacedString = RenameUtilities.ReplaceMatchingSubStrings(originalString, _originalText, _replacementText);
            if (replacedString != originalString)
            {
                var oldSpan = trivia.Span;
                var newTrivia = SyntaxFactory.Comment(replacedString);
                AddModifiedSpan(oldSpan, newTrivia.Span);
                return trivia.CopyAnnotationsTo(_renameAnnotations.WithAdditionalAnnotations(newTrivia, new RenameTokenSimplificationAnnotation() { OriginalTextSpan = oldSpan }));
            }

            return trivia;
        }

        private SyntaxToken RenameWithinToken(SyntaxToken oldToken, SyntaxToken newToken)
        {
            ImmutableSortedSet<TextSpan>? subSpansToReplace = null;
            if (_isProcessingComplexifiedSpans ||
                (_isProcessingTrivia == 0 &&
                !_stringAndCommentTextSpans.TryGetValue(oldToken.Span, out subSpansToReplace)))
            {
                return newToken;
            }

            if (_isRenamingInStrings || subSpansToReplace?.Count > 0)
            {
                if (newToken.IsKind(SyntaxKind.StringLiteralToken))
                {
                    newToken = RenameInStringLiteral(oldToken, newToken, subSpansToReplace, SyntaxFactory.Literal);
                }
                else if (newToken.IsKind(SyntaxKind.InterpolatedStringTextToken))
                {
                    newToken = RenameInStringLiteral(oldToken, newToken, subSpansToReplace, (leadingTrivia, text, value, trailingTrivia) =>
                        SyntaxFactory.Token(newToken.LeadingTrivia, SyntaxKind.InterpolatedStringTextToken, text, value, newToken.TrailingTrivia));
                }
            }

            if (_isRenamingInComments)
            {
                if (newToken.IsKind(SyntaxKind.XmlTextLiteralToken))
                {
                    newToken = RenameInStringLiteral(oldToken, newToken, subSpansToReplace, SyntaxFactory.XmlTextLiteral);
                }
                else if (newToken.IsKind(SyntaxKind.IdentifierToken) && newToken.Parent.IsKind(SyntaxKind.XmlName) && newToken.ValueText == _originalText)
                {
                    var newIdentifierToken = SyntaxFactory.Identifier(newToken.LeadingTrivia, _replacementText, newToken.TrailingTrivia);
                    newToken = newToken.CopyAnnotationsTo(_renameAnnotations.WithAdditionalAnnotations(newIdentifierToken, new RenameTokenSimplificationAnnotation() { OriginalTextSpan = oldToken.Span }));
                    AddModifiedSpan(oldToken.Span, newToken.Span);
                }

                if (newToken.HasLeadingTrivia)
                {
                    var updatedToken = RenameInTrivia(oldToken, oldToken.LeadingTrivia);
                    if (updatedToken != oldToken)
                    {
                        newToken = newToken.WithLeadingTrivia(updatedToken.LeadingTrivia);
                    }
                }

                if (newToken.HasTrailingTrivia)
                {
                    var updatedToken = RenameInTrivia(oldToken, oldToken.TrailingTrivia);
                    if (updatedToken != oldToken)
                    {
                        newToken = newToken.WithTrailingTrivia(updatedToken.TrailingTrivia);
                    }
                }
            }

            return newToken;
        }
    }

    #endregion

    #region "Declaration Conflicts"

    public override bool LocalVariableConflict(
        SyntaxToken token,
        IEnumerable<ISymbol> newReferencedSymbols)
    {
        if (token.Parent is ExpressionSyntax(SyntaxKind.IdentifierName) expression &&
            token.Parent.IsParentKind(SyntaxKind.InvocationExpression) &&
            token.GetPreviousToken().Kind() != SyntaxKind.DotToken &&
            token.GetNextToken().Kind() != SyntaxKind.DotToken)
        {
            var enclosingMemberDeclaration = expression.FirstAncestorOrSelf<MemberDeclarationSyntax>();
            if (enclosingMemberDeclaration != null)
            {
                var locals = enclosingMemberDeclaration.GetLocalDeclarationMap()[token.ValueText];
                if (locals.Length > 0)
                {
                    // This unqualified invocation name matches the name of an existing local
                    // or parameter. Report a conflict if the matching local/parameter is not
                    // a delegate type.

                    var relevantLocals = newReferencedSymbols
                        .Where(s => s.MatchesKind(SymbolKind.Local, SymbolKind.Parameter) && s.Name == token.ValueText);

                    if (relevantLocals.Count() != 1)
                    {
                        return true;
                    }

                    var matchingLocal = relevantLocals.Single();
                    var invocationTargetsLocalOfDelegateType =
                        (matchingLocal.IsKind(SymbolKind.Local) && ((ILocalSymbol)matchingLocal).Type.IsDelegateType()) ||
                        (matchingLocal.IsKind(SymbolKind.Parameter) && ((IParameterSymbol)matchingLocal).Type.IsDelegateType());

                    return !invocationTargetsLocalOfDelegateType;
                }
            }
        }

        return false;
    }

    public override async Task<ImmutableArray<Location>> ComputeDeclarationConflictsAsync(
        string replacementText,
        ISymbol renamedSymbol,
        ISymbol renameSymbol,
        IEnumerable<ISymbol> referencedSymbols,
        Solution baseSolution,
        Solution newSolution,
        IDictionary<Location, Location> reverseMappedLocations,
        CancellationToken cancellationToken)
    {
        try
        {
            using var _ = ArrayBuilder<Location>.GetInstance(out var conflicts);

            // If we're renaming a named type, we can conflict with members w/ our same name.  Note:
            // this doesn't apply to enums.
            if (renamedSymbol is INamedTypeSymbol { TypeKind: not TypeKind.Enum } namedType)
                AddSymbolSourceSpans(conflicts, namedType.GetMembers(renamedSymbol.Name), reverseMappedLocations);

            // If we're contained in a named type (we may be a named type ourself!) then we have a
            // conflict.  NOTE(cyrusn): This does not apply to enums. 
            if (renamedSymbol.ContainingSymbol is INamedTypeSymbol { TypeKind: not TypeKind.Enum } containingNamedType &&
                containingNamedType.Name == renamedSymbol.Name)
            {
                AddSymbolSourceSpans(conflicts, [containingNamedType], reverseMappedLocations);
            }

            if (renamedSymbol.Kind is SymbolKind.Parameter or
                SymbolKind.Local or
                SymbolKind.RangeVariable)
            {
                var token = renamedSymbol.Locations.Single().FindToken(cancellationToken);
                var memberDeclaration = token.GetAncestor<MemberDeclarationSyntax>();
                var visitor = new LocalConflictVisitor(token);

                visitor.Visit(memberDeclaration);
                conflicts.AddRange(visitor.ConflictingTokens.Select(t => reverseMappedLocations[t.GetLocation()]));

                // If this is a parameter symbol for a partial method definition, be sure we visited 
                // the implementation part's body.
                if (renamedSymbol is IParameterSymbol renamedParameterSymbol &&
                    renamedSymbol.ContainingSymbol is IMethodSymbol methodSymbol &&
                    methodSymbol.PartialImplementationPart != null)
                {
                    var matchingParameterSymbol = methodSymbol.PartialImplementationPart.Parameters[renamedParameterSymbol.Ordinal];

                    token = matchingParameterSymbol.Locations.Single().FindToken(cancellationToken);
                    memberDeclaration = token.GetAncestor<MemberDeclarationSyntax>();
                    visitor = new LocalConflictVisitor(token);
                    visitor.Visit(memberDeclaration);
                    conflicts.AddRange(visitor.ConflictingTokens.Select(t => reverseMappedLocations[t.GetLocation()]));
                }
            }
            else if (renamedSymbol.Kind == SymbolKind.Label)
            {
                var token = renamedSymbol.Locations.Single().FindToken(cancellationToken);
                var memberDeclaration = token.GetAncestor<MemberDeclarationSyntax>();
                var visitor = new LabelConflictVisitor(token);

                visitor.Visit(memberDeclaration);
                conflicts.AddRange(visitor.ConflictingTokens.Select(t => reverseMappedLocations[t.GetLocation()]));
            }
            else if (renamedSymbol.Kind == SymbolKind.Method)
            {
                conflicts.AddRange(DeclarationConflictHelpers.GetMembersWithConflictingSignatures((IMethodSymbol)renamedSymbol, trimOptionalParameters: false).Select(t => reverseMappedLocations[t]));

                // we allow renaming overrides of VB property accessors with parameters in C#.
                // VB has a special rule that properties are not allowed to have the same name as any of the parameters. 
                // Because this declaration in C# affects the property declaration in VB, we need to check this VB rule here in C#.
                var properties = new List<ISymbol>();
                foreach (var referencedSymbol in referencedSymbols)
                {
                    var property = await RenameUtilities.TryGetPropertyFromAccessorOrAnOverrideAsync(
                        referencedSymbol, baseSolution, cancellationToken).ConfigureAwait(false);
                    if (property != null)
                        properties.Add(property);
                }

                AddConflictingParametersOfProperties(properties.Distinct(), replacementText, conflicts);
            }
            else if (renamedSymbol.Kind == SymbolKind.Alias)
            {
                // in C# there can only be one using with the same alias name in the same block (top of file of namespace). 
                // It's ok to redefine the alias in different blocks.
                var location = renamedSymbol.Locations.Single();
                var tree = location.SourceTree;
                Contract.ThrowIfNull(tree);

                var token = await tree.GetTouchingTokenAsync(location.SourceSpan.Start, cancellationToken, findInsideTrivia: true).ConfigureAwait(false);
                var currentUsing = (UsingDirectiveSyntax)token.Parent!.Parent!.Parent!;

                var namespaceDecl = token.Parent.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
                SyntaxList<UsingDirectiveSyntax> usings;
                if (namespaceDecl != null)
                {
                    usings = namespaceDecl.Usings;
                }
                else
                {
                    var compilationUnit = (CompilationUnitSyntax)await tree.GetRootAsync(cancellationToken).ConfigureAwait(false);
                    usings = compilationUnit.Usings;
                }

                foreach (var usingDirective in usings)
                {
                    if (usingDirective.Alias != null && usingDirective != currentUsing)
                    {
                        if (usingDirective.Alias.Name.Identifier.ValueText == currentUsing.Alias!.Name.Identifier.ValueText)
                            conflicts.Add(reverseMappedLocations[usingDirective.Alias.Name.GetLocation()]);
                    }
                }
            }
            else if (renamedSymbol.Kind == SymbolKind.TypeParameter)
            {
                foreach (var location in renamedSymbol.Locations)
                {
                    var token = await location.SourceTree!.GetTouchingTokenAsync(location.SourceSpan.Start, cancellationToken, findInsideTrivia: true).ConfigureAwait(false);
                    var currentTypeParameter = token.Parent!;

                    foreach (var typeParameter in ((TypeParameterListSyntax)currentTypeParameter.Parent!).Parameters)
                    {
                        if (typeParameter != currentTypeParameter && token.ValueText == typeParameter.Identifier.ValueText)
                            conflicts.Add(reverseMappedLocations[typeParameter.Identifier.GetLocation()]);
                    }
                }
            }

            // if the renamed symbol is a type member, it's name should not conflict with a type parameter
            if (renamedSymbol.ContainingType != null && renamedSymbol.ContainingType.GetMembers(renamedSymbol.Name).Contains(renamedSymbol))
            {
                var conflictingLocations = renamedSymbol.ContainingType.TypeParameters
                    .Where(t => t.Name == renamedSymbol.Name)
                    .SelectMany(t => t.Locations);

                foreach (var location in conflictingLocations)
                {
                    var typeParameterToken = location.FindToken(cancellationToken);
                    conflicts.Add(reverseMappedLocations[typeParameterToken.GetLocation()]);
                }
            }

            return conflicts.ToImmutableAndClear();
        }
        catch (Exception e) when (FatalError.ReportAndPropagateUnlessCanceled(e, cancellationToken))
        {
            throw ExceptionUtilities.Unreachable();
        }
    }

    private static void AddSymbolSourceSpans(
        ArrayBuilder<Location> conflicts, IEnumerable<ISymbol> symbols,
        IDictionary<Location, Location> reverseMappedLocations)
    {
        foreach (var symbol in symbols)
        {
            foreach (var location in symbol.Locations)
            {
                // reverseMappedLocations may not contain the location if the location's token
                // does not contain the text of it's name (e.g. the getter of "int X { get; }"
                // does not contain the text "get_X" so conflicting renames to "get_X" will not
                // have added the getter to reverseMappedLocations).
                if (location.IsInSource && reverseMappedLocations.TryGetValue(location, out var conflictingLocation))
                {
                    conflicts.Add(conflictingLocation);
                }
            }
        }
    }

    public override async Task<ImmutableArray<Location>> ComputeImplicitReferenceConflictsAsync(
        ISymbol renameSymbol, ISymbol renamedSymbol, IEnumerable<ReferenceLocation> implicitReferenceLocations, CancellationToken cancellationToken)
    {
        // Handle renaming of symbols used for foreach
        var implicitReferencesMightConflict = renameSymbol.Kind == SymbolKind.Property &&
                                            string.Compare(renameSymbol.Name, "Current", StringComparison.OrdinalIgnoreCase) == 0;

        implicitReferencesMightConflict =
            implicitReferencesMightConflict ||
                (renameSymbol.Kind == SymbolKind.Method &&
                    (string.Compare(renameSymbol.Name, WellKnownMemberNames.MoveNextMethodName, StringComparison.OrdinalIgnoreCase) == 0 ||
                    string.Compare(renameSymbol.Name, WellKnownMemberNames.GetEnumeratorMethodName, StringComparison.OrdinalIgnoreCase) == 0 ||
                    string.Compare(renameSymbol.Name, WellKnownMemberNames.GetAwaiter, StringComparison.OrdinalIgnoreCase) == 0 ||
                    string.Compare(renameSymbol.Name, WellKnownMemberNames.DeconstructMethodName, StringComparison.OrdinalIgnoreCase) == 0));

        // TODO: handle Dispose for using statement and Add methods for collection initializers.

        if (implicitReferencesMightConflict)
        {
            if (renamedSymbol.Name != renameSymbol.Name)
            {
                foreach (var implicitReferenceLocation in implicitReferenceLocations)
                {
                    var token = await implicitReferenceLocation.Location.SourceTree!.GetTouchingTokenAsync(
                        implicitReferenceLocation.Location.SourceSpan.Start, cancellationToken, findInsideTrivia: false).ConfigureAwait(false);

                    switch (token.Kind())
                    {
                        case SyntaxKind.ForEachKeyword:
                            return [((CommonForEachStatementSyntax)token.Parent!).Expression.GetLocation()];
                        case SyntaxKind.AwaitKeyword:
                            return [token.GetLocation()];
                    }

                    if (token.Parent.IsInDeconstructionLeft(out var deconstructionLeft))
                    {
                        return [deconstructionLeft.GetLocation()];
                    }
                }
            }
        }

        return [];
    }

    public override ImmutableArray<Location> ComputePossibleImplicitUsageConflicts(
        ISymbol renamedSymbol,
        SemanticModel semanticModel,
        Location originalDeclarationLocation,
        int newDeclarationLocationStartingPosition,
        CancellationToken cancellationToken)
    {
        // TODO: support other implicitly used methods like dispose

        if ((renamedSymbol.Name == "MoveNext" || renamedSymbol.Name == "GetEnumerator" || renamedSymbol.Name == "Current") && renamedSymbol.GetAllTypeArguments().Length == 0)
        {
            // TODO: partial methods currently only show the location where the rename happens as a conflict.
            //       Consider showing both locations as a conflict.
            var baseType = renamedSymbol.ContainingType?.GetBaseTypes().FirstOrDefault();
            if (baseType != null)
            {
                var implicitSymbols = semanticModel.LookupSymbols(
                    newDeclarationLocationStartingPosition,
                    baseType,
                    renamedSymbol.Name)
                        .Where(sym => !sym.Equals(renamedSymbol));

                foreach (var symbol in implicitSymbols)
                {
                    if (symbol.GetAllTypeArguments().Length != 0)
                    {
                        continue;
                    }

                    if (symbol.Kind == SymbolKind.Method)
                    {
                        var method = (IMethodSymbol)symbol;

                        if (symbol.Name == "MoveNext")
                        {
                            if (!method.ReturnsVoid && !method.Parameters.Any() && method.ReturnType.SpecialType == SpecialType.System_Boolean)
                            {
                                return [originalDeclarationLocation];
                            }
                        }
                        else if (symbol.Name == "GetEnumerator")
                        {
                            // we are a bit pessimistic here. 
                            // To be sure we would need to check if the returned type is having a MoveNext and Current as required by foreach
                            if (!method.ReturnsVoid &&
                                !method.Parameters.Any())
                            {
                                return [originalDeclarationLocation];
                            }
                        }
                    }
                    else if (symbol is IPropertySymbol
                    {
                        Name: "Current",
                        Parameters.Length: 0,
                        IsWriteOnly: false,
                    })
                    {
                        return [originalDeclarationLocation];
                    }
                }
            }
        }

        return [];
    }

    #endregion

    public override void TryAddPossibleNameConflicts(ISymbol symbol, string replacementText, ICollection<string> possibleNameConflicts)
    {
        if (replacementText.EndsWith("Attribute", StringComparison.Ordinal) && replacementText.Length > 9)
        {
            var conflict = replacementText[..^9];
            if (!possibleNameConflicts.Contains(conflict))
            {
                possibleNameConflicts.Add(conflict);
            }
        }

        if (symbol.Kind == SymbolKind.Property)
        {
            foreach (var conflict in new string[] { "_" + replacementText, "get_" + replacementText, "set_" + replacementText })
            {
                if (!possibleNameConflicts.Contains(conflict))
                {
                    possibleNameConflicts.Add(conflict);
                }
            }
        }

        // in C# we also need to add the valueText because it can be different from the text in source
        // e.g. it can contain escaped unicode characters. Otherwise conflicts would be detected for
        // v\u0061r and var or similar.
        var valueText = replacementText;
        var kind = SyntaxFacts.GetKeywordKind(replacementText);
        if (kind != SyntaxKind.None)
        {
            valueText = SyntaxFacts.GetText(kind);
        }
        else
        {
            var name = SyntaxFactory.ParseName(replacementText);
            if (name is IdentifierNameSyntax identifierName)
                valueText = identifierName.Identifier.ValueText;
        }

        // this also covers the case of an escaped replacementText
        if (valueText != replacementText)
        {
            possibleNameConflicts.Add(valueText);
        }
    }

    /// <summary>
    /// Gets the top most enclosing statement or CrefSyntax as target to call MakeExplicit on.
    /// It's either the enclosing statement, or if this statement is inside of a lambda expression, the enclosing
    /// statement of this lambda.
    /// </summary>
    /// <param name="token">The token to get the complexification target for.</param>
    public override SyntaxNode? GetExpansionTargetForLocation(SyntaxToken token)
        => GetExpansionTarget(token);

    private static SyntaxNode? GetExpansionTarget(SyntaxToken token)
    {
        // get the directly enclosing statement
        var enclosingStatement = token.GetAncestor<StatementSyntax>();

        // System.Func<int, int> myFunc = arg => X;
        var possibleLambdaExpression = enclosingStatement == null
            ? token.GetAncestor<LambdaExpressionSyntax>()
            : null;
        if (possibleLambdaExpression?.ExpressionBody is not null)
            return possibleLambdaExpression.ExpressionBody;

        // int M() => X;
        var possibleArrowExpressionClause = enclosingStatement == null
            ? token.GetAncestor<ArrowExpressionClauseSyntax>()
            : null;
        if (possibleArrowExpressionClause != null)
            return possibleArrowExpressionClause.Expression;

        var enclosingNameMemberCrefOrnull = token.GetAncestors(n => n is NameMemberCrefSyntax).LastOrDefault();
        if (enclosingNameMemberCrefOrnull != null && token.Parent is TypeSyntax && token.Parent.Parent is TypeSyntax)
            enclosingNameMemberCrefOrnull = null;

        var enclosingXmlNameAttr = token.GetAncestor<XmlNameAttributeSyntax>();
        if (enclosingXmlNameAttr != null)
            return null;

        var enclosingInitializer = token.GetAncestor<EqualsValueClauseSyntax>();
        if (enclosingStatement == null && enclosingInitializer != null && enclosingInitializer.Parent is VariableDeclaratorSyntax)
            return enclosingInitializer.Value;

        var attributeSyntax = token.GetAncestor<AttributeSyntax>();
        if (attributeSyntax != null)
            return attributeSyntax;

        // there seems to be no statement above this one. Let's see if we can at least get an SimpleNameSyntax
        return enclosingStatement ?? enclosingNameMemberCrefOrnull ?? token.GetAncestor<SimpleNameSyntax>();
    }

    #region "Helper Methods"

    public override bool IsIdentifierValid(string replacementText, ISyntaxFactsService syntaxFactsService)
    {
        // Identifiers we never consider valid to rename to.
        switch (replacementText)
        {
            case "var":
            case "dynamic":
            case "unmanaged":
            case "notnull":
                return false;
        }

        var escapedIdentifier = replacementText.StartsWith("@", StringComparison.Ordinal)
            ? replacementText : "@" + replacementText;

        // Make sure we got an identifier. 
        if (!syntaxFactsService.IsValidIdentifier(escapedIdentifier))
        {
            // We still don't have an identifier, so let's fail
            return false;
        }

        return true;
    }

    /// <summary>
    /// Gets the semantic model for the given node.
    /// If the node belongs to the syntax tree of the original semantic model, then returns originalSemanticModel.
    /// Otherwise, returns a speculative model.
    /// The assumption for the later case is that span start position of the given node in it's syntax tree is same as
    /// the span start of the original node in the original syntax tree.
    /// </summary>
    public static SemanticModel? GetSemanticModelForNode(SyntaxNode node, SemanticModel originalSemanticModel)
    {
        if (node.SyntaxTree == originalSemanticModel.SyntaxTree)
        {
            // This is possible if the previous rename phase didn't rewrite any nodes in this tree.
            return originalSemanticModel;
        }

        var nodeToSpeculate = node.GetAncestorsOrThis(n => SpeculationAnalyzer.CanSpeculateOnNode(n)).LastOrDefault();
        if (nodeToSpeculate == null)
        {
            if (node is NameMemberCrefSyntax nameMember)
            {
                nodeToSpeculate = nameMember.Name;
            }
            else if (node is QualifiedCrefSyntax qualifiedCref)
            {
                nodeToSpeculate = qualifiedCref.Container;
            }
            else if (node is TypeConstraintSyntax typeConstraint)
            {
                nodeToSpeculate = typeConstraint.Type;
            }
            else if (node is BaseTypeSyntax baseType)
            {
                nodeToSpeculate = baseType.Type;
            }
            else
            {
                return null;
            }
        }

        var isInNamespaceOrTypeContext = SyntaxFacts.IsInNamespaceOrTypeContext(node as ExpressionSyntax);
        var position = nodeToSpeculate.SpanStart;
        return SpeculationAnalyzer.CreateSpeculativeSemanticModelForNode(nodeToSpeculate, originalSemanticModel, position, isInNamespaceOrTypeContext);
    }

    #endregion
}

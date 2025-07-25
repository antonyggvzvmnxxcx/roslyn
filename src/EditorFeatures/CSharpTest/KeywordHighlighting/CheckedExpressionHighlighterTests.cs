﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

#nullable disable

using System;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.KeywordHighlighting.KeywordHighlighters;
using Microsoft.CodeAnalysis.Test.Utilities;
using Xunit;

namespace Microsoft.CodeAnalysis.Editor.CSharp.UnitTests.KeywordHighlighting;

[Trait(Traits.Feature, Traits.Features.KeywordHighlighting)]
public sealed class CheckedExpressionHighlighterTests : AbstractCSharpKeywordHighlighterTests
{
    internal override Type GetHighlighterType()
        => typeof(CheckedExpressionHighlighter);

    [Fact]
    public Task TestExample1_1()
        => TestAsync(
            """
            class C
            {
                void M()
                {
                    short x = short.MaxValue;
                    short y = short.MaxValue;
                    int z;
                    try
                    {
                        z = {|Cursor:[|checked|]|}((short)(x + y));
                    }
                    catch (OverflowException e)
                    {
                        z = -1;
                    }

                    return z;
                }
            }
            """);

    [Fact]
    public Task TestExample2_1()
        => TestAsync(
            """
            class C
            {
                void M()
                {
                    short x = short.MaxValue;
                    short y = short.MaxValue;
                    int z = {|Cursor:[|unchecked|]|}((short)(x + y));
                    return z;
                }
            }
            """);
}

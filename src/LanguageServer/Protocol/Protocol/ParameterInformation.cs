﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Roslyn.LanguageServer.Protocol
{
    using System;
    using System.Text.Json.Serialization;

    /// <summary>
    /// Class representing a parameter of a callable signature.
    ///
    /// See the <see href="https://microsoft.github.io/language-server-protocol/specifications/specification-current/#parameterInformation">Language Server Protocol specification</see> for additional information.
    /// </summary>
    [JsonConverter(typeof(ParameterInformationConverter))]
    internal class ParameterInformation
    {
        /// <summary>
        /// Gets or sets the label of the parameter.
        /// </summary>
        [JsonPropertyName("label")]
        public SumType<string, Tuple<int, int>> Label
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets the human-readable documentation of the parameter.
        /// </summary>
        [JsonPropertyName("documentation")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public SumType<string, MarkupContent>? Documentation
        {
            get;
            set;
        }
    }
}

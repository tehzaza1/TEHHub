// <copyright file="LocalizationJsonContext.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Localization;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
///     Source-generated JSON metadata for the fixed localization-file schema.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class LocalizationJsonContext : JsonSerializerContext
{
}

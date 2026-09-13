// <copyright file="PluginMetadataJsonContext.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace GameHelper.Plugin;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
///     Source-generated JSON metadata for the small, fixed plugin metadata file.
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, PluginMetadata>))]
internal sealed partial class PluginMetadataJsonContext : JsonSerializerContext
{
}

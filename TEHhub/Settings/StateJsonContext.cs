// <copyright file="StateJsonContext.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Settings
{
    using System.Text.Json.Serialization;

    /// <summary>
    ///     Source-generated metadata for the persisted core settings schema.
    /// </summary>
    [JsonSourceGenerationOptions(
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        UseStringEnumConverter = true)]
    [JsonSerializable(typeof(State))]
    internal sealed partial class StateJsonContext : JsonSerializerContext
    {
    }
}

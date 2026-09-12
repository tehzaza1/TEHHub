// <copyright file="LauncherJsonContext.cs" company="None">
// Copyright (c) None. All rights reserved.
// </copyright>

namespace TEHhub.Launcher;

using System.Collections.Generic;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<string>))]
internal sealed partial class LauncherJsonContext : JsonSerializerContext
{
}

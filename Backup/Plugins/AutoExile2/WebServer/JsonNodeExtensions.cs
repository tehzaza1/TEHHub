namespace AutoExile2.WebServer
{
    using System;
    using System.Text.Json;
    using System.Text.Json.Nodes;

    internal static class JsonNodeExtensions
    {
        public static T Value<T>(this JsonNode? node)
        {
            if (node == null)
            {
                return default!;
            }

            try
            {
                return node.Deserialize<T>(AutoExileJson.Options)!;
            }
            catch (JsonException)
            {
                return default!;
            }
        }
    }
}

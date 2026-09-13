namespace ExpeditionPlanner
{
    using System;
    using TEHhub.RemoteObjects.States.InGameStateObjects;

    /// <summary>
    /// Immutable walkability data captured on TEHhub's game thread. Route workers must
    /// never retain AreaInstance because its native-backed contents change between frames.
    /// </summary>
    internal sealed class ExpeditionTerrainSnapshot
    {
        public byte[] Data { get; }
        public int BytesPerRow { get; }

        private ExpeditionTerrainSnapshot(byte[] data, int bytesPerRow)
        {
            this.Data = data;
            this.BytesPerRow = bytesPerRow;
        }

        public static ExpeditionTerrainSnapshot? Capture(AreaInstance area)
        {
            var data = area.GridWalkableData;
            var bytesPerRow = area.TerrainMetadata.BytesPerRow;
            if (data == null || data.Length == 0 || bytesPerRow <= 0)
            {
                return null;
            }

            return new ExpeditionTerrainSnapshot((byte[])data.Clone(), bytesPerRow);
        }
    }
}

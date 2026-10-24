using System;
using UnityEngine;

namespace Asoda.World
{
    /// <summary>
    /// Immutable 2D grid coordinate representing a single world chunk on the XZ plane.
    /// Implements <see cref="IEquatable{T}"/> to prevent boxing allocations in dictionaries and hash sets.
    /// </summary>
    public readonly struct ChunkCoord : IEquatable<ChunkCoord>
    {
        public readonly int X;
        public readonly int Z;

        public ChunkCoord(int x, int z)
        {
            X = x;
            Z = z;
        }

        /// <summary>
        /// Converts world-space X and Z coordinates into a chunk grid coordinate using floor division
        /// so negative and positive coordinates map uniformly to fixed-size chunks.
        /// </summary>
        public static ChunkCoord FromWorldPosition(float worldX, float worldZ, float chunkSize)
        {
            int cx = Mathf.FloorToInt(worldX / chunkSize);
            int cz = Mathf.FloorToInt(worldZ / chunkSize);
            return new ChunkCoord(cx, cz);
        }

        public static ChunkCoord FromWorldPosition(Vector3 worldPosition, float chunkSize)
        {
            return FromWorldPosition(worldPosition.x, worldPosition.z, chunkSize);
        }

        /// <summary>
        /// Returns the minimum world-space corner (MinX, 0, MinZ) of this chunk.
        /// </summary>
        public Vector3 GetWorldOrigin(float chunkSize)
        {
            return new Vector3(X * chunkSize, 0f, Z * chunkSize);
        }

        /// <summary>
        /// Returns the world-space center point of this chunk on the XZ plane.
        /// </summary>
        public Vector3 GetWorldCenter(float chunkSize)
        {
            return new Vector3((X + 0.5f) * chunkSize, 0f, (Z + 0.5f) * chunkSize);
        }

        /// <summary>
        /// Computes the world-space axis-aligned XZ bounds for this chunk.
        /// </summary>
        public ChunkBounds GetBounds(float chunkSize)
        {
            return new ChunkBounds(X * chunkSize, Z * chunkSize, chunkSize);
        }

        /// <summary>
        /// Chebyshev (chessboard) grid distance: radius 1 corresponds to a 3x3 chunk neighborhood.
        /// </summary>
        public int ChebyshevDistanceTo(ChunkCoord other)
        {
            int dx = Mathf.Abs(X - other.X);
            int dz = Mathf.Abs(Z - other.Z);
            return dx > dz ? dx : dz;
        }

        public bool Equals(ChunkCoord other)
        {
            return X == other.X && Z == other.Z;
        }

        public override bool Equals(object obj)
        {
            return obj is ChunkCoord other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (X * 73856093) ^ (Z * 19349663);
            }
        }

        public static bool operator ==(ChunkCoord left, ChunkCoord right) => left.Equals(right);

        public static bool operator !=(ChunkCoord left, ChunkCoord right) => !left.Equals(right);

        public override string ToString() => $"({X}, {Z})";
    }

    /// <summary>
    /// Axis-aligned world-space XZ boundaries [MinX, MaxX) x [MinZ, MaxZ) of a chunk.
    /// </summary>
    public readonly struct ChunkBounds
    {
        public readonly float MinX;
        public readonly float MinZ;
        public readonly float MaxX;
        public readonly float MaxZ;

        public ChunkBounds(float minX, float minZ, float chunkSize)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = minX + chunkSize;
            MaxZ = minZ + chunkSize;
        }

        public Vector3 Center => new Vector3((MinX + MaxX) * 0.5f, 0f, (MinZ + MaxZ) * 0.5f);

        /// <summary>
        /// Half-open interval containment [Min, Max) ensuring every world coordinate belongs to exactly one chunk.
        /// </summary>
        public bool Contains(float worldX, float worldZ)
        {
            return worldX >= MinX && worldX < MaxX && worldZ >= MinZ && worldZ < MaxZ;
        }
    }
}

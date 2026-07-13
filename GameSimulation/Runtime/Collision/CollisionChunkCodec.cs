using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ShooterMmo.GameSimulation
{
    public sealed class CollisionChunk
    {
        public CollisionChunk(
            int x,
            int z,
            CollisionAabb bounds,
            IReadOnlyList<CollisionBox> boxes)
        {
            X = x;
            Z = z;
            Bounds = bounds;
            Boxes = boxes ?? throw new ArgumentNullException(nameof(boxes));
        }

        public int X { get; }

        public int Z { get; }

        public CollisionAabb Bounds { get; }

        public IReadOnlyList<CollisionBox> Boxes { get; }
    }

    public static class CollisionChunkCodec
    {
        public static byte[] Encode(CollisionChunk chunk)
        {
            if (chunk == null)
            {
                throw new ArgumentNullException(nameof(chunk));
            }

            if (chunk.Boxes.Count > CollisionDataFormat.MaximumBoxesPerChunk)
            {
                throw new ArgumentException("Collision chunk contains too many boxes.", nameof(chunk));
            }

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(CollisionDataFormat.ChunkMagic);
                writer.Write(CollisionDataFormat.Version);
                writer.Write(chunk.X);
                writer.Write(chunk.Z);
                WriteVector(writer, chunk.Bounds.Minimum);
                WriteVector(writer, chunk.Bounds.Maximum);
                writer.Write(chunk.Boxes.Count);

                foreach (var box in chunk.Boxes.OrderBy(value => value.StableId))
                {
                    writer.Write(box.StableId);
                    writer.Write(box.LayerMask);
                    WriteVector(writer, box.Center);
                    WriteVector(writer, box.HalfExtents);
                    writer.Write(box.Rotation.X);
                    writer.Write(box.Rotation.Y);
                    writer.Write(box.Rotation.Z);
                    writer.Write(box.Rotation.W);
                }

                writer.Flush();
                return stream.ToArray();
            }
        }

        public static CollisionChunk Decode(byte[] data)
        {
            if (data == null || data.Length == 0)
            {
                throw new InvalidDataException("Collision chunk is empty.");
            }

            try
            {
                using (var stream = new MemoryStream(data, false))
                using (var reader = new BinaryReader(stream))
                {
                    if (reader.ReadUInt32() != CollisionDataFormat.ChunkMagic)
                    {
                        throw new InvalidDataException("Collision chunk magic is invalid.");
                    }

                    if (reader.ReadUInt16() != CollisionDataFormat.Version)
                    {
                        throw new InvalidDataException("Collision chunk version is unsupported.");
                    }

                    var x = reader.ReadInt32();
                    var z = reader.ReadInt32();
                    var minimum = ReadVector(reader);
                    var maximum = ReadVector(reader);
                    var bounds = new CollisionAabb(minimum, maximum);
                    var count = reader.ReadInt32();
                    if (count < 0 || count > CollisionDataFormat.MaximumBoxesPerChunk)
                    {
                        throw new InvalidDataException("Collision chunk box count is invalid.");
                    }

                    var boxes = new CollisionBox[count];
                    for (var index = 0; index < count; index++)
                    {
                        boxes[index] = new CollisionBox(
                            reader.ReadUInt64(),
                            reader.ReadUInt32(),
                            ReadVector(reader),
                            ReadVector(reader),
                            new SimulationQuaternion(
                                reader.ReadSingle(),
                                reader.ReadSingle(),
                                reader.ReadSingle(),
                                reader.ReadSingle()));
                    }

                    if (stream.Position != stream.Length)
                    {
                        throw new InvalidDataException("Collision chunk contains trailing data.");
                    }

                    return new CollisionChunk(x, z, bounds, boxes);
                }
            }
            catch (EndOfStreamException exception)
            {
                throw new InvalidDataException("Collision chunk is incomplete.", exception);
            }
            catch (ArgumentException exception)
            {
                throw new InvalidDataException("Collision chunk contains invalid geometry.", exception);
            }
        }

        private static void WriteVector(BinaryWriter writer, SimulationVector3 value)
        {
            writer.Write(value.X);
            writer.Write(value.Y);
            writer.Write(value.Z);
        }

        private static SimulationVector3 ReadVector(BinaryReader reader)
        {
            return new SimulationVector3(
                reader.ReadSingle(),
                reader.ReadSingle(),
                reader.ReadSingle());
        }
    }
}

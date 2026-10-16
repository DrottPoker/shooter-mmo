using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Mmorpg.WorldData.Editor.Actors
{
    public enum WorldActorSpawnEntryKind
    {
        Point,
        Group,
        Area,
        PatrolPath
    }

    // One entry of a World's spawn file as Spawn Authoring lists it: a spawn point, group,
    // area, or patrol path. It reads and writes the authoring object it wraps, so one list and
    // one set of bulk fields serve every kind.
    public sealed class WorldActorSpawnEntry
    {
        private WorldActorSpawnEntry(
            WorldActorSpawnEntryKind kind,
            WorldActorEditorSpawnPoint point = null,
            WorldActorEditorSpawnGroup group = null,
            WorldActorEditorSpawnArea area = null,
            WorldActorEditorPatrolPath patrolPath = null)
        {
            Kind = kind;
            Point = point;
            Group = group;
            Area = area;
            PatrolPath = patrolPath;
        }

        public WorldActorSpawnEntryKind Kind { get; }

        public WorldActorEditorSpawnPoint Point { get; }

        public WorldActorEditorSpawnGroup Group { get; }

        public WorldActorEditorSpawnArea Area { get; }

        public WorldActorEditorPatrolPath PatrolPath { get; }

        public string Collection
        {
            get
            {
                switch (Kind)
                {
                    case WorldActorSpawnEntryKind.Point:
                        return WorldActorEditorIssues.SpawnPoints;
                    case WorldActorSpawnEntryKind.Group:
                        return WorldActorEditorIssues.SpawnGroups;
                    case WorldActorSpawnEntryKind.Area:
                        return WorldActorEditorIssues.SpawnAreas;
                    default:
                        return WorldActorEditorIssues.PatrolPaths;
                }
            }
        }

        public string Key => WorldActorEditorIssues.Key(Collection, Id);

        // Points, groups, and areas place actors. Patrol paths only describe movement.
        public bool PlacesActors => Kind != WorldActorSpawnEntryKind.PatrolPath;

        public string KindLabel => Label(Kind);

        public string Id
        {
            get
            {
                switch (Kind)
                {
                    case WorldActorSpawnEntryKind.Point:
                        return Point.id;
                    case WorldActorSpawnEntryKind.Group:
                        return Group.id;
                    case WorldActorSpawnEntryKind.Area:
                        return Area.id;
                    default:
                        return PatrolPath.id;
                }
            }
            set
            {
                switch (Kind)
                {
                    case WorldActorSpawnEntryKind.Point:
                        Point.id = value;
                        break;
                    case WorldActorSpawnEntryKind.Group:
                        Group.id = value;
                        break;
                    case WorldActorSpawnEntryKind.Area:
                        Area.id = value;
                        break;
                    default:
                        PatrolPath.id = value;
                        break;
                }
            }
        }

        public string ActorId
        {
            get => Point?.actorId ?? Group?.actorId ?? Area?.actorId ?? string.Empty;
            set
            {
                if (Point != null)
                {
                    Point.actorId = value;
                }
                else if (Group != null)
                {
                    Group.actorId = value;
                }
                else if (Area != null)
                {
                    Area.actorId = value;
                }
            }
        }

        public string PatrolPathId
        {
            get => Point?.patrolPathId ?? Group?.patrolPathId ?? Area?.patrolPathId ?? string.Empty;
            set
            {
                if (Point != null)
                {
                    Point.patrolPathId = value;
                }
                else if (Group != null)
                {
                    Group.patrolPathId = value;
                }
                else if (Area != null)
                {
                    Area.patrolPathId = value;
                }
            }
        }

        public float YawDegrees
        {
            get => Point?.yawDegrees ?? Group?.yawDegrees ?? Area?.yawDegrees ?? 0f;
            set
            {
                if (Point != null)
                {
                    Point.yawDegrees = value;
                }
                else if (Group != null)
                {
                    Group.yawDegrees = value;
                }
                else if (Area != null)
                {
                    Area.yawDegrees = value;
                }
            }
        }

        // A point's position, a group's center, an area's center at its height, or a patrol
        // path's first point. Moving an area keeps its size; moving a patrol path moves every
        // point.
        public Vector3 Position
        {
            get
            {
                switch (Kind)
                {
                    case WorldActorSpawnEntryKind.Point:
                        return new Vector3(Point.x, Point.y, Point.z);
                    case WorldActorSpawnEntryKind.Group:
                        return new Vector3(Group.centerX, Group.centerY, Group.centerZ);
                    case WorldActorSpawnEntryKind.Area:
                        return new Vector3((Area.minimumX + Area.maximumX) * 0.5f, Area.y, (Area.minimumZ + Area.maximumZ) * 0.5f);
                    default:
                        var first = PatrolPath.points?.FirstOrDefault();
                        return first == null ? Vector3.zero : new Vector3(first.x, first.y, first.z);
                }
            }
            set
            {
                switch (Kind)
                {
                    case WorldActorSpawnEntryKind.Point:
                        Point.x = value.x;
                        Point.y = value.y;
                        Point.z = value.z;
                        break;
                    case WorldActorSpawnEntryKind.Group:
                        Group.centerX = value.x;
                        Group.centerY = value.y;
                        Group.centerZ = value.z;
                        break;
                    case WorldActorSpawnEntryKind.Area:
                        var halfX = (Area.maximumX - Area.minimumX) * 0.5f;
                        var halfZ = (Area.maximumZ - Area.minimumZ) * 0.5f;
                        Area.minimumX = value.x - halfX;
                        Area.maximumX = value.x + halfX;
                        Area.minimumZ = value.z - halfZ;
                        Area.maximumZ = value.z + halfZ;
                        Area.y = value.y;
                        break;
                    default:
                        var offset = value - Position;
                        foreach (var point in PatrolPath.points ?? Array.Empty<WorldActorEditorPatrolPoint>())
                        {
                            point.x += offset.x;
                            point.y += offset.y;
                            point.z += offset.z;
                        }

                        break;
                }
            }
        }

        // The actors the entry places at startup.
        public long InstanceCount
        {
            get
            {
                switch (Kind)
                {
                    case WorldActorSpawnEntryKind.Point:
                        return 1;
                    case WorldActorSpawnEntryKind.Group:
                        return Math.Max(0L, (long)Group.rows * Group.columns);
                    case WorldActorSpawnEntryKind.Area:
                        return Math.Max(0, Area.count);
                    default:
                        return 0;
                }
            }
        }

        public static string Label(WorldActorSpawnEntryKind kind)
        {
            switch (kind)
            {
                case WorldActorSpawnEntryKind.Point:
                    return "Point";
                case WorldActorSpawnEntryKind.Group:
                    return "Group";
                case WorldActorSpawnEntryKind.Area:
                    return "Area";
                default:
                    return "Patrol Path";
            }
        }

        // Every entry of the spawn file, in file order: points, groups, areas, patrol paths.
        public static IReadOnlyList<WorldActorSpawnEntry> List(WorldActorEditorSpawns spawns)
        {
            if (spawns == null)
            {
                return Array.Empty<WorldActorSpawnEntry>();
            }

            return (spawns.spawnPoints ?? Array.Empty<WorldActorEditorSpawnPoint>())
                .Where(value => value != null)
                .Select(value => new WorldActorSpawnEntry(WorldActorSpawnEntryKind.Point, point: value))
                .Concat((spawns.spawnGroups ?? Array.Empty<WorldActorEditorSpawnGroup>())
                    .Where(value => value != null)
                    .Select(value => new WorldActorSpawnEntry(WorldActorSpawnEntryKind.Group, group: value)))
                .Concat((spawns.spawnAreas ?? Array.Empty<WorldActorEditorSpawnArea>())
                    .Where(value => value != null)
                    .Select(value => new WorldActorSpawnEntry(WorldActorSpawnEntryKind.Area, area: value)))
                .Concat((spawns.patrolPaths ?? Array.Empty<WorldActorEditorPatrolPath>())
                    .Where(value => value != null)
                    .Select(value => new WorldActorSpawnEntry(WorldActorSpawnEntryKind.PatrolPath, patrolPath: value)))
                .ToArray();
        }
    }
}

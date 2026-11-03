using System;
using System.Collections.Generic;
using UnityEngine;

namespace Mmorpg.Client.Worlds
{
    // The sides of a World beyond which the sea runs on to the horizon.
    [Flags]
    public enum HorizonSides
    {
        None = 0,
        West = 1,
        East = 2,
        South = 4,
        North = 8
    }

    // A sea: water that moves in waves and rolls lines of foam onto its beaches, at the height of
    // its transform inside its outline in the transform's X and Z. On the sides of the World where
    // it reaches the horizon, a still surface over a seabed continues it beyond the World's
    // ground, where nothing walks, and the waves calm down toward that edge so both meet without a
    // seam. Where a river flows into it, its waves and foam lines calm down too.
    [DisallowMultipleComponent]
    public sealed class SeaShape : WaterSurfaceShape
    {
        [Tooltip("The sea inside the World, a closed outline in the transform's X and Z (m).")]
        [SerializeField] private List<Vector2> outline = new List<Vector2>
        {
            new Vector2(-20f, -20f),
            new Vector2(-20f, 20f),
            new Vector2(20f, 20f),
            new Vector2(20f, -20f)
        };

        [Tooltip("The size of the surface's grid cells, which the waves lift (m).")]
        [SerializeField, Min(0.5f)] private float cellSize = 2f;

        [Tooltip("How much the waves lift the open sea, from 0 to 1.")]
        [SerializeField, Range(0f, 1f)] private float waves = 1f;

        [Tooltip("Over how much water the waves calm down toward the horizon's edge and a river's mouth (m).")]
        [SerializeField, Min(0.1f)] private float calmDistance = 12f;

        [Tooltip("The sides of the World beyond which the sea runs on to the horizon.")]
        [SerializeField] private HorizonSides horizon;

        [Tooltip("How far beyond the World's ground the sea runs on (m).")]
        [SerializeField, Min(0f)] private float horizonExtent = 400f;

        [Tooltip("The material of the seabed beyond the World's ground.")]
        [SerializeField] private Material seabedMaterial;

        [Tooltip("How deep the seabed beyond the World's ground lies below the surface (m).")]
        [SerializeField, Min(0f)] private float seabedDepth = 3f;

        public IReadOnlyList<Vector2> Outline
        {
            get { return outline; }
        }

        public float CellSize
        {
            get { return cellSize; }
        }

        public float Waves
        {
            get { return waves; }
        }

        public float CalmDistance
        {
            get { return calmDistance; }
        }

        public HorizonSides Horizon
        {
            get { return horizon; }
        }

        public float HorizonExtent
        {
            get { return horizonExtent; }
        }

        public Material SeabedMaterial
        {
            get { return seabedMaterial; }
        }

        public float SeabedDepth
        {
            get { return seabedDepth; }
        }

        // The sea's surface height in world space.
        public float Level
        {
            get { return transform.position.y; }
        }
    }
}

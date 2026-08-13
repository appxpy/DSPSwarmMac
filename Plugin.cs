using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DSPSwarmDrawFix
{
    // Fixes the invisible Dyson Swarm under CrossOver/D3DMetal on macOS.
    //
    // The game draws far sails as one Graphics.DrawProceduralNow(MeshTopology.Quads,
    // sailCursor * 12) call. Direct3D 11 has no quad primitive, so Unity emulates it
    // with an internal index buffer, and D3DMetal silently drops the emulated draw.
    // We issue the same shader and buffers as real indexed triangles instead, using
    // the shader's own instancing: sailIndex = SV_InstanceID * _Stride + SV_VertexID / 12.

    // How to draw near sails — the solid mesh the game uses within 2500 units,
    // instead of the flat far-sail sprite.
    public enum NearSailsMode
    {
        Off,    // never draw them; sails stay flat up close
        Auto,   // draw only when a sail could actually be in range
        Always, // draw every frame — the 1.1.0 behaviour, costly on large swarms
    }

    [BepInPlugin("me.gp.dspswarmdrawfix", "DSPSwarmDrawFix", "1.2.0")]
    public class SwarmDrawFixPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static ConfigEntry<NearSailsMode> NearMode;

        // FieldRef rather than FieldInfo.GetValue: these are read every frame, so we
        // want no reflection and no boxing of sizeInMat.
        internal static readonly AccessTools.FieldRef<DysonSwarm, ComputeBuffer> SwarmBuffer =
            AccessTools.FieldRefAccess<DysonSwarm, ComputeBuffer>("swarmBuffer");
        internal static readonly AccessTools.FieldRef<DysonSwarm, ComputeBuffer> SwarmInfoBuffer =
            AccessTools.FieldRefAccess<DysonSwarm, ComputeBuffer>("swarmInfoBuffer");
        internal static readonly AccessTools.FieldRef<DysonSwarm, ComputeBuffer> OrbitColorsBuffer =
            AccessTools.FieldRefAccess<DysonSwarm, ComputeBuffer>("sailOrbitColorsBuffer");
        internal static readonly AccessTools.FieldRef<DysonSwarm, ComputeBuffer> BulletBuffer =
            AccessTools.FieldRefAccess<DysonSwarm, ComputeBuffer>("bulletBuffer");
        internal static readonly AccessTools.FieldRef<DysonSwarm, float> SizeInMat =
            AccessTools.FieldRefAccess<DysonSwarm, float>("sizeInMat");

        private void Awake()
        {
            Log = Logger;

            NearMode = Config.Bind(
                "Rendering", "NearSails", NearSailsMode.Auto,
                "Near sails: the solid mesh drawn within 2500 units instead of a flat sprite.\n" +
                "The game culls them on the GPU through an append buffer and an indirect draw, " +
                "which D3DMetal does not handle, so this mod instances the whole swarm instead — " +
                "cost then scales with sail count.\n" +
                "Auto: only draw when the camera is close enough to a sail orbit for anything " +
                "to be in range. Off: never draw them. Always: 1.1.0 behaviour, costly on large swarms.");

            var h = new Harmony("me.gp.dspswarmdrawfix");
            h.PatchAll(typeof(SwarmDrawPatch));
            h.PatchAll(typeof(NearDrawPatch));
            Log.LogInfo($"DSPSwarmDrawFix active: far sails as indexed triangles, near sails {NearMode.Value}");
        }

        private void OnDestroy()
        {
            SwarmDrawPatch.ReleaseBuffers();
            NearDrawPatch.ReleaseBuffers();
        }
    }

    // Resolved once, so we don't re-hash property name strings on every set call.
    internal static class Prop
    {
        internal static readonly int SwarmBuffer = Shader.PropertyToID("_SwarmBuffer");
        internal static readonly int SwarmInfoBuffer = Shader.PropertyToID("_SwarmInfoBuffer");
        internal static readonly int NodeBuffer = Shader.PropertyToID("_NodeBuffer");
        internal static readonly int OrbitColor = Shader.PropertyToID("_OrbitColor");
        internal static readonly int NearIdBuffer = Shader.PropertyToID("_NearIdBuffer");
        internal static readonly int BulletBuffer = Shader.PropertyToID("_BulletBuffer");
        internal static readonly int SunPosition = Shader.PropertyToID("_SunPosition");
        internal static readonly int SunPositionMap = Shader.PropertyToID("_SunPosition_Map");
        internal static readonly int LocalRot = Shader.PropertyToID("_LocalRot");
        internal static readonly int GameTick = Shader.PropertyToID("_GameTick");
        internal static readonly int Stride = Shader.PropertyToID("_Stride");
        internal static readonly int DistScalePoint = Shader.PropertyToID("_DistScalePoint");
        internal static readonly int RenderPlace = Shader.PropertyToID("_Global_DS_RenderPlace");
        internal static readonly int EditorMaskS = Shader.PropertyToID("_Global_DS_EditorMaskS");
        internal static readonly int GameMaskS = Shader.PropertyToID("_Global_DS_GameMaskS");
    }

    // Star position relative to the player and the local planet's rotation, shared by
    // both draw paths. Mirrors the setup in the vanilla DrawPost/DrawModel.
    internal struct SwarmFrame
    {
        internal Vector3 SunPos;
        internal Vector3 SunPosMap;
        internal Vector4 LocalRot;
        internal uint Tick;

        internal static SwarmFrame Build(DysonSwarm sw)
        {
            var f = new SwarmFrame
            {
                SunPos = Vector3.zero,
                SunPosMap = Vector3.zero,
                LocalRot = new Vector4(0f, 0f, 0f, 1f),
                Tick = (uint)(GameMain.gameTick & 0xFFFFFFFFu),
            };
            if (sw.starData == null || sw.gameData == null) return f;

            PlanetData localPlanet = sw.gameData.localPlanet;
            Player mainPlayer = sw.gameData.mainPlayer;
            VectorLF3 v = sw.starData.uPosition - mainPlayer.uPosition;
            if (localPlanet != null)
            {
                v = Maths.QInvRotateLF(localPlanet.runtimeRotation, v);
                v += (VectorLF3)mainPlayer.position;
                f.LocalRot = new Vector4(localPlanet.runtimeRotation.x, localPlanet.runtimeRotation.y,
                                         localPlanet.runtimeRotation.z, localPlanet.runtimeRotation.w);
            }
            f.SunPos = v;
            if (DysonSphere.renderPlace == ERenderPlace.Starmap)
            {
                f.SunPosMap = (sw.starData.uPosition - UIStarmap.viewTargetStatic) * 0.00025;
            }
            return f;
        }

        internal void ApplyTo(Material mat)
        {
            mat.SetVector(Prop.SunPosition, SunPos);
            mat.SetVector(Prop.SunPositionMap, SunPosMap);
            mat.SetVector(Prop.LocalRot, LocalRot);
            mat.SetInt(Prop.GameTick, (int)Tick);
        }

        internal static int LayerForRenderPlace()
        {
            if (DysonSphere.renderPlace == ERenderPlace.Starmap) return 20;
            if (DysonSphere.renderPlace == ERenderPlace.Dysonmap) return 21;
            return 16;
        }
    }

    internal static class SwarmDrawPatch
    {
        // Sails per instance. 3 quads (12 vertices) each, so 5460 keeps every index
        // value under 65536 — a leftover constraint from 16-bit indices, which the
        // 32-bit buffer below does not actually have. Raising it would not help:
        // this is a single instanced draw either way, so the vertex count is the same
        // and only the index buffer grows. Keeping it low also caps the overdraw of
        // the final partial instance, which is always issued in full.
        private const int ChunkSails = 5460;
        private static GraphicsBuffer quadIndexBuffer;
        private static bool failedOnce;

        internal static void ReleaseBuffers()
        {
            quadIndexBuffer?.Release();
            quadIndexBuffer = null;
        }

        private static GraphicsBuffer GetIndexBuffer()
        {
            if (quadIndexBuffer != null && quadIndexBuffer.IsValid()) return quadIndexBuffer;
            int quads = ChunkSails * 3;
            var idx = new int[quads * 6];
            for (int q = 0; q < quads; q++)
            {
                int v = q * 4, t = q * 6;
                idx[t] = v; idx[t + 1] = v + 1; idx[t + 2] = v + 2;
                idx[t + 3] = v; idx[t + 4] = v + 2; idx[t + 5] = v + 3;
            }
            quadIndexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Index, idx.Length, sizeof(int));
            quadIndexBuffer.SetData(idx);
            return quadIndexBuffer;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(DysonSwarm), "DrawPost")]
        private static bool DrawPostPrefix(DysonSwarm __instance)
        {
            try
            {
                ReplacementDrawPost(__instance);
                return false;
            }
            catch (Exception e)
            {
                if (!failedOnce)
                {
                    failedOnce = true;
                    SwarmDrawFixPlugin.Log.LogError($"DrawPost replacement threw, falling back to vanilla: {e}");
                }
                return true;
            }
        }

        // Mirrors DysonSwarm.DrawPost; only the far-sail draw call differs.
        private static void ReplacementDrawPost(DysonSwarm sw)
        {
            var frame = SwarmFrame.Build(sw);

            Camera camera = Camera.main;
            if (DysonSphere.renderPlace == ERenderPlace.Starmap)
            {
                var starmap = UIRoot.instance.uiGame.starmap;
                if (starmap != null) camera = starmap.screenCamera;
            }
            else if (DysonSphere.renderPlace == ERenderPlace.Dysonmap)
            {
                var dysonEditor = UIRoot.instance.uiGame.dysonEditor;
                if (dysonEditor != null) camera = dysonEditor.screenCamera;
            }
            if (camera != null)
            {
                float num = SwarmDrawFixPlugin.SizeInMat(sw) * (float)Screen.height * 0.4f;
                float distScale = 1f / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) * num;
                sw.sailFarMaterial.SetFloat(Prop.DistScalePoint, distScale);
            }

            var swarmBuffer = SwarmDrawFixPlugin.SwarmBuffer(sw);
            var mat = sw.sailFarMaterial;
            mat.SetInt(Prop.Stride, ChunkSails);
            mat.SetBuffer(Prop.SwarmBuffer, swarmBuffer);
            mat.SetBuffer(Prop.SwarmInfoBuffer, SwarmDrawFixPlugin.SwarmInfoBuffer(sw));
            mat.SetBuffer(Prop.NodeBuffer, sw.dysonSphere.nrdBuffer);
            mat.SetBuffer(Prop.OrbitColor, SwarmDrawFixPlugin.OrbitColorsBuffer(sw));
            frame.ApplyTo(mat);
            mat.SetPass(0);

            if (sw.sailCursor > 0 && swarmBuffer != null)
            {
                int instances = (sw.sailCursor + ChunkSails - 1) / ChunkSails;
                Graphics.DrawProceduralNow(MeshTopology.Triangles, GetIndexBuffer(),
                                           ChunkSails * 18, instances);
            }

            var bulletBuffer = SwarmDrawFixPlugin.BulletBuffer(sw);
            if (bulletBuffer != null)
            {
                sw.bulletMaterial.SetBuffer(Prop.BulletBuffer, bulletBuffer);
                sw.bulletMaterial.SetPass(0);
                Graphics.DrawProceduralNow(MeshTopology.Quads, sw.bulletCursor * 8);
            }
        }
    }

    internal static class NearDrawPatch
    {
        // Same threshold the game hands to its AppendNear compute pass.
        private const float NearThreshold = 2500f;

        private static ComputeBuffer identityBuffer;
        private static bool failedOnce;

        internal static void ReleaseBuffers()
        {
            identityBuffer?.Release();
            identityBuffer = null;
        }

        private static ComputeBuffer GetIdentityBuffer(int minCount)
        {
            if (identityBuffer != null && identityBuffer.IsValid() && identityBuffer.count >= minCount)
                return identityBuffer;
            int cap = Mathf.NextPowerOfTwo(minCount);
            identityBuffer?.Release();
            identityBuffer = new ComputeBuffer(cap, 4, ComputeBufferType.Default);
            var ids = new int[cap];
            for (int i = 0; i < cap; i++) ids[i] = i;
            identityBuffer.SetData(ids);
            return identityBuffer;
        }

        [HarmonyPrefix, HarmonyPatch(typeof(DysonSwarm), "DrawModel")]
        private static bool DrawModelPrefix(DysonSwarm __instance, ERenderPlace place, int editorMask, int gameMask)
        {
            try
            {
                ReplacementDrawModel(__instance, editorMask, gameMask);
                return false;
            }
            catch (Exception e)
            {
                if (!failedOnce)
                {
                    failedOnce = true;
                    SwarmDrawFixPlugin.Log.LogError($"DrawModel replacement threw, falling back to vanilla: {e}");
                }
                return true;
            }
        }

        // Whether any sail could be within NearThreshold of the camera.
        //
        // Sails on an orbit of radius R sit on a sphere of radius R around the star, so
        // none of them can be closer to the camera than |d - R|, where d is the camera's
        // distance to the star. If no active orbit passes near the camera, there is
        // nothing to draw. The bound is conservative and never hides a sail on an orbit.
        //
        // Free sails not yet settled onto an orbit are not covered — up close those stay
        // flat unless the mode is Always.
        private static bool AnySailCanBeNear(DysonSwarm sw, Camera camera)
        {
            var orbits = sw.orbits;
            if (orbits == null) return false;

            VectorLF3 camWorld = sw.gameData.mainPlayer.uPosition + (VectorLF3)camera.transform.position;
            float camDist = (float)(camWorld - sw.starData.uPosition).magnitude;

            for (int i = 1; i < sw.orbitCursor; i++)
            {
                if (!orbits[i].enabled || orbits[i].count <= 0) continue;
                if (Mathf.Abs(camDist - orbits[i].radius) <= NearThreshold) return true;
            }
            return false;
        }

        // Mirrors DysonSwarm.DrawModel, minus the GPU culling of near sails: the vanilla
        // AppendNear + CopyCount + DrawMeshInstancedIndirect path relies on an append
        // buffer counter and indirect args, neither of which is reliable on D3DMetal.
        // We draw with a plain instanced call and an identity _NearIdBuffer instead.
        //
        // Without culling that call costs sailCursor mesh instances, scaling with swarm
        // size, hence the AnySailCanBeNear gate — in almost every frame the camera is
        // nowhere near the swarm's orbits and the whole pass is skipped.
        private static void ReplacementDrawModel(DysonSwarm sw, int editorMask, int gameMask)
        {
            var mode = SwarmDrawFixPlugin.NearMode.Value;
            if (mode == NearSailsMode.Off) return;
            if (Configs.builtin.solarSailMesh == null) return;
            if (sw.starData == null || sw.gameData == null) return;
            if (sw.sailCursor <= 1) return;

            Camera camera = Camera.main;
            if (camera == null) return;
            if (mode == NearSailsMode.Auto && !AnySailCanBeNear(sw, camera)) return;

            var swarmBuffer = SwarmDrawFixPlugin.SwarmBuffer(sw);
            if (swarmBuffer == null) return;

            var frame = SwarmFrame.Build(sw);
            var mat = sw.sailNearMaterial;
            mat.SetBuffer(Prop.NearIdBuffer, GetIdentityBuffer(sw.sailCursor));
            mat.SetBuffer(Prop.SwarmBuffer, swarmBuffer);
            mat.SetBuffer(Prop.SwarmInfoBuffer, SwarmDrawFixPlugin.SwarmInfoBuffer(sw));
            mat.SetBuffer(Prop.NodeBuffer, sw.dysonSphere.nrdBuffer);
            mat.SetBuffer(Prop.OrbitColor, SwarmDrawFixPlugin.OrbitColorsBuffer(sw));
            frame.ApplyTo(mat);
            mat.SetInt(Prop.RenderPlace, (int)DysonSphere.renderPlace);
            mat.SetInt(Prop.EditorMaskS, editorMask);
            mat.SetInt(Prop.GameMaskS, gameMask);

            Graphics.DrawMeshInstancedProcedural(Configs.builtin.solarSailMesh, 0, mat,
                new Bounds(Vector3.zero, new Vector3(100000f, 100000f, 100000f)),
                sw.sailCursor, null, UnityEngine.Rendering.ShadowCastingMode.Off, false,
                SwarmFrame.LayerForRenderPlace());
        }
    }
}

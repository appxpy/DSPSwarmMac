using System;
using System.Reflection;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace DSPSwarmDrawFix
{
    // Чинит невидимый Dyson Swarm под CrossOver/D3DMetal (macOS).
    //
    // Причина бага: дальние паруса роя игра рисует одним вызовом
    // Graphics.DrawProceduralNow(MeshTopology.Quads, sailCursor * 12) — при десятках
    // тысяч парусов это сотни тысяч вершин. Квадовую топологию D3D11 не поддерживает,
    // Unity эмулирует её внутренним индексным буфером, и на D3DMetal такой гигантский
    // вызов молча теряется (мелкие квадовые вызовы — пули, ЛЭП, иконки — работают).
    //
    // Фикс: рисуем те же данные тем же шейдером, но индексированными треугольниками,
    // чанками по 5460 парусов (65 520 вершин < 65 536), используя штатный инстансинг
    // шейдера: sailIndex = SV_InstanceID * _Stride + SV_VertexID / 12.
    [BepInPlugin("me.gp.dspswarmdrawfix", "DSPSwarmDrawFix", "1.1.0")]
    public class SwarmDrawFixPlugin : BaseUnityPlugin
    {
        internal static ManualLogSource Log;
        internal static readonly FieldInfo SwarmBufferField =
            AccessTools.Field(typeof(DysonSwarm), "swarmBuffer");
        internal static readonly FieldInfo SwarmInfoBufferField =
            AccessTools.Field(typeof(DysonSwarm), "swarmInfoBuffer");
        internal static readonly FieldInfo OrbitColorsBufferField =
            AccessTools.Field(typeof(DysonSwarm), "sailOrbitColorsBuffer");
        internal static readonly FieldInfo BulletBufferField =
            AccessTools.Field(typeof(DysonSwarm), "bulletBuffer");
        internal static readonly FieldInfo SizeInMatField =
            AccessTools.Field(typeof(DysonSwarm), "sizeInMat");

        private void Awake()
        {
            Log = Logger;
            var h = new Harmony("me.gp.dspswarmdrawfix");
            h.PatchAll(typeof(SwarmDrawPatch));
            h.PatchAll(typeof(NearDrawPatch));
            Log.LogInfo("DSPSwarmDrawFix активен: дальние паруса — индексированные треугольники, ближние — instanced без indirect");
        }
    }

    internal static class SwarmDrawPatch
    {
        private const int ChunkSails = 5460;
        private static GraphicsBuffer quadIndexBuffer;
        private static bool failedOnce;

        private static GraphicsBuffer GetIndexBuffer()
        {
            if (quadIndexBuffer != null && quadIndexBuffer.IsValid()) return quadIndexBuffer;
            int quads = ChunkSails * 3; // 3 квада (12 вершин) на парус
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
                    SwarmDrawFixPlugin.Log.LogError($"замена DrawPost упала, откат на оригинал: {e}");
                }
                return true;
            }
        }

        // Копия DysonSwarm.DrawPost; отличие — только сам draw-вызов дальних парусов.
        private static void ReplacementDrawPost(DysonSwarm sw)
        {
            uint tick = (uint)(GameMain.gameTick & 0xFFFFFFFFu);
            Vector3 sunPos = Vector3.zero;
            Vector4 localRot = new Vector4(0f, 0f, 0f, 1f);
            Vector3 sunPosMap = Vector3.zero;
            if (sw.starData != null && sw.gameData != null)
            {
                PlanetData localPlanet = sw.gameData.localPlanet;
                Player mainPlayer = sw.gameData.mainPlayer;
                VectorLF3 v = sw.starData.uPosition;
                if (localPlanet != null)
                {
                    v -= mainPlayer.uPosition;
                    v = Maths.QInvRotateLF(localPlanet.runtimeRotation, v);
                    v += (VectorLF3)mainPlayer.position;
                    localRot = new Vector4(localPlanet.runtimeRotation.x, localPlanet.runtimeRotation.y,
                                           localPlanet.runtimeRotation.z, localPlanet.runtimeRotation.w);
                }
                else
                {
                    v -= mainPlayer.uPosition;
                }
                sunPos = v;
                if (DysonSphere.renderPlace == ERenderPlace.Starmap)
                {
                    sunPosMap = (sw.starData.uPosition - UIStarmap.viewTargetStatic) * 0.00025;
                }
            }

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
                float sizeInMat = (float)SwarmDrawFixPlugin.SizeInMatField.GetValue(sw);
                float num = sizeInMat * (float)Screen.height * 0.4f;
                float distScale = 1f / Mathf.Tan(camera.fieldOfView * Mathf.Deg2Rad * 0.5f) * num;
                sw.sailFarMaterial.SetFloat("_DistScalePoint", distScale);
            }

            var swarmBuffer = (ComputeBuffer)SwarmDrawFixPlugin.SwarmBufferField.GetValue(sw);
            var swarmInfoBuffer = (ComputeBuffer)SwarmDrawFixPlugin.SwarmInfoBufferField.GetValue(sw);
            var orbitColorsBuffer = (ComputeBuffer)SwarmDrawFixPlugin.OrbitColorsBufferField.GetValue(sw);
            var bulletBuffer = (ComputeBuffer)SwarmDrawFixPlugin.BulletBufferField.GetValue(sw);

            var mat = sw.sailFarMaterial;
            mat.SetInt("_Stride", ChunkSails);
            mat.SetBuffer("_SwarmBuffer", swarmBuffer);
            mat.SetBuffer("_SwarmInfoBuffer", swarmInfoBuffer);
            mat.SetBuffer("_NodeBuffer", sw.dysonSphere.nrdBuffer);
            mat.SetBuffer("_OrbitColor", orbitColorsBuffer);
            mat.SetVector("_SunPosition", sunPos);
            mat.SetVector("_SunPosition_Map", sunPosMap);
            mat.SetVector("_LocalRot", localRot);
            mat.SetInt("_GameTick", (int)tick);
            mat.SetPass(0);

            if (sw.sailCursor > 0 && swarmBuffer != null)
            {
                int instances = (sw.sailCursor + ChunkSails - 1) / ChunkSails;
                Graphics.DrawProceduralNow(MeshTopology.Triangles, GetIndexBuffer(),
                                           ChunkSails * 18, instances);
            }

            if (bulletBuffer != null)
            {
                sw.bulletMaterial.SetBuffer("_BulletBuffer", bulletBuffer);
                sw.bulletMaterial.SetPass(0);
                Graphics.DrawProceduralNow(MeshTopology.Quads, sw.bulletCursor * 8);
            }
        }
    }

    internal static class NearDrawPatch
    {
        private static ComputeBuffer identityBuffer;
        private static bool failedOnce;

        private static ComputeBuffer GetIdentityBuffer(int minCount)
        {
            if (identityBuffer != null && identityBuffer.IsValid() && identityBuffer.count >= minCount)
                return identityBuffer;
            int cap = Mathf.NextPowerOfTwo(Mathf.Max(minCount, 65536));
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
                    SwarmDrawFixPlugin.Log.LogError($"замена DrawModel упала, откат на оригинал: {e}");
                }
                return true;
            }
        }

        // Копия DysonSwarm.DrawModel без GPU-отсева ближних парусов: вместо
        // AppendNear + CopyCount + DrawMeshInstancedIndirect (счётчик append-буфера и
        // indirect-аргументы ненадёжны на D3DMetal) рисуем ВСЕ паруса обычным
        // инстанс-вызовом, а _NearIdBuffer подменяем identity-буфером (instance i -> парус i).
        // Дальше нескольких сотен метров меш паруса субпиксельный, так что картинка та же.
        private static void ReplacementDrawModel(DysonSwarm sw, int editorMask, int gameMask)
        {
            if (Configs.builtin.solarSailMesh == null) return;
            if (sw.starData == null || sw.gameData == null || Camera.main == null) return;
            if (sw.sailCursor <= 1) return;

            uint tick = (uint)(GameMain.gameTick & 0xFFFFFFFFu);
            Vector3 sunPos = Vector3.zero;
            Vector4 localRot = new Vector4(0f, 0f, 0f, 1f);
            Vector3 sunPosMap = Vector3.zero;
            PlanetData localPlanet = sw.gameData.localPlanet;
            Player mainPlayer = sw.gameData.mainPlayer;
            VectorLF3 v = sw.starData.uPosition;
            if (localPlanet != null)
            {
                v -= mainPlayer.uPosition;
                v = Maths.QInvRotateLF(localPlanet.runtimeRotation, v);
                v += (VectorLF3)mainPlayer.position;
                localRot = new Vector4(localPlanet.runtimeRotation.x, localPlanet.runtimeRotation.y,
                                       localPlanet.runtimeRotation.z, localPlanet.runtimeRotation.w);
            }
            else
            {
                v -= mainPlayer.uPosition;
            }
            sunPos = v;
            if (DysonSphere.renderPlace == ERenderPlace.Starmap)
            {
                sunPosMap = (sw.starData.uPosition - UIStarmap.viewTargetStatic) * 0.00025;
            }

            var swarmBuffer = (ComputeBuffer)SwarmDrawFixPlugin.SwarmBufferField.GetValue(sw);
            var swarmInfoBuffer = (ComputeBuffer)SwarmDrawFixPlugin.SwarmInfoBufferField.GetValue(sw);
            var orbitColorsBuffer = (ComputeBuffer)SwarmDrawFixPlugin.OrbitColorsBufferField.GetValue(sw);
            if (swarmBuffer == null) return;

            var mat = sw.sailNearMaterial;
            mat.SetBuffer("_NearIdBuffer", GetIdentityBuffer(sw.sailCursor));
            mat.SetBuffer("_SwarmBuffer", swarmBuffer);
            mat.SetBuffer("_SwarmInfoBuffer", swarmInfoBuffer);
            mat.SetBuffer("_NodeBuffer", sw.dysonSphere.nrdBuffer);
            mat.SetBuffer("_OrbitColor", orbitColorsBuffer);
            mat.SetVector("_SunPosition", sunPos);
            mat.SetVector("_SunPosition_Map", sunPosMap);
            mat.SetVector("_LocalRot", localRot);
            mat.SetInt("_GameTick", (int)tick);
            mat.SetInt("_Global_DS_RenderPlace", (int)DysonSphere.renderPlace);
            mat.SetInt("_Global_DS_EditorMaskS", editorMask);
            mat.SetInt("_Global_DS_GameMaskS", gameMask);

            int layer = 16;
            if (DysonSphere.renderPlace == ERenderPlace.Starmap) layer = 20;
            else if (DysonSphere.renderPlace == ERenderPlace.Dysonmap) layer = 21;

            Graphics.DrawMeshInstancedProcedural(Configs.builtin.solarSailMesh, 0, mat,
                new Bounds(Vector3.zero, new Vector3(100000f, 100000f, 100000f)),
                sw.sailCursor, null, UnityEngine.Rendering.ShadowCastingMode.Off, false, layer);
        }
    }

}

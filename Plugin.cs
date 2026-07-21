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
    [BepInPlugin("me.gp.dspswarmdrawfix", "DSPSwarmDrawFix", "1.0.0")]
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
            new Harmony("me.gp.dspswarmdrawfix").PatchAll(typeof(SwarmDrawPatch));
            Log.LogInfo("DSPSwarmDrawFix активен: дальние паруса рисуются индексированными треугольниками");
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
}

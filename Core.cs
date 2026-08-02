using System;
using System.Collections;
using System.Text;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using BoneLib;
using BoneLib.BoneMenu;
using Il2CppSLZ.Marrow;
using Avatar = Il2CppSLZ.VRMK.Avatar;

[assembly: MelonInfo(typeof(OffsetRemover.Core), "Head Offset Remover", "1.0.0", "Hiiiiii :3")]
[assembly: MelonGame("Stress Level Zero", "BONELAB")]

namespace OffsetRemover
{
    sealed class BonePair { public Transform src; public Transform dst; public bool isHeadSubtree; public bool isPathMatched; public Vector3 headRestLocal; }
    sealed class RendererPair { public Renderer src; public Renderer dst; public bool forceDisable; }

    public class Core : MelonMod
    {
        public static GameObject headlessAvatarClone;
        public static Animator headlessAvatarCloneAnimator;

        public static bool IsEnabled = true;
        public static bool ignoreSpectator = true;

        private static BonePair[] bonePairsArr = new BonePair[0];
        private static int bonePairCount = 0;
        private static RendererPair[] rendererPairsArr = new RendererPair[0];
        private static int rendererPairCount = 0;

        private enum CameraKind { Headset, Spectator, Other }
        private static readonly System.Collections.Generic.Dictionary<int, CameraKind> cameraKindCache = new System.Collections.Generic.Dictionary<int, CameraKind>();

        private static CameraKind ClassifyCamera(Camera cam)
        {
            int id = cam.GetInstanceID();
            if (cameraKindCache.TryGetValue(id, out var kind)) return kind;
            if (cam.name == "Headset") kind = CameraKind.Headset;
            else if (cam.name == "Spectator Camera") kind = CameraKind.Spectator;
            else kind = CameraKind.Other;
            cameraKindCache[id] = kind;
            return kind;
        }

        private static Transform headBoneOnClone;
        private static Transform headBoneOnSource;
        private static Vector3 headLocalRestPosition;
        private static Quaternion headLocalRestRotation;
        private static bool hasHeadCorrection;
        private static System.Collections.Generic.Dictionary<string, Vector3> headSubtreeLocalPositions = new System.Collections.Generic.Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        private static System.Collections.Generic.List<Renderer> selfRenderers = new System.Collections.Generic.List<Renderer>(64);
        private static System.Collections.Generic.List<Renderer> cloneAvatarRenderers = new System.Collections.Generic.List<Renderer>(64);

        private static int cloneBuiltForAvatarId = 0;
        private static volatile bool buildInProgress = false;
        private static float lastBuildAttemptTime = -999f;
        private const float RetryCooldownSeconds = 3f;
        private const float MinAcceptableRendererFraction = 0.5f;

        private static float lastErrorLogTime = -999f;
        private const float ErrorLogCooldownSeconds = 5f;

        private static readonly StringBuilder pathSb = new StringBuilder(256);
        private static readonly string[] nameStack = new string[64];

        public override void OnInitializeMelon()
        {
            MelonLogger.Msg("Offset Remover Loaded");
            RenderPipelineManager.beginCameraRendering += (Action<ScriptableRenderContext, Camera>)OnBeginCameraRendering;
            Hooking.OnSwitchAvatarPostfix += avatar =>
            {
                if (IsEnabled) MelonCoroutines.Start(RebuildClone(Player.RigManager));
            };

            var page = Page.Root.CreatePage("OffsetRemover", Color.green, 0, true);
            page.CreateBool("Enable OffsetRemover", Color.white, IsEnabled, v =>
            {
                IsEnabled = v;
                if (!v) { DisableAndCleanUp(); ApplyVisibilityToAllCameras(); }
                else { ForceRebuild(); ApplyVisibilityToAllCameras(); }
            });
            page.CreateBool("Don't Fix Spectator Camera", Color.yellow, ignoreSpectator, v => { ignoreSpectator = v; ApplyVisibilityToAllCameras(); });
            page.CreateFunction("Rebuild Clone", Color.red, ForceRebuild);

            MelonCoroutines.Start(SelfHealLoop());
        }

        private static void DisableAndCleanUp()
        {
            var mgr = Player.RigManager;
            if (mgr != null && mgr.avatar)
            {
                var renderers = mgr.avatar.gameObject.GetComponentsInChildren<Renderer>(true);
                for (int i = 0; i < renderers.Length; i++)
                {
                    if (renderers[i] == null) continue;
                    try { renderers[i].forceRenderingOff = false; } catch { }
                }
            }

            if (headlessAvatarClone != null) UnityEngine.Object.DestroyImmediate(headlessAvatarClone);
            ResetCloneState();
        }

        private static void ApplyVisibilityToAllCameras()
        {
            try
            {
                var cams = Camera.allCameras;
                foreach (var cam in cams) if (cam != null) CheckRenderLoop(ClassifyCamera(cam));
            }
            catch { }
        }

        private static void OnBeginCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            if (!IsEnabled) return;
            try
            {
                var kind = ClassifyCamera(cam);
                if (kind != CameraKind.Headset) SyncCloneIfNeeded();
                CheckRenderLoop(kind);
            }
            catch (Exception e)
            {
                if (Time.time - lastErrorLogTime > ErrorLogCooldownSeconds)
                {
                    lastErrorLogTime = Time.time;
                    MelonLogger.Error($"[OffsetRemover] Render hook error: {e}");
                }
            }
        }

        private static IEnumerator SelfHealLoop()
        {
            while (true)
            {
                yield return new WaitForSeconds(2f);
                if (!IsEnabled) continue;
                var mgr = Player.RigManager;
                if (mgr == null || !mgr.avatar) continue;
                int currentId = mgr.avatar.GetInstanceID();
                bool cloneIsValid = headlessAvatarClone != null && cloneBuiltForAvatarId == currentId;
                if (!cloneIsValid && !buildInProgress && Time.time - lastBuildAttemptTime > RetryCooldownSeconds)
                    MelonCoroutines.Start(RebuildClone(mgr));
            }
        }

        private static int lastSyncedFrame = -1;

        private static void SyncCloneIfNeeded()
        {
            if (headlessAvatarCloneAnimator == null) return;
            if (Time.frameCount == lastSyncedFrame) return;
            lastSyncedFrame = Time.frameCount;

            try
            {
                var bpArr = bonePairsArr;
                for (int i = 0; i < bonePairCount; ++i)
                {
                    var p = bpArr[i];
                    var s = p.src;
                    var d = p.dst;
                    if (s == null || d == null) continue;

                    if (p.isHeadSubtree)
                    {
                        d.localPosition = p.headRestLocal;
                        d.localRotation = s.localRotation;
                    }
                    else if (p.isPathMatched)
                    {
                        d.localPosition = s.localPosition;
                        d.localRotation = s.localRotation;
                    }
                    else
                    {
                        d.SetPositionAndRotation(s.position, s.rotation);
                    }
                }

                var rpArr = rendererPairsArr;
                for (int i = 0; i < rendererPairCount; ++i)
                {
                    var r = rpArr[i];
                    var s = r.src; var d = r.dst;
                    if (s == null || d == null) continue;
                    d.enabled = s.enabled && s.gameObject.activeInHierarchy;
                }

                if (hasHeadCorrection && headBoneOnSource == null && headBoneOnClone != null)
                    headBoneOnClone.localRotation = headLocalRestRotation;
            }
            catch (Exception e)
            {
                if (Time.time - lastErrorLogTime > ErrorLogCooldownSeconds)
                {
                    lastErrorLogTime = Time.time;
                    MelonLogger.Warning($"[OffsetRemover] Clone sync failed, rebuilding: {e.Message}");
                }
                if (headlessAvatarClone != null) UnityEngine.Object.DestroyImmediate(headlessAvatarClone);
                ResetCloneState();
            }
        }

        public static void ForceRebuild()
        {
            if (!IsEnabled) { MelonLogger.Msg("[OffsetRemover] Rebuild ignored -- disabled."); return; }
            var mgr = Player.RigManager;
            if (mgr == null) { MelonLogger.Warning("[OffsetRemover] Rebuild: no RigManager."); return; }
            MelonCoroutines.Start(RebuildClone(mgr));
        }

        private static IEnumerator RebuildClone(RigManager manager)
        {
            if (buildInProgress) yield break;
            buildInProgress = true;
            lastBuildAttemptTime = Time.time;
            cameraKindCache.Clear();

            if (headlessAvatarClone != null) UnityEngine.Object.DestroyImmediate(headlessAvatarClone);
            ResetCloneState();

            float waitStart = Time.time;
            const float maxWait = 5f;
            while (manager == null || !manager.avatar || manager.avatar.animator == null ||
                   manager.avatar.gameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length == 0)
            {
                if (Time.time - waitStart > maxWait) { buildInProgress = false; yield break; }
                yield return null;
            }

            selfRenderers.Clear();
            var srcR = manager.avatar.gameObject.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < srcR.Length; ++i) selfRenderers.Add(srcR[i]);

            GameObject clone = null;
            bool ok = true; string failReason = null;

            try
            {
                var disabledContainer = new GameObject("DISABLED CONTAINER");
                disabledContainer.SetActive(false);
                clone = UnityEngine.Object.Instantiate(manager.avatar.gameObject, disabledContainer.transform);
                CleanClone(clone);
                clone.transform.parent = null;
                UnityEngine.Object.Destroy(disabledContainer);
            }
            catch (Exception e)
            {
                ok = false; failReason = "instantiate/clean: " + e.Message;
            }

            if (!ok)
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                ResetCloneState();
                buildInProgress = false;
                yield break;
            }

            cloneAvatarRenderers.Clear();
            var clnR = clone.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < clnR.Length; ++i) cloneAvatarRenderers.Add(clnR[i]);

            int sourceRendererCount = srcR.Length;
            var cloneAvatar = clone.GetComponentInChildren<Avatar>();
            Animator cloneAnim = cloneAvatar != null ? cloneAvatar.animator : null;

            try
            {
                BuildPairings(manager.avatar.animator, cloneAnim, manager.avatar.gameObject, clone);
            }
            catch (Exception e)
            {
                ok = false; failReason = "pairing: " + e.Message;
            }

            if (sourceRendererCount > 0 && cloneAvatarRenderers.Count < sourceRendererCount * MinAcceptableRendererFraction)
                ok = false;

            if (!ok)
            {
                if (clone != null) UnityEngine.Object.DestroyImmediate(clone);
                ResetCloneState();
                buildInProgress = false;
                yield break;
            }

            clone.name = "HEADFIX DISPLAY CLONE";
            headlessAvatarClone = clone;
            headlessAvatarCloneAnimator = cloneAnim;
            cloneBuiltForAvatarId = manager.avatar.GetInstanceID();
            buildInProgress = false;

            int fastPathBones = 0;
            for (int i = 0; i < bonePairCount; ++i) if (bonePairsArr[i].isPathMatched || bonePairsArr[i].isHeadSubtree) fastPathBones++;

            ApplyVisibilityToAllCameras();
            MelonLogger.Msg($"[OffsetRemover] Clone built -- bonePairs={bonePairCount} ({fastPathBones} fast/local, " +
                             $"{bonePairCount - fastPathBones} world-space fallback), renderers={cloneAvatarRenderers.Count}, " +
                             $"headCorrection={(hasHeadCorrection ? "yes" : "no")}");
        }

        private static void BuildPairings(Animator srcAnim, Animator clnAnim, GameObject srcRoot, GameObject clnRoot)
        {
            headBoneOnClone = null; headBoneOnSource = null; hasHeadCorrection = false;
            headLocalRestPosition = Vector3.zero; headLocalRestRotation = Quaternion.identity;
            headSubtreeLocalPositions.Clear();

            var srcTransforms = CollectSkinnedBones(srcRoot);
            var clnTransforms = CollectSkinnedBones(clnRoot);

            var srcPathMap = new System.Collections.Generic.Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            var clnPathMap = new System.Collections.Generic.Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < srcTransforms.Length; ++i)
            {
                var t = srcTransforms[i];
                if (t == null) continue;
                string p = GetRelativePathFast(t, srcRoot.transform);
                if (!srcPathMap.ContainsKey(p)) srcPathMap[p] = t;
            }
            for (int i = 0; i < clnTransforms.Length; ++i)
            {
                var t = clnTransforms[i];
                if (t == null) continue;
                string p = GetRelativePathFast(t, clnRoot.transform);
                if (!clnPathMap.ContainsKey(p)) clnPathMap[p] = t;
            }

            Transform srcHead = (srcAnim != null && srcAnim.isHuman) ? srcAnim.GetBoneTransform(HumanBodyBones.Head) : null;
            Transform clnHead = (clnAnim != null && clnAnim.isHuman) ? clnAnim.GetBoneTransform(HumanBodyBones.Head) : null;

            if (clnHead == null)
            {
                foreach (var kv in clnPathMap) { if (kv.Key.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0) { clnHead = kv.Value; break; } }
            }
            if (srcHead == null)
            {
                foreach (var kv in srcPathMap) { if (kv.Key.IndexOf("head", StringComparison.OrdinalIgnoreCase) >= 0) { srcHead = kv.Value; break; } }
            }

            if (clnHead != null)
            {
                clnAnim?.Rebind(); clnAnim?.Update(0f);
                headBoneOnClone = clnHead;
                headBoneOnSource = srcHead;
                headLocalRestPosition = clnHead.localPosition;
                headLocalRestRotation = clnHead.localRotation;
                hasHeadCorrection = true;
                var children = clnHead.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < children.Length; ++i)
                {
                    var t = children[i];
                    if (t == null) continue;
                    if (!headSubtreeLocalPositions.ContainsKey(t.name)) headSubtreeLocalPositions[t.name] = t.localPosition;
                }
            }

            EnsureBonePairCapacity(srcTransforms.Length);
            EnsureRendererPairCapacity(Math.Max(16, clnRendersCountEstimate(clnRoot)));

            bonePairCount = 0;
            rendererPairCount = 0;

            var matchedCloneTransforms = new System.Collections.Generic.HashSet<Transform>();
            foreach (var kv in srcPathMap)
            {
                if (clnPathMap.TryGetValue(kv.Key, out var clnT))
                {
                    var bp = new BonePair { src = kv.Value, dst = clnT, isHeadSubtree = IsInHeadSubtreeCached(clnT), isPathMatched = true };
                    if (bp.isHeadSubtree && headSubtreeLocalPositions.TryGetValue(clnT.name, out var restLocal)) bp.headRestLocal = restLocal;
                    bonePairsArr[bonePairCount++] = bp;
                    matchedCloneTransforms.Add(clnT);
                }
            }

            var srcNameMap = BuildNameMap(srcTransforms);
            var clnNameMap = BuildNameMap(clnTransforms);
            foreach (var kv in srcNameMap)
            {
                var srcList = kv.Value;
                if (!clnNameMap.TryGetValue(kv.Key, out var clnList) || clnList.Count == 0) continue;
                for (int i = 0; i < srcList.Count; ++i)
                {
                    var s = srcList[i];
                    bool alreadyPaired = false;
                    for (int j = 0; j < bonePairCount; ++j) if (bonePairsArr[j].src == s) { alreadyPaired = true; break; }
                    if (alreadyPaired) continue;

                    Transform chosen = null;
                    for (int k = 0; k < clnList.Count; ++k) if (!matchedCloneTransforms.Contains(clnList[k])) { chosen = clnList[k]; break; }
                    if (chosen == null) chosen = clnList[0];
                    if (chosen == null) continue;

                    var bp = new BonePair { src = s, dst = chosen, isHeadSubtree = IsInHeadSubtreeCached(chosen) };
                    if (bp.isHeadSubtree && headSubtreeLocalPositions.TryGetValue(chosen.name, out var restLocal)) bp.headRestLocal = restLocal;
                    bonePairsArr[bonePairCount++] = bp;
                    matchedCloneTransforms.Add(chosen);
                }
            }

            var srcRenders = srcRoot.GetComponentsInChildren<Renderer>(true);
            var clnRenders = clnRoot.GetComponentsInChildren<Renderer>(true);
            var clnByName = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Renderer>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < clnRenders.Length; ++i)
            {
                var r = clnRenders[i];
                if (r == null) continue;
                if (!clnByName.TryGetValue(r.name, out var lst)) { lst = new System.Collections.Generic.List<Renderer>(2); clnByName[r.name] = lst; }
                lst.Add(r);
            }

            var matchedClnR = new System.Collections.Generic.HashSet<Renderer>();
            for (int i = 0; i < srcRenders.Length; ++i)
            {
                var s = srcRenders[i];
                if (s == null || string.IsNullOrEmpty(s.name)) continue;
                if (!clnByName.TryGetValue(s.name, out var candidates) || candidates.Count == 0) continue;

                Renderer chosen = null;
                for (int k = 0; k < candidates.Count; ++k) if (!matchedClnR.Contains(candidates[k])) { chosen = candidates[k]; break; }
                if (chosen == null) chosen = candidates[0];
                if (chosen == null) continue;

                try { chosen.sharedMaterials = s.sharedMaterials; } catch { }

                bool forceDisable = s.sharedMaterial != null && s.sharedMaterial.shader != null && s.sharedMaterial.shader.name == "SLZ/Icon Billboard";

                if (forceDisable)
                {
                    try { chosen.forceRenderingOff = true; } catch { }
                    cloneAvatarRenderers.Remove(chosen);
                }
                else
                {
                    var rp = new RendererPair { src = s, dst = chosen, forceDisable = false };
                    rendererPairsArr[rendererPairCount++] = rp;
                }
                matchedClnR.Add(chosen);
            }
        }

        private static readonly System.Collections.Generic.HashSet<Transform> boneCollectScratch = new System.Collections.Generic.HashSet<Transform>();

        private static Transform[] CollectSkinnedBones(GameObject root)
        {
            boneCollectScratch.Clear();
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < smrs.Length; ++i)
            {
                var bones = smrs[i].bones;
                if (bones == null) continue;
                for (int j = 0; j < bones.Length; ++j)
                    if (bones[j] != null) boneCollectScratch.Add(bones[j]);
            }
            var result = new Transform[boneCollectScratch.Count];
            boneCollectScratch.CopyTo(result);
            return result;
        }

        private static int clnRendersCountEstimate(GameObject clnRoot)
        {
            var r = clnRoot.GetComponentsInChildren<Renderer>(true);
            return r != null ? r.Length : 0;
        }

        private static bool IsInHeadSubtreeCached(Transform t)
        {
            if (!hasHeadCorrection || t == null || headBoneOnClone == null) return false;
            var cur = t;
            while (cur != null)
            {
                if (cur == headBoneOnClone) return true;
                cur = cur.parent;
            }
            return false;
        }

        private static System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Transform>> BuildNameMap(Transform[] arr)
        {
            var map = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<Transform>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < arr.Length; ++i)
            {
                var t = arr[i];
                if (t == null || string.IsNullOrEmpty(t.name)) continue;
                if (!map.TryGetValue(t.name, out var lst)) { lst = new System.Collections.Generic.List<Transform>(2); map[t.name] = lst; }
                lst.Add(t);
            }
            return map;
        }

        private static void EnsureBonePairCapacity(int needed)
        {
            if (bonePairsArr.Length >= needed) return;
            int newCap = Math.Max(needed, Math.Max(8, bonePairsArr.Length * 2));
            var newArr = new BonePair[newCap];
            for (int i = 0; i < bonePairsArr.Length; ++i) newArr[i] = bonePairsArr[i];
            bonePairsArr = newArr;
        }
        private static void EnsureRendererPairCapacity(int needed)
        {
            if (rendererPairsArr.Length >= needed) return;
            int newCap = Math.Max(needed, Math.Max(8, rendererPairsArr.Length * 2));
            var newArr = new RendererPair[newCap];
            for (int i = 0; i < rendererPairsArr.Length; ++i) newArr[i] = rendererPairsArr[i];
            rendererPairsArr = newArr;
        }

        private static void ResetCloneState()
        {
            headlessAvatarClone = null;
            headlessAvatarCloneAnimator = null;
            headBoneOnClone = null;
            headBoneOnSource = null;
            hasHeadCorrection = false;
            headLocalRestPosition = Vector3.zero;
            headLocalRestRotation = Quaternion.identity;
            headSubtreeLocalPositions.Clear();
            bonePairCount = 0;
            rendererPairCount = 0;
            selfRenderers.Clear();
            cloneAvatarRenderers.Clear();
            lastAppliedShowSelf = null;
        }

        private static void CleanClone(GameObject clone)
        {
            try { SafeDestroyAll<ConfigurableJoint>(clone.GetComponentsInChildren<ConfigurableJoint>(true)); } catch { }
            try { SafeDestroyAll<Collider>(clone.GetComponentsInChildren<Collider>(true)); } catch { }
            try { SafeDestroyAll<Rigidbody>(clone.GetComponentsInChildren<Rigidbody>(true)); } catch { }
            try { SafeDestroyAll<InteractableHost>(clone.GetComponentsInChildren<InteractableHost>(true)); } catch { }

            var monos = clone.GetComponentsInChildren<MonoBehaviour>(true);
            for (int i = 0; i < monos.Length; ++i)
            {
                var mb = monos[i];
                if (mb == null) continue;
                if (mb.TryCast<Avatar>() != null) continue;
                var pa = mb.TryCast<PlayerAvatarArt>();
                if (pa != null) { pa.enabled = false; continue; }
                try { UnityEngine.Object.DestroyImmediate(mb); } catch { }
            }
        }

        private static void SafeDestroyAll<T>(T[] comps) where T : UnityEngine.Object
        {
            for (int i = 0; i < comps.Length; ++i) { var c = comps[i]; if (c == null) continue; try { UnityEngine.Object.DestroyImmediate(c); } catch { } }
        }

        private static string GetRelativePathFast(Transform t, Transform root)
        {
            if (t == root) return "";
            int depth = 0;
            var cur = t;
            while (cur != null && cur != root && depth < nameStack.Length)
            {
                nameStack[depth++] = cur.name;
                cur = cur.parent;
            }
            pathSb.Clear();
            for (int i = depth - 1; i >= 0; --i)
            {
                pathSb.Append(nameStack[i]);
                if (i > 0) pathSb.Append('/');
            }
            return pathSb.ToString();
        }

        private static bool? lastAppliedShowSelf = null;

        private static void CheckRenderLoop(CameraKind kind)
        {
            if (!Player.HandsExist || !Player.Avatar) return;

            bool showSelf;
            if (kind == CameraKind.Headset) showSelf = true;
            else if (kind == CameraKind.Spectator) showSelf = ignoreSpectator;
            else showSelf = false;

            bool cloneReady = headlessAvatarClone != null && cloneAvatarRenderers.Count > 0;
            bool wantsSelf = showSelf || !cloneReady;

            if (lastAppliedShowSelf == wantsSelf) return;
            lastAppliedShowSelf = wantsSelf;

            if (wantsSelf)
            {
                ToggleAvatarVisibility(selfRenderers, true);
                if (headlessAvatarClone != null) ToggleAvatarVisibility(cloneAvatarRenderers, false);
            }
            else
            {
                ToggleAvatarVisibility(selfRenderers, false);
                ToggleAvatarVisibility(cloneAvatarRenderers, true);
            }
        }

        private static void ToggleAvatarVisibility(System.Collections.Generic.List<Renderer> renderers, bool visible)
        {
            for (int i = 0; i < renderers.Count; ++i)
            {
                var r = renderers[i];
                if (r == null) continue;
                try { r.forceRenderingOff = !visible; } catch { }
            }
        }
    }
}

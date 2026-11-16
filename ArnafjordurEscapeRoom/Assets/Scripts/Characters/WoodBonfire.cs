using System;
using System.Collections.Generic;
using Arnafjordur.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// A stack of <see cref="Firewood"/> arranged like a bonfire. Each piece adds its burn duration
    /// (5 minutes per wood by default), so 2 stacked pieces burn for 10 minutes, 3 for 15 minutes, etc.
    /// Can be ignited with a <see cref="Lighter"/> and fed more wood before or while burning.
    /// </summary>
    public sealed class WoodBonfire : MonoBehaviour
    {
        private const int SampleRate = 22050;
        private const int FlameTongueCount = 5;
        private const int SparkCount = 12;
        private const int SmokeCount = 8;

        private static readonly float[] TeepeeAzimuths =
        {
            0f, 180f, 90f, 270f, 45f, 225f, 135f, 315f, 22.5f, 202.5f, 112.5f, 292.5f,
        };

        private static readonly List<WoodBonfire> ActiveBonfires = new List<WoodBonfire>();
        private static AudioClip s_crackleClip;

        private struct FlameTongue
        {
            public Transform Transform;
            public Vector3 BasePos;
            public Vector3 BaseScale;
            public float Phase;
            public float Speed;
            public float BaseYaw;
        }

        private struct FloatingParticle
        {
            public Transform Transform;
            public Material Material;
            public float Age;
            public float Life;
            public Vector3 Velocity;
            public float StartScale;
            public float EndScale;
            public float SwirlPhase;
        }

        private readonly List<Firewood> _woods = new List<Firewood>();
        private readonly List<Material> _ownedMaterials = new List<Material>();
        private readonly List<Mesh> _ownedMeshes = new List<Mesh>();

        private GameObject _fireRoot;
        private Transform _emberBed;
        private Light _fireLight;
        private AudioSource _audioSource;
        private FlameTongue[] _tongues;
        private FloatingParticle[] _sparks;
        private FloatingParticle[] _smoke;
        private GameObject _ashPatch;
        private Material _ashMaterial;
        private float _burnOutAge;
        private float _nextSparkTime;
        private int _sparkIndex;
        private float _nextSmokeTime;
        private int _smokeIndex;
        private float _lightSeed;

        /// <summary>All currently active bonfires in the scene.</summary>
        public static IReadOnlyList<WoodBonfire> All => ActiveBonfires;

        /// <summary>Fired whenever any bonfire is created, stacked, lit, or burned out.</summary>
        public static event Action AnyChanged;

        /// <summary>Fired when this bonfire's stack or burn state changes.</summary>
        public event Action Changed;

        /// <summary>Number of wood pieces stacked in this bonfire.</summary>
        public int WoodCount => _woods.Count;

        /// <summary>True while actively burning.</summary>
        public bool IsLit { get; private set; }

        /// <summary>True after all fuel has burned out.</summary>
        public bool IsBurnedOut { get; private set; }

        /// <summary>Seconds of burn time currently remaining.</summary>
        public float RemainingSeconds { get; private set; }

        /// <summary>Total burn time accumulated from all pieces added to this bonfire.</summary>
        public float TotalBurnSeconds { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetRegistry()
        {
            ActiveBonfires.Clear();
            AnyChanged = null;
        }

        private void OnEnable()
        {
            EnsureRegistered();
        }

        private void EnsureRegistered()
        {
            if (!ActiveBonfires.Contains(this))
            {
                ActiveBonfires.Add(this);
            }
        }

        private void OnDisable()
        {
            ActiveBonfires.Remove(this);
        }

        private void OnDestroy()
        {
            ActiveBonfires.Remove(this);
            CleanupParticleArray(_sparks);
            CleanupParticleArray(_smoke);
            for (int i = 0; i < _ownedMaterials.Count; i++)
            {
                if (_ownedMaterials[i] != null) DestroyImmediateOrRuntime(_ownedMaterials[i]);
            }

            for (int i = 0; i < _ownedMeshes.Count; i++)
            {
                if (_ownedMeshes[i] != null) DestroyImmediateOrRuntime(_ownedMeshes[i]);
            }

            _ownedMaterials.Clear();
            _ownedMeshes.Clear();
        }

        /// <summary>
        /// Stacks <paramref name="addedWood"/> onto <paramref name="existingBonfire"/> or <paramref name="targetWood"/>,
        /// creating a new <see cref="WoodBonfire"/> when stacking onto a standalone piece of wood.
        /// </summary>
        public static WoodBonfire StackOnto(WoodBonfire existingBonfire, Firewood targetWood, Firewood addedWood)
        {
            if (addedWood == null || addedWood.IsConsumed) return existingBonfire;

            if (existingBonfire == null && targetWood != null)
            {
                existingBonfire = targetWood.Stack;
            }

            if (existingBonfire != null)
            {
                if (existingBonfire.IsBurnedOut) return null;
                existingBonfire.AddWood(addedWood);
                return existingBonfire;
            }

            if (targetWood == null || targetWood.IsConsumed) return null;

            Vector3 basePos = targetWood.transform.position;
            float groundY = SampleGroundY(basePos, basePos.y - targetWood.HalfThickness);
            var root = new GameObject("WoodBonfire");
            root.transform.SetParent(GetOrCreateWorldItemsRoot(), true);
            root.transform.SetPositionAndRotation(
                new Vector3(basePos.x, groundY, basePos.z),
                Quaternion.Euler(0f, targetWood.transform.eulerAngles.y, 0f));

            SphereCollider trigger = root.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = 0.65f;
            trigger.center = new Vector3(0f, 0.25f, 0f);

            WoodBonfire bonfire = root.AddComponent<WoodBonfire>();
            bonfire.EnsureRegistered();
            bonfire._lightSeed = UnityEngine.Random.Range(0f, 100f);
            bonfire.AddWood(targetWood);
            bonfire.AddWood(addedWood);
            return bonfire;
        }

        /// <summary>Lights a single loose <see cref="Firewood"/> piece (or its existing bonfire).</summary>
        public static WoodBonfire IgniteSingle(Firewood wood)
        {
            if (wood == null || wood.IsConsumed || wood.IsHeld) return null;

            if (wood.Stack != null)
            {
                wood.Stack.Ignite();
                return wood.Stack;
            }

            Vector3 basePos = wood.transform.position;
            float groundY = SampleGroundY(basePos, basePos.y - wood.HalfThickness);
            var root = new GameObject("WoodBonfire");
            root.transform.SetParent(GetOrCreateWorldItemsRoot(), true);
            root.transform.SetPositionAndRotation(
                new Vector3(basePos.x, groundY, basePos.z),
                Quaternion.Euler(0f, wood.transform.eulerAngles.y, 0f));

            SphereCollider trigger = root.AddComponent<SphereCollider>();
            trigger.isTrigger = true;
            trigger.radius = 0.65f;
            trigger.center = new Vector3(0f, 0.25f, 0f);

            WoodBonfire bonfire = root.AddComponent<WoodBonfire>();
            bonfire.EnsureRegistered();
            bonfire._lightSeed = UnityEngine.Random.Range(0f, 100f);
            bonfire.AddWood(wood);
            bonfire.Ignite();
            return bonfire;
        }

        /// <summary>Finds the nearest stackable bonfire or loose firewood near <paramref name="referencePoint"/>.</summary>
        public static bool TryFindStackTarget(
            Vector3 referencePoint,
            Vector3 characterPos,
            Firewood ignoreWood,
            float maxRadiusFromRef,
            float maxRadiusFromChar,
            out WoodBonfire bonfire,
            out Firewood looseWood)
        {
            bonfire = null;
            looseWood = null;
            float bestDist = float.PositiveInfinity;

            for (int i = 0; i < ActiveBonfires.Count; i++)
            {
                WoodBonfire candidate = ActiveBonfires[i];
                if (candidate == null || candidate.IsBurnedOut || candidate.WoodCount == 0) continue;

                float refDist = PlanarDistance(referencePoint, candidate.transform.position);
                float charDist = PlanarDistance(characterPos, candidate.transform.position);
                if (refDist <= maxRadiusFromRef
                    && charDist <= maxRadiusFromChar
                    && Mathf.Abs(characterPos.y - candidate.transform.position.y) <= 1.8f
                    && refDist < bestDist)
                {
                    bestDist = refDist;
                    bonfire = candidate;
                    looseWood = null;
                }
            }

            var allWood = Firewood.All;
            for (int i = 0; i < allWood.Count; i++)
            {
                Firewood candidate = allWood[i];
                if (candidate == null || candidate == ignoreWood || candidate.IsHeld || candidate.IsConsumed || candidate.Stack != null)
                {
                    continue;
                }

                float refDist = PlanarDistance(referencePoint, candidate.transform.position);
                float charDist = PlanarDistance(characterPos, candidate.transform.position);
                if (refDist <= maxRadiusFromRef
                    && charDist <= maxRadiusFromChar
                    && Mathf.Abs(characterPos.y - candidate.transform.position.y) <= 1.8f
                    && refDist < bestDist)
                {
                    bestDist = refDist;
                    bonfire = null;
                    looseWood = candidate;
                }
            }

            return bonfire != null || looseWood != null;
        }

        /// <summary>Finds the nearest unlit bonfire or loose piece of wood within <paramref name="maxRadius"/>.</summary>
        public static bool TryFindIgnitionTarget(
            Vector3 characterPos,
            float maxRadius,
            out WoodBonfire unlitBonfire,
            out Firewood looseWood)
        {
            unlitBonfire = null;
            looseWood = null;
            float bestDist = maxRadius;

            for (int i = 0; i < ActiveBonfires.Count; i++)
            {
                WoodBonfire candidate = ActiveBonfires[i];
                if (candidate == null || candidate.IsLit || candidate.IsBurnedOut || candidate.WoodCount == 0) continue;

                float dist = PlanarDistance(characterPos, candidate.transform.position);
                if (dist <= bestDist && Mathf.Abs(characterPos.y - candidate.transform.position.y) <= 1.8f)
                {
                    bestDist = dist;
                    unlitBonfire = candidate;
                    looseWood = null;
                }
            }

            var allWood = Firewood.All;
            for (int i = 0; i < allWood.Count; i++)
            {
                Firewood candidate = allWood[i];
                if (candidate == null || candidate.IsHeld || candidate.IsConsumed || candidate.Stack != null)
                {
                    continue;
                }

                float dist = PlanarDistance(characterPos, candidate.transform.position);
                if (dist <= bestDist && Mathf.Abs(characterPos.y - candidate.transform.position.y) <= 1.8f)
                {
                    bestDist = dist;
                    unlitBonfire = null;
                    looseWood = candidate;
                }
            }

            return unlitBonfire != null || looseWood != null;
        }

        /// <summary>Returns the nearest active bonfire (lit or unlit) within <paramref name="maxRadius"/>.</summary>
        public static WoodBonfire FindNearestInRange(Vector3 characterPos, float maxRadius)
        {
            WoodBonfire best = null;
            float bestDist = maxRadius;
            for (int i = 0; i < ActiveBonfires.Count; i++)
            {
                WoodBonfire candidate = ActiveBonfires[i];
                if (candidate == null || candidate.IsBurnedOut || candidate.WoodCount == 0) continue;
                float dist = PlanarDistance(characterPos, candidate.transform.position);
                if (dist <= bestDist && Mathf.Abs(characterPos.y - candidate.transform.position.y) <= 1.8f)
                {
                    bestDist = dist;
                    best = candidate;
                }
            }

            return best;
        }

        /// <summary>Adds a piece of firewood to this bonfire and extends its burn duration.</summary>
        public void AddWood(Firewood wood)
        {
            if (wood == null || IsBurnedOut || _woods.Contains(wood)) return;

            if (wood.Stack != null && wood.Stack != this)
            {
                wood.Stack.RemoveWood(wood);
            }

            wood.Stack = this;
            wood.transform.SetParent(transform, true);
            wood.SetCharProgress(0f);
            _woods.Add(wood);

            float addedSeconds = Mathf.Max(0f, wood.BurnSeconds);
            RemainingSeconds += addedSeconds;
            TotalBurnSeconds += addedSeconds;

            if (wood.Pickup != null)
            {
                if (IsLit)
                {
                    wood.Pickup.SetCollidersEnabled(false);
                    wood.Pickup.enabled = false;
                }
                else
                {
                    wood.Pickup.enabled = true;
                    wood.Pickup.OnDropped(transform);
                }
            }

            ArrangeStackLayout();
            UpdateFireScaleAndLight(1f);
            NotifyChanged();
        }

        /// <summary>Removes an unlit piece of firewood when picked up; dissolves the stack if only one remains.</summary>
        public void RemoveWood(Firewood wood)
        {
            if (wood == null || !_woods.Remove(wood)) return;

            wood.Stack = null;
            wood.SetCharProgress(0f);

            if (!IsLit)
            {
                RemainingSeconds = Mathf.Max(0f, RemainingSeconds - wood.BurnSeconds);
                TotalBurnSeconds = Mathf.Max(0f, TotalBurnSeconds - wood.BurnSeconds);
            }

            if (!IsLit && _woods.Count == 1)
            {
                Firewood last = _woods[0];
                _woods.Clear();
                if (last != null)
                {
                    last.Stack = null;
                    last.SetCharProgress(0f);
                    Transform worldRoot = GetOrCreateWorldItemsRoot();
                    if (last.Pickup != null)
                    {
                        last.Pickup.enabled = true;
                        last.Pickup.OnDropped(worldRoot);
                    }
                    else
                    {
                        last.transform.SetParent(worldRoot, true);
                    }

                    Vector3 pos = transform.position;
                    last.transform.SetPositionAndRotation(
                        new Vector3(pos.x, pos.y + last.HalfThickness, pos.z),
                        last.GetFlatDropRotation(transform.eulerAngles.y));
                }

                NotifyChanged();
                DestroyImmediateOrRuntime(gameObject);
                return;
            }

            if (_woods.Count == 0)
            {
                NotifyChanged();
                DestroyImmediateOrRuntime(gameObject);
                return;
            }

            ArrangeStackLayout();
            NotifyChanged();
        }

        /// <summary>Lights this bonfire, locking its fuel and starting flames, light, smoke, and audio.</summary>
        public bool Ignite()
        {
            if (IsLit || IsBurnedOut || _woods.Count == 0) return false;

            if (RemainingSeconds <= 0f)
            {
                float sum = 0f;
                for (int i = 0; i < _woods.Count; i++)
                {
                    if (_woods[i] != null) sum += _woods[i].BurnSeconds;
                }

                RemainingSeconds = sum;
                TotalBurnSeconds = Mathf.Max(TotalBurnSeconds, sum);
            }

            IsLit = true;
            for (int i = 0; i < _woods.Count; i++)
            {
                Firewood w = _woods[i];
                if (w != null && w.Pickup != null)
                {
                    w.Pickup.SetCollidersEnabled(false);
                    w.Pickup.enabled = false;
                }
            }

            EnsureFireVisuals();
            if (_fireRoot != null) _fireRoot.SetActive(true);
            if (_audioSource != null && Application.isPlaying && !_audioSource.isPlaying)
            {
                _audioSource.Play();
            }

            UpdateFireScaleAndLight(1f);
            NotifyChanged();
            return true;
        }

        /// <summary>Advances the burn timer and visual effects by <paramref name="deltaTime"/> seconds.</summary>
        public void TickBurn(float deltaTime)
        {
            if (deltaTime <= 0f) return;

            if (IsBurnedOut)
            {
                _burnOutAge += deltaTime;
                if (_ashMaterial != null)
                {
                    float alpha = Mathf.Clamp01(1f - _burnOutAge / 15f) * 0.65f;
                    _ashMaterial.SetColor("_BaseColor", new Color(0.08f, 0.08f, 0.08f, alpha));
                }

                if (_burnOutAge >= 15f)
                {
                    DestroyImmediateOrRuntime(gameObject);
                }

                return;
            }

            if (!IsLit) return;

            RemainingSeconds = Mathf.Max(0f, RemainingSeconds - deltaTime);
            float burnedFraction = TotalBurnSeconds > 0.01f
                ? 1f - Mathf.Clamp01(RemainingSeconds / TotalBurnSeconds)
                : 1f;

            for (int i = 0; i < _woods.Count; i++)
            {
                if (_woods[i] != null)
                {
                    _woods[i].SetCharProgress(burnedFraction);
                }
            }

            float fadeOut = Mathf.Clamp01(RemainingSeconds / 12f);
            UpdateFireVisuals(fadeOut, deltaTime);

            if (RemainingSeconds <= 0f)
            {
                FinishBurn();
            }
        }

        private void Update()
        {
            TickBurn(Time.deltaTime);
        }

        private void FinishBurn()
        {
            IsLit = false;
            IsBurnedOut = true;
            _burnOutAge = 0f;

            if (_audioSource != null) _audioSource.Stop();
            if (_fireRoot != null) _fireRoot.SetActive(false);
            DeactivateParticles(_sparks);
            DeactivateParticles(_smoke);

            for (int i = 0; i < _woods.Count; i++)
            {
                Firewood w = _woods[i];
                if (w != null)
                {
                    w.IsConsumed = true;
                    w.Stack = null;
                    DestroyImmediateOrRuntime(w.gameObject);
                }
            }

            _woods.Clear();
            SpawnAshPatch();
            NotifyChanged();
        }

        /// <summary>Formats seconds as M:SS (e.g. 5:00 or 10:00).</summary>
        public static string FormatDuration(float seconds)
        {
            int total = Mathf.Max(0, Mathf.CeilToInt(seconds));
            int mins = total / 60;
            int secs = total % 60;
            return $"{mins}:{secs:00}";
        }

        private void ArrangeStackLayout()
        {
            int n = _woods.Count;
            if (n == 0) return;

            if (n == 1)
            {
                PlaceWoodLocal(_woods[0], new Vector3(0f, _woods[0].HalfThickness, 0f), Vector3.right, Vector3.up);
                return;
            }

            if (n == 2)
            {
                Firewood w0 = _woods[0];
                Firewood w1 = _woods[1];
                float r0 = w0.HalfThickness;
                float r1 = w1.HalfThickness;

                PlaceWoodLocal(w0, new Vector3(0f, r0, 0f), Vector3.right, Vector3.up);

                // Second wood crosses right on top of the first at ~72 deg with a gentle tilt.
                Vector3 dir1 = Quaternion.Euler(0f, 72f, 0f) * Vector3.right;
                float pitchRad = 11f * Mathf.Deg2Rad;
                Vector3 slopedDir1 = (dir1 * Mathf.Cos(pitchRad) + Vector3.up * Mathf.Sin(pitchRad)).normalized;
                float centerY = Mathf.Max(r1 + Mathf.Sin(pitchRad) * (w1.Length * 0.45f), r0 * 2f + r1 * 0.72f);
                PlaceWoodLocal(w1, new Vector3(0f, centerY, 0f), slopedDir1, Vector3.up);
                return;
            }

            if (n == 3)
            {
                Firewood w0 = _woods[0];
                Firewood w1 = _woods[1];
                Firewood w2 = _woods[2];
                float r0 = w0.HalfThickness;
                float r1 = w1.HalfThickness;
                float r2 = w2.HalfThickness;

                PlaceWoodLocal(w0, new Vector3(0f, r0, 0f), Vector3.right, Vector3.up);

                Vector3 dir1 = Quaternion.Euler(0f, 64f, 0f) * Vector3.right;
                float p1 = 9f * Mathf.Deg2Rad;
                Vector3 sloped1 = (dir1 * Mathf.Cos(p1) + Vector3.up * Mathf.Sin(p1)).normalized;
                float y1 = Mathf.Max(r1 + Mathf.Sin(p1) * (w1.Length * 0.45f), r0 * 1.9f + r1 * 0.7f);
                PlaceWoodLocal(w1, new Vector3(0.03f, y1, 0.02f), sloped1, Vector3.up);

                Vector3 dir2 = Quaternion.Euler(0f, 130f, 0f) * Vector3.right;
                float p2 = 24f * Mathf.Deg2Rad;
                Vector3 sloped2 = (-dir2 * Mathf.Cos(p2) + Vector3.up * Mathf.Sin(p2)).normalized;
                float y2 = Mathf.Max(r2 + Mathf.Sin(p2) * (w2.Length * 0.46f), r0 * 2f + r1 * 1.3f + r2 * 0.55f);
                PlaceWoodLocal(w2, dir2 * 0.06f + Vector3.up * y2, sloped2, Vector3.up);
                return;
            }

            // 4+ pieces: 3-piece crossed base bed + balanced teepee logs leaning inward around the fire.
            float baseR0 = _woods[0].HalfThickness;
            float baseR1 = _woods[1].HalfThickness;
            float baseR2 = _woods[2].HalfThickness;

            PlaceWoodLocal(_woods[0], new Vector3(-0.03f, baseR0, 0f), Vector3.right, Vector3.up);

            Vector3 bDir1 = Quaternion.Euler(0f, 62f, 0f) * Vector3.right;
            float bp1 = 7f * Mathf.Deg2Rad;
            PlaceWoodLocal(
                _woods[1],
                new Vector3(0.03f, Mathf.Max(baseR1 + Mathf.Sin(bp1) * (_woods[1].Length * 0.45f), baseR0 * 1.75f + baseR1 * 0.65f), 0.03f),
                (bDir1 * Mathf.Cos(bp1) + Vector3.up * Mathf.Sin(bp1)).normalized,
                Vector3.up);

            Vector3 bDir2 = Quaternion.Euler(0f, 124f, 0f) * Vector3.right;
            float bp2 = 12f * Mathf.Deg2Rad;
            PlaceWoodLocal(
                _woods[2],
                new Vector3(-0.02f, Mathf.Max(baseR2 + Mathf.Sin(bp2) * (_woods[2].Length * 0.45f), baseR0 * 1.9f + baseR1 * 1.2f + baseR2 * 0.55f), -0.03f),
                (bDir2 * Mathf.Cos(bp2) + Vector3.up * Mathf.Sin(bp2)).normalized,
                Vector3.up);

            for (int i = 3; i < n; i++)
            {
                Firewood w = _woods[i];
                if (w == null) continue;

                int k = i - 3;
                int ring = k / 8;
                float azDeg = k < TeepeeAzimuths.Length
                    ? TeepeeAzimuths[k]
                    : ((k * 137.508f) % 360f);
                azDeg += ring * 18f;

                float pitchDeg = ring == 0 ? 48f - (k % 3) * 3f : 40f;
                float pitchRad = pitchDeg * Mathf.Deg2Rad;
                Vector3 radial = Quaternion.Euler(0f, azDeg, 0f) * Vector3.forward;
                Vector3 localDir = (-radial * Mathf.Cos(pitchRad) + Vector3.up * Mathf.Sin(pitchRad)).normalized;

                float len = w.Length;
                float r = w.HalfThickness;
                float centerY = (len * 0.5f) * Mathf.Sin(pitchRad) + r * 0.9f;
                float radialDist = Mathf.Max(0.08f, (len * 0.5f) * Mathf.Cos(pitchRad) - (ring == 0 ? 0.04f : -0.06f));
                PlaceWoodLocal(w, radial * radialDist + Vector3.up * centerY, localDir, Vector3.up);
            }
        }

        private void PlaceWoodLocal(Firewood wood, Vector3 localPos, Vector3 localLengthDir, Vector3 localUpHint)
        {
            if (wood == null) return;
            Vector3 worldPos = transform.TransformPoint(localPos);
            Vector3 worldDir = transform.TransformDirection(localLengthDir);
            Vector3 worldUp = transform.TransformDirection(localUpHint);
            wood.transform.SetPositionAndRotation(worldPos, wood.ComputeOrientation(worldDir, worldUp));
        }

        private void EnsureFireVisuals()
        {
            if (_fireRoot != null) return;

            _fireRoot = new GameObject("FireEffects");
            _fireRoot.transform.SetParent(transform, false);
            _fireRoot.transform.localPosition = Vector3.zero;

            Mesh tongueMesh = CreateFlameTongueMesh();
            Mesh emberMesh = CreateEmberMoundMesh();
            _ownedMeshes.Add(tongueMesh);
            _ownedMeshes.Add(emberMesh);

            Material outerMat = CreateAdditiveFlameMat(Color.Lerp(Palette.LanternAmber, new Color(1f, 0.36f, 0.05f), 0.45f), 1.45f);
            Material innerMat = CreateAdditiveFlameMat(Color.Lerp(Palette.SunbreakKey, new Color(1f, 0.92f, 0.55f), 0.35f), 1.65f);
            Material emberMat = CreateAdditiveFlameMat(Color.Lerp(Palette.BoathouseRust, new Color(1f, 0.32f, 0.04f), 0.6f), 1.15f);
            _ownedMaterials.Add(outerMat);
            _ownedMaterials.Add(innerMat);
            _ownedMaterials.Add(emberMat);

            var emberGo = new GameObject("EmberBed");
            emberGo.transform.SetParent(_fireRoot.transform, false);
            emberGo.transform.localPosition = new Vector3(0f, 0.025f, 0f);
            emberGo.AddComponent<MeshFilter>().sharedMesh = emberMesh;
            MeshRenderer emberMr = emberGo.AddComponent<MeshRenderer>();
            emberMr.sharedMaterial = emberMat;
            emberMr.shadowCastingMode = ShadowCastingMode.Off;
            emberMr.receiveShadows = false;
            _emberBed = emberGo.transform;

            _tongues = new FlameTongue[FlameTongueCount];
            for (int i = 0; i < FlameTongueCount; i++)
            {
                bool isCore = i < 2;
                float angle = i * (360f / FlameTongueCount);
                float radius = isCore ? 0.03f : 0.08f;
                Vector3 basePos = new Vector3(Mathf.Cos(angle * Mathf.Deg2Rad) * radius, 0.04f, Mathf.Sin(angle * Mathf.Deg2Rad) * radius);
                Vector3 baseScale = isCore
                    ? new Vector3(0.11f, 0.28f, 0.11f)
                    : new Vector3(0.16f + (i % 2) * 0.03f, 0.40f + (i % 3) * 0.06f, 0.16f + (i % 2) * 0.03f);

                var go = new GameObject($"FlameTongue_{i}");
                go.transform.SetParent(_fireRoot.transform, false);
                go.transform.localPosition = basePos;
                go.transform.localRotation = Quaternion.Euler(0f, angle, 0f);
                go.transform.localScale = baseScale;
                go.AddComponent<MeshFilter>().sharedMesh = tongueMesh;
                MeshRenderer mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = isCore ? innerMat : outerMat;
                mr.shadowCastingMode = ShadowCastingMode.Off;
                mr.receiveShadows = false;

                _tongues[i] = new FlameTongue
                {
                    Transform = go.transform,
                    BasePos = basePos,
                    BaseScale = baseScale,
                    Phase = i * 1.37f + _lightSeed,
                    Speed = 6.2f + i * 1.1f,
                    BaseYaw = angle,
                };
            }

            var lightGo = new GameObject("FireLight");
            lightGo.transform.SetParent(_fireRoot.transform, false);
            lightGo.transform.localPosition = new Vector3(0f, 0.42f, 0f);
            _fireLight = lightGo.AddComponent<Light>();
            _fireLight.type = LightType.Point;
            _fireLight.color = Color.Lerp(Palette.LanternAmber, new Color(1f, 0.55f, 0.18f), 0.35f);
            _fireLight.range = 8f;
            _fireLight.intensity = 2.6f;
            _fireLight.shadows = LightShadows.None;

            _audioSource = gameObject.GetComponent<AudioSource>();
            if (_audioSource == null) _audioSource = gameObject.AddComponent<AudioSource>();
            _audioSource.playOnAwake = false;
            _audioSource.loop = true;
            _audioSource.spatialBlend = 1f;
            _audioSource.minDistance = 2.5f;
            _audioSource.maxDistance = 24f;
            _audioSource.rolloffMode = AudioRolloffMode.Linear;
            if (s_crackleClip == null) s_crackleClip = BuildFireCrackleClip();
            _audioSource.clip = s_crackleClip;

            _sparks = CreateParticlePool("Spark", SparkCount, new Color(1f, 0.62f, 0.14f, 0.95f), additive: true);
            _smoke = CreateParticlePool("Smoke", SmokeCount, new Color(0.68f, 0.72f, 0.76f, 0.24f), additive: false);
        }

        private float GetSizeScale()
        {
            int count = Mathf.Max(1, _woods.Count);
            return Mathf.Min(1.75f, 0.58f + Mathf.Sqrt(count) * 0.34f);
        }

        private void UpdateFireScaleAndLight(float fadeOut)
        {
            if (_fireRoot == null || !IsLit) return;

            float scale = GetSizeScale() * Mathf.Lerp(0.25f, 1f, fadeOut);
            if (_emberBed != null)
            {
                _emberBed.localScale = new Vector3(scale * 0.42f, scale * 0.08f, scale * 0.42f);
            }

            if (_fireLight != null)
            {
                float flicker = Mathf.PerlinNoise(Time.time * 10.5f, _lightSeed);
                _fireLight.range = Mathf.Lerp(5.5f, 12.5f, Mathf.InverseLerp(0.6f, 1.7f, GetSizeScale()));
                _fireLight.intensity = (2.1f + flicker * 1.15f) * scale;
                _fireLight.transform.localPosition = new Vector3(
                    (flicker - 0.5f) * 0.06f,
                    0.28f * scale + 0.14f,
                    (Mathf.PerlinNoise(_lightSeed, Time.time * 9f) - 0.5f) * 0.06f);
            }

            if (_audioSource != null)
            {
                _audioSource.volume = Mathf.Clamp01(0.32f + 0.18f * scale) * fadeOut;
            }
        }

        private void UpdateFireVisuals(float fadeOut, float dt)
        {
            UpdateFireScaleAndLight(fadeOut);

            float size = GetSizeScale() * Mathf.Lerp(0.2f, 1f, fadeOut);
            float t = Time.time;

            if (_tongues != null)
            {
                for (int i = 0; i < _tongues.Length; i++)
                {
                    ref FlameTongue tongue = ref _tongues[i];
                    if (tongue.Transform == null) continue;

                    float wave = Mathf.Sin(t * tongue.Speed + tongue.Phase);
                    float noise = Mathf.PerlinNoise(t * 7.5f + tongue.Phase, _lightSeed + i);
                    float hScale = (0.82f + 0.36f * noise) * size;
                    float wScale = (0.86f + 0.22f * wave) * size;

                    tongue.Transform.localPosition = tongue.BasePos * Mathf.Max(0.65f, size);
                    tongue.Transform.localScale = new Vector3(
                        tongue.BaseScale.x * wScale,
                        tongue.BaseScale.y * hScale,
                        tongue.BaseScale.z * wScale);
                    tongue.Transform.localRotation = Quaternion.Euler(
                        wave * 9f,
                        tongue.BaseYaw + noise * 22f,
                        (noise - 0.5f) * 14f);
                }
            }

            if (!Application.isPlaying) return;

            UpdateParticles(_sparks, ref _nextSparkTime, ref _sparkIndex, 0.16f / Mathf.Max(0.6f, size), dt,
                isSpark: true, size: size, fadeOut: fadeOut, baseColor: new Color(1f, 0.62f, 0.14f, 0.95f));
            UpdateParticles(_smoke, ref _nextSmokeTime, ref _smokeIndex, 0.38f, dt,
                isSpark: false, size: size, fadeOut: fadeOut, baseColor: new Color(0.70f, 0.74f, 0.78f, 0.22f));
        }

        private void UpdateParticles(
            FloatingParticle[] pool,
            ref float nextSpawn,
            ref int index,
            float interval,
            float dt,
            bool isSpark,
            float size,
            float fadeOut,
            Color baseColor)
        {
            if (pool == null) return;

            if (Time.time >= nextSpawn && fadeOut > 0.1f)
            {
                nextSpawn = Time.time + interval;
                ref FloatingParticle p = ref pool[index];
                index = (index + 1) % pool.Length;

                p.Age = 0f;
                p.Life = isSpark ? UnityEngine.Random.Range(0.75f, 1.4f) : UnityEngine.Random.Range(1.6f, 2.6f);
                Vector2 jitter = UnityEngine.Random.insideUnitCircle * (isSpark ? 0.16f : 0.12f) * size;
                float startY = isSpark ? 0.12f * size : 0.36f * size;
                p.Transform.position = transform.position + new Vector3(jitter.x, startY, jitter.y);
                p.Velocity = isSpark
                    ? new Vector3(UnityEngine.Random.Range(-0.18f, 0.18f), UnityEngine.Random.Range(0.75f, 1.35f) * size, UnityEngine.Random.Range(-0.18f, 0.18f))
                    : new Vector3(UnityEngine.Random.Range(-0.08f, 0.08f), UnityEngine.Random.Range(0.35f, 0.55f), UnityEngine.Random.Range(-0.08f, 0.08f));
                p.StartScale = isSpark ? 0.018f : 0.07f * size;
                p.EndScale = isSpark ? 0.004f : 0.28f * size;
                p.SwirlPhase = UnityEngine.Random.Range(0f, Mathf.PI * 2f);
                p.Transform.localScale = Vector3.one * p.StartScale;
                p.Transform.gameObject.SetActive(true);
            }

            for (int i = 0; i < pool.Length; i++)
            {
                ref FloatingParticle p = ref pool[i];
                if (p.Transform == null || !p.Transform.gameObject.activeSelf) continue;

                p.Age += dt;
                if (p.Age >= p.Life)
                {
                    p.Transform.gameObject.SetActive(false);
                    continue;
                }

                float u = p.Age / p.Life;
                Vector3 swirl = isSpark
                    ? new Vector3(Mathf.Sin(p.Age * 7f + p.SwirlPhase) * 0.25f, 0f, Mathf.Cos(p.Age * 6f + p.SwirlPhase) * 0.25f)
                    : Vector3.zero;
                p.Transform.position += (p.Velocity + swirl) * dt;
                p.Transform.localScale = Vector3.one * Mathf.Lerp(p.StartScale, p.EndScale, u);

                if (p.Material != null)
                {
                    float fade = (1f - u) * fadeOut;
                    Color c = isSpark
                        ? new Color(baseColor.r * fade, baseColor.g * fade, baseColor.b * fade, baseColor.a)
                        : new Color(baseColor.r, baseColor.g, baseColor.b, baseColor.a * fade);
                    p.Material.SetColor("_BaseColor", c);
                }
            }
        }

        private FloatingParticle[] CreateParticlePool(string label, int count, Color color, bool additive)
        {
            var pool = new FloatingParticle[count];
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            for (int i = 0; i < count; i++)
            {
                GameObject go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = $"{label}_{i}";
                go.hideFlags = HideFlags.DontSave;
                if (go.TryGetComponent(out Collider col)) DestroyImmediateOrRuntime(col);
                go.SetActive(false);

                Material mat = null;
                if (shader != null && go.TryGetComponent(out MeshRenderer mr))
                {
                    mat = CreateTransparentUnlitMaterial(shader, color, additive);
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                }

                pool[i] = new FloatingParticle
                {
                    Transform = go.transform,
                    Material = mat,
                };
            }

            return pool;
        }

        private void SpawnAshPatch()
        {
            if (_ashPatch != null) return;
            _ashPatch = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            _ashPatch.name = "AshPatch";
            _ashPatch.transform.SetParent(transform, false);
            _ashPatch.transform.localPosition = new Vector3(0f, 0.008f, 0f);
            _ashPatch.transform.localScale = new Vector3(0.45f, 0.008f, 0.45f);
            if (_ashPatch.TryGetComponent(out Collider col)) DestroyImmediateOrRuntime(col);

            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader != null && _ashPatch.TryGetComponent(out MeshRenderer mr))
            {
                _ashMaterial = CreateTransparentUnlitMaterial(shader, new Color(0.08f, 0.08f, 0.08f, 0.65f), additive: false);
                _ownedMaterials.Add(_ashMaterial);
                mr.sharedMaterial = _ashMaterial;
                mr.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        private static Material CreateAdditiveFlameMat(Color color, float intensity)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            if (shader == null) return null;
            Color c = color * intensity;
            c.a = 1f;
            return CreateTransparentUnlitMaterial(shader, c, additive: true);
        }

        private static Material CreateTransparentUnlitMaterial(Shader shader, Color color, bool additive)
        {
            var mat = new Material(shader);
            mat.SetFloat("_Surface", 1f);
            mat.SetFloat("_Blend", additive ? 2f : 0f);
            mat.SetFloat("_SrcBlend", (float)(additive ? BlendMode.One : BlendMode.SrcAlpha));
            mat.SetFloat("_DstBlend", (float)(additive ? BlendMode.One : BlendMode.OneMinusSrcAlpha));
            mat.SetFloat("_ZWrite", 0f);
            mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            mat.renderQueue = (int)RenderQueue.Transparent;
            mat.SetOverrideTag("RenderType", "Transparent");
            mat.SetColor("_BaseColor", color);
            return mat;
        }

        private static Mesh CreateFlameTongueMesh()
        {
            var profile = new[]
            {
                new Vector2(0.08f, 0f),
                new Vector2(0.46f, 0.15f),
                new Vector2(0.44f, 0.36f),
                new Vector2(0.28f, 0.64f),
                new Vector2(0.11f, 0.88f),
                new Vector2(0f, 1.0f),
            };
            return BuildLatheMesh("BonfireFlameTongue", profile, 8);
        }

        private static Mesh CreateEmberMoundMesh()
        {
            var profile = new[]
            {
                new Vector2(0f, 0f),
                new Vector2(0.55f, 0f),
                new Vector2(0.48f, 0.45f),
                new Vector2(0.24f, 0.85f),
                new Vector2(0f, 1.0f),
            };
            return BuildLatheMesh("BonfireEmberMound", profile, 10);
        }

        private static Mesh BuildLatheMesh(string name, Vector2[] profile, int segs)
        {
            int rings = profile.Length;
            var verts = new Vector3[rings * segs];
            var tris = new List<int>(rings * segs * 6);

            for (int r = 0; r < rings; r++)
            {
                float rad = profile[r].x;
                float y = profile[r].y;
                for (int s = 0; s < segs; s++)
                {
                    float a = 2f * Mathf.PI * s / segs;
                    verts[r * segs + s] = new Vector3(Mathf.Cos(a) * rad, y, Mathf.Sin(a) * rad);
                }
            }

            for (int r = 0; r < rings - 1; r++)
            {
                for (int s = 0; s < segs; s++)
                {
                    int s1 = (s + 1) % segs;
                    int a = r * segs + s;
                    int b = r * segs + s1;
                    int c = (r + 1) * segs + s;
                    int d = (r + 1) * segs + s1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            }

            var mesh = new Mesh { name = name };
            mesh.vertices = verts;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            return mesh;
        }

        private static AudioClip BuildFireCrackleClip()
        {
            const float seconds = 2.5f;
            int length = Mathf.CeilToInt(SampleRate * seconds);
            var data = new float[length];
            var rng = new System.Random(4207);

            // Soft low-passed noise bed for warm fire roar
            float lp = 0f;
            for (int i = 0; i < length; i++)
            {
                float white = (float)rng.NextDouble() * 2f - 1f;
                lp = lp * 0.92f + white * 0.08f;
                data[i] = lp * 0.28f;
            }

            // Scattered crisp wood pops & crackles
            for (int p = 0; p < 34; p++)
            {
                int start = rng.Next(0, length - 400);
                float amp = 0.20f + (float)rng.NextDouble() * 0.35f;
                float freq = 900f + (float)rng.NextDouble() * 1900f;
                for (int j = 0; j < 260 && start + j < length; j++)
                {
                    float t = j / (float)SampleRate;
                    float env = Mathf.Exp(-t * 240f);
                    float pop = Mathf.Sin(2f * Mathf.PI * freq * t) + ((float)rng.NextDouble() * 2f - 1f) * 0.65f;
                    data[start + j] += pop * env * amp;
                }
            }

            // Gentle crossfade at wrap boundary for seamless looping
            int fade = 400;
            for (int i = 0; i < fade; i++)
            {
                float w = i / (float)fade;
                data[i] = Mathf.Lerp(data[length - fade + i], data[i], w);
            }

            AudioClip clip = AudioClip.Create("BonfireCrackle", length, 1, SampleRate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static void DeactivateParticles(FloatingParticle[] pool)
        {
            if (pool == null) return;
            for (int i = 0; i < pool.Length; i++)
            {
                if (pool[i].Transform != null) pool[i].Transform.gameObject.SetActive(false);
            }
        }

        private static void CleanupParticleArray(FloatingParticle[] pool)
        {
            if (pool == null) return;
            for (int i = 0; i < pool.Length; i++)
            {
                if (pool[i].Transform != null) DestroyImmediateOrRuntime(pool[i].Transform.gameObject);
                if (pool[i].Material != null) DestroyImmediateOrRuntime(pool[i].Material);
            }
        }

        private void NotifyChanged()
        {
            Changed?.Invoke();
            AnyChanged?.Invoke();
        }

        private static float PlanarDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static float SampleGroundY(Vector3 at, float fallback)
        {
            RaycastHit[] hits = Physics.RaycastAll(
                new Vector3(at.x, at.y + 1.5f, at.z),
                Vector3.down,
                12f,
                Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);

            if (hits != null && hits.Length > 0)
            {
                Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
                for (int i = 0; i < hits.Length; i++)
                {
                    Collider col = hits[i].collider;
                    if (col == null) continue;
                    if (col.gameObject.layer == 2 || col.gameObject.name == "CarObstacle") continue;
                    if (col.GetComponentInParent<CharacterMotor>() != null || col.GetComponentInParent<PickupItem>() != null) continue;
                    return hits[i].point.y;
                }
            }

            return fallback;
        }

        private static Transform GetOrCreateWorldItemsRoot()
        {
            GameObject root = GameObject.Find("_WorldItems");
            if (root == null) root = new GameObject("_WorldItems");
            return root.transform;
        }

        private static void DestroyImmediateOrRuntime(UnityEngine.Object obj)
        {
            if (obj == null) return;
            if (Application.isPlaying) Destroy(obj);
            else DestroyImmediate(obj);
        }
    }
}

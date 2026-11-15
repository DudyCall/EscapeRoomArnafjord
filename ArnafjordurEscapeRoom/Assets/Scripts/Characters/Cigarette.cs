using UnityEngine;

namespace Arnafjordur.Characters
{
    /// <summary>
    /// A single cigarette taken from a <see cref="CigarettePack"/> and held in the character's mouth.
    /// Starts unlit; a <see cref="Lighter"/> can ignite its ember and rising smoke puffs.
    /// </summary>
    public sealed class Cigarette : MonoBehaviour
    {
        private const int PuffCount = 5;

        [SerializeField] private GameObject ember;
        [SerializeField] private Light emberLight;
        [SerializeField] private Transform tipPoint;

        private struct SmokePuff
        {
            public Transform transform;
            public Material material;
            public float age;
            public float life;
            public Vector3 drift;
        }

        private SmokePuff[] _puffs;
        private float _nextPuffTime;
        private int _puffIndex;

        private bool _thrown;
        private Vector3 _from, _to, _spinAxis;
        private float _flightTime, _flightT, _extinguishAt;

        public bool IsLit { get; private set; }

        /// <summary>World position of the tobacco tip where a lighter flame should touch.</summary>
        public Vector3 TipWorldPosition => tipPoint != null
            ? tipPoint.position
            : transform.TransformPoint(new Vector3(0f, 0f, 0.082f));

        public void Configure(GameObject emberObject, Light glow, Transform tip)
        {
            ember = emberObject;
            emberLight = glow;
            tipPoint = tip;
            SetLit(false);
        }

        public void SetLit(bool lit)
        {
            IsLit = lit;
            if (ember != null) ember.SetActive(lit);
            if (emberLight != null) emberLight.enabled = lit;
            if (!lit && _puffs != null)
            {
                for (int i = 0; i < _puffs.Length; i++)
                {
                    if (_puffs[i].transform != null)
                        _puffs[i].transform.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>Flicks the finished cigarette butt onto the ground ahead of <paramref name="thrower"/>.</summary>
        public void ThrowFrom(Transform thrower)
        {
            _thrown = true;
            transform.SetParent(null, true);
            _from = transform.position;

            Vector3 fwd = thrower.forward;
            fwd.y = 0f;
            fwd = fwd.sqrMagnitude > 0.001f ? fwd.normalized : Vector3.forward;
            Vector3 side = Vector3.Cross(Vector3.up, fwd);
            Vector3 target = thrower.position + fwd * Random.Range(1.1f, 1.7f) + side * Random.Range(-0.55f, 0.25f);
            _to = new Vector3(target.x, GroundY(target, thrower.position.y) + 0.004f, target.z);
            _flightTime = 0.55f;
            _flightT = 0f;
            _spinAxis = (side + fwd * 0.3f).normalized;
            _extinguishAt = Time.time + 4.5f;
            Destroy(gameObject, _flightTime + 30f);
        }

        private void Update()
        {
            if (_thrown)
            {
                if (_flightT < _flightTime)
                {
                    _flightT = Mathf.Min(_flightTime, _flightT + Time.deltaTime);
                    float u = _flightT / _flightTime;
                    Vector3 p = Vector3.Lerp(_from, _to, u);
                    p.y += Mathf.Sin(u * Mathf.PI) * 0.35f;
                    transform.position = p;
                    if (u < 1f) transform.Rotate(_spinAxis, 720f * Time.deltaTime, Space.World);
                    else transform.rotation = Quaternion.LookRotation(Vector3.Cross(_spinAxis, Vector3.up), Vector3.up);
                }

                if (IsLit && Time.time >= _extinguishAt)
                {
                    SetLit(false);
                }
            }

            if (!IsLit) return;

            if (emberLight != null)
            {
                emberLight.intensity = 0.10f + 0.05f * Mathf.PerlinNoise(Time.time * 9f, 1.7f);
            }

            if (ember != null)
            {
                float pulse = 1f + 0.08f * Mathf.Sin(Time.time * 14f);
                ember.transform.localScale = new Vector3(pulse, pulse, 1f);
            }

            UpdateSmokePuffs();
        }

        private void UpdateSmokePuffs()
        {
            if (_puffs == null) CreateSmokePuffs();

            if (Time.time >= _nextPuffTime)
            {
                _nextPuffTime = Time.time + 0.42f;
                ref SmokePuff p = ref _puffs[_puffIndex];
                _puffIndex = (_puffIndex + 1) % _puffs.Length;

                p.age = 0f;
                p.life = 1.6f;
                p.drift = new Vector3(Random.Range(-0.02f, 0.02f), 0.14f, Random.Range(-0.02f, 0.02f));
                p.transform.position = TipWorldPosition + Vector3.up * 0.008f;
                p.transform.localScale = Vector3.one * 0.007f;
                p.transform.gameObject.SetActive(true);
            }

            for (int i = 0; i < _puffs.Length; i++)
            {
                ref SmokePuff p = ref _puffs[i];
                if (p.transform == null || !p.transform.gameObject.activeSelf) continue;

                p.age += Time.deltaTime;
                if (p.age >= p.life)
                {
                    p.transform.gameObject.SetActive(false);
                    continue;
                }

                float t = p.age / p.life;
                p.transform.position += p.drift * Time.deltaTime;
                p.transform.localScale = Vector3.one * Mathf.Lerp(0.007f, 0.032f, t);
                if (p.material != null)
                {
                    float alpha = (1f - t) * 0.28f;
                    p.material.SetColor("_BaseColor", new Color(0.82f, 0.84f, 0.87f, alpha));
                }
            }
        }

        private void CreateSmokePuffs()
        {
            _puffs = new SmokePuff[PuffCount];
            Shader shader = Shader.Find("Universal Render Pipeline/Unlit");
            for (int i = 0; i < PuffCount; i++)
            {
                var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                go.name = "SmokePuff";
                go.hideFlags = HideFlags.DontSave;
                if (go.TryGetComponent(out Collider col)) Destroy(col);
                go.SetActive(false);

                Material mat = null;
                if (shader != null && go.TryGetComponent(out MeshRenderer mr))
                {
                    mat = new Material(shader);
                    mat.SetFloat("_Surface", 1f);
                    mat.SetFloat("_Blend", 0f);
                    mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetFloat("_ZWrite", 0f);
                    mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    mat.SetColor("_BaseColor", new Color(0.82f, 0.84f, 0.87f, 0.28f));
                    mr.sharedMaterial = mat;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                }

                _puffs[i] = new SmokePuff { transform = go.transform, material = mat };
            }
        }

        private void OnDestroy()
        {
            if (_puffs == null) return;
            for (int i = 0; i < _puffs.Length; i++)
            {
                if (_puffs[i].transform != null) Destroy(_puffs[i].transform.gameObject);
                if (_puffs[i].material != null) Destroy(_puffs[i].material);
            }
        }

        private static float GroundY(Vector3 at, float fallback)
        {
            RaycastHit[] hits = Physics.RaycastAll(at + Vector3.up * 4f, Vector3.down, 12f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            float best = float.NegativeInfinity;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.GetComponentInParent<CharacterMotor>() != null) continue;
                if (hit.point.y > best) best = hit.point.y;
            }

            return float.IsNegativeInfinity(best) ? fallback : best;
        }
    }
}

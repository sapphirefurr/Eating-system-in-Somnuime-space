using SomniumSpace.Bridge;
using SomniumSpace.Bridge.Player;
using SomniumSpace.Worlds.Snacks;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

namespace SomniumSpace.Worlds.Snacks
{
    public class GrabAndEat : MonoBehaviour
    {
        [Header("Table Settings")]
        [Tooltip("Where the food teleports back to after being eaten.")]
        [SerializeField] private Transform tableSpawnPoint;

        [Header("Mesh")]
        [Tooltip("The GameObject to hide/show when eaten and respawned. Defaults to this GameObject if unassigned.")]
        [SerializeField] private GameObject foodMeshObject;
        [Tooltip("How long in seconds before the food reappears on the table.")]
        [SerializeField] private float respawnDelay = 1f;

        [Header("Mouth Detection")]
        [Tooltip("Distance from head/camera to count as eating in metres.")]
        [SerializeField] private float mouthRadius = 0.3f;

        [Header("Networking")]
        [Tooltip("The NetworkEnableObject on this same GameObject.")]
        [SerializeField] private NetworkEnableObject networkEnableObject;

        [Header("Optional Feedback")]
        [SerializeField] private AudioSource audioSource;
        [SerializeField] private AudioClip eatSFX;
        [SerializeField] private ParticleSystem eatParticles;

        [Header("Lost Food Recovery")]
        [Tooltip("If the food is held for longer than this many seconds without being eaten, it teleports back to the table. Set to 0 to disable.")]
        [SerializeField] private float grabTimeoutSeconds = 5f;

        // Cached components
        private XRGrabInteractable _grabInteractable;
        private Rigidbody _rigidbody;

        // State
        private bool _isGrabbed;
        private bool _hasBeenEaten;
        private bool _respawnPending;
        private bool _audioPlayed;
        private bool _particlePlayed;

        // Player head references
        private Transform _localPlayerHead;
        private bool _headSearchDone;
        private Camera _mainCamera;

        // 10Hz mouth check timer
        private float _mouthCheckTimer;
        private const float MOUTH_CHECK_INTERVAL = 0.1f;

        // Debug distance log timer
        private float _debugLogTimer;
        private const float DEBUG_LOG_INTERVAL = 1f;

        // Grab timeout timer
        private float _grabTimer;

        #region Unity Lifecycle

        private void Awake()
        {
            _grabInteractable = GetComponent<XRGrabInteractable>();
            _rigidbody = GetComponent<Rigidbody>();

            if (foodMeshObject == null)
                foodMeshObject = gameObject;

            ValidateReferences();

            if (_grabInteractable != null)
            {
                _grabInteractable.selectEntered.AddListener(OnGrabbed);
                _grabInteractable.selectExited.AddListener(OnReleased);
            }
        }

        private void OnEnable()
        {
            TryGetPlayerReferences();

            var container = SomniumBridge.PlayersContainer;
            if (container != null)
                container.OnLocalPlayerAdded += OnLocalPlayerAdded;
        }

        private void OnDisable()
        {
            var container = SomniumBridge.PlayersContainer;
            if (container != null)
                container.OnLocalPlayerAdded -= OnLocalPlayerAdded;
        }

        private void OnDestroy()
        {
            CancelInvoke(nameof(ShowMesh));

            if (_grabInteractable != null)
            {
                _grabInteractable.selectEntered.RemoveListener(OnGrabbed);
                _grabInteractable.selectExited.RemoveListener(OnReleased);
            }
        }

        private void Update()
        {
            if (!_isGrabbed || _hasBeenEaten) return;

            // Grab timeout - teleport food back if held too long without eating
            if (grabTimeoutSeconds > 0f)
            {
                _grabTimer += Time.deltaTime;
                if (_grabTimer >= grabTimeoutSeconds)
                {
                    Debug.Log("GrabAndEat: Grab timeout reached - teleporting food back to table.");
                    _grabTimer = 0f;
                    TeleportToTable();
                    return;
                }
            }

            // Throttle mouth check to 10Hz
            _mouthCheckTimer += Time.deltaTime;
            if (_mouthCheckTimer < MOUTH_CHECK_INTERVAL) return;
            _mouthCheckTimer = 0f;

            if (!TryGetMouthPosition(out Vector3 mouthPos, out string mouthSource)) return;

            float dist = Vector3.Distance(transform.position, mouthPos);

            LogDistanceDebug(dist, mouthSource);

            if (dist <= mouthRadius)
            {
                Debug.Log("GrabAndEat: Mouth distance reached! Sending eat event.");
                SendEatEvent();
            }
        }

        #endregion

        #region Grab Events

        private void OnGrabbed(SelectEnterEventArgs args)
        {
            _isGrabbed = true;
            _grabTimer = 0f;
            _mouthCheckTimer = 0f;
            _debugLogTimer = 0f;
            TryGetPlayerReferences();
            Debug.Log("GrabAndEat: Grabbed | head=" + (_localPlayerHead != null)
                + " camera=" + (_mainCamera != null)
                + " eaten=" + _hasBeenEaten);
        }

        private void OnReleased(SelectExitEventArgs args)
        {
            _isGrabbed = false;
            _grabTimer = 0f;
            Debug.Log("GrabAndEat: Released.");
        }

        #endregion

        #region Player Reference

        private void OnLocalPlayerAdded(ISomniumPlayer player)
        {
            _localPlayerHead = player?.References?.Body?.Head;
            _headSearchDone = _localPlayerHead != null;
            Debug.Log("GrabAndEat: OnLocalPlayerAdded - head found: " + _headSearchDone);
        }

        private void TryGetPlayerReferences()
        {
            if (!_headSearchDone)
            {
                var localPlayer = SomniumBridge.PlayersContainer?.LocalPlayer;
                if (localPlayer != null)
                {
                    _localPlayerHead = localPlayer.References?.Body?.Head;
                    _headSearchDone = _localPlayerHead != null;
                    Debug.Log("GrabAndEat: Head reference found: " + _headSearchDone);
                }
                else
                {
                    Debug.Log("GrabAndEat: LocalPlayer null - will retry on grab.");
                }
            }

            if (_mainCamera == null && Camera.main != null)
            {
                _mainCamera = Camera.main;
                Debug.Log("GrabAndEat: Camera.main cached: " + _mainCamera.name);
            }
        }

        private bool TryGetMouthPosition(out Vector3 mouthPos, out string source)
        {
            if (_localPlayerHead != null)
            {
                mouthPos = _localPlayerHead.position;
                source = "BridgeHead";
                return true;
            }

            if (_mainCamera == null && Camera.main != null)
                _mainCamera = Camera.main;

            if (_mainCamera != null)
            {
                mouthPos = _mainCamera.transform.position;
                source = "Camera";
                return true;
            }

            Debug.LogWarning("GrabAndEat: No head or camera reference available.");
            mouthPos = Vector3.zero;
            source = "None";
            return false;
        }

        #endregion

        #region Eat Logic

        private void SendEatEvent()
        {
            _hasBeenEaten = true;

            if (networkEnableObject != null)
            {
                Debug.Log("GrabAndEat: Sending eat event via NetworkEnableObject.");
                networkEnableObject.SetTargetEnabled(false);
            }
            else
            {
                Debug.LogWarning("GrabAndEat: No NetworkEnableObject - falling back to local eat.");
                OnNetworkEatReceived(false);
            }
        }

        /// <summary>
        /// Wire this to NetworkEnableObject's _onChange UnityEvent in the Inspector.
        /// Fires on ALL clients when the eat RPC is received.
        /// </summary>
        public void OnNetworkEatReceived(bool isEnabled)
        {
            // Ignore re-enable calls - those are handled by ShowMesh
            if (isEnabled) return;

            // Guard against duplicate calls from multiple clients
            if (_respawnPending)
            {
                Debug.Log("GrabAndEat: Respawn already pending, ignoring duplicate call.");
                return;
            }

            _respawnPending = true;
            Debug.Log("GrabAndEat: Eat received - hiding mesh and scheduling respawn.");

            HideMesh();
            PlayEatFeedback();

            Invoke(nameof(ShowMesh), respawnDelay);
        }

        private void HideMesh()
        {
            if (foodMeshObject != null)
            {
                foodMeshObject.SetActive(false);
                Debug.Log("GrabAndEat: Mesh hidden.");
            }
        }

        private void PlayEatFeedback()
        {
            if (!_audioPlayed && audioSource != null && eatSFX != null)
            {
                _audioPlayed = true;
                audioSource.PlayOneShot(eatSFX);
            }

            // Guard particles the same way as audio - only plays once per eat
            if (!_particlePlayed && eatParticles != null)
            {
                _particlePlayed = true;
                eatParticles.Play();
            }
        }

        private void ShowMesh()
        {
            _respawnPending = false;
            _hasBeenEaten = false;
            _audioPlayed = false;
            _particlePlayed = false;

            // Skip teleport if currently grabbed to avoid snap-while-held issues
            if (_isGrabbed)
            {
                Debug.Log("GrabAndEat: Skipping teleport - object is currently grabbed.");
                if (foodMeshObject != null) foodMeshObject.SetActive(true);
                networkEnableObject?.SetTargetEnabled(true);
                return;
            }

            // Teleport while mesh is still hidden, then reveal
            TeleportToTable();

            if (foodMeshObject != null)
            {
                foodMeshObject.SetActive(true);
                Debug.Log("GrabAndEat: Mesh shown.");
            }

            networkEnableObject?.SetTargetEnabled(true);
        }

        private void TeleportToTable()
        {
            if (tableSpawnPoint == null)
            {
                Debug.LogWarning("GrabAndEat: tableSpawnPoint not assigned - cannot teleport.");
                return;
            }

            if (_rigidbody != null)
            {
                _rigidbody.constraints = RigidbodyConstraints.None;
                _rigidbody.linearVelocity = Vector3.zero;
                _rigidbody.angularVelocity = Vector3.zero;
            }

            transform.position = tableSpawnPoint.position;
            transform.rotation = tableSpawnPoint.rotation;

            if (_rigidbody != null)
                _rigidbody.constraints = RigidbodyConstraints.FreezeAll;

            Debug.Log("GrabAndEat: Teleported to " + tableSpawnPoint.position);
        }

        #endregion

        #region Validation & Debug

        private void ValidateReferences()
        {
            if (_grabInteractable == null)
                Debug.LogWarning("GrabAndEat: No XRGrabInteractable found on " + gameObject.name);
            if (networkEnableObject == null)
                Debug.LogWarning("GrabAndEat: NetworkEnableObject not assigned on " + gameObject.name);
            if (tableSpawnPoint == null)
                Debug.LogWarning("GrabAndEat: tableSpawnPoint not assigned on " + gameObject.name);
        }

        private void LogDistanceDebug(float dist, string source)
        {
            _debugLogTimer += Time.deltaTime;
            if (_debugLogTimer < DEBUG_LOG_INTERVAL) return;
            _debugLogTimer = 0f;
            Debug.Log("GrabAndEat: dist=" + dist.ToString("F3")
                + " radius=" + mouthRadius
                + " source=" + source);
        }

        #endregion
    }
}